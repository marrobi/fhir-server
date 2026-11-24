// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using Hl7.Fhir.Model;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SubscriptionMatcherTests : IClassFixture<SubscriptionTestFixture>
    {
        private readonly SubscriptionMatcher _matcher;

        public SubscriptionMatcherTests(SubscriptionTestFixture fixture)
        {
            _matcher = fixture.Matcher;
        }

        [Theory]
        [InlineData("Encounter", true)]
        [InlineData("DomainResource", true)]
        [InlineData("Resource", true)]
        [InlineData("http://hl7.org/fhir/StructureDefinition/Encounter", true)]
        [InlineData("Patient", false)]
        public void GivenATriggerResource_WhenCheckingApplicability_ThenBaseTypesAndCanonicalsAreHonored(string triggerResource, bool expected)
        {
            Assert.Equal(expected, SubscriptionMatcher.AppliesTo(triggerResource, "Encounter"));
        }

        [Fact]
        public void GivenADomainResourceTrigger_WhenTheResourceIsABundle_ThenItDoesNotApply()
        {
            Assert.False(SubscriptionMatcher.AppliesTo("DomainResource", "Bundle"));
            Assert.True(SubscriptionMatcher.AppliesTo("Resource", "Bundle"));
        }

        [Theory]
        [InlineData(SubscriptionInteraction.Create, 1, false, MatchOutcome.Match)]
        [InlineData(SubscriptionInteraction.Create, 2, false, MatchOutcome.NoMatch)]
        [InlineData(SubscriptionInteraction.Update, 2, false, MatchOutcome.Match)]
        [InlineData(SubscriptionInteraction.Delete, 2, true, MatchOutcome.Match)]
        [InlineData(SubscriptionInteraction.Delete, 2, false, MatchOutcome.NoMatch)]
        public void GivenATriggerWithOneInteraction_WhenAChangeIsMatched_ThenOnlyThatInteractionMatches(SubscriptionInteraction interaction, int version, bool deleted, MatchOutcome expected)
        {
            // Arrange
            SubscriptionTopicInfo topic = Topic(new TopicResourceTrigger { Resource = "Encounter", SupportedInteractions = [interaction] });
            Encounter encounter = Enc("e1", EncounterStatus());
            Encounter previous = version > 1 ? Enc("e1", EncounterStatus(), version - 1) : null;
            ResourceChange change = SubscriptionTestFixture.Change(deleted ? null : encounter, previous, deleted, version);

            // Act
            MatchOutcome outcome = _matcher.Match(topic, [], change);

            // Assert
            Assert.Equal(expected, outcome);
        }

        [Fact]
        public void GivenAReCreateAfterDelete_WhenMatched_ThenItIsACreate()
        {
            ResourceChange change = SubscriptionTestFixture.Change(Enc("e1", EncounterStatus()), Enc("e1", EncounterStatus(), 1), version: 2);
            change.PreviousIsDeleted = true;

            Assert.Equal(SubscriptionInteraction.Create, change.Interaction);
            Assert.Equal(MatchOutcome.Match, _matcher.Match(Topic(new TopicResourceTrigger { Resource = "Encounter", SupportedInteractions = [SubscriptionInteraction.Create] }), [], change));
        }

        [Fact]
        public void GivenQueryCriteria_WhenTheStatusTransitions_ThenOnlyTheTransitionMatches()
        {
            // Arrange: "encounter finished" transitions from anything but finished to finished.
            SubscriptionTopicInfo topic = Topic(new TopicResourceTrigger
            {
                Resource = "Encounter",
                QueryCriteria = new TopicQueryCriteria { Previous = "status:not=cancelled", Current = "status=cancelled", RequireBoth = true, ResultForCreate = CriteriaNotExistsBehavior.TestPasses },
            });

            ResourceChange transition = SubscriptionTestFixture.Change(Enc("e1", Finished()), Enc("e1", InProgress(), 1));
            ResourceChange alreadyFinished = SubscriptionTestFixture.Change(Enc("e1", Finished()), Enc("e1", Finished(), 1));
            ResourceChange stillInProgress = SubscriptionTestFixture.Change(Enc("e1", InProgress()), Enc("e1", InProgress(), 1));
            ResourceChange createdFinished = SubscriptionTestFixture.Change(Enc("e1", Finished()));

            // Act + Assert
            Assert.Equal(MatchOutcome.Match, _matcher.Match(topic, [], transition));
            Assert.Equal(MatchOutcome.NoMatch, _matcher.Match(topic, [], alreadyFinished));
            Assert.Equal(MatchOutcome.NoMatch, _matcher.Match(topic, [], stillInProgress));
            Assert.Equal(MatchOutcome.Match, _matcher.Match(topic, [], createdFinished));
        }

        [Fact]
        public void GivenResultForCreateTestFails_WhenAResourceIsCreated_ThenItDoesNotMatch()
        {
            SubscriptionTopicInfo topic = Topic(new TopicResourceTrigger
            {
                Resource = "Encounter",
                QueryCriteria = new TopicQueryCriteria { Previous = "status:not=cancelled", Current = "status=cancelled", RequireBoth = true, ResultForCreate = CriteriaNotExistsBehavior.TestFails },
            });

            Assert.Equal(MatchOutcome.NoMatch, _matcher.Match(topic, [], SubscriptionTestFixture.Change(Enc("e1", Finished()))));
        }

        [Fact]
        public void GivenRequireBothFalse_WhenEitherCriterionPasses_ThenItMatches()
        {
            SubscriptionTopicInfo topic = Topic(new TopicResourceTrigger
            {
                Resource = "Encounter",
                QueryCriteria = new TopicQueryCriteria { Previous = "status=cancelled", Current = "status=cancelled", RequireBoth = false },
            });

            Assert.Equal(MatchOutcome.Match, _matcher.Match(topic, [], SubscriptionTestFixture.Change(Enc("e1", InProgress()), Enc("e1", Finished(), 1))));
            Assert.Equal(MatchOutcome.NoMatch, _matcher.Match(topic, [], SubscriptionTestFixture.Change(Enc("e1", InProgress()), Enc("e1", InProgress(), 1))));
        }

        [Fact]
        public void GivenPreviousCriteria_WhenThePreviousVersionIsUnavailable_ThenTheOutcomeIsUnevaluable()
        {
            SubscriptionTopicInfo topic = Topic(new TopicResourceTrigger
            {
                Resource = "Encounter",
                QueryCriteria = new TopicQueryCriteria { Previous = "status:not=cancelled", Current = "status=cancelled", RequireBoth = true },
            });
            ResourceChange change = SubscriptionTestFixture.Change(Enc("e1", Finished()), version: 2);

            Assert.Equal(MatchOutcome.Unevaluable, _matcher.Match(topic, [], change));
        }

        [Fact]
        public void GivenCurrentCriteria_WhenTheWrittenVersionIsUnavailable_ThenTheOutcomeIsUnevaluable()
        {
            SubscriptionTopicInfo topic = Topic(new TopicResourceTrigger { Resource = "Encounter", QueryCriteria = new TopicQueryCriteria { Current = "status=cancelled" } });
            ResourceChange change = SubscriptionTestFixture.Change(null, Enc("e1", Finished(), 1), version: 2);

            Assert.Equal(MatchOutcome.Unevaluable, _matcher.Match(topic, [], change));
        }

        [Fact]
        public void GivenFhirPathCriteria_WhenUsingPreviousAndCurrent_ThenBothVersionsAreAvailable()
        {
            SubscriptionTopicInfo topic = Topic(new TopicResourceTrigger
            {
                Resource = "Encounter",
                FhirPathCriteria = "%previous.status != 'cancelled' and %current.status = 'cancelled'",
            });

            Assert.Equal(MatchOutcome.Match, _matcher.Match(topic, [], SubscriptionTestFixture.Change(Enc("e1", Finished()), Enc("e1", InProgress(), 1))));
            Assert.Equal(MatchOutcome.NoMatch, _matcher.Match(topic, [], SubscriptionTestFixture.Change(Enc("e1", Finished()), Enc("e1", Finished(), 1))));
        }

        [Fact]
        public void GivenFhirPathCriteriaReturningANonBoolean_WhenEvaluated_ThenItDoesNotMatch()
        {
            SubscriptionTopicInfo topic = Topic(new TopicResourceTrigger { Resource = "Encounter", FhirPathCriteria = "status" });

            Assert.Equal(MatchOutcome.NoMatch, _matcher.Match(topic, [], SubscriptionTestFixture.Change(Enc("e1", Finished()))));
        }

        [Fact]
        public void GivenSubscriptionFilters_WhenMatching_ThenAllApplicableFiltersMustMatch()
        {
            SubscriptionTopicInfo topic = Topic(new TopicResourceTrigger { Resource = "Encounter" });
            IReadOnlyList<SubscriptionFilter> filters =
            [
                new SubscriptionFilter { FilterParameter = "subject", Value = "Patient/p1" },
                new SubscriptionFilter { ResourceType = "Observation", FilterParameter = "code", Value = "x" },
            ];

            Assert.Equal(MatchOutcome.Match, _matcher.Match(topic, filters, SubscriptionTestFixture.Change(Enc("e1", Finished(), patient: "p1"))));
            Assert.Equal(MatchOutcome.NoMatch, _matcher.Match(topic, filters, SubscriptionTestFixture.Change(Enc("e1", Finished(), patient: "p2"))));
        }

        [Fact]
        public void GivenADelete_WhenFiltered_ThenTheDeletedVersionIsUsed()
        {
            SubscriptionTopicInfo topic = Topic(new TopicResourceTrigger { Resource = "Encounter" });
            IReadOnlyList<SubscriptionFilter> filters = [new SubscriptionFilter { FilterParameter = "subject", Value = "Patient/p1" }];

            ResourceChange delete = SubscriptionTestFixture.Change(null, Enc("e1", Finished(), 1, "p1"), deleted: true, version: 2);
            ResourceChange otherPatient = SubscriptionTestFixture.Change(null, Enc("e1", Finished(), 1, "p2"), deleted: true, version: 2);
            ResourceChange unknown = SubscriptionTestFixture.Change(null, null, deleted: true, version: 2, type: "Encounter", id: "e1");

            Assert.Equal(MatchOutcome.Match, _matcher.Match(topic, filters, delete));
            Assert.Equal(MatchOutcome.NoMatch, _matcher.Match(topic, filters, otherPatient));
            Assert.Equal(MatchOutcome.Unevaluable, _matcher.Match(topic, filters, unknown));
        }

        [Fact]
        public void GivenSeveralTriggers_WhenOneMatches_ThenTheTopicMatches()
        {
            SubscriptionTopicInfo topic = Topic(
                new TopicResourceTrigger { Resource = "Encounter", QueryCriteria = new TopicQueryCriteria { Current = "status=cancelled" } },
                new TopicResourceTrigger { Resource = "Encounter", QueryCriteria = new TopicQueryCriteria { Current = "status=cancelled" } });

            Assert.Equal(MatchOutcome.Match, _matcher.Match(topic, [], SubscriptionTestFixture.Change(Enc("e1", Finished()))));
        }

        private static SubscriptionTopicInfo Topic(params TopicResourceTrigger[] triggers) => new() { Canonical = "http://example.org/topic", ResourceTriggers = triggers };

        private static string Finished() => "cancelled";

        private static string InProgress() => "in-progress";

        private static string EncounterStatus() => "in-progress";

        private static Encounter Enc(string id, string status, int version = 1, string patient = "p1")
        {
            string json = $"{{\"resourceType\":\"Encounter\",\"id\":\"{id}\",\"meta\":{{\"versionId\":\"{version}\"}},\"status\":\"{status}\",\"class\":{EncounterClass()},\"subject\":{{\"reference\":\"Patient/{patient}\"}}}}";
            return new Hl7.Fhir.Serialization.FhirJsonParser().Parse<Encounter>(json);
        }

#if R5
        private static string EncounterClass() => "[{\"coding\":[{\"system\":\"http://terminology.hl7.org/CodeSystem/v3-ActCode\",\"code\":\"AMB\"}]}]";
#else
        private static string EncounterClass() => "{\"system\":\"http://terminology.hl7.org/CodeSystem/v3-ActCode\",\"code\":\"AMB\"}";
#endif
    }
}
