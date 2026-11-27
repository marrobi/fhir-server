// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SubscriptionValidatorTests : IClassFixture<SubscriptionTestFixture>
    {
        private readonly SubscriptionTestFixture _fixture;
        private readonly SubscriptionValidator _validator;

        public SubscriptionValidatorTests(SubscriptionTestFixture fixture)
        {
            _fixture = fixture;
            _validator = fixture.CreateValidator(new SubscriptionsConfiguration { MinHeartbeatPeriodSeconds = 30, MaxTimeoutSeconds = 60 });
        }

        [Fact]
        public void GivenAValidTopic_WhenValidated_ThenThereAreNoErrors()
        {
            Assert.Empty(_validator.ValidateTopic(ValidTopic()));
        }

        [Theory]
        [InlineData(UnversionedResourceTypePolicy.AllowWithGaps, "Encounter", true)]
        [InlineData(UnversionedResourceTypePolicy.RequireHistory, "Encounter", false)]
        [InlineData(UnversionedResourceTypePolicy.RequireHistory, "Observation", true)]
        [InlineData(UnversionedResourceTypePolicy.RequireHistory, "Resource", false)]
        public void GivenAnUnversionedResourceType_WhenATopicTriggersOnIt_ThenThePolicyDecidesWhetherItIsValid(UnversionedResourceTypePolicy policy, string trigger, bool valid)
        {
            // Arrange: Encounter does not keep history; everything else does.
            var coreFeatures = new CoreFeatureConfiguration();
            coreFeatures.Versioning.ResourceTypeOverrides["Encounter"] = "no-version";
            SubscriptionValidator validator = _fixture.CreateValidator(new SubscriptionsConfiguration { UnversionedResourceTypes = policy }, coreFeatures);
            var topic = new SubscriptionTopicInfo
            {
                Canonical = "http://example.org/topic/any",
                Status = "active",
                ResourceTriggers = [new TopicResourceTrigger { Resource = trigger, FhirPathCriteria = "true" }],
            };

            // Act
            IReadOnlyList<string> errors = validator.ValidateTopic(topic);

            // Assert
            Assert.Equal(valid, errors.Count == 0);
        }

        [Fact]
        public void GivenATopicWithEventTriggers_WhenValidated_ThenItIsRejected()
        {
            Assert.NotEmpty(_validator.ValidateTopic(Copy(ValidTopic(), hasEventTriggers: true)));
        }

        [Theory]
        [InlineData(null, "status=finished")]
        [InlineData("NotAType", null)]
        [InlineData("Subscription", null)]
        [InlineData("Encounter", "subject.name=x")]
        [InlineData("Encounter", "_has:Observation:encounter:status=final")]
        [InlineData("Encounter", "code:in=http://vs")]
        [InlineData("Encounter", "Patient?gender=male")]
        public void GivenAnUnsupportedTrigger_WhenValidated_ThenItIsRejected(string resource, string current)
        {
            var topic = new SubscriptionTopicInfo
            {
                Canonical = "http://example.org/topic",
                ResourceTriggers = [new TopicResourceTrigger { Resource = resource, QueryCriteria = new TopicQueryCriteria { Current = current } }],
            };

            Assert.NotEmpty(_validator.ValidateTopic(topic));
        }

        [Fact]
        public void GivenAnInvalidFhirPath_WhenValidated_ThenItIsRejected()
        {
            var topic = new SubscriptionTopicInfo { Canonical = "http://example.org/topic", ResourceTriggers = [new TopicResourceTrigger { Resource = "Encounter", FhirPathCriteria = "status = (" }] };

            Assert.NotEmpty(_validator.ValidateTopic(topic));
        }

        [Fact]
        public void GivenATopicWithoutUrlOrTriggers_WhenValidated_ThenBothAreReported()
        {
            Assert.Equal(2, _validator.ValidateTopic(new SubscriptionTopicInfo()).Count);
        }

        [Fact]
        public void GivenACanFilterByForAnUnknownParameter_WhenValidated_ThenItIsRejected()
        {
            Assert.NotEmpty(_validator.ValidateTopic(Copy(ValidTopic(), canFilterBy: [new TopicCanFilterBy { FilterParameter = "nope" }])));
        }

        [Fact]
        public void GivenAValidSubscription_WhenValidated_ThenThereAreNoErrors()
        {
            Assert.Empty(_validator.ValidateSubscription(ValidSubscription(), ValidTopic()));
        }

        [Fact]
        public void GivenAMissingOrRetiredTopic_WhenValidated_ThenTheSubscriptionIsRejected()
        {
            Assert.NotEmpty(_validator.ValidateSubscription(ValidSubscription(), null));
            Assert.NotEmpty(_validator.ValidateSubscription(ValidSubscription(), Copy(ValidTopic(), status: "retired")));
        }

        [Theory]
        [InlineData(SubscriptionStatusCode.Off)]
        [InlineData(SubscriptionStatusCode.EnteredInError)]
        public void GivenAMissingRetiredOrInvalidTopic_WhenAStoppedSubscriptionIsValidated_ThenItIsAccepted(SubscriptionStatusCode status)
        {
            SubscriptionInfo subscription = With(status: status);

            Assert.Empty(_validator.ValidateSubscription(subscription, null));
            Assert.Empty(_validator.ValidateSubscription(subscription, Copy(ValidTopic(), status: "retired")));
            Assert.Empty(_validator.ValidateSubscription(subscription, Copy(ValidTopic(), hasEventTriggers: true)));
        }

        [Fact]
        public void GivenAFilterTheTopicNoLongerAllows_WhenValidated_ThenOnlyAStoppedSubscriptionIsAccepted()
        {
            IReadOnlyList<SubscriptionFilter> filters = [new SubscriptionFilter { ResourceType = "Encounter", FilterParameter = "status", Value = "finished" }];

            Assert.Empty(_validator.ValidateSubscription(With(filters: filters, status: SubscriptionStatusCode.Off), ValidTopic()));
            Assert.NotEmpty(_validator.ValidateSubscription(With(filters: filters), ValidTopic()));
        }

        public static IEnumerable<object[]> InvalidSubscriptions()
        {
            yield return [With(channelType: "websocket")];
            yield return [With(endpoint: "http://hooks.example.com/")];
            yield return [With(endpoint: "https://10.0.0.1/")];
            yield return [With(contentType: "text/plain")];
            yield return [With(heartbeat: 5)];
            yield return [With(timeout: 600)];
            yield return [With(maxCount: 0)];
            yield return [With(headers: [new SubscriptionHeader("Host", "evil")])];
            yield return [With(headers: [new SubscriptionHeader("X-Ok", "a\r\nInjected: 1")])];
            yield return [With(filters: [new SubscriptionFilter { FilterParameter = "status", Value = "finished" }])];
            yield return [With(filters: [new SubscriptionFilter { FilterParameter = "subject", Modifier = "missing", Value = "true" }])];
            yield return [With(filters: [new SubscriptionFilter { FilterParameter = "subject" }])];
            yield return [With(filters: [new SubscriptionFilter { ResourceType = "Observation", FilterParameter = "subject", Value = "Patient/p1" }])];
        }

        [Theory]
        [MemberData(nameof(InvalidSubscriptions))]
        public void GivenASubscriptionTheServerCannotHonor_WhenValidated_ThenItIsRejected(SubscriptionInfo subscription)
        {
            Assert.NotEmpty(_validator.ValidateSubscription(subscription, ValidTopic()));
        }

        [Theory]
        [InlineData(SubscriptionStatusCode.Off, true)]
        [InlineData(SubscriptionStatusCode.EnteredInError, true)]
        [InlineData(SubscriptionStatusCode.Requested, false)]
        [InlineData(SubscriptionStatusCode.Active, false)]
        [InlineData(SubscriptionStatusCode.Error, false)]
        public void GivenAnEndpointHostNotInTheAllowList_WhenValidated_ThenOnlyAStoppedSubscriptionIsAccepted(SubscriptionStatusCode status, bool accepted)
        {
            // Arrange: the allow-list was tightened after the subscription was written.
            var configuration = new SubscriptionsConfiguration();
            configuration.AllowedEndpointHosts.Add("hooks.partner.org");
            SubscriptionValidator validator = _fixture.CreateValidator(configuration);
            SubscriptionInfo subscription = With(status: status);

            // Act
            IReadOnlyList<string> errors = validator.ValidateSubscription(subscription, ValidTopic());

            // Assert
            Assert.Equal(accepted, errors.Count == 0);
        }

        [Fact]
        public void GivenAFilterAllowedByTheTopic_WhenValidated_ThenItIsAccepted()
        {
            SubscriptionInfo subscription = With(filters: [new SubscriptionFilter { ResourceType = "Encounter", FilterParameter = "subject", Value = "Patient/p1" }]);

            Assert.Empty(_validator.ValidateSubscription(subscription, ValidTopic()));
        }

        [Theory]
        [InlineData("gt", new[] { "gt", "lt" }, true)]
        [InlineData("ge", new[] { "gt", "lt" }, false)]
        [InlineData("eq", new[] { "gt", "lt" }, true)]
        [InlineData("ge", new string[0], true)]
        public void GivenAFilterComparator_WhenValidated_ThenOnlyComparatorsTheTopicAllowsAreAccepted(string comparator, string[] allowed, bool accepted)
        {
            SubscriptionTopicInfo topic = Copy(ValidTopic(), canFilterBy: [new TopicCanFilterBy { Resource = "Encounter", FilterParameter = "date", Comparators = allowed }]);
            SubscriptionInfo subscription = With(filters: [new SubscriptionFilter { ResourceType = "Encounter", FilterParameter = "date", Comparator = comparator, Value = "2026-01-01" }]);

            IReadOnlyList<string> errors = _validator.ValidateSubscription(subscription, topic);

            Assert.Equal(accepted, errors.Count == 0);
        }

        [Theory]
        [InlineData("date", "gt2026-01-01", true)]
        [InlineData("date", "ge2026-01-01", false)]
        [InlineData("date", "2026-01-01", true)]
        [InlineData("date", "eq2026-01-01", true)]
        [InlineData("date", "gt2026-01-01,ge2026-06-01", false)]
        [InlineData("length", "ge5", false)]
        public void GivenABackportFilterWithAComparatorPrefix_WhenValidated_ThenThePrefixIsCheckedLikeAComparator(string parameter, string value, bool accepted)
        {
            // Backport filters are query strings, so the comparator arrives as a prefix of the value rather than as Comparator.
            SubscriptionTopicInfo topic = Copy(ValidTopic(), canFilterBy: [new TopicCanFilterBy { Resource = "Encounter", FilterParameter = parameter, Comparators = ["gt", "lt"] }]);
            SubscriptionInfo subscription = With(filters: [new SubscriptionFilter { ResourceType = "Encounter", FilterParameter = parameter, Value = value }]);

            IReadOnlyList<string> errors = _validator.ValidateSubscription(subscription, topic);

            Assert.Equal(accepted, errors.Count == 0);
        }

        [Fact]
        public void GivenATokenFilterWhoseValueStartsWithComparatorLetters_WhenValidated_ThenItIsNotTreatedAsAComparator()
        {
            SubscriptionTopicInfo topic = Copy(ValidTopic(), canFilterBy: [new TopicCanFilterBy { Resource = "Encounter", FilterParameter = "status", Comparators = ["gt"] }]);
            SubscriptionInfo subscription = With(filters: [new SubscriptionFilter { ResourceType = "Encounter", FilterParameter = "status", Value = "ge-custom" }]);

            Assert.Empty(_validator.ValidateSubscription(subscription, topic));
        }

        [Fact]
        public void GivenAFilterWithAModifierAndAComparator_WhenValidated_ThenItIsRejected()
        {
            // Both are allowed by the topic, so only the R5 invariant scr-1 rejects the filter.
            SubscriptionTopicInfo topic = Copy(ValidTopic(), canFilterBy: [new TopicCanFilterBy { Resource = "Encounter", FilterParameter = "date", Modifiers = ["missing"], Comparators = ["gt"] }]);
            SubscriptionInfo subscription = With(filters: [new SubscriptionFilter { ResourceType = "Encounter", FilterParameter = "date", Modifier = "missing", Comparator = "gt", Value = "2026-01-01" }]);

            IReadOnlyList<string> errors = _validator.ValidateSubscription(subscription, topic);

            Assert.Contains(errors, e => e.Contains("both a modifier and a comparator", StringComparison.Ordinal));
        }

        private static SubscriptionTopicInfo ValidTopic() => new()
        {
            Canonical = "http://example.org/topic/encounter",
            Status = "active",
            ResourceTriggers = [new TopicResourceTrigger { Resource = "Encounter", QueryCriteria = new TopicQueryCriteria { Current = "status=cancelled" } }],
            CanFilterBy = [new TopicCanFilterBy { Resource = "Encounter", FilterParameter = "subject", Modifiers = ["type"] }],
        };

        private static SubscriptionTopicInfo Copy(SubscriptionTopicInfo topic, bool hasEventTriggers = false, string status = null, IReadOnlyList<TopicCanFilterBy> canFilterBy = null) => new()
        {
            Canonical = topic.Canonical,
            Status = status ?? topic.Status,
            ResourceTriggers = topic.ResourceTriggers,
            CanFilterBy = canFilterBy ?? topic.CanFilterBy,
            HasEventTriggers = hasEventTriggers,
        };

        private static SubscriptionInfo ValidSubscription() => With();

        private static SubscriptionInfo With(
            string channelType = "rest-hook",
            string endpoint = "https://hooks.example.com/notify",
            string contentType = "application/fhir+json",
            int? heartbeat = 60,
            int? timeout = 30,
            int? maxCount = 10,
            IReadOnlyList<SubscriptionHeader> headers = null,
            IReadOnlyList<SubscriptionFilter> filters = null,
            SubscriptionStatusCode status = SubscriptionStatusCode.Requested) => new()
        {
            Id = "s1",
            Status = status,
            TopicCanonical = "http://example.org/topic/encounter",
            ChannelType = channelType,
            Endpoint = endpoint,
            ContentType = contentType,
            HeartbeatPeriodSeconds = heartbeat,
            TimeoutSeconds = timeout,
            MaxCount = maxCount,
            Headers = headers ?? [new SubscriptionHeader("Authorization", "Bearer x")],
            Filters = filters ?? [],
        };
    }
}
