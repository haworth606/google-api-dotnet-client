﻿/*
Copyright 2014 Google Inc

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using Google.Apis.Auth.OAuth2.Responses;
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Google.Apis.Auth.OAuth2
{
    /// <summary>
    /// Google OAuth 2.0 credential for accessing protected resources using an access token. The Google OAuth 2.0 
    /// Authorization Server supports server-to-server interactions such as those between a web application and Google
    /// Cloud Storage. The requesting application has to prove its own identity to gain access to an API, and an 
    /// end-user doesn't have to be involved. 
    /// <para>
    /// More details about Compute Engine authentication is available at:
    /// https://cloud.google.com/compute/docs/authentication.
    /// </para>
    /// </summary>
    public class ComputeCredential : ServiceCredential
    {
        /// <summary>The metadata server url.</summary>
        public const string MetadataServerUrl = "http://169.254.169.254"; // IP address instead of name to avoid DNS resolution

        /// <summary>Caches result from first call to <c>IsRunningOnComputeEngine</c> </summary>
        private readonly static Lazy<Task<bool>> isRunningOnComputeEngineCached = new Lazy<Task<bool>>(
            () => IsRunningOnComputeEngineNoCache());

        /// <summary>
        /// Originally 1000ms was used without a retry. This proved inadequate; even 2000ms without
        /// a retry occasionally failed. We have observed that after a timeout, the next attempt
        /// succeeds very quickly (sub-50ms) which suggests that this should be fine.
        /// </summary>
        private const int MetadataServerPingTimeoutInMilliseconds = 500;

        private const int MetadataServerPingAttempts = 3;

        /// <summary>The Metadata flavor header name.</summary>
        private const string MetadataFlavor = "Metadata-Flavor";

        /// <summary>The Metadata header response indicating Google.</summary>
        private const string GoogleMetadataHeader = "Google";
        
        /// <summary>
        /// An initializer class for the Compute credential. It uses <see cref="GoogleAuthConsts.ComputeTokenUrl"/>
        /// as the token server URL.
        /// </summary>
        new public class Initializer : ServiceCredential.Initializer
        {
            /// <summary>Constructs a new initializer using the default compute token URL.</summary>
            public Initializer()
                : this(GoogleAuthConsts.ComputeTokenUrl) {}

            /// <summary>Constructs a new initializer using the given token URL.</summary>
            public Initializer(string tokenUrl)
                : base(tokenUrl) {}
        }

        /// <summary>Constructs a new Compute credential instance.</summary>
        public ComputeCredential() : this(new Initializer()) { }

        /// <summary>Constructs a new Compute credential instance.</summary>
        public ComputeCredential(Initializer initializer) : base(initializer) { }

        #region ServiceCredential overrides

        /// <inheritdoc/>
        public override async Task<bool> RequestAccessTokenAsync(CancellationToken taskCancellationToken)
        {
            // Create and send the HTTP request to compute server token URL.
            var httpRequest = new HttpRequestMessage(HttpMethod.Get, TokenServerUrl);
            httpRequest.Headers.Add(MetadataFlavor, GoogleMetadataHeader);
            var response = await HttpClient.SendAsync(httpRequest, taskCancellationToken).ConfigureAwait(false);
            Token = await TokenResponse.FromHttpResponseAsync(response, Clock, Logger);
            return true;
        }

        #endregion

        /// <summary>
        /// Detects if application is running on Google Compute Engine. This is achieved by attempting to contact
        /// GCE metadata server, that is only available on GCE. The check is only performed the first time you
        /// call this method, subsequent invocations used cached result of the first call.
        /// </summary>
        public static Task<bool> IsRunningOnComputeEngine()
        {
            return isRunningOnComputeEngineCached.Value;
        }

        private static async Task<bool> IsRunningOnComputeEngineNoCache()
        {
            Logger.Info("Checking connectivity to ComputeEngine metadata server.");

            // Using the built-in HttpClient, as we want bare bones functionality - we'll control retries.
            // Use the same one across all attempts, which may contribute to speedier retries.
            using (var httpClient = new HttpClient())
            {
                for (int i = 0; i < MetadataServerPingAttempts; i++)
                {
                    var cts = new CancellationTokenSource();
                    cts.CancelAfter(MetadataServerPingTimeoutInMilliseconds);
                    try
                    {
                        var httpRequest = new HttpRequestMessage(HttpMethod.Get, MetadataServerUrl);
                        var response = await httpClient.SendAsync(httpRequest, cts.Token).ConfigureAwait(false);
                        if (response.Headers.TryGetValues(MetadataFlavor, out var headerValues)
                            && headerValues.Contains(GoogleMetadataHeader))
                        {
                            return true;
                        }

                        // Response came from another source, possibly a proxy server in the caller's network.
                        Logger.Info("Response came from a source other than the Google Compute Engine metadata server.");
                        return false;
                    }
                    catch (Exception e) when (e is HttpRequestException || e is WebException || e is OperationCanceledException)
                    {
                        // No-op; we'll retry.
                        // We may eventually want to handle the different exception types in different ways,
                        // e.g. returning false rather than retrying for some exception types. However,
                        // for now it's safe just to retry.
                    }
                }
            }
            // Only log after all attempts have failed.
            Logger.Debug("Could not reach the Google Compute Engine metadata service. That is expected if this application is not running on GCE.");
            return false;
        }
    }
}
