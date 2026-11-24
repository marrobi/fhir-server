// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Writes server-owned status changes directly to the data store, bypassing the request pipeline so that the write is not
    /// treated as a client change. The write is conditional on the expected version, so concurrent client updates win.
    /// </summary>
    public sealed class SubscriptionStatusWriter : ISubscriptionStatusWriter
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IResourceDeserializer _deserializer;
        private readonly ISubscriptionModelConverter _converter;
        private readonly ILogger<SubscriptionStatusWriter> _logger;

        public SubscriptionStatusWriter(IServiceScopeFactory scopeFactory, IResourceDeserializer deserializer, ISubscriptionModelConverter converter, ILogger<SubscriptionStatusWriter> logger)
        {
            _scopeFactory = EnsureArg.IsNotNull(scopeFactory, nameof(scopeFactory));
            _deserializer = EnsureArg.IsNotNull(deserializer, nameof(deserializer));
            _converter = EnsureArg.IsNotNull(converter, nameof(converter));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        public async Task<bool> SetStatusAsync(string subscriptionId, string expectedVersionId, SubscriptionStatusCode status, string error, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(subscriptionId, nameof(subscriptionId));

            using IServiceScope scope = _scopeFactory.CreateScope();
            IFhirDataStore dataStore = scope.ServiceProvider.GetRequiredService<IFhirDataStore>();
            IResourceWrapperFactory wrapperFactory = scope.ServiceProvider.GetRequiredService<IResourceWrapperFactory>();

            ResourceWrapper current = await dataStore.GetAsync(new ResourceKey(KnownResourceTypes.Subscription, subscriptionId), cancellationToken);
            if (current == null || current.IsDeleted || (expectedVersionId != null && !string.Equals(current.Version, expectedVersionId, StringComparison.Ordinal)))
            {
                return false;
            }

            ResourceElement updated = _converter.WithStatus(_deserializer.Deserialize(current), status, error);
            ResourceWrapper wrapper = wrapperFactory.Create(updated, deleted: false, keepMeta: true);

            try
            {
                await dataStore.UpsertAsync(
                    new ResourceWrapperOperation(wrapper, allowCreate: false, keepHistory: true, weakETag: WeakETag.FromVersionId(current.Version), requireETagOnUpdate: false, keepVersion: false, bundleResourceContext: null),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is PreconditionFailedException or ResourceConflictException or ResourceNotFoundException)
            {
                _logger.LogInformation("Subscription {SubscriptionId} changed before its status could be set to {Status}.", subscriptionId, status);
                return false;
            }

            _logger.LogInformation("Subscription {SubscriptionId} status set to {Status}.", subscriptionId, status);
            return true;
        }
    }
}
