// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

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

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SubscriptionModelConverterTests
    {
        private const string TopicUrl = "http://example.org/fhir/SubscriptionTopic/encounter-cancelled";
        private readonly SubscriptionModelConverter _converter = new();

        [Fact]
        public void GivenATopicResource_WhenConverted_ThenTriggersAndFiltersAreRead()
        {
            // Arrange
            ResourceElement resource = Parse(SubscriptionResources.Topic(TopicUrl));

            // Act
            bool isTopic = _converter.IsTopic(resource);
            SubscriptionTopicInfo topic = _converter.ToTopic(resource);

            // Assert
            Assert.True(isTopic);
            Assert.Equal(TopicUrl, topic.Canonical);
            Assert.Equal("active", topic.Status);
            TopicResourceTrigger trigger = Assert.Single(topic.ResourceTriggers);
            Assert.Equal("Encounter", trigger.Resource);
            Assert.Equal([SubscriptionInteraction.Create, SubscriptionInteraction.Update], trigger.SupportedInteractions);
            Assert.Equal("status:not=cancelled", trigger.QueryCriteria.Previous);
            Assert.Equal("status=cancelled", trigger.QueryCriteria.Current);
            Assert.Equal(CriteriaNotExistsBehavior.TestPasses, trigger.QueryCriteria.ResultForCreate);
            Assert.True(trigger.QueryCriteria.RequireBoth);
            Assert.Equal("subject", Assert.Single(topic.CanFilterBy).FilterParameter);
            Assert.False(topic.HasEventTriggers);
        }

#if R4 && !R4B
        [Fact]
        public void GivenAnOrdinaryBasicResource_WhenChecked_ThenItIsNotATopic()
        {
            var basic = new Basic { Id = "b1", Code = new CodeableConcept("http://example.org", "other") };

            Assert.False(_converter.IsTopic(basic.ToResourceElement()));
            Assert.Equal("Basic", _converter.TopicResourceType);
            Assert.Equal("code", Assert.Single(_converter.TopicSearchParameters).Item1);
        }

        [Fact]
        public void GivenAClassicCriteriaSubscription_WhenConverted_ThenItIsRejected()
        {
            var subscription = new Subscription
            {
                Status = Subscription.SubscriptionStatus.Requested,
                Criteria = "Observation?code=1234",
                Channel = new Subscription.ChannelComponent { Type = Subscription.SubscriptionChannelType.RestHook, Endpoint = "https://hooks.example.com" },
            };

            Assert.Throws<SubscriptionValidationException>(() => _converter.ToSubscription(subscription.ToResourceElement()));
        }
#else
        [Fact]
        public void GivenTheServerVersion_WhenAskedForTheTopicResource_ThenItIsSubscriptionTopic()
        {
            Assert.Equal("SubscriptionTopic", _converter.TopicResourceType);
            Assert.Empty(_converter.TopicSearchParameters);
        }
#endif

        [Fact]
        public void GivenASubscription_WhenConverted_ThenChannelPayloadAndFiltersAreRead()
        {
            ResourceElement resource = Parse(SubscriptionResources.Subscription(TopicUrl, "https://hooks.example.com/notify", content: "full-resource", filter: "Encounter?subject=Patient/p1"));

            SubscriptionInfo info = _converter.ToSubscription(resource);

            Assert.Equal(TopicUrl, info.TopicCanonical);
            Assert.Equal("rest-hook", info.ChannelType);
            Assert.Equal("https://hooks.example.com/notify", info.Endpoint);
            Assert.Equal(SubscriptionContentLevel.FullResource, info.Content);
            Assert.Equal("application/fhir+json", info.ContentType);
            Assert.Equal(120, info.HeartbeatPeriodSeconds);
            Assert.Equal(SubscriptionStatusCode.Requested, info.Status);
            SubscriptionFilter filter = Assert.Single(info.Filters);
            Assert.Equal("Encounter", filter.ResourceType);
            Assert.Equal("subject", filter.FilterParameter);
            Assert.Equal("Patient/p1", filter.Value);
            SubscriptionHeader header = Assert.Single(info.Headers);
            Assert.Equal("Authorization", header.Name);
            Assert.Equal("Bearer secret", header.Value);
        }

        [Fact]
        public void GivenASubscriptionWithoutContent_WhenServerValuesAreApplied_ThenIdOnlyIsTheDefaultAndStatusIsServerOwned()
        {
            // Arrange
            ResourceElement resource = Parse(SubscriptionResources.Subscription(TopicUrl, "https://hooks.example.com/notify", content: null, status: "active"));
            DateTimeOffset end = new(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);

            // Act
            _converter.ApplyServerValues(resource, SubscriptionStatusCode.Requested, end);
            SubscriptionInfo info = _converter.ToSubscription(resource.ToPoco<Resource>().ToResourceElement());

            // Assert
            Assert.Equal(SubscriptionContentLevel.IdOnly, info.Content);
            Assert.Equal(SubscriptionStatusCode.Requested, info.Status);
            Assert.Equal(end, info.End);
#if !R5
            string json = resource.Instance.ToJson();
            Assert.Contains("backport-payload-content", json, StringComparison.Ordinal);
            Assert.Contains("id-only", json, StringComparison.Ordinal);
#endif
        }

        [Fact]
        public void GivenTheContentHash_WhenOnlyStatusChanges_ThenItIsStableButContentChangesAreDetected()
        {
            SubscriptionInfo original = _converter.ToSubscription(Parse(SubscriptionResources.Subscription(TopicUrl, "https://hooks.example.com/notify")));
            SubscriptionInfo statusOnly = _converter.ToSubscription(_converter.WithStatus(Parse(SubscriptionResources.Subscription(TopicUrl, "https://hooks.example.com/notify")), SubscriptionStatusCode.Active, "x"));
            SubscriptionInfo newEndpoint = _converter.ToSubscription(Parse(SubscriptionResources.Subscription(TopicUrl, "https://other.example.com/notify")));

            Assert.Equal(original.ComputeContentHash(), statusOnly.ComputeContentHash());
            Assert.NotEqual(original.ComputeContentHash(), newEndpoint.ComputeContentHash());
            Assert.Equal(SubscriptionStatusCode.Active, statusOnly.Status);
        }

#if !R5
        // On R5 content is a typed element, so the FHIR parser itself rejects an unknown code.
        [Fact]
        public void GivenAnInvalidPayloadContent_WhenConverted_ThenItIsRejected()
        {
            Assert.Throws<SubscriptionValidationException>(() => _converter.ToSubscription(Parse(SubscriptionResources.Subscription(TopicUrl, "https://hooks.example.com/notify", content: "everything"))));
        }
#endif

        private static ResourceElement Parse(string json) => new FhirJsonParser().Parse<Resource>(json).ToResourceElement();
    }
}
