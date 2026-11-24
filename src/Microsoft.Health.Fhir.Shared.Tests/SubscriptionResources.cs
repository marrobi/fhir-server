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
        public static string Topic(string url, string id = "t1")
        {
#if R4 && !R4B
            const string P = SubscriptionsBackport.TopicExtensionPrefix;
            var basic = new JObject
            {
                ["resourceType"] = "Basic",
                ["id"] = id,
                ["code"] = new JObject { ["coding"] = new JArray(new JObject { ["system"] = SubscriptionsBackport.FhirTypesSystem, ["code"] = "SubscriptionTopic" }) },
                ["extension"] = new JArray(
                    Ext(P + "url", "valueUri", url),
                    Ext(P + "status", "valueCode", "active"),
                    new JObject
                    {
                        ["url"] = P + "resourceTrigger",
                        ["extension"] = new JArray(
                            Ext("resource", "valueUri", "Encounter"),
                            Ext("supportedInteraction", "valueCode", "create"),
                            Ext("supportedInteraction", "valueCode", "update"),
                            new JObject
                            {
                                ["url"] = "queryCriteria",
                                ["extension"] = new JArray(
                                    Ext("previous", "valueString", "status:not=cancelled"),
                                    Ext("resultForCreate", "valueCode", "test-passes"),
                                    Ext("current", "valueString", "status=cancelled"),
                                    new JObject { ["url"] = "requireBoth", ["valueBoolean"] = true }),
                            }),
                    },
                    new JObject
                    {
                        ["url"] = P + "canFilterBy",
                        ["extension"] = new JArray(Ext("resource", "valueUri", "Encounter"), Ext("filterParameter", "valueString", "subject")),
                    }),
            };
            return basic.ToString();
#else
            var topic = new JObject
            {
                ["resourceType"] = "SubscriptionTopic",
                ["id"] = id,
                ["url"] = url,
                ["status"] = "active",
                ["resourceTrigger"] = new JArray(new JObject
                {
                    ["resource"] = "Encounter",
                    ["supportedInteraction"] = new JArray("create", "update"),
                    ["queryCriteria"] = new JObject
                    {
                        ["previous"] = "status:not=cancelled",
                        ["resultForCreate"] = "test-passes",
                        ["current"] = "status=cancelled",
                        ["requireBoth"] = true,
                    },
                }),
                ["canFilterBy"] = new JArray(new JObject { ["resource"] = "Encounter", ["filterParameter"] = "subject" }),
            };
            return topic.ToString();
#endif
        }

        public static string Subscription(string topicUrl, string endpoint, string content = "id-only", string filter = null, string status = "requested", string id = null, int heartbeat = 120, DateTimeOffset? end = null)
        {
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
                ["contentType"] = "application/fhir+json",
                ["maxCount"] = 10,
            };

            if (content != null)
            {
                subscription["content"] = content;
            }

            if (filter != null)
            {
                IReadOnlyList<KeyValuePair<string, string>> parameters = SubscriptionQueryParser.Parse(filter, out string type);
                subscription["filterBy"] = new JArray();
                foreach (KeyValuePair<string, string> parameter in parameters)
                {
                    ((JArray)subscription["filterBy"]).Add(new JObject { ["resourceType"] = type, ["filterParameter"] = parameter.Key, ["value"] = parameter.Value });
                }
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
                ["payload"] = "application/fhir+json",
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

            if (filter != null)
            {
                subscription["_criteria"] = new JObject { ["extension"] = new JArray(Ext(SubscriptionsBackport.FilterCriteria, "valueString", filter)) };
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
    }
}
