// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using EnsureThat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Authorization;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching
{
    /// <summary>
    /// Matches committed resource changes to subscriptions and decides which become outbox events.
    /// </summary>
    public sealed class SubscriptionEventProcessor
    {
        private readonly SubscriptionMatcher _matcher;
        private readonly SubscriptionAuthorizer _authorizer;
        private readonly ILogger<SubscriptionEventProcessor> _logger;
        private readonly bool _notifyHistoricalVersions;

        public SubscriptionEventProcessor(SubscriptionMatcher matcher, SubscriptionAuthorizer authorizer, ILogger<SubscriptionEventProcessor> logger, IOptions<SubscriptionsConfiguration> configuration = null)
        {
            _matcher = EnsureArg.IsNotNull(matcher, nameof(matcher));
            _authorizer = EnsureArg.IsNotNull(authorizer, nameof(authorizer));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
            _notifyHistoricalVersions = configuration?.Value?.NotifyHistoricalVersions ?? false;
        }

        /// <summary>
        /// Returns true when some subscription's topic has a trigger for <paramref name="resourceType"/>.
        /// </summary>
        /// <param name="subscriptions">Matchable subscriptions.</param>
        /// <param name="resourceType">The resource type.</param>
        /// <returns>Whether changes of this type must be evaluated.</returns>
        public static bool IsCovered(IReadOnlyList<RegisteredSubscription> subscriptions, string resourceType)
        {
            EnsureArg.IsNotNull(subscriptions, nameof(subscriptions));

            return !string.Equals(resourceType, KnownResourceTypes.Subscription, StringComparison.Ordinal) &&
                subscriptions.Any(s => s.Topic.ResourceTriggers.Any(t => SubscriptionMatcher.AppliesTo(t.Resource, resourceType)));
        }

        /// <summary>
        /// Matches changes to subscriptions.
        /// </summary>
        /// <param name="changes">Changes in transaction order, with previous versions loaded where needed.</param>
        /// <param name="subscriptions">Matchable subscriptions.</param>
        /// <returns>Outbox events in change order.</returns>
        public IReadOnlyList<SubscriptionEventCandidate> Process(IReadOnlyList<ResourceChange> changes, IReadOnlyList<RegisteredSubscription> subscriptions)
        {
            EnsureArg.IsNotNull(changes, nameof(changes));
            EnsureArg.IsNotNull(subscriptions, nameof(subscriptions));

            var events = new List<SubscriptionEventCandidate>();
            foreach (ResourceChange change in changes)
            {
                // Writes to Subscription resources, including the server's own status updates, never trigger notifications.
                if (string.Equals(change.ResourceType, KnownResourceTypes.Subscription, StringComparison.Ordinal))
                {
                    continue;
                }

                if (change.IsWrittenAsHistory && !_notifyHistoricalVersions)
                {
                    continue;
                }

                foreach (RegisteredSubscription subscription in subscriptions)
                {
                    if (!subscription.IsMatchable || change.TransactionId <= subscription.State.ActivationTransactionId.Value)
                    {
                        continue;
                    }

                    SubscriptionEventStatus? status = Evaluate(subscription, change);
                    if (status.HasValue)
                    {
                        events.Add(new SubscriptionEventCandidate(subscription.Id, change, status.Value));
                    }
                }
            }

            return events;
        }

        private SubscriptionEventStatus? Evaluate(RegisteredSubscription subscription, ResourceChange change)
        {
            MatchOutcome outcome;
            try
            {
                outcome = _matcher.Match(subscription.Topic, subscription.Info.Filters, change);
            }
            catch (Exception ex) when (ex is SubscriptionCriteriaException or InvalidOperationException or FormatException)
            {
                _logger.LogWarning(ex, "Subscription {SubscriptionId} could not evaluate {ResourceType}/{ResourceId}/_history/{Version}.", subscription.Id, change.ResourceType, change.ResourceId, change.Version);
                outcome = MatchOutcome.Unevaluable;
            }

            switch (outcome)
            {
                case MatchOutcome.Match:
                    ResourceSnapshot target = change.Interaction == SubscriptionInteraction.Delete ? change.Previous ?? change.Current : change.Current;
                    if (target == null)
                    {
                        return UnevaluableIfTypeAuthorized(subscription, change);
                    }

                    // Authorize before the event is numbered, so events the subscriber may not see leave no gap (R5 3.9.8).
                    return _authorizer.IsAuthorized(subscription.Grant, target) ? SubscriptionEventStatus.Pending : null;

                case MatchOutcome.Unevaluable:
                    return UnevaluableIfTypeAuthorized(subscription, change);

                default:
                    return null;
            }
        }

        private SubscriptionEventStatus? UnevaluableIfTypeAuthorized(RegisteredSubscription subscription, ResourceChange change)
        {
            SubscriptionGrant grant = subscription.Grant;

            // Without the content, compartment and fine-grained scope restrictions cannot be checked, so restricted
            // subscribers get no indication at all rather than a gap that could reveal the change.
            bool restricted = !string.IsNullOrEmpty(grant.CompartmentId) || grant.Scopes.Any(s => s.SearchParameters.Count > 0);
            if (restricted || !_authorizer.CanRead(grant, change.ResourceType))
            {
                _logger.LogWarning("Subscription {SubscriptionId}: change {ResourceType}/{ResourceId}/_history/{Version} could not be evaluated and is not recorded for a restricted subscriber.", subscription.Id, change.ResourceType, change.ResourceId, change.Version);
                return null;
            }

            _logger.LogWarning("Subscription {SubscriptionId}: change {ResourceType}/{ResourceId}/_history/{Version} could not be evaluated and is recorded as unevaluable.", subscription.Id, change.ResourceType, change.ResourceId, change.Version);
            return SubscriptionEventStatus.Unevaluable;
        }
    }
}
