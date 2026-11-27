// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Options;
using Microsoft.Health.Core.Features.Security.Authorization;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Exceptions;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Security;
using Microsoft.Health.Fhir.Core.Features.Security.Authorization;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Checks deletes that may remove subscription topics (ADR-2611). Deleting a topic needs the <see cref="DataActions.SubscriptionTopic"/>
    /// permission, and is refused while a subscription in <c>requested</c>, <c>active</c> or <c>error</c> uses it. On R4 topics are
    /// <c>Basic</c> resources, so only the resources that actually are topics are checked, not every <c>Basic</c>.
    /// </summary>
    public sealed class SubscriptionTopicDeletionGuard : ISubscriptionTopicDeletionGuard
    {
        private readonly SubscriptionsConfiguration _configuration;
        private readonly ISubscriptionModelConverter _converter;
        private readonly ISubscriptionResourceLoader _loader;
        private readonly SubscriptionRegistry _registry;
        private readonly IAuthorizationService<DataActions> _authorizationService;

        public SubscriptionTopicDeletionGuard(
            IOptions<SubscriptionsConfiguration> configuration,
            ISubscriptionModelConverter converter,
            ISubscriptionResourceLoader loader,
            SubscriptionRegistry registry,
            IAuthorizationService<DataActions> authorizationService)
        {
            _configuration = EnsureArg.IsNotNull(configuration?.Value, nameof(configuration));
            _converter = EnsureArg.IsNotNull(converter, nameof(converter));
            _loader = EnsureArg.IsNotNull(loader, nameof(loader));
            _registry = EnsureArg.IsNotNull(registry, nameof(registry));
            _authorizationService = EnsureArg.IsNotNull(authorizationService, nameof(authorizationService));
        }

        /// <inheritdoc />
        /// <exception cref="UnauthorizedFhirActionException">A topic is deleted without the permission.</exception>
        /// <exception cref="ResourceConflictException">A topic is used by a live subscription.</exception>
        public async Task EnsureCanDeleteAsync(IEnumerable<(string ResourceType, string Id)> resources, bool checkPermission, bool removesCurrentVersion, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(resources, nameof(resources));

            if (!_configuration.Enabled || !_converter.IsSupported || (!checkPermission && !removesCurrentVersion))
            {
                return;
            }

            var ids = resources
                .Where(r => string.Equals(r.ResourceType, _converter.TopicResourceType, StringComparison.Ordinal))
                .Select(r => r.Id)
                .ToHashSet(StringComparer.Ordinal);
            if (ids.Count == 0)
            {
                return;
            }

            List<ResourceElement> topics = (await _loader.GetTopicsAsync(cancellationToken))
                .Where(t => ids.Contains(t.Id) && _converter.IsTopic(t))
                .ToList();
            if (topics.Count == 0)
            {
                return;
            }

            if (checkPermission)
            {
                await _authorizationService.CheckAccess(DataActions.SubscriptionTopic, throwException: true, cancellationToken);
            }

            _registry.Invalidate();
            if (!removesCurrentVersion)
            {
                return;
            }

            var canonicals = new HashSet<string>(StringComparer.Ordinal);
            foreach (ResourceElement resource in topics)
            {
                try
                {
                    SubscriptionTopicInfo topic = _converter.ToTopic(resource);
                    if (!string.IsNullOrEmpty(topic.Canonical))
                    {
                        canonicals.Add(topic.Canonical);
                        canonicals.Add(topic.VersionedCanonical);
                    }
                }
                catch (SubscriptionValidationException)
                {
                    // A topic that cannot be read cannot be used by any subscription.
                }
            }

            foreach (ResourceElement resource in await _loader.GetSubscriptionsAsync(cancellationToken))
            {
                SubscriptionInfo subscription;
                try
                {
                    subscription = _converter.ToSubscription(resource);
                }
                catch (SubscriptionValidationException)
                {
                    continue;
                }

                if (subscription.Status.IsLive() && subscription.TopicCanonical != null && canonicals.Contains(subscription.TopicCanonical))
                {
                    throw new ResourceConflictException(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionTopicInUse, subscription.TopicCanonical, subscription.Id));
                }
            }
        }
    }
}
