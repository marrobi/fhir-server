// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using Hl7.Fhir.Model;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Authorization;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Registry;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SubscriptionEventProcessorTests : IClassFixture<SubscriptionTestFixture>
    {
        private readonly SubscriptionTestFixture _fixture;
        private readonly SubscriptionEventProcessor _processor;

        public SubscriptionEventProcessorTests(SubscriptionTestFixture fixture)
        {
            _fixture = fixture;
            _processor = new SubscriptionEventProcessor(fixture.Matcher, fixture.CreateAuthorizer(), NullLogger<SubscriptionEventProcessor>.Instance);
        }

        [Fact]
        public void GivenAChangeAtOrBeforeTheActivationWatermark_WhenProcessed_ThenNoEventIsCreated()
        {
            // Arrange
            RegisteredSubscription subscription = Subscription("s1", activation: 100);
            ResourceChange atWatermark = Change(transactionId: 100);
            ResourceChange after = Change(transactionId: 101);

            // Act
            IReadOnlyList<SubscriptionEventCandidate> events = _processor.Process([atWatermark, after], [subscription]);

            // Assert
            SubscriptionEventCandidate single = Assert.Single(events);
            Assert.Equal(101, single.Change.TransactionId);
            Assert.Equal(SubscriptionEventStatus.Pending, single.Status);
        }

        [Theory]
        [InlineData(false, 0)]
        [InlineData(true, 1)]
        public void GivenAVersionWrittenAsHistory_WhenProcessed_ThenAnEventIsCreatedOnlyWhenHistoricalVersionsNotify(bool notifyHistoricalVersions, int expectedEvents)
        {
            // Arrange
            var processor = new SubscriptionEventProcessor(
                _fixture.Matcher,
                _fixture.CreateAuthorizer(),
                NullLogger<SubscriptionEventProcessor>.Instance,
                Options.Create(new SubscriptionsConfiguration { NotifyHistoricalVersions = notifyHistoricalVersions }));
            Observation observation = InMemorySearchEvaluatorTests.CreateObservation();
            observation.Subject = new ResourceReference("Patient/p1");
            ResourceChange imported = SubscriptionTestFixture.Change(observation, transactionId: 101, writtenAsHistory: true);

            // Act
            IReadOnlyList<SubscriptionEventCandidate> events = processor.Process([imported], [Subscription("s1")]);

            // Assert
            Assert.Equal(expectedEvents, events.Count);
        }

        [Fact]
        public void GivenAnInactiveSubscription_WhenProcessed_ThenNoEventIsCreated()
        {
            RegisteredSubscription requested = Subscription("s1", status: SubscriptionStatusCode.Requested);
            RegisteredSubscription notActivated = Subscription("s2", activation: null);
            RegisteredSubscription noGrant = Subscription("s3", withGrant: false);

            Assert.Empty(_processor.Process([Change()], [requested, notActivated, noGrant]));
        }

        [Fact]
        public void GivenASubscriptionInError_WhenProcessed_ThenEventsAreStillRetained()
        {
            Assert.Single(_processor.Process([Change()], [Subscription("s1", status: SubscriptionStatusCode.Error)]));
        }

        [Fact]
        public void GivenASubscriptionResourceChange_WhenProcessed_ThenItNeverTriggers()
        {
            // Loop prevention: the server's own status writes change Subscription resources.
            RegisteredSubscription subscription = Subscription("s1", trigger: "Resource");
            var change = SubscriptionTestFixture.Change(new Hl7.Fhir.Model.Subscription { Id = "s1" }, transactionId: 101);

            Assert.Empty(_processor.Process([change], [subscription]));
            Assert.False(SubscriptionEventProcessor.IsCovered([subscription], "Subscription"));
        }

        [Fact]
        public void GivenACompartmentGrant_WhenAChangeIsOutsideTheCompartment_ThenItIsNotNumbered()
        {
            // Authorization is checked before an event number is assigned, so unauthorized changes leave no gap.
            var grant = new SubscriptionGrant { ContentHash = "h", CompartmentResourceType = "Patient", CompartmentId = "p1" };
            RegisteredSubscription subscription = Subscription("s1", grant: grant);

            IReadOnlyList<SubscriptionEventCandidate> events = _processor.Process([Change(patient: "p2", transactionId: 101), Change(patient: "p1", transactionId: 102)], [subscription]);

            Assert.Equal(102, Assert.Single(events).Change.TransactionId);
        }

        [Fact]
        public void GivenAnUnevaluableChange_WhenTheGrantIsUnrestricted_ThenAnUnevaluableEventIsRecorded()
        {
            RegisteredSubscription subscription = Subscription("s1", currentCriteria: "status=final");
            ResourceChange change = SubscriptionTestFixture.Change(null, null, version: 2, transactionId: 101, type: "Observation", id: "o1");

            SubscriptionEventCandidate e = Assert.Single(_processor.Process([change], [subscription]));

            Assert.Equal(SubscriptionEventStatus.Unevaluable, e.Status);
        }

        [Fact]
        public void GivenAnUnevaluableChange_WhenTheGrantIsRestricted_ThenNothingIsRecorded()
        {
            var grant = new SubscriptionGrant { ContentHash = "h", CompartmentResourceType = "Patient", CompartmentId = "p1" };
            RegisteredSubscription subscription = Subscription("s1", grant: grant, currentCriteria: "status=final");
            ResourceChange change = SubscriptionTestFixture.Change(null, null, version: 2, transactionId: 101, type: "Observation", id: "o1");

            Assert.Empty(_processor.Process([change], [subscription]));
        }

        [Fact]
        public void GivenSeveralSubscriptions_WhenProcessed_ThenEachGetsItsOwnEventsInChangeOrder()
        {
            RegisteredSubscription a = Subscription("a");
            RegisteredSubscription b = Subscription("b", activation: 101);

            IReadOnlyList<SubscriptionEventCandidate> events = _processor.Process([Change(transactionId: 101), Change(transactionId: 102)], [a, b]);

            Assert.Equal(["a", "a", "b"], events.Select(e => e.SubscriptionId).OrderBy(x => x).ToArray());
            Assert.Equal([101L, 102L], events.Where(e => e.SubscriptionId == "a").Select(e => e.Change.TransactionId).ToArray());
        }

        [Theory]
        [InlineData("Observation", true)]
        [InlineData("Patient", false)]
        public void GivenMatchableSubscriptions_WhenCheckingCoverage_ThenOnlyTriggeredTypesAreRead(string type, bool expected)
        {
            Assert.Equal(expected, SubscriptionEventProcessor.IsCovered([Subscription("s1")], type));
        }

        private static ResourceChange Change(string patient = "p1", long transactionId = 101)
        {
            Observation observation = InMemorySearchEvaluatorTests.CreateObservation();
            observation.Subject = new ResourceReference("Patient/" + patient);
            return SubscriptionTestFixture.Change(observation, transactionId: transactionId);
        }

        private static RegisteredSubscription Subscription(
            string id,
            long? activation = 100,
            SubscriptionStatusCode status = SubscriptionStatusCode.Active,
            SubscriptionGrant grant = default,
            string trigger = "Observation",
            string currentCriteria = null,
            bool withGrant = true)
        {
            grant = withGrant ? grant ?? new SubscriptionGrant { ContentHash = "h" } : null;
            return new RegisteredSubscription
            {
                Id = id,
                Info = new SubscriptionInfo { Id = id, Status = status, TopicCanonical = "http://example.org/topic", ChannelType = "rest-hook" },
                Topic = new SubscriptionTopicInfo
                {
                    Canonical = "http://example.org/topic",
                    ResourceTriggers = [new TopicResourceTrigger { Resource = trigger, QueryCriteria = currentCriteria == null ? null : new TopicQueryCriteria { Current = currentCriteria } }],
                },
                State = new SubscriptionState { SubscriptionId = id, ActivationTransactionId = activation },
                Grant = grant,
            };
        }
    }
}
