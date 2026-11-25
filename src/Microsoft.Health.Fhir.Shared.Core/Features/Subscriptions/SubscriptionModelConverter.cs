// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using EnsureThat;
using Hl7.Fhir.Model;
using Hl7.Fhir.Utility;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Converts R4 Backport, R4B Backport and R5 subscription resources to and from the internal R5-shaped model.
    /// On R4, topics are <c>Basic</c> resources with <c>Basic.code</c> = <c>fhir-types#SubscriptionTopic</c> and the R5 topic elements
    /// carried as R5 cross-version extensions.
    /// </summary>
    public sealed class SubscriptionModelConverter : ISubscriptionModelConverter
    {
#if Stu3
        public string TopicResourceType => KnownResourceTypes.Basic;

        public IReadOnlyList<Tuple<string, string>> TopicSearchParameters => [];

        public bool IsSupported => false;

        public bool IsTopic(ResourceElement resource) => false;

        public SubscriptionTopicInfo ToTopic(ResourceElement resource) => throw new NotSupportedException();

        public SubscriptionInfo ToSubscription(ResourceElement resource) => throw new SubscriptionValidationException(Core.Resources.SubscriptionsNotSupportedForVersion);

        public void ApplyServerValues(ResourceElement resource, SubscriptionStatusCode status, DateTimeOffset? end) => throw new NotSupportedException();

        public ResourceElement WithStatus(ResourceElement resource, SubscriptionStatusCode status, string error) => throw new NotSupportedException();
#else
        private const string DefaultContentType = "application/fhir+json";

        public bool IsSupported => true;

#if R4 && !R4B
        public string TopicResourceType => KnownResourceTypes.Basic;

        public IReadOnlyList<Tuple<string, string>> TopicSearchParameters { get; } =
            [Tuple.Create("code", SubscriptionsBackport.FhirTypesSystem + "|" + KnownResourceTypes.SubscriptionTopic)];

        public bool IsTopic(ResourceElement resource)
        {
            EnsureArg.IsNotNull(resource, nameof(resource));

            if (!string.Equals(resource.InstanceType, KnownResourceTypes.Basic, StringComparison.Ordinal))
            {
                return false;
            }

            var basic = resource.ToPoco<Basic>();
            return basic.Code?.Coding?.Any(c => c.System == SubscriptionsBackport.FhirTypesSystem && c.Code == KnownResourceTypes.SubscriptionTopic) == true;
        }

        public SubscriptionTopicInfo ToTopic(ResourceElement resource)
        {
            EnsureArg.IsNotNull(resource, nameof(resource));
            if (!IsTopic(resource))
            {
                throw new SubscriptionValidationException(Core.Resources.SubscriptionTopicNotATopic);
            }

            var basic = resource.ToPoco<Basic>();
            return new SubscriptionTopicInfo
            {
                ResourceId = basic.Id,
                Canonical = TopicString(basic, "url"),
                Version = TopicString(basic, "version"),
                Title = TopicString(basic, "title"),
                Status = TopicString(basic, "status") ?? "active",
                ResourceTriggers = TopicExtensions(basic, "resourceTrigger").Select(ToTrigger).ToList(),
                CanFilterBy = TopicExtensions(basic, "canFilterBy").Select(ToCanFilterBy).ToList(),
                HasEventTriggers = TopicExtensions(basic, "eventTrigger").Any(),
            };

            // HAPI FHIR writes resourceTrigger, canFilterBy and notificationShape with the 4.3 prefix and status as a modifier
            // extension; earlier topics use the 5.0 prefix and plain extensions. Both layouts are read.
            static IEnumerable<Extension> TopicExtensions(Basic b, string element) =>
                b.Extension.Concat(b.ModifierExtension).Where(e =>
                    e.Url == SubscriptionsBackport.TopicExtensionPrefix + element ||
                    e.Url == SubscriptionsBackport.TopicBackboneExtensionPrefix + element);

            static string TopicString(Basic b, string element) => ExtensionString(TopicExtensions(b, element).FirstOrDefault());

            static TopicResourceTrigger ToTrigger(Extension extension)
            {
                Extension query = extension.GetExtension("queryCriteria");
                return new TopicResourceTrigger
                {
                    Description = ExtensionString(extension.GetExtension("description")),
                    Resource = ExtensionString(extension.GetExtension("resource")),
                    SupportedInteractions = extension.GetExtensions("supportedInteraction").Select(e => ParseInteraction(ExtensionString(e))).ToList(),
                    QueryCriteria = query == null ? null : new TopicQueryCriteria
                    {
                        Previous = ExtensionString(query.GetExtension("previous")),
                        ResultForCreate = ParseBehavior(ExtensionString(query.GetExtension("resultForCreate"))),
                        Current = ExtensionString(query.GetExtension("current")),
                        ResultForDelete = ParseBehavior(ExtensionString(query.GetExtension("resultForDelete"))),
                        RequireBoth = (query.GetExtension("requireBoth")?.Value as FhirBoolean)?.Value ?? false,
                    },
                    FhirPathCriteria = ExtensionString(extension.GetExtension("fhirPathCriteria")),
                };
            }

            static TopicCanFilterBy ToCanFilterBy(Extension extension) => new()
            {
                Description = ExtensionString(extension.GetExtension("description")),
                Resource = ExtensionString(extension.GetExtension("resource")),
                FilterParameter = ExtensionString(extension.GetExtension("filterParameter")),
                Comparators = extension.GetExtensions("comparator").Select(ExtensionString).Where(v => v != null).ToList(),
                Modifiers = extension.GetExtensions("modifier").Select(ExtensionString).Where(v => v != null).ToList(),
            };
        }
#else
        public string TopicResourceType => KnownResourceTypes.SubscriptionTopic;

        public IReadOnlyList<Tuple<string, string>> TopicSearchParameters { get; } = [];

        public bool IsTopic(ResourceElement resource)
        {
            EnsureArg.IsNotNull(resource, nameof(resource));
            return string.Equals(resource.InstanceType, KnownResourceTypes.SubscriptionTopic, StringComparison.Ordinal);
        }

        public SubscriptionTopicInfo ToTopic(ResourceElement resource)
        {
            EnsureArg.IsNotNull(resource, nameof(resource));
            if (!IsTopic(resource))
            {
                throw new SubscriptionValidationException(Core.Resources.SubscriptionTopicNotATopic);
            }

            var topic = resource.ToPoco<SubscriptionTopic>();
            return new SubscriptionTopicInfo
            {
                ResourceId = topic.Id,
                Canonical = topic.Url,
                Version = topic.Version,
                Title = topic.Title,
                Status = topic.Status?.GetLiteral() ?? "unknown",
                ResourceTriggers = topic.ResourceTrigger.Select(t => new TopicResourceTrigger
                {
                    Description = t.Description,
                    Resource = t.Resource,
                    SupportedInteractions = t.SupportedInteraction.Where(i => i.HasValue).Select(i => ParseInteraction(i.Value.GetLiteral())).ToList(),
                    QueryCriteria = t.QueryCriteria == null ? null : new TopicQueryCriteria
                    {
                        Previous = t.QueryCriteria.Previous,
                        ResultForCreate = ParseBehavior(t.QueryCriteria.ResultForCreate?.GetLiteral()),
                        Current = t.QueryCriteria.Current,
                        ResultForDelete = ParseBehavior(t.QueryCriteria.ResultForDelete?.GetLiteral()),
                        RequireBoth = t.QueryCriteria.RequireBoth ?? false,
                    },
                    FhirPathCriteria = t.FhirPathCriteria,
                }).ToList(),
                CanFilterBy = topic.CanFilterBy.Select(f => new TopicCanFilterBy
                {
                    Description = f.Description,
                    Resource = f.Resource,
                    FilterParameter = f.FilterParameter,
#if R5
                    Comparators = f.Comparator.Where(c => c.HasValue).Select(c => c.Value.GetLiteral()).ToList(),
#endif
                    Modifiers = f.Modifier.Where(m => m.HasValue).Select(m => m.Value.GetLiteral()).ToList(),
                }).ToList(),
                HasEventTriggers = topic.EventTrigger.Count > 0,
#if R5
#endif
            };
        }
#endif

#if R5
        public SubscriptionInfo ToSubscription(ResourceElement resource)
        {
            EnsureArg.IsNotNull(resource, nameof(resource));
            var subscription = resource.ToPoco<Subscription>();
            var errors = new List<string>();

            if (string.IsNullOrWhiteSpace(subscription.Topic))
            {
                errors.Add(Core.Resources.SubscriptionTopicRequired);
            }

            string channelType = subscription.ChannelType?.Code;
            if (string.IsNullOrEmpty(channelType))
            {
                errors.Add(Core.Resources.SubscriptionChannelTypeRequired);
            }

            ThrowIfErrors(errors);

            return new SubscriptionInfo
            {
                Id = subscription.Id,
                VersionId = subscription.Meta?.VersionId,
                Status = SubscriptionStatusCodeExtensions.ParseSubscriptionStatus(subscription.Status?.GetLiteral()),
                TopicCanonical = subscription.Topic,
                Filters = subscription.FilterBy.Select(f => new SubscriptionFilter
                {
                    ResourceType = SubscriptionMatcher.GetTypeName(f.ResourceType),
                    FilterParameter = f.FilterParameter,
                    Comparator = f.Comparator?.GetLiteral(),
                    Modifier = f.Modifier?.GetLiteral(),
                    Value = f.Value,
                }).ToList(),
                ChannelType = channelType,
                Endpoint = subscription.Endpoint,
                Headers = subscription.Parameter.Select(p => new SubscriptionHeader(p.Name, p.Value)).ToList(),
                HeartbeatPeriodSeconds = subscription.HeartbeatPeriod,
                TimeoutSeconds = subscription.Timeout,
                ContentType = string.IsNullOrWhiteSpace(subscription.ContentType) ? DefaultContentType : subscription.ContentType,
                Content = ParseContent(subscription.Content?.GetLiteral()),
                MaxCount = subscription.MaxCount,
                End = subscription.End,
            };
        }

        public void ApplyServerValues(ResourceElement resource, SubscriptionStatusCode status, DateTimeOffset? end)
        {
            var subscription = GetInstance(resource);
            subscription.Status = ToPocoStatus(status);
            subscription.Content ??= Subscription.SubscriptionPayloadContent.IdOnly;
            if (string.IsNullOrWhiteSpace(subscription.ContentType))
            {
                subscription.ContentType = DefaultContentType;
            }

            if (end.HasValue)
            {
                subscription.End = end;
            }
        }

        public ResourceElement WithStatus(ResourceElement resource, SubscriptionStatusCode status, string error)
        {
            EnsureArg.IsNotNull(resource, nameof(resource));

            // R5 has no Subscription.error; errors are reported through $status.
            var subscription = (Subscription)resource.ToPoco<Subscription>().DeepCopy();
            subscription.Status = ToPocoStatus(status);
            return subscription.ToResourceElement();
        }
#else
        public SubscriptionInfo ToSubscription(ResourceElement resource)
        {
            EnsureArg.IsNotNull(resource, nameof(resource));
            var subscription = resource.ToPoco<Subscription>();
            var errors = new List<string>();

            string topic = subscription.Criteria;
            if (string.IsNullOrWhiteSpace(topic) || !Uri.TryCreate(topic.Split('|')[0], UriKind.Absolute, out Uri topicUri) || topicUri.Scheme is not ("http" or "https" or "urn"))
            {
                // Classic R4 subscriptions put a search URL in criteria; only topic-based (Backport) subscriptions are supported.
                errors.Add(Core.Resources.SubscriptionClassicNotSupported);
            }

            Subscription.ChannelComponent channel = subscription.Channel;
            if (channel == null)
            {
                errors.Add(Core.Resources.SubscriptionChannelTypeRequired);
            }

            ThrowIfErrors(errors);

            var filters = new List<SubscriptionFilter>();
            foreach (Extension extension in subscription.CriteriaElement.Extension.Where(e => e.Url == SubscriptionsBackport.FilterCriteria))
            {
                string criteria = ExtensionString(extension);
                IReadOnlyList<KeyValuePair<string, string>> parameters;
                string resourceType;
                try
                {
                    parameters = SubscriptionQueryParser.Parse(criteria, out resourceType);
                }
                catch (FormatException ex)
                {
                    throw new SubscriptionValidationException(ex.Message);
                }

                foreach (KeyValuePair<string, string> parameter in parameters)
                {
                    string[] key = parameter.Key.Split(':', 2);
                    filters.Add(new SubscriptionFilter
                    {
                        ResourceType = resourceType,
                        FilterParameter = key[0],
                        Modifier = key.Length > 1 ? key[1] : null,
                        Value = parameter.Value,
                    });
                }
            }

            Coding customChannel = channel.TypeElement?.GetExtensionValue<Coding>(SubscriptionsBackport.ChannelType);
            string channelType = customChannel?.Code ?? channel.Type?.GetLiteral();

            return new SubscriptionInfo
            {
                Id = subscription.Id,
                VersionId = subscription.Meta?.VersionId,
                Status = SubscriptionStatusCodeExtensions.ParseSubscriptionStatus(subscription.Status?.GetLiteral()),
                TopicCanonical = topic,
                Filters = filters,
                ChannelType = channelType,
                Endpoint = channel.Endpoint,
                Headers = channel.Header.Select(ParseHeader).ToList(),
                HeartbeatPeriodSeconds = (int?)channel.GetExtensionValue<UnsignedInt>(SubscriptionsBackport.HeartbeatPeriod)?.Value,
                TimeoutSeconds = (int?)channel.GetExtensionValue<UnsignedInt>(SubscriptionsBackport.Timeout)?.Value,
                MaxCount = channel.GetExtensionValue<PositiveInt>(SubscriptionsBackport.MaxCount)?.Value,
                ContentType = string.IsNullOrWhiteSpace(channel.Payload) ? DefaultContentType : channel.Payload,
                Content = ParseContent(channel.PayloadElement?.GetExtensionValue<Code>(SubscriptionsBackport.PayloadContent)?.Value),
                End = subscription.End,
                Error = subscription.Error,
            };

            static SubscriptionHeader ParseHeader(string header)
            {
                int colon = header?.IndexOf(':', StringComparison.Ordinal) ?? -1;
                return colon <= 0 ? new SubscriptionHeader(header, null) : new SubscriptionHeader(header.Substring(0, colon).Trim(), header.Substring(colon + 1).Trim());
            }
        }

        public void ApplyServerValues(ResourceElement resource, SubscriptionStatusCode status, DateTimeOffset? end)
        {
            var subscription = GetInstance(resource);
            subscription.Status = ToPocoStatus(status);
            subscription.Error = null;
            if (string.IsNullOrWhiteSpace(subscription.Channel.Payload))
            {
                subscription.Channel.Payload = DefaultContentType;
            }

            if (subscription.Channel.PayloadElement.GetExtension(SubscriptionsBackport.PayloadContent) == null)
            {
                // id-only is this server's default; the Backport profile requires the content extension to be present.
                subscription.Channel.PayloadElement.SetExtension(SubscriptionsBackport.PayloadContent, new Code("id-only"));
            }

            if (end.HasValue)
            {
                subscription.End = end;
            }
        }

        public ResourceElement WithStatus(ResourceElement resource, SubscriptionStatusCode status, string error)
        {
            EnsureArg.IsNotNull(resource, nameof(resource));
            var subscription = (Subscription)resource.ToPoco<Subscription>().DeepCopy();
            subscription.Status = ToPocoStatus(status);
            subscription.Error = error;
            return subscription.ToResourceElement();
        }
#endif

#if R4 && !R4B
        // R4 has no entered-in-error status.
        private static Subscription.SubscriptionStatus ToPocoStatus(SubscriptionStatusCode status) =>
            EnumUtility.ParseLiteral<Subscription.SubscriptionStatus>(status == SubscriptionStatusCode.EnteredInError ? "off" : status.ToLiteral()).Value;
#else
        private static SubscriptionStatusCodes ToPocoStatus(SubscriptionStatusCode status) => EnumUtility.ParseLiteral<SubscriptionStatusCodes>(status.ToLiteral()).Value;
#endif

        // The server values must be written to the instance the caller saves; a copy made from the typed element would be dropped.
        private static Subscription GetInstance(ResourceElement resource)
        {
            EnsureArg.IsNotNull(resource, nameof(resource));
            return resource.ResourceInstance as Subscription
                ?? throw new ArgumentException("The subscription resource must be backed by a resource instance.", nameof(resource));
        }

        private static string ExtensionString(Extension extension)
        {
            return extension?.Value switch
            {
                null => null,
                PrimitiveType primitive => primitive.ObjectValue?.ToString(),
                _ => null,
            };
        }

        private static void ThrowIfErrors(List<string> errors)
        {
            if (errors.Count > 0)
            {
                throw new SubscriptionValidationException(errors);
            }
        }

        private static SubscriptionInteraction ParseInteraction(string literal) => literal switch
        {
            "create" => SubscriptionInteraction.Create,
            "update" => SubscriptionInteraction.Update,
            "delete" => SubscriptionInteraction.Delete,
            _ => throw new SubscriptionValidationException(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionTopicInteractionInvalid, literal)),
        };

        private static CriteriaNotExistsBehavior? ParseBehavior(string literal) => literal switch
        {
            null => null,
            "test-passes" => CriteriaNotExistsBehavior.TestPasses,
            "test-fails" => CriteriaNotExistsBehavior.TestFails,
            _ => throw new SubscriptionValidationException(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionTopicCriteriaBehaviorInvalid, literal)),
        };

        private static SubscriptionContentLevel ParseContent(string literal) => literal switch
        {
            null or "id-only" => SubscriptionContentLevel.IdOnly,
            "empty" => SubscriptionContentLevel.Empty,
            "full-resource" => SubscriptionContentLevel.FullResource,
            _ => throw new SubscriptionValidationException(string.Format(CultureInfo.InvariantCulture, Core.Resources.SubscriptionContentInvalid, literal)),
        };

#endif
    }
}
