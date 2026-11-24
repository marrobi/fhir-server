// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

#if !Stu3
using System;
using System.Linq;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;
using FhirParameters = Hl7.Fhir.Model.Parameters;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SubscriptionNotificationFactoryTests
    {
        private static readonly Uri BaseUri = new("https://fhir.example/");
        private readonly SubscriptionNotificationFactory _factory = new();

        [Fact]
        public void GivenAnEventNotification_WhenBuilt_ThenTheBundleHasTheVersionSpecificShape()
        {
            // Act
            Bundle bundle = Build(SubscriptionContentLevel.IdOnly, Events(2));

            // Assert
#if R5
            Assert.Equal(Bundle.BundleType.SubscriptionNotification, bundle.Type);
#else
            Assert.Equal(Bundle.BundleType.History, bundle.Type);
#endif
            Assert.Equal(3, bundle.Entry.Count);
#if R4 && !R4B
            var status = Assert.IsType<FhirParameters>(bundle.Entry[0].Resource);
            Assert.Contains(SubscriptionsBackport.StatusR4Profile, status.Meta.Profile);
            Assert.Equal("event-notification", ((Code)status.GetSingle("type").Value).Value);
            Assert.Equal("8", ((FhirString)status.GetSingle("events-since-subscription-start").Value).Value);
            Assert.Equal(2, status.Parameter.Count(p => p.Name == "notification-event"));
            Assert.Equal("https://fhir.example/Encounter/e1", ((ResourceReference)status.Parameter.First(p => p.Name == "notification-event").Part.Single(p => p.Name == "focus").Value).Reference);
#else
            var status = Assert.IsType<SubscriptionStatus>(bundle.Entry[0].Resource);
            Assert.Equal(SubscriptionStatus.SubscriptionNotificationType.EventNotification, status.Type);
            Assert.Equal("https://fhir.example/Subscription/s1", status.Subscription.Reference);
            Assert.Equal(2, status.NotificationEvent.Count);
            Assert.Equal("https://fhir.example/Encounter/e1", status.NotificationEvent[0].Focus.Reference);
#endif
        }

        [Fact]
        public void GivenIdOnlyContent_WhenBuilt_ThenEntriesHaveFullUrlAndRequestButNoResource()
        {
            Bundle bundle = Build(SubscriptionContentLevel.IdOnly, Events(2));

            foreach (Bundle.EntryComponent entry in bundle.Entry.Skip(1))
            {
                Assert.StartsWith("https://fhir.example/Encounter/", entry.FullUrl, StringComparison.Ordinal);
                Assert.NotNull(entry.Request);
                Assert.Null(entry.Resource);
            }

            Assert.Equal(Bundle.HTTPVerb.POST, bundle.Entry[1].Request.Method);
            Assert.Equal(Bundle.HTTPVerb.PUT, bundle.Entry[2].Request.Method);
        }

        [Fact]
        public void GivenEmptyContent_WhenBuilt_ThenOnlyTheStatusIsIncludedAndThereIsNoFocus()
        {
            Bundle bundle = Build(SubscriptionContentLevel.Empty, Events(2));

            Assert.Single(bundle.Entry);
            Assert.DoesNotContain("focus", bundle.ToJson(), StringComparison.Ordinal);
        }

        [Fact]
        public void GivenFullResourceContent_WhenBuilt_ThenResourcesAreIncluded()
        {
            Bundle bundle = Build(SubscriptionContentLevel.FullResource, Events(1, withResource: true));

            Assert.Equal("e1", bundle.Entry[1].Resource.Id);
        }

        [Fact]
        public void GivenAHandshake_WhenBuilt_ThenItHasNoEvents()
        {
            ResourceElement notification = _factory.CreateNotification(Request(SubscriptionContentLevel.IdOnly, [], SubscriptionNotificationType.Handshake));
            Bundle bundle = notification.ToPoco<Bundle>();

            Assert.Single(bundle.Entry);
            Assert.Contains("handshake", bundle.ToJson(), StringComparison.Ordinal);
        }

        [Fact]
        public void GivenStatuses_WhenTheStatusBundleIsBuilt_ThenItIsASearchsetWithErrors()
        {
            SubscriptionNotificationRequest status = Request(SubscriptionContentLevel.IdOnly, [], SubscriptionNotificationType.QueryStatus, errors: ["endpoint down"]);

            Bundle bundle = _factory.CreateStatusBundle([status]).ToPoco<Bundle>();

            Assert.Equal(Bundle.BundleType.Searchset, bundle.Type);
            Assert.Equal(1, bundle.Total);
            Assert.Contains("query-status", bundle.ToJson(), StringComparison.Ordinal);
            Assert.Contains("endpoint down", bundle.ToJson(), StringComparison.Ordinal);
        }

        [Theory]
        [InlineData("application/fhir+json", "{")]
        [InlineData("application/fhir+xml", "<")]
        public void GivenAContentType_WhenSerialized_ThenTheFormatMatches(string contentType, string prefix)
        {
            string payload = _factory.Serialize(_factory.CreateNotification(Request(SubscriptionContentLevel.IdOnly, [], SubscriptionNotificationType.Heartbeat)), contentType);

            Assert.StartsWith(prefix, payload.TrimStart(), StringComparison.Ordinal);
        }

        private Bundle Build(SubscriptionContentLevel content, SubscriptionNotificationEvent[] events)
        {
            string json = _factory.Serialize(_factory.CreateNotification(Request(content, events, SubscriptionNotificationType.EventNotification)), "application/fhir+json");
            return new FhirJsonParser().Parse<Bundle>(json);
        }

        private static SubscriptionNotificationRequest Request(SubscriptionContentLevel content, SubscriptionNotificationEvent[] events, SubscriptionNotificationType type, string[] errors = null) => new()
        {
            Subscription = new SubscriptionInfo { Id = "s1", TopicCanonical = "http://example.org/topic", Content = content, Status = SubscriptionStatusCode.Active },
            Type = type,
            Status = SubscriptionStatusCode.Active,
            EventsSinceSubscriptionStart = 8,
            Events = events,
            Errors = errors ?? [],
            BaseUri = BaseUri,
        };

        private static SubscriptionNotificationEvent[] Events(int count, bool withResource = false) => Enumerable.Range(1, count).Select(i => new SubscriptionNotificationEvent
        {
            EventNumber = 6 + i,
            Timestamp = DateTimeOffset.UtcNow,
            ResourceType = "Encounter",
            ResourceId = "e" + i,
            ResourceVersion = i,
            Interaction = i == 1 ? SubscriptionInteraction.Create : SubscriptionInteraction.Update,
            Resource = withResource ? new Encounter { Id = "e" + i }.ToResourceElement() : null,
        }).ToArray();
    }
}
#endif
