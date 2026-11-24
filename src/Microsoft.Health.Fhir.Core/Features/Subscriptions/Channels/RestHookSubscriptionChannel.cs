// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Channels
{
    /// <summary>
    /// The <c>rest-hook</c> channel: an HTTPS POST of the notification bundle to the subscription endpoint.
    /// </summary>
    public sealed class RestHookSubscriptionChannel : ISubscriptionChannel
    {
        /// <summary>
        /// The name of the <see cref="HttpClient"/> used for notifications. Its handler enforces <see cref="SubscriptionEndpointPolicy"/>
        /// on every connection.
        /// </summary>
        public const string HttpClientName = "FhirSubscriptionsRestHook";

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly SubscriptionEndpointPolicy _endpointPolicy;
        private readonly SubscriptionsConfiguration _configuration;
        private readonly ILogger<RestHookSubscriptionChannel> _logger;

        public RestHookSubscriptionChannel(
            IHttpClientFactory httpClientFactory,
            SubscriptionEndpointPolicy endpointPolicy,
            IOptions<SubscriptionsConfiguration> configuration,
            ILogger<RestHookSubscriptionChannel> logger)
        {
            _httpClientFactory = EnsureArg.IsNotNull(httpClientFactory, nameof(httpClientFactory));
            _endpointPolicy = EnsureArg.IsNotNull(endpointPolicy, nameof(endpointPolicy));
            _configuration = EnsureArg.IsNotNull(configuration?.Value, nameof(configuration));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        public string ChannelType => SubscriptionInfo.RestHookChannel;

        public async Task<ChannelDeliveryResult> SendAsync(SubscriptionInfo subscription, string payload, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(subscription, nameof(subscription));
            EnsureArg.IsNotNull(payload, nameof(payload));

            if (!_endpointPolicy.TryValidate(subscription.Endpoint, out string policyError))
            {
                return ChannelDeliveryResult.Failure(policyError);
            }

            int timeoutSeconds = Math.Clamp(subscription.TimeoutSeconds ?? _configuration.DefaultTimeoutSeconds, 1, _configuration.MaxTimeoutSeconds);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Post, subscription.Endpoint);
            var content = new StringContent(payload, Encoding.UTF8);
            content.Headers.ContentType = MediaTypeHeaderValue.Parse(subscription.ContentType);
            if (string.IsNullOrEmpty(content.Headers.ContentType.CharSet))
            {
                content.Headers.ContentType.CharSet = "utf-8";
            }

            request.Content = content;
            foreach (SubscriptionHeader header in subscription.Headers)
            {
                request.Headers.TryAddWithoutValidation(header.Name, header.Value);
            }

            try
            {
                HttpClient client = _httpClientFactory.CreateClient(HttpClientName);
                using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (response.IsSuccessStatusCode)
                {
                    return ChannelDeliveryResult.Success;
                }

                return ChannelDeliveryResult.Failure(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionEndpointReturnedStatus, (int)response.StatusCode));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return ChannelDeliveryResult.Failure(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionEndpointTimedOut, timeoutSeconds));
            }
            catch (HttpRequestException ex)
            {
                // Header values are never logged, because they may carry subscriber secrets.
                _logger.LogInformation(ex, "Notification delivery for subscription {SubscriptionId} failed.", subscription.Id);
                return ChannelDeliveryResult.Failure(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionEndpointUnreachable, ex.HttpRequestError));
            }
        }
    }
}
