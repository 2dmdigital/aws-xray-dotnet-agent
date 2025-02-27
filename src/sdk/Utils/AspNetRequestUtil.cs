﻿//-----------------------------------------------------------------------------
// <copyright file="AspNetRequestUtil.cs" company="Amazon.com">
//      Copyright 2020 Amazon.com, Inc. or its affiliates. All Rights Reserved.
//
//      Licensed under the Apache License, Version 2.0 (the "License").
//      You may not use this file except in compliance with the License.
//      A copy of the License is located at
//
//      http://aws.amazon.com/apache2.0
//
//      or in the "license" file accompanying this file. This file is distributed
//      on an "AS IS" BASIS, WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either
//      express or implied. See the License for the specific language governing
//      permissions and limitations under the License.
// </copyright>
//-----------------------------------------------------------------------------

#if NET45
using Amazon.Runtime.Internal.Util;
using Amazon.XRay.Recorder.Core;
using Amazon.XRay.Recorder.Core.Exceptions;
using Amazon.XRay.Recorder.Core.Internal.Context;
using Amazon.XRay.Recorder.Core.Internal.Entities;
using Amazon.XRay.Recorder.Core.Sampling;
using Amazon.XRay.Recorder.Core.Strategies;
using System;
using System.Collections.Generic;
using System.Web;

namespace Amazon.XRay.Recorder.AutoInstrumentation.Utils
{
    /// <summary>
    /// This class provides methods to set up segment naming strategy, process Asp.Net incoming
    /// request, response and exception.
    /// </summary>
    public class AspNetRequestUtil
    {
        private static AWSXRayRecorder _recorder;
        private static SegmentNamingStrategy segmentNamingStrategy;
        private static readonly Logger _logger = Logger.GetLogger(typeof(AspNetRequestUtil));

        /// <summary>
        /// Initialize AWSXRayRecorder instance, register configurations and tracing handlers
        /// </summary>
        internal static void InitializeAspNet()
        {
            if (!AWSXRayRecorder.IsCustomRecorder) // If custom recorder is not set
            {
                AWSXRayRecorder.Instance.SetTraceContext(new HybridContextContainer()); // configure Trace Context
            }

            _recorder = AWSXRayRecorder.Instance;

            // Register configurations 
            var xrayAutoInstrumentationOptions = XRayConfiguration.Register();

            _recorder.SetDaemonAddress(xrayAutoInstrumentationOptions.DaemonAddress);

            if (GetSegmentNamingStrategy() == null) // ensures only one time initialization among many HTTPApplication instances
            {
                var serviceName = xrayAutoInstrumentationOptions.ServiceName;
                InitializeAspNet(new FixedSegmentNamingStrategy(serviceName));
            }

            // Initialize tracing handlers for Asp.Net platform
            AspNetTracingHandlers.Initialize(xrayAutoInstrumentationOptions);
        }

        private static SegmentNamingStrategy GetSegmentNamingStrategy()
        {
            return segmentNamingStrategy;
        }

        private static void InitializeAspNet(FixedSegmentNamingStrategy segmentNamingStrategy)
        {
            if (segmentNamingStrategy == null)
            {
                throw new ArgumentNullException("segmentNamingStrategy");
            }

            if (GetSegmentNamingStrategy() == null) // ensures only one time initialization among many HTTPApplication instances
            {
                SetSegmentNamingStrategy(segmentNamingStrategy);
            }
        }

        private static void SetSegmentNamingStrategy(SegmentNamingStrategy value)
        {
            segmentNamingStrategy = value;
        }

        internal static void ProcessHTTPRequest(object sender, EventArgs e)
        {
            var context = ((HttpApplication)sender).Context;

            string ruleName = null;

            var request = context.Request;
            TraceHeader traceHeader = GetTraceHeader(context);

            var segmentName = GetSegmentNamingStrategy().GetSegmentName(request);
            // Make sample decision
            if (traceHeader.Sampled == SampleDecision.Unknown || traceHeader.Sampled == SampleDecision.Requested)
            {
                SamplingResponse response = MakeSamplingDecision(request, traceHeader, segmentName);
                ruleName = response.RuleName;
            }

            var timestamp = context.Timestamp.ToUniversalTime(); // Gets initial timestamp of current HTTP Request

            SamplingResponse samplingResponse = new SamplingResponse(ruleName, traceHeader.Sampled); // get final ruleName and SampleDecision
            _recorder.BeginSegment(segmentName, traceHeader.RootTraceId, traceHeader.ParentId, samplingResponse, timestamp);

            // Mark the segment as auto-instrumented
            try
            {
                AgentUtil.AddAutoInstrumentationMark();
            }
            catch (Exception exception)
            {
                //ToDo Log this
            }

            if (!AWSXRayRecorder.Instance.IsTracingDisabled())
            {
                var requestAttributes = ProcessRequestAttributes(request);
                _recorder.AddHttpInformation("request", requestAttributes);
            }
        }

        private static Dictionary<string, object> ProcessRequestAttributes(HttpRequest request)
        {
            var requestAttributes = new Dictionary<string, object>();

            requestAttributes["url"] = request.Url.AbsoluteUri;
            requestAttributes["user_agent"] = request.UserAgent;
            requestAttributes["method"] = request.HttpMethod;
            string xForwardedFor = GetXForwardedFor(request);

            if (xForwardedFor == null)
            {
                requestAttributes["client_ip"] = GetClientIpAddress(request);
            }
            else
            {
                requestAttributes["client_ip"] = xForwardedFor;
                requestAttributes["x_forwarded_for"] = true;
            }

            return requestAttributes;
        }

        private static object GetClientIpAddress(HttpRequest request)
        {
            return request.UserHostAddress;
        }

        private static string GetXForwardedFor(HttpRequest request)
        {
            string clientIp = request.ServerVariables["HTTP_X_FORWARDED_FOR"];
            return string.IsNullOrEmpty(clientIp) ? null : clientIp.Split(',')[0].Trim();
        }

        private static SamplingResponse MakeSamplingDecision(HttpRequest request, TraceHeader traceHeader, string segmentName)
        {
            string host = request.Headers.Get("Host");
            string url = request.Url.AbsolutePath;
            string method = request.HttpMethod;
            SamplingInput samplingInput = new SamplingInput(host, url, method, segmentName, _recorder.Origin);
            SamplingResponse sampleResponse = _recorder.SamplingStrategy.ShouldTrace(samplingInput);
            traceHeader.Sampled = sampleResponse.SampleDecision;
            return sampleResponse;
        }

        private static TraceHeader GetTraceHeader(HttpContext context)
        {
            var request = context.Request;
            string headerString = request.Headers.Get(TraceHeader.HeaderKey);

            // Trace header doesn't exist, which means this is the root node. Create a new traceId and inject the trace header.
            if (!TraceHeader.TryParse(headerString, out TraceHeader traceHeader))
            {
                _logger.DebugFormat("Trace header doesn't exist or not valid : ({0}). Injecting a new one.", headerString);
                traceHeader = new TraceHeader
                {
                    RootTraceId = TraceId.NewId(),
                    ParentId = null,
                    Sampled = SampleDecision.Unknown
                };
            }

            return traceHeader;
        }

        internal static void ProcessHTTPResponse(object sender, EventArgs e)
        {
            var context = ((HttpApplication)sender).Context;
            var response = context.Response;

            if (!AWSXRayRecorder.Instance.IsTracingDisabled() && response != null)
            {
                var responseAttributes = ProcessResponseAttributes(response);
                _recorder.AddHttpInformation("response", responseAttributes);
            }

            Exception exc = context.Error; // Record exception, if any

            if (exc != null)
            {
                _recorder.AddException(exc);
            }

            TraceHeader traceHeader = GetTraceHeader(context);
            bool isSampleDecisionRequested = traceHeader.Sampled == SampleDecision.Requested;

            if (traceHeader.Sampled == SampleDecision.Unknown || traceHeader.Sampled == SampleDecision.Requested)
            {
                SetSamplingDecision(traceHeader); // extracts sampling decision from the available segment
            }

            _recorder.EndSegment();
            // if the sample decision is requested, add the trace header to response
            if (isSampleDecisionRequested)
            {
                response.Headers.Add(TraceHeader.HeaderKey, traceHeader.ToString());
            }
        }

        private static Dictionary<string, object> ProcessResponseAttributes(HttpResponse response)
        {
            var responseAttributes = new Dictionary<string, object>();

            int statusCode = (int)response.StatusCode;
            responseAttributes["status"] = statusCode;

            AgentUtil.MarkEntityFromStatus(statusCode);

            return responseAttributes;
        }

        private static void SetSamplingDecision(TraceHeader traceHeader)
        {
            try
            {
                Segment segment = (Segment)AWSXRayRecorder.Instance.GetEntity();
                traceHeader.Sampled = segment.Sampled;
            }

            catch (InvalidCastException e)
            {
                _logger.Error(new EntityNotAvailableException("Failed to cast the entity to Segment.", e), "Failed to  get the segment from trace context for setting sampling decision in the response.");
            }

            catch (EntityNotAvailableException e)
            {
                AWSXRayRecorder.Instance.TraceContext.HandleEntityMissing(AWSXRayRecorder.Instance, e, "Failed to get entity since it is not available in trace context while processing ASPNET request.");
            }
        }

        internal static void ProcessHTTPError(object sender, EventArgs e)
        {
            ProcessHTTPRequest(sender, e);
        }
    }
}
#endif
