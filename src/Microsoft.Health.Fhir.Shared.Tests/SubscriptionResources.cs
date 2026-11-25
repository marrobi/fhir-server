// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Newtonsoft.Json.Linq;

namespace Microsoft.Health.Fhir.Tests.Common
{
    /// <summary>
    /// Builds topic and subscription resources in the wire shape of the FHIR version under test
    /// (R4: Basic topic + Backport subscription; R4B: SubscriptionTopic + Backport subscription; R5: native).
    /// </summary>
    public static class SubscriptionResources
    {
        public static string Topic(string url, string id = "t1") =>
            Topic(
                url,
                id,
                [new TopicTrigger("Encounter", ["create", "update"]) { Previous = "status:not=cancelled", ResultForCreate = "test-passes", Current = "status=cancelled", RequireBoth = true }],
                [("Encounter", "subject")]);

        public static string Topic(string url, string id, IReadOnlyList<TopicTrigger> triggers, IReadOnlyList<TopicFilter> canFilterBy, bool eventTrigger = false)
        {
#if R4 && !R4B
            // HAPI FHIR's layout: metadata on the 5.0 prefix, status as a modifier extension, backbone elements on the 4.3 prefix.
            const string P = SubscriptionsBackport.TopicExtensionPrefix;
            const string B = SubscriptionsBackport.TopicBackboneExtensionPrefix;
            var extensions = new JArray(Ext(P + "url", "valueUri", url));
            foreach (TopicTrigger trigger in triggers)
            {
                var parts = new JArray(Ext("resource", "valueUri", trigger.Resource));
                foreach (string interaction in trigger.Interactions)
                {
                    parts.Add(Ext("supportedInteraction", "valueCode", interaction));
                }

                if (trigger.HasQueryCriteria)
                {
                    var criteria = new JArray();
                    AddIfSet(criteria, "previous", "valueString", trigger.Previous);
                    AddIfSet(criteria, "resultForCreate", "valueCode", trigger.ResultForCreate);
                    AddIfSet(criteria, "current", "valueString", trigger.Current);
                    AddIfSet(criteria, "resultForDelete", "valueCode", trigger.ResultForDelete);
                    if (trigger.RequireBoth.HasValue)
                    {
                        criteria.Add(new JObject { ["url"] = "requireBoth", ["valueBoolean"] = trigger.RequireBoth.Value });
                    }

                    parts.Add(new JObject { ["url"] = "queryCriteria", ["extension"] = criteria });
                }

                AddIfSet(parts, "fhirPathCriteria", "valueString", trigger.FhirPathCriteria);
                extensions.Add(new JObject { ["url"] = B + "resourceTrigger", ["extension"] = parts });
            }

            foreach (TopicFilter filter in canFilterBy)
            {
                var parts = new JArray(Ext("resource", "valueUri", filter.Resource), Ext("filterParameter", "valueString", filter.Parameter));
                foreach (string modifier in filter.Modifiers)
                {
                    parts.Add(Ext("modifier", "valueCode", modifier));
                }

                foreach (string comparator in filter.Comparators)
                {
                    parts.Add(Ext("comparator", "valueCode", comparator));
                }

                extensions.Add(new JObject { ["url"] = B + "canFilterBy", ["extension"] = parts });
            }

            if (eventTrigger)
            {
                extensions.Add(new JObject { ["url"] = B + "eventTrigger", ["extension"] = new JArray(new JObject { ["url"] = "event", ["valueCodeableConcept"] = new JObject { ["text"] = "x" } }) });
            }

            var basic = new JObject
            {
                ["resourceType"] = "Basic",
                ["id"] = id,
                ["modifierExtension"] = new JArray(Ext(P + "status", "valueCode", "active")),
                ["code"] = new JObject { ["coding"] = new JArray(new JObject { ["system"] = SubscriptionsBackport.FhirTypesSystem, ["code"] = "SubscriptionTopic" }) },
                ["extension"] = extensions,
            };
            return basic.ToString();
#else
            var resourceTriggers = new JArray();
            foreach (TopicTrigger trigger in triggers)
            {
                var resourceTrigger = new JObject { ["resource"] = trigger.Resource, ["supportedInteraction"] = new JArray(trigger.Interactions) };
                if (trigger.HasQueryCriteria)
                {
                    var criteria = new JObject();
                    SetIfSet(criteria, "previous", trigger.Previous);
                    SetIfSet(criteria, "resultForCreate", trigger.ResultForCreate);
                    SetIfSet(criteria, "current", trigger.Current);
                    SetIfSet(criteria, "resultForDelete", trigger.ResultForDelete);
                    if (trigger.RequireBoth.HasValue)
                    {
                        criteria["requireBoth"] = trigger.RequireBoth.Value;
                    }

                    resourceTrigger["queryCriteria"] = criteria;
                }

                SetIfSet(resourceTrigger, "fhirPathCriteria", trigger.FhirPathCriteria);
                resourceTriggers.Add(resourceTrigger);
            }

            var filters = new JArray();
            foreach (TopicFilter filter in canFilterBy)
            {
                var canFilter = new JObject { ["resource"] = filter.Resource, ["filterParameter"] = filter.Parameter };
                if (filter.Modifiers.Length > 0)
                {
                    canFilter["modifier"] = new JArray(filter.Modifiers);
                }

                if (filter.Comparators.Length > 0)
                {
                    canFilter["comparator"] = new JArray(filter.Comparators);
                }

                filters.Add(canFilter);
            }

            var topic = new JObject
            {
                ["resourceType"] = "SubscriptionTopic",
                ["id"] = id,
                ["url"] = url,
                ["status"] = "active",
                ["resourceTrigger"] = resourceTriggers,
                ["canFilterBy"] = filters,
            };

            if (eventTrigger)
            {
                topic["eventTrigger"] = new JArray(new JObject { ["event"] = new JObject { ["text"] = "x" }, ["resource"] = "Encounter" });
            }

            return topic.ToString();
#endif
        }

        public static string Subscription(string topicUrl, string endpoint, string content = "id-only", string filter = null, string status = "requested", string id = null, int heartbeat = 120, DateTimeOffset? end = null, string contentType = "application/fhir+json", IReadOnlyList<string> filters = null)
        {
            var allFilters = new List<string>(filters ?? []);
            if (filter != null)
            {
                allFilters.Insert(0, filter);
            }

#if R5
            var subscription = new JObject
            {
                ["resourceType"] = "Subscription",
                ["status"] = status,
                ["topic"] = topicUrl,
                ["channelType"] = new JObject { ["system"] = SubscriptionsBackport.ChannelTypeSystem, ["code"] = "rest-hook" },
                ["endpoint"] = endpoint,
                ["parameter"] = new JArray(new JObject { ["name"] = "Authorization", ["value"] = "Bearer secret" }),
                ["heartbeatPeriod"] = heartbeat,
                ["timeout"] = 30,
                ["contentType"] = contentType,
                ["maxCount"] = 10,
            };

            if (content != null)
            {
                subscription["content"] = content;
            }

            if (allFilters.Count > 0)
            {
                var filterBy = new JArray();
                foreach (string criteria in allFilters)
                {
                    IReadOnlyList<KeyValuePair<string, string>> parameters = SubscriptionQueryParser.Parse(criteria, out string type);
                    foreach (KeyValuePair<string, string> parameter in parameters)
                    {
                        string[] key = parameter.Key.Split(':', 2);
                        var entry = new JObject { ["resourceType"] = type, ["filterParameter"] = key[0], ["value"] = parameter.Value };
                        if (key.Length > 1)
                        {
                            entry["modifier"] = key[1];
                        }

                        filterBy.Add(entry);
                    }
                }

                subscription["filterBy"] = filterBy;
            }
#else
            var channel = new JObject
            {
                ["extension"] = new JArray(
                    new JObject { ["url"] = SubscriptionsBackport.HeartbeatPeriod, ["valueUnsignedInt"] = heartbeat },
                    new JObject { ["url"] = SubscriptionsBackport.Timeout, ["valueUnsignedInt"] = 30 },
                    new JObject { ["url"] = SubscriptionsBackport.MaxCount, ["valuePositiveInt"] = 10 }),
                ["type"] = "rest-hook",
                ["endpoint"] = endpoint,
                ["payload"] = contentType,
                ["header"] = new JArray("Authorization: Bearer secret"),
            };

            if (content != null)
            {
                channel["_payload"] = new JObject { ["extension"] = new JArray(Ext(SubscriptionsBackport.PayloadContent, "valueCode", content)) };
            }

            var subscription = new JObject
            {
                ["resourceType"] = "Subscription",
                ["meta"] = new JObject { ["profile"] = new JArray(SubscriptionsBackport.SubscriptionProfile) },
                ["status"] = status,
                ["reason"] = "Test subscription",
                ["criteria"] = topicUrl,
                ["channel"] = channel,
            };

            if (allFilters.Count > 0)
            {
                var criteria = new JArray();
                foreach (string value in allFilters)
                {
                    criteria.Add(Ext(SubscriptionsBackport.FilterCriteria, "valueString", value));
                }

                subscription["_criteria"] = new JObject { ["extension"] = criteria };
            }
#endif
            if (id != null)
            {
                subscription["id"] = id;
            }

            // API writes always store an end within the server's maximum lifetime.
            subscription["end"] = (end ?? DateTimeOffset.UtcNow.AddDays(7)).ToString("o", System.Globalization.CultureInfo.InvariantCulture);

            return subscription.ToString();
        }

        private static JObject Ext(string url, string valueName, string value) => new() { ["url"] = url, [valueName] = value };

        private static void AddIfSet(JArray extensions, string url, string valueName, string value)
        {
            if (value != null)
            {
                extensions.Add(Ext(url, valueName, value));
            }
        }

        private static void SetIfSet(JObject target, string name, string value)
        {
            if (value != null)
            {
                target[name] = value;
            }
        }
    }
}
