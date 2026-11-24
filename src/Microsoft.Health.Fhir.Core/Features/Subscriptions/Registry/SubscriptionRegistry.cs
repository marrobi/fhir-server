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
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Authorization;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry
{
    /// <summary>
    /// Caches every stored subscription and topic, revalidated on each load. Resources written through <c>$import</c> or bulk
    /// operations bypass write-time validation, so validity is always decided here, and a subscription is only matched when
    /// it also has a grant bound to its current content.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "Registered as a singleton for the lifetime of the process.")]
    public sealed class SubscriptionRegistry
    {
        private readonly ISubscriptionResourceLoader _loader;
        private readonly ISubscriptionModelConverter _converter;
        private readonly ISubscriptionStore _store;
        private readonly SubscriptionValidator _validator;
        private readonly ILogger<SubscriptionRegistry> _logger;
        private readonly SemaphoreSlim _refreshLock = new(1, 1);
        private volatile SubscriptionRegistrySnapshot _current = SubscriptionRegistrySnapshot.Empty;
        private volatile bool _stale = true;

        public SubscriptionRegistry(
            ISubscriptionResourceLoader loader,
            ISubscriptionModelConverter converter,
            ISubscriptionStore store,
            SubscriptionValidator validator,
            ILogger<SubscriptionRegistry> logger)
        {
            _loader = EnsureArg.IsNotNull(loader, nameof(loader));
            _converter = EnsureArg.IsNotNull(converter, nameof(converter));
            _store = EnsureArg.IsNotNull(store, nameof(store));
            _validator = EnsureArg.IsNotNull(validator, nameof(validator));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        public SubscriptionRegistrySnapshot Current => _current;

        /// <summary>
        /// Marks the snapshot stale, so the next <see cref="GetAsync"/> reloads it.
        /// </summary>
        public void Invalidate() => _stale = true;

        /// <summary>
        /// Returns the current snapshot, reloading it when stale or older than <paramref name="maxAge"/>.
        /// </summary>
        /// <param name="maxAge">The maximum snapshot age.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The snapshot.</returns>
        public async Task<SubscriptionRegistrySnapshot> GetAsync(TimeSpan maxAge, CancellationToken cancellationToken)
        {
            SubscriptionRegistrySnapshot snapshot = _current;
            if (!_stale && Clock.UtcNow - snapshot.LoadedUtc < maxAge)
            {
                return snapshot;
            }

            return await RefreshAsync(cancellationToken);
        }

        /// <summary>
        /// Returns a snapshot that includes every subscription in <paramref name="states"/> that is activated, reloading if any is
        /// missing or out of date. The event pump calls this before matching, so a newly activated subscription is never skipped.
        /// </summary>
        /// <param name="states">The current subscription states.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The snapshot.</returns>
        public async Task<SubscriptionRegistrySnapshot> EnsureIncludesAsync(IReadOnlyList<SubscriptionState> states, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(states, nameof(states));

            SubscriptionRegistrySnapshot snapshot = _current;
            bool missing = _stale || states.Any(s =>
                s.ActivationTransactionId != null &&
                (!snapshot.ById.TryGetValue(s.SubscriptionId, out RegisteredSubscription registered) ||
                 registered.State?.ActivationTransactionId != s.ActivationTransactionId ||
                 !string.Equals(registered.State?.ContentHash, s.ContentHash, StringComparison.Ordinal)));

            return missing ? await RefreshAsync(cancellationToken) : snapshot;
        }

        /// <summary>
        /// Reloads all subscriptions, topics and states.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The new snapshot.</returns>
        public async Task<SubscriptionRegistrySnapshot> RefreshAsync(CancellationToken cancellationToken)
        {
            await _refreshLock.WaitAsync(cancellationToken);
            try
            {
                _stale = false;
                IReadOnlyList<ResourceElement> topics = await _loader.GetTopicsAsync(cancellationToken);
                IReadOnlyList<ResourceElement> subscriptions = await _loader.GetSubscriptionsAsync(cancellationToken);
                IReadOnlyList<SubscriptionState> states = await _store.GetStatesAsync(cancellationToken);

                _current = Build(subscriptions, topics, states);
                return _current;
            }
            catch
            {
                _stale = true;
                throw;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        /// <summary>
        /// Builds a snapshot from stored resources and states.
        /// </summary>
        /// <param name="subscriptions">The subscription resources.</param>
        /// <param name="topics">The topic resources.</param>
        /// <param name="states">The subscription states.</param>
        /// <returns>The snapshot.</returns>
        public SubscriptionRegistrySnapshot Build(IReadOnlyList<ResourceElement> subscriptions, IReadOnlyList<ResourceElement> topics, IReadOnlyList<SubscriptionState> states)
        {
            EnsureArg.IsNotNull(subscriptions, nameof(subscriptions));
            EnsureArg.IsNotNull(topics, nameof(topics));
            EnsureArg.IsNotNull(states, nameof(states));

            var topicMap = new Dictionary<string, SubscriptionTopicInfo>(StringComparer.Ordinal);
            foreach (ResourceElement resource in topics)
            {
                if (!_converter.IsTopic(resource))
                {
                    continue;
                }

                try
                {
                    SubscriptionTopicInfo topic = _converter.ToTopic(resource);
                    if (string.IsNullOrEmpty(topic.Canonical))
                    {
                        continue;
                    }

                    if (topicMap.ContainsKey(topic.Canonical))
                    {
                        _logger.LogWarning("More than one subscription topic has canonical {Canonical}; resource {ResourceId} takes precedence.", topic.Canonical, topic.ResourceId);
                    }

                    topicMap[topic.Canonical] = topic;
                    if (!string.IsNullOrEmpty(topic.Version))
                    {
                        topicMap[topic.VersionedCanonical] = topic;
                    }
                }
                catch (SubscriptionValidationException ex)
                {
                    _logger.LogWarning("Stored subscription topic {ResourceId} is not valid: {Errors}", resource.Id, ex.Message);
                }
            }

            Dictionary<string, SubscriptionState> stateMap = states.ToDictionary(s => s.SubscriptionId, StringComparer.Ordinal);
            var registered = new List<RegisteredSubscription>(subscriptions.Count);
            foreach (ResourceElement resource in subscriptions)
            {
                registered.Add(Register(resource, topicMap, stateMap.GetValueOrDefault(resource.Id)));
            }

            return new SubscriptionRegistrySnapshot(registered, topicMap, Clock.UtcNow);
        }

        /// <summary>
        /// Validates one subscription against topics and its state.
        /// </summary>
        /// <param name="resource">The subscription resource.</param>
        /// <param name="topics">Topics keyed by canonical.</param>
        /// <param name="state">The stored state, or null.</param>
        /// <returns>The registered subscription.</returns>
        public RegisteredSubscription Register(ResourceElement resource, IReadOnlyDictionary<string, SubscriptionTopicInfo> topics, SubscriptionState state)
        {
            EnsureArg.IsNotNull(resource, nameof(resource));
            EnsureArg.IsNotNull(topics, nameof(topics));

            SubscriptionInfo info;
            try
            {
                info = _converter.ToSubscription(resource);
            }
            catch (SubscriptionValidationException ex)
            {
                return new RegisteredSubscription { Id = resource.Id, State = state, Errors = ex.Errors, RawStatus = resource.Scalar<string>("Resource.status") };
            }

            SubscriptionTopicInfo topic = ResolveTopic(topics, info.TopicCanonical);
            IReadOnlyList<string> errors = [.. _validator.ValidateSubscription(info, topic), .. _validator.ValidateLifetime(info)];

            SubscriptionGrant grant = null;
            if (state?.Grant != null && string.Equals(state.ContentHash, info.ComputeContentHash(), StringComparison.Ordinal))
            {
                try
                {
                    grant = SubscriptionGrant.Deserialize(state.Grant);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    _logger.LogWarning(ex, "The grant of subscription {SubscriptionId} could not be read.", info.Id);
                }
            }

            return new RegisteredSubscription { Id = info.Id, Info = info, Topic = topic, State = state, Grant = grant, Errors = errors, LastUpdated = resource.LastUpdated };
        }

        /// <summary>
        /// Finds a topic by canonical, with or without a <c>|version</c> suffix.
        /// </summary>
        /// <param name="topics">Topics keyed by canonical.</param>
        /// <param name="canonical">The canonical.</param>
        /// <returns>The topic, or null.</returns>
        public static SubscriptionTopicInfo ResolveTopic(IReadOnlyDictionary<string, SubscriptionTopicInfo> topics, string canonical)
        {
            if (topics == null || string.IsNullOrEmpty(canonical))
            {
                return null;
            }

            return topics.TryGetValue(canonical, out SubscriptionTopicInfo topic) ? topic : null;
        }
    }
}
