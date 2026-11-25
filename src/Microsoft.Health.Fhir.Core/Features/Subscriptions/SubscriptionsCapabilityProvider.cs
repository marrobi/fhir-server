// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Conformance;
using Microsoft.Health.Fhir.Core.Features.Conformance.Models;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry;
using Microsoft.Health.Fhir.Core.Models;
using Newtonsoft.Json.Linq;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Declares subscription support in the CapabilityStatement: the Backport server CapabilityStatement in <c>instantiates</c>
    /// and the Backport subscription profile in <c>supportedProfile</c> (R4, R4B), the <c>$status</c> operation, and on R4 the
    /// known topics, through the topic-canonical extension refreshed with the statement, and the topic profile on <c>Basic</c>.
    /// </summary>
    public sealed class SubscriptionsCapabilityProvider : IRefreshCapability, IInstantiateCapability
    {
        private readonly SubscriptionsConfiguration _configuration;
        private readonly ISubscriptionModelConverter _converter;
        private readonly IModelInfoProvider _modelInfoProvider;
        private readonly SubscriptionRegistry _registry;
        private readonly ISubscriptionStore _store;
        private readonly ILogger<SubscriptionsCapabilityProvider> _logger;

        public SubscriptionsCapabilityProvider(
            IOptions<SubscriptionsConfiguration> configuration,
            ISubscriptionModelConverter converter,
            IModelInfoProvider modelInfoProvider,
            SubscriptionRegistry registry,
            ISubscriptionStore store,
            ILogger<SubscriptionsCapabilityProvider> logger)
        {
            _configuration = EnsureArg.IsNotNull(configuration?.Value, nameof(configuration));
            _converter = EnsureArg.IsNotNull(converter, nameof(converter));
            _modelInfoProvider = EnsureArg.IsNotNull(modelInfoProvider, nameof(modelInfoProvider));
            _registry = EnsureArg.IsNotNull(registry, nameof(registry));
            _store = EnsureArg.IsNotNull(store, nameof(store));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        // A store that is not ready yet, for example during the database schema upgrade, becomes ready without a restart, and
        // subscription writes return 503 meanwhile; so it is advertised. A store that does not support subscriptions is not.
        private bool IsEnabled => _configuration.Enabled && _converter.IsSupported && _store.Availability != SubscriptionStoreAvailability.NotSupported;

        public async Task BuildAsync(ICapabilityStatementBuilder builder, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(builder, nameof(builder));
            if (!IsEnabled)
            {
                return;
            }

            if (_modelInfoProvider.Version == FhirSpecification.R4)
            {
                // R4 topics are Basic resources; declare the profile that describes them.
                builder.ApplyToResource(KnownResourceTypes.Basic, resource => resource.SupportedProfile.Add(SubscriptionsBackport.R4TopicProfile));
            }

            builder.ApplyToResource(KnownResourceTypes.Subscription, resource =>
            {
                if (_modelInfoProvider.Version is FhirSpecification.R4 or FhirSpecification.R4B)
                {
                    resource.SupportedProfile.Add(SubscriptionsBackport.SubscriptionProfile);
                }

                resource.Operation.Add(new OperationComponent
                {
                    Name = "status",
                    Definition = new ReferenceComponent { Reference = OperationsConstants.SubscriptionStatusUri },
                });
            });

            await RefreshAsync(builder, cancellationToken);
        }

        /// <summary>
        /// Replaces the topic-canonical extensions (R4) with the topics known now.
        /// </summary>
        /// <param name="builder">The CapabilityStatement builder.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task.</returns>
        public async Task RefreshAsync(ICapabilityStatementBuilder builder, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(builder, nameof(builder));
            if (!IsEnabled || _modelInfoProvider.Version != FhirSpecification.R4 || _store.Availability != SubscriptionStoreAvailability.Available)
            {
                return;
            }

            List<string> topics;
            try
            {
                SubscriptionRegistrySnapshot snapshot = await _registry.GetAsync(TimeSpan.FromSeconds(_configuration.RegistryRefreshIntervalSeconds), cancellationToken);
                topics = snapshot.Topics.Values.Select(t => t.Canonical).Distinct(StringComparer.Ordinal).OrderBy(t => t, StringComparer.Ordinal).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep the topics listed last time; topics are also discoverable by search.
                _logger.LogInformation(ex, "Subscription topics could not be listed in the CapabilityStatement.");
                return;
            }

            builder.ApplyToResource(KnownResourceTypes.Subscription, resource =>
            {
                if (topics.Count == 0)
                {
                    resource.AdditionalData.Remove("extension");
                    return;
                }

                resource.AdditionalData["extension"] = new JArray(topics.Select(t => new JObject
                {
                    ["url"] = SubscriptionsBackport.TopicCanonicalCapability,
                    ["valueCanonical"] = t,
                }));
            });
        }

        public Task<ICollection<string>> GetCanonicalUrlsAsync(CancellationToken cancellationToken)
        {
            ICollection<string> urls = !IsEnabled ? [] : _modelInfoProvider.Version switch
            {
                FhirSpecification.R4 => [SubscriptionsBackport.ServerCapabilityStatementR4],
                FhirSpecification.R4B => [SubscriptionsBackport.ServerCapabilityStatementR4B],
                _ => [],
            };

            return Task.FromResult(urls);
        }
    }
}
