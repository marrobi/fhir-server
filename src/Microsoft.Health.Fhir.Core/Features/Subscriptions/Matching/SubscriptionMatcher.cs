// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using EnsureThat;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching
{
    /// <summary>
    /// Decides whether a resource change matches a subscription's topic and filters (R5 3.9.4 and 3.9.5.1).
    /// </summary>
    public sealed class SubscriptionMatcher
    {
        private static readonly HashSet<string> NonDomainResources = new(StringComparer.Ordinal)
        {
            KnownResourceTypes.Bundle, KnownResourceTypes.Binary, KnownResourceTypes.Parameters,
        };

        private readonly InMemorySearchEvaluator _searchEvaluator;
        private readonly FhirPathCriteriaEvaluator _fhirPathEvaluator;

        public SubscriptionMatcher(InMemorySearchEvaluator searchEvaluator, FhirPathCriteriaEvaluator fhirPathEvaluator)
        {
            _searchEvaluator = EnsureArg.IsNotNull(searchEvaluator, nameof(searchEvaluator));
            _fhirPathEvaluator = EnsureArg.IsNotNull(fhirPathEvaluator, nameof(fhirPathEvaluator));
        }

        /// <summary>
        /// Returns true when a trigger declared for <paramref name="triggerResource"/> covers <paramref name="resourceType"/>.
        /// </summary>
        /// <param name="triggerResource">The trigger's resource, which may be a base type.</param>
        /// <param name="resourceType">A concrete resource type.</param>
        /// <returns>Whether the trigger applies.</returns>
        public static bool AppliesTo(string triggerResource, string resourceType)
        {
            if (string.IsNullOrEmpty(triggerResource))
            {
                return false;
            }

            string type = GetTypeName(triggerResource);
            return string.Equals(type, resourceType, StringComparison.Ordinal)
                || string.Equals(type, KnownResourceTypes.Resource, StringComparison.Ordinal)
                || (string.Equals(type, KnownResourceTypes.DomainResource, StringComparison.Ordinal) && !NonDomainResources.Contains(resourceType));
        }

        /// <summary>
        /// Gets a value indicating whether the type is the abstract <c>Resource</c> or <c>DomainResource</c> base type.
        /// </summary>
        /// <param name="type">The type name.</param>
        /// <returns>Whether it is a base type.</returns>
        public static bool IsBaseType(string type) =>
            string.Equals(type, KnownResourceTypes.Resource, StringComparison.Ordinal) || string.Equals(type, KnownResourceTypes.DomainResource, StringComparison.Ordinal);

        /// <summary>
        /// Gets the concrete or base resource type name of a trigger, without any canonical prefix. R5 allows a full canonical
        /// (http://hl7.org/fhir/StructureDefinition/Encounter) as well as a type name.
        /// </summary>
        /// <param name="triggerResource">The trigger resource.</param>
        /// <returns>The type name.</returns>
        public static string GetTypeName(string triggerResource)
        {
            int slash = triggerResource?.LastIndexOf("/", StringComparison.Ordinal) ?? -1;
            return slash >= 0 ? triggerResource.Substring(slash + 1) : triggerResource;
        }

        /// <summary>
        /// Matches a change against a topic and the subscription's filters.
        /// </summary>
        /// <param name="topic">The subscription topic.</param>
        /// <param name="filters">The subscription filters.</param>
        /// <param name="change">The resource change.</param>
        /// <returns>The outcome.</returns>
        public MatchOutcome Match(SubscriptionTopicInfo topic, IReadOnlyList<SubscriptionFilter> filters, ResourceChange change)
        {
            EnsureArg.IsNotNull(topic, nameof(topic));
            EnsureArg.IsNotNull(filters, nameof(filters));
            EnsureArg.IsNotNull(change, nameof(change));

            MatchOutcome topicOutcome = MatchTopic(topic, change);
            if (topicOutcome != MatchOutcome.Match)
            {
                return topicOutcome;
            }

            return MatchFilters(filters, change);
        }

        /// <summary>
        /// Matches a change against the topic's resource triggers only.
        /// </summary>
        /// <param name="topic">The subscription topic.</param>
        /// <param name="change">The resource change.</param>
        /// <returns>The outcome.</returns>
        public MatchOutcome MatchTopic(SubscriptionTopicInfo topic, ResourceChange change)
        {
            EnsureArg.IsNotNull(topic, nameof(topic));
            EnsureArg.IsNotNull(change, nameof(change));

            bool unevaluable = false;
            foreach (TopicResourceTrigger trigger in topic.ResourceTriggers)
            {
                if (!AppliesTo(trigger.Resource, change.ResourceType))
                {
                    continue;
                }

                if (trigger.SupportedInteractions.Count > 0 && !trigger.SupportedInteractions.Contains(change.Interaction))
                {
                    continue;
                }

                MatchOutcome outcome = EvaluateTrigger(trigger, change);
                if (outcome == MatchOutcome.Match)
                {
                    return MatchOutcome.Match;
                }

                unevaluable |= outcome == MatchOutcome.Unevaluable;
            }

            return unevaluable ? MatchOutcome.Unevaluable : MatchOutcome.NoMatch;
        }

        /// <summary>
        /// Matches a change against subscription filters. All filters that apply to the resource type must match.
        /// Delete events are filtered on the version that was deleted.
        /// </summary>
        /// <param name="filters">The subscription filters.</param>
        /// <param name="change">The resource change.</param>
        /// <returns>The outcome.</returns>
        public MatchOutcome MatchFilters(IReadOnlyList<SubscriptionFilter> filters, ResourceChange change)
        {
            EnsureArg.IsNotNull(filters, nameof(filters));
            EnsureArg.IsNotNull(change, nameof(change));

            var applicable = new List<KeyValuePair<string, string>>();
            foreach (SubscriptionFilter filter in filters)
            {
                if (filter.ResourceType != null && !string.Equals(filter.ResourceType, change.ResourceType, StringComparison.Ordinal))
                {
                    continue;
                }

                applicable.Add(new KeyValuePair<string, string>(filter.QueryKey, filter.QueryValue));
            }

            if (applicable.Count == 0)
            {
                return MatchOutcome.Match;
            }

            ResourceSnapshot target = change.Target;
            if (target == null)
            {
                return MatchOutcome.Unevaluable;
            }

            var expressions = new List<Expression>(applicable.Count);
            foreach (KeyValuePair<string, string> parameter in applicable)
            {
                try
                {
                    expressions.AddRange(_searchEvaluator.Parse(change.ResourceType, [parameter]));
                }
                catch (SubscriptionCriteriaException)
                {
                    // A filter whose parameter is not defined for this resource type does not apply to it (R5 3.9.5.1).
                }
            }

            return _searchEvaluator.Matches(target, expressions) ? MatchOutcome.Match : MatchOutcome.NoMatch;
        }

        private MatchOutcome EvaluateTrigger(TopicResourceTrigger trigger, ResourceChange change)
        {
            bool isCreate = change.Interaction == SubscriptionInteraction.Create;
            bool isDelete = change.Interaction == SubscriptionInteraction.Delete;
            ResourceSnapshot previous = isCreate ? null : change.Previous;
            ResourceSnapshot current = isDelete ? null : change.Current;

            TopicQueryCriteria criteria = trigger.QueryCriteria;
            if (criteria != null && !criteria.IsEmpty)
            {
                bool? previousResult = null;
                bool? currentResult = null;

                if (!string.IsNullOrWhiteSpace(criteria.Previous))
                {
                    if (isCreate)
                    {
                        previousResult = criteria.ResultForCreate != CriteriaNotExistsBehavior.TestFails;
                    }
                    else if (previous == null)
                    {
                        return MatchOutcome.Unevaluable;
                    }
                    else
                    {
                        previousResult = EvaluateQuery(criteria.Previous, previous);
                    }
                }

                if (!string.IsNullOrWhiteSpace(criteria.Current))
                {
                    if (isDelete)
                    {
                        currentResult = criteria.ResultForDelete != CriteriaNotExistsBehavior.TestFails;
                    }
                    else if (current == null)
                    {
                        return MatchOutcome.Unevaluable;
                    }
                    else
                    {
                        currentResult = EvaluateQuery(criteria.Current, current);
                    }
                }

                bool queryPasses = (previousResult, currentResult) switch
                {
                    (bool p, bool c) => criteria.RequireBoth ? p && c : p || c,
                    (bool p, null) => p,
                    (null, bool c) => c,
                    _ => true,
                };

                if (!queryPasses)
                {
                    return MatchOutcome.NoMatch;
                }
            }

            if (!string.IsNullOrWhiteSpace(trigger.FhirPathCriteria))
            {
                if ((!isDelete && current == null) || (isDelete && previous == null) || (!isCreate && previous == null && FhirPathCriteriaEvaluator.UsesPrevious(trigger.FhirPathCriteria)))
                {
                    return MatchOutcome.Unevaluable;
                }

                if (!_fhirPathEvaluator.Evaluate(trigger.FhirPathCriteria, previous, current))
                {
                    return MatchOutcome.NoMatch;
                }
            }

            return MatchOutcome.Match;
        }

        private bool EvaluateQuery(string query, ResourceSnapshot snapshot)
        {
            IReadOnlyList<KeyValuePair<string, string>> parameters = SubscriptionQueryParser.Parse(query, out _);
            return _searchEvaluator.Matches(snapshot, _searchEvaluator.Parse(snapshot.ResourceType, parameters));
        }
    }
}
