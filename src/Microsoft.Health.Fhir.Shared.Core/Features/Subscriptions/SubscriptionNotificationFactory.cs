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
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Models;
using FhirParameters = Hl7.Fhir.Model.Parameters;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Builds notification and status bundles in the shape each FHIR version defines:
    /// R4 Backport (<c>history</c> bundle, <c>Parameters</c> status), R4B Backport (<c>history</c> bundle, <c>SubscriptionStatus</c>)
    /// and R5 (<c>subscription-notification</c> bundle, <c>SubscriptionStatus</c>).
    /// </summary>
    public sealed class SubscriptionNotificationFactory : ISubscriptionNotificationFactory
    {
#if Stu3
        public ResourceElement CreateNotification(SubscriptionNotificationRequest request) => throw new NotSupportedException();

        public ResourceElement CreateStatusBundle(IReadOnlyList<SubscriptionNotificationRequest> statuses) => throw new NotSupportedException();

        public string Serialize(ResourceElement resource, string contentType) => throw new NotSupportedException();
#else
        public ResourceElement CreateNotification(SubscriptionNotificationRequest request)
        {
            EnsureArg.IsNotNull(request, nameof(request));
            EnsureArg.IsNotNull(request.Subscription, nameof(request));

            string subscriptionUrl = Absolute(request.BaseUri, $"{KnownResourceTypes.Subscription}/{request.Subscription.Id}");
            var bundle = new Bundle
            {
                Id = Guid.NewGuid().ToString(),
#if R5
                Type = Bundle.BundleType.SubscriptionNotification,
#else
                Type = Bundle.BundleType.History,
#endif
                Timestamp = Clock.UtcNow,
            };

            bundle.Entry.Add(new Bundle.EntryComponent
            {
                FullUrl = "urn:uuid:" + Guid.NewGuid(),
                Resource = CreateStatusResource(request),
                Request = new Bundle.RequestComponent { Method = Bundle.HTTPVerb.GET, Url = subscriptionUrl + "/$status" },
                Response = new Bundle.ResponseComponent { Status = "200" },
            });

            if (request.Subscription.Content == SubscriptionContentLevel.Empty)
            {
                // empty notifications SHALL NOT contain entries other than the status.
                return bundle.ToResourceElement();
            }

#if R5
            var seen = new HashSet<string>(StringComparer.Ordinal);
#endif
            foreach (SubscriptionNotificationEvent notificationEvent in request.Events)
            {
                string relative = $"{notificationEvent.ResourceType}/{notificationEvent.ResourceId}";
                string fullUrl = Absolute(request.BaseUri, relative);
                Resource resource = request.Subscription.Content == SubscriptionContentLevel.FullResource ? notificationEvent.Resource?.ToPoco<Resource>() : null;

#if R5
                // Outside history bundles, entries with the same fullUrl must differ in meta.versionId.
                if (resource == null && !seen.Add(fullUrl))
                {
                    continue;
                }
#endif

                bundle.Entry.Add(new Bundle.EntryComponent
                {
                    FullUrl = fullUrl,
                    Resource = resource,
                    Request = notificationEvent.Interaction switch
                    {
                        SubscriptionInteraction.Create => new Bundle.RequestComponent { Method = Bundle.HTTPVerb.POST, Url = notificationEvent.ResourceType },
                        SubscriptionInteraction.Delete => new Bundle.RequestComponent { Method = Bundle.HTTPVerb.DELETE, Url = relative },
                        _ => new Bundle.RequestComponent { Method = Bundle.HTTPVerb.PUT, Url = relative },
                    },
                    Response = new Bundle.ResponseComponent
                    {
                        Status = notificationEvent.Interaction switch
                        {
                            SubscriptionInteraction.Create => "201",
                            SubscriptionInteraction.Delete => "204",
                            _ => "200",
                        },
                        Etag = $"W/\"{notificationEvent.ResourceVersion}\"",
                    },
                });
            }

            return bundle.ToResourceElement();
        }

        public ResourceElement CreateStatusBundle(IReadOnlyList<SubscriptionNotificationRequest> statuses)
        {
            EnsureArg.IsNotNull(statuses, nameof(statuses));

            var bundle = new Bundle
            {
                Id = Guid.NewGuid().ToString(),
                Type = Bundle.BundleType.Searchset,
                Timestamp = Clock.UtcNow,
                Total = statuses.Count,
            };

            foreach (SubscriptionNotificationRequest status in statuses)
            {
                bundle.Entry.Add(new Bundle.EntryComponent
                {
                    FullUrl = "urn:uuid:" + Guid.NewGuid(),
                    Resource = CreateStatusResource(status),
                    Search = new Bundle.SearchComponent { Mode = Bundle.SearchEntryMode.Match },
                });
            }

            return bundle.ToResourceElement();
        }

        public string Serialize(ResourceElement resource, string contentType)
        {
            EnsureArg.IsNotNull(resource, nameof(resource));

            bool xml = MediaTypeHeaderValue.TryParse(contentType, out MediaTypeHeaderValue parsed) &&
                parsed.MediaType?.EndsWith("xml", StringComparison.OrdinalIgnoreCase) == true;

            return xml ? resource.Instance.ToXml() : resource.Instance.ToJson();
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance", Justification = "The concrete type differs by FHIR version.")]
        private static Resource CreateStatusResource(SubscriptionNotificationRequest request)
        {
            SubscriptionInfo subscription = request.Subscription;
            string subscriptionUrl = Absolute(request.BaseUri, $"{KnownResourceTypes.Subscription}/{subscription.Id}");
            bool includeFocus = subscription.Content != SubscriptionContentLevel.Empty;

#if R4 && !R4B
            var parameters = new FhirParameters { Id = Guid.NewGuid().ToString(), Meta = new Meta { Profile = [SubscriptionsBackport.StatusR4Profile] } };
            parameters.Parameter.Add(new FhirParameters.ParameterComponent { Name = "subscription", Value = new ResourceReference(subscriptionUrl) });
            parameters.Parameter.Add(new FhirParameters.ParameterComponent { Name = "topic", Value = new Canonical(subscription.TopicCanonical) });
            parameters.Parameter.Add(new FhirParameters.ParameterComponent { Name = "status", Value = new Code(ToLiteral(request.Status)) });
            parameters.Parameter.Add(new FhirParameters.ParameterComponent { Name = "type", Value = new Code(ToLiteral(request.Type)) });
            parameters.Parameter.Add(new FhirParameters.ParameterComponent { Name = "events-since-subscription-start", Value = new FhirString(request.EventsSinceSubscriptionStart.ToString(CultureInfo.InvariantCulture)) });

            foreach (SubscriptionNotificationEvent notificationEvent in request.Events)
            {
                var eventParameter = new FhirParameters.ParameterComponent { Name = "notification-event" };
                eventParameter.Part.Add(new FhirParameters.ParameterComponent { Name = "event-number", Value = new FhirString(notificationEvent.EventNumber.ToString(CultureInfo.InvariantCulture)) });
                eventParameter.Part.Add(new FhirParameters.ParameterComponent { Name = "timestamp", Value = new Instant(notificationEvent.Timestamp) });
                if (includeFocus)
                {
                    eventParameter.Part.Add(new FhirParameters.ParameterComponent { Name = "focus", Value = new ResourceReference(Absolute(request.BaseUri, $"{notificationEvent.ResourceType}/{notificationEvent.ResourceId}")) });
                }

                parameters.Parameter.Add(eventParameter);
            }

            foreach (string error in request.Errors)
            {
                parameters.Parameter.Add(new FhirParameters.ParameterComponent { Name = "error", Value = new CodeableConcept { Text = error } });
            }

            return parameters;
#else
            var status = new SubscriptionStatus
            {
                Id = Guid.NewGuid().ToString(),
                Status = Hl7.Fhir.Utility.EnumUtility.ParseLiteral<SubscriptionStatusCodes>(ToLiteral(request.Status)),
                Type = Hl7.Fhir.Utility.EnumUtility.ParseLiteral<SubscriptionStatus.SubscriptionNotificationType>(ToLiteral(request.Type)),
#if R5
                EventsSinceSubscriptionStart = request.EventsSinceSubscriptionStart,
#else
                EventsSinceSubscriptionStart = request.EventsSinceSubscriptionStart.ToString(CultureInfo.InvariantCulture),
#endif
                Subscription = new ResourceReference(subscriptionUrl),
                Topic = subscription.TopicCanonical,
            };

            foreach (SubscriptionNotificationEvent notificationEvent in request.Events)
            {
                status.NotificationEvent.Add(new SubscriptionStatus.NotificationEventComponent
                {
#if R5
                    EventNumber = notificationEvent.EventNumber,
#else
                    EventNumber = notificationEvent.EventNumber.ToString(CultureInfo.InvariantCulture),
#endif
                    Timestamp = notificationEvent.Timestamp,
                    Focus = includeFocus ? new ResourceReference(Absolute(request.BaseUri, $"{notificationEvent.ResourceType}/{notificationEvent.ResourceId}")) : null,
                });
            }

            foreach (string error in request.Errors)
            {
                status.Error.Add(new CodeableConcept { Text = error });
            }

            return status;
#endif
        }

        private static string Absolute(Uri baseUri, string relative)
        {
            return baseUri == null ? relative : new Uri(baseUri, relative).ToString();
        }

        private static string ToLiteral(SubscriptionStatusCode status) => status switch
        {
            SubscriptionStatusCode.Active => "active",
            SubscriptionStatusCode.Error => "error",
            SubscriptionStatusCode.Off => "off",
            SubscriptionStatusCode.EnteredInError => "entered-in-error",
            _ => "requested",
        };

        private static string ToLiteral(SubscriptionNotificationType type) => type switch
        {
            SubscriptionNotificationType.Handshake => "handshake",
            SubscriptionNotificationType.Heartbeat => "heartbeat",
            SubscriptionNotificationType.QueryStatus => "query-status",
            _ => "event-notification",
        };
#endif
    }
}
