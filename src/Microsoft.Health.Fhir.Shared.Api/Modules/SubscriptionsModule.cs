// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using EnsureThat;
using Medino;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Health.Extensions.DependencyInjection;
using Microsoft.Health.Fhir.Api.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Authorization;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Channels;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Delivery;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry;
using Microsoft.Health.Fhir.Core.Logging.Metrics;
using Microsoft.Health.Fhir.Core.Logging.Metrics.Handlers;
using Microsoft.Health.Fhir.Core.Messages.Search;

namespace Microsoft.Health.Fhir.Api.Modules
{
    /// <summary>
    /// Registers topic-based subscriptions (ADR-2611). The services are always registered so that the request pipeline resolves
    /// consistently, but every component is inert unless <c>FhirServer:Subscriptions:Enabled</c> is true.
    /// </summary>
    public class SubscriptionsModule : IStartupModule
    {
        private readonly FhirServerConfiguration _configuration;

        public SubscriptionsModule(FhirServerConfiguration configuration)
        {
            _configuration = EnsureArg.IsNotNull(configuration, nameof(configuration));
        }

        public void Load(IServiceCollection services)
        {
            EnsureArg.IsNotNull(services, nameof(services));

            services.AddSingleton<ISubscriptionModelConverter, SubscriptionModelConverter>();
            services.AddSingleton<ISubscriptionNotificationFactory, SubscriptionNotificationFactory>();
            services.AddSingleton<ISubscriptionResourceLoader, SubscriptionResourceLoader>();
            services.AddSingleton<ISubscriptionStatusWriter, SubscriptionStatusWriter>();
            services.AddSingleton<SubscriptionEndpointPolicy>();
            services.AddSingleton<InMemorySearchEvaluator>();
            services.AddSingleton<FhirPathCriteriaEvaluator>();
            services.AddSingleton<SubscriptionMatcher>();
            services.AddSingleton<SubscriptionAuthorizer>();
            services.AddSingleton<SubscriptionValidator>();
            services.AddSingleton<SubscriptionRegistry>();
            services.AddSingleton<SubscriptionEventProcessor>();
            services.AddSingleton<ISubscriptionChannel, RestHookSubscriptionChannel>();
            services.TryAddSingleton<ISubscriptionMetricHandler, DefaultSubscriptionMetricHandler>();

            // The SQL Server registration replaces this; other data stores do not support subscriptions.
            services.TryAddSingleton<ISubscriptionStore, UnsupportedSubscriptionStore>();

            services.AddHttpClient(RestHookSubscriptionChannel.HttpClientName)
                .ConfigurePrimaryHttpMessageHandler(provider => CreateRestHookHandler(provider.GetRequiredService<SubscriptionEndpointPolicy>()));

            services
                .RemoveServiceTypeExact<SubscriptionDeliveryService, INotificationHandler<SearchParametersInitializedNotification>>()
                .Add<SubscriptionDeliveryService>()
                .Singleton()
                .AsSelf()
                .AsService<IHostedService>()
                .AsService<INotificationHandler<SearchParametersInitializedNotification>>();
        }

        /// <summary>
        /// Creates the handler for rest-hook notifications. Every address the endpoint resolves to is checked when connecting, so DNS
        /// cannot redirect a notification to a disallowed address. Proxies are not used, because the check would then apply to the
        /// proxy rather than the endpoint, and redirects are not followed.
        /// </summary>
        /// <param name="policy">The endpoint policy.</param>
        /// <returns>The handler.</returns>
        public static SocketsHttpHandler CreateRestHookHandler(SubscriptionEndpointPolicy policy)
        {
            EnsureArg.IsNotNull(policy, nameof(policy));

            return new SocketsHttpHandler
            {
                UseProxy = false,
                AllowAutoRedirect = false,
                UseCookies = false,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                ConnectCallback = async (context, cancellationToken) =>
                {
                    IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
                    IPAddress[] allowed = addresses.Where(policy.IsAddressAllowed).ToArray();
                    if (allowed.Length == 0)
                    {
                        throw new HttpRequestException(string.Format(System.Globalization.CultureInfo.InvariantCulture, Core.Resources.SubscriptionEndpointAddressNotAllowed, context.DnsEndPoint.Host));
                    }

                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            };
        }
    }
}
