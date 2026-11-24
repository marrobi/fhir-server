// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http.Headers;
using EnsureThat;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Channels;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.ValueSets;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Server acceptance rules for subscription topics and subscriptions. Anything the server cannot honor is rejected,
    /// as the Backport IG and R5 require, rather than accepted and ignored.
    /// </summary>
    public sealed class SubscriptionValidator
    {
        private static readonly HashSet<string> SupportedContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "application/fhir+json", "application/json", "application/fhir+xml", "application/xml",
        };

        private static readonly HashSet<string> RestrictedHeaders = new(StringComparer.OrdinalIgnoreCase)
        {
            "Host", "Content-Type", "Content-Length", "Transfer-Encoding", "Connection", "Upgrade", "Expect", "TE", "Trailer", "Keep-Alive", "Proxy-Authorization", "Proxy-Connection",
        };

        private static readonly HashSet<string> BaseResourceTypes = new(StringComparer.Ordinal) { "Resource", "DomainResource" };

        private readonly InMemorySearchEvaluator _searchEvaluator;
        private readonly FhirPathCriteriaEvaluator _fhirPathEvaluator;
        private readonly SubscriptionEndpointPolicy _endpointPolicy;
        private readonly SubscriptionsConfiguration _configuration;
        private readonly IModelInfoProvider _modelInfoProvider;
        private readonly HashSet<string> _channelTypes;
        private readonly VersioningConfiguration _versioning;

        public SubscriptionValidator(
            InMemorySearchEvaluator searchEvaluator,
            FhirPathCriteriaEvaluator fhirPathEvaluator,
            SubscriptionEndpointPolicy endpointPolicy,
            IOptions<SubscriptionsConfiguration> configuration,
            IModelInfoProvider modelInfoProvider,
            IEnumerable<ISubscriptionChannel> channels,
            IOptions<CoreFeatureConfiguration> coreFeatures = null)
        {
            _searchEvaluator = EnsureArg.IsNotNull(searchEvaluator, nameof(searchEvaluator));
            _fhirPathEvaluator = EnsureArg.IsNotNull(fhirPathEvaluator, nameof(fhirPathEvaluator));
            _endpointPolicy = EnsureArg.IsNotNull(endpointPolicy, nameof(endpointPolicy));
            _configuration = EnsureArg.IsNotNull(configuration?.Value, nameof(configuration));
            _modelInfoProvider = EnsureArg.IsNotNull(modelInfoProvider, nameof(modelInfoProvider));
            _channelTypes = new HashSet<string>(EnsureArg.IsNotNull(channels, nameof(channels)).Select(c => c.ChannelType), StringComparer.Ordinal);
            _versioning = coreFeatures?.Value?.Versioning;
        }

        /// <summary>
        /// Validates a topic.
        /// </summary>
        /// <param name="topic">The topic.</param>
        /// <returns>The validation errors; empty when valid.</returns>
        public IReadOnlyList<string> ValidateTopic(SubscriptionTopicInfo topic)
        {
            EnsureArg.IsNotNull(topic, nameof(topic));
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(topic.Canonical) || !Uri.TryCreate(topic.Canonical, UriKind.Absolute, out _))
            {
                errors.Add(Core.Resources.SubscriptionTopicUrlRequired);
            }

            if (topic.HasEventTriggers)
            {
                errors.Add(Core.Resources.SubscriptionTopicEventTriggerNotSupported);
            }

            if (topic.ResourceTriggers.Count == 0)
            {
                errors.Add(Core.Resources.SubscriptionTopicResourceTriggerRequired);
            }

            foreach (TopicResourceTrigger trigger in topic.ResourceTriggers)
            {
                string type = SubscriptionMatcher.GetTypeName(trigger.Resource);
                if (!IsResourceType(type))
                {
                    errors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionTopicUnknownResource, trigger.Resource));
                    continue;
                }

                if (string.Equals(type, KnownResourceTypes.Subscription, StringComparison.Ordinal))
                {
                    // Topics on Subscription could be triggered by the server's own status writes.
                    errors.Add(Core.Resources.SubscriptionTopicOnSubscriptionNotSupported);
                    continue;
                }

                if (_configuration.UnversionedResourceTypes == UnversionedResourceTypePolicy.RequireHistory && !KeepsHistory(type))
                {
                    errors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionTopicRequiresHistory, trigger.Resource));
                }

                if (trigger.QueryCriteria != null)
                {
                    TryParseQuery(type, trigger.QueryCriteria.Previous, errors);
                    TryParseQuery(type, trigger.QueryCriteria.Current, errors);
                }

                if (!string.IsNullOrWhiteSpace(trigger.FhirPathCriteria))
                {
                    try
                    {
                        _fhirPathEvaluator.Validate(trigger.FhirPathCriteria);
                    }
                    catch (SubscriptionCriteriaException ex)
                    {
                        errors.Add(ex.Message);
                    }
                }
            }

            foreach (TopicCanFilterBy filter in topic.CanFilterBy)
            {
                if (string.IsNullOrWhiteSpace(filter.FilterParameter))
                {
                    errors.Add(Core.Resources.SubscriptionTopicFilterParameterRequired);
                    continue;
                }

                IEnumerable<string> types = string.IsNullOrEmpty(filter.Resource)
                    ? topic.ResourceTriggers.Select(t => SubscriptionMatcher.GetTypeName(t.Resource))
                    : [SubscriptionMatcher.GetTypeName(filter.Resource)];

                if (!types.Any(t => IsResourceType(t) && IsKnownParameter(t, filter.FilterParameter)))
                {
                    errors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionTopicFilterParameterUnknown, filter.FilterParameter));
                }
            }

            return errors;
        }

        /// <summary>
        /// Validates that a stored subscription has an <c>end</c> within the maximum lifetime. API writes set <c>end</c>, so this
        /// catches subscriptions written another way (for example, by <c>$import</c>) that remove or extend it.
        /// </summary>
        /// <param name="subscription">The subscription.</param>
        /// <returns>The validation errors; empty when valid.</returns>
        public IReadOnlyList<string> ValidateLifetime(SubscriptionInfo subscription)
        {
            EnsureArg.IsNotNull(subscription, nameof(subscription));

            // A small allowance covers clock differences between the writing and the reading instance.
            if (_configuration.MaxLifetimeDays > 0 &&
                (subscription.End == null || subscription.End.Value > Clock.UtcNow.AddDays(_configuration.MaxLifetimeDays).AddHours(1)))
            {
                return [string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionEndBeyondMaximum, _configuration.MaxLifetimeDays)];
            }

            return [];
        }

        /// <summary>
        /// Validates a subscription against its topic and the server's channel and endpoint policy.
        /// </summary>
        /// <param name="subscription">The subscription.</param>
        /// <param name="topic">The referenced topic, or null when it was not found.</param>
        /// <returns>The validation errors; empty when valid.</returns>
        public IReadOnlyList<string> ValidateSubscription(SubscriptionInfo subscription, SubscriptionTopicInfo topic)
        {
            EnsureArg.IsNotNull(subscription, nameof(subscription));
            var errors = new List<string>();

            if (topic == null)
            {
                errors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionTopicNotFound, subscription.TopicCanonical));
            }
            else if (string.Equals(topic.Status, "retired", StringComparison.Ordinal))
            {
                errors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionTopicRetired, subscription.TopicCanonical));
            }
            else if (ValidateTopic(topic).Count > 0)
            {
                errors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionTopicNotValid, subscription.TopicCanonical));
            }

            if (string.IsNullOrEmpty(subscription.ChannelType) || !_channelTypes.Contains(subscription.ChannelType))
            {
                errors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionChannelNotSupported, subscription.ChannelType));
            }
            else if (!_endpointPolicy.TryValidate(subscription.Endpoint, out string endpointError))
            {
                errors.Add(endpointError);
            }

            if (!IsSupportedContentType(subscription.ContentType))
            {
                errors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionContentTypeNotSupported, subscription.ContentType));
            }

            if (subscription.HeartbeatPeriodSeconds.HasValue && subscription.HeartbeatPeriodSeconds.Value < _configuration.MinHeartbeatPeriodSeconds)
            {
                errors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionHeartbeatTooShort, _configuration.MinHeartbeatPeriodSeconds));
            }

            if (subscription.TimeoutSeconds.HasValue && (subscription.TimeoutSeconds.Value < 1 || subscription.TimeoutSeconds.Value > _configuration.MaxTimeoutSeconds))
            {
                errors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionTimeoutOutOfRange, _configuration.MaxTimeoutSeconds));
            }

            if (subscription.MaxCount.HasValue && subscription.MaxCount.Value < 1)
            {
                errors.Add(Core.Resources.SubscriptionMaxCountInvalid);
            }

            foreach (SubscriptionHeader header in subscription.Headers)
            {
                if (string.IsNullOrWhiteSpace(header.Name) || RestrictedHeaders.Contains(header.Name) || !IsValidHeaderName(header.Name) ||
                    header.Value == null || header.Value.Contains('\r', StringComparison.Ordinal) || header.Value.Contains('\n', StringComparison.Ordinal))
                {
                    errors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionHeaderNotAllowed, header.Name));
                }
            }

            if (topic != null)
            {
                ValidateFilters(subscription, topic, errors);
            }

            return errors;
        }

        private void ValidateFilters(SubscriptionInfo subscription, SubscriptionTopicInfo topic, List<string> errors)
        {
            foreach (SubscriptionFilter filter in subscription.Filters)
            {
                if (string.IsNullOrWhiteSpace(filter.FilterParameter) || string.IsNullOrWhiteSpace(filter.Value))
                {
                    errors.Add(Core.Resources.SubscriptionFilterIncomplete);
                    continue;
                }

                List<TopicCanFilterBy> allowed = topic.CanFilterBy
                    .Where(c => string.Equals(c.FilterParameter, filter.FilterParameter, StringComparison.Ordinal) &&
                        (string.IsNullOrEmpty(c.Resource) || filter.ResourceType == null || string.Equals(SubscriptionMatcher.GetTypeName(c.Resource), filter.ResourceType, StringComparison.Ordinal)))
                    .ToList();

                if (allowed.Count == 0)
                {
                    errors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionFilterNotAllowedByTopic, filter.FilterParameter, topic.Canonical));
                    continue;
                }

                if (!string.IsNullOrEmpty(filter.Modifier) && !allowed.Any(c => c.Modifiers.Count == 0 || c.Modifiers.Contains(filter.Modifier, StringComparer.Ordinal)))
                {
                    errors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionFilterModifierNotAllowed, filter.Modifier, filter.FilterParameter));
                    continue;
                }

                if (!string.IsNullOrEmpty(filter.Comparator) && !string.Equals(filter.Comparator, "eq", StringComparison.Ordinal) &&
                    !allowed.Any(c => c.Comparators.Count == 0 || c.Comparators.Contains(filter.Comparator, StringComparer.Ordinal)))
                {
                    errors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionFilterComparatorNotAllowed, filter.Comparator, filter.FilterParameter));
                    continue;
                }

                IEnumerable<string> types = filter.ResourceType != null
                    ? [filter.ResourceType]
                    : topic.ResourceTriggers.Select(t => SubscriptionMatcher.GetTypeName(t.Resource)).Where(t => !BaseResourceTypes.Contains(t)).Distinct();

                string lastError = null;
                bool parsed = false;
                foreach (string type in types)
                {
                    try
                    {
                        _searchEvaluator.Parse(type, [new KeyValuePair<string, string>(filter.QueryKey, filter.QueryValue)]);
                        parsed = true;
                    }
                    catch (SubscriptionCriteriaException ex)
                    {
                        lastError = ex.Message;
                    }
                }

                if (!parsed)
                {
                    errors.Add(lastError ?? string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionFilterNotAllowedByTopic, filter.FilterParameter, topic.Canonical));
                }
            }
        }

        private void TryParseQuery(string resourceType, string query, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return;
            }

            try
            {
                IReadOnlyList<KeyValuePair<string, string>> parameters = SubscriptionQueryParser.Parse(query, out string prefix);
                if (prefix != null && !string.Equals(prefix, resourceType, StringComparison.Ordinal) && !BaseResourceTypes.Contains(resourceType))
                {
                    errors.Add(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionTopicCriteriaTypeMismatch, query, resourceType));
                    return;
                }

                _searchEvaluator.Parse(resourceType, parameters);
            }
            catch (Exception ex) when (ex is SubscriptionCriteriaException or FormatException)
            {
                errors.Add(ex.Message);
            }
        }

        private bool KeepsHistory(string type)
        {
            if (_versioning == null)
            {
                return true;
            }

            static bool IsUnversioned(string policy) => string.Equals(policy, ResourceVersionPolicy.NoVersion, StringComparison.OrdinalIgnoreCase);

            // A base-type trigger covers every resource type, so any unversioned type breaks the guarantee.
            if (BaseResourceTypes.Contains(type))
            {
                return !IsUnversioned(_versioning.Default) && !_versioning.ResourceTypeOverrides.Values.Any(IsUnversioned);
            }

            return !IsUnversioned(_versioning.ResourceTypeOverrides.TryGetValue(type, out string policy) ? policy : _versioning.Default);
        }

        private bool IsResourceType(string type) =>
            !string.IsNullOrEmpty(type) && (BaseResourceTypes.Contains(type) || _modelInfoProvider.IsKnownResource(type, ignoreCase: false));

        private bool IsKnownParameter(string resourceType, string code)
        {
            try
            {
                _searchEvaluator.Parse(resourceType, [new KeyValuePair<string, string>(code + ":missing", "true")]);
                return true;
            }
            catch (SubscriptionCriteriaException)
            {
                return false;
            }
        }

        private static bool IsSupportedContentType(string contentType)
        {
            return !string.IsNullOrWhiteSpace(contentType) &&
                MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue parsed) &&
                SupportedContentTypes.Contains(parsed.MediaType);
        }

        private static bool IsValidHeaderName(string name) =>
            name.All(c => c > 32 && c < 127 && "()<>@,;:\\\"/[]?={}".IndexOf(c, StringComparison.Ordinal) < 0);
    }
}
