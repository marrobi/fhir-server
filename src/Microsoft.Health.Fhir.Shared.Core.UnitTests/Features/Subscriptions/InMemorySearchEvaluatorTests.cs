// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using Hl7.Fhir.Model;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class InMemorySearchEvaluatorTests : IClassFixture<SubscriptionTestFixture>
    {
        private readonly InMemorySearchEvaluator _evaluator;

        public InMemorySearchEvaluatorTests(SubscriptionTestFixture fixture)
        {
            _evaluator = fixture.SearchEvaluator;
        }

        public static IEnumerable<object[]> MatchingCases()
        {
            yield return ["code", "http://loinc.org|1234-5", true];
            yield return ["code", "1234-5", true];
            yield return ["code", "http://other|1234-5", false];
            yield return ["code", "http://loinc.org|", true];
            yield return ["code:not", "1234-5", false];
            yield return ["code:not", "9999-9", true];
            yield return ["code", "9999-9,1234-5", true];
            yield return ["status", "final", true];
            yield return ["status", "preliminary", false];
            yield return ["subject", "Patient/p1", true];
            yield return ["subject", "Patient/p2", false];
            yield return ["subject:Patient", "p1", true];
            yield return ["value-quantity", "gt5", true];
            yield return ["value-quantity", "lt5", false];
            yield return ["value-quantity", "7.5", true];
            yield return ["value-quantity", "7.5|http://unitsofmeasure.org|mg", true];
            yield return ["value-quantity", "7.5|http://unitsofmeasure.org|g", false];
            yield return ["date", "ge2020-01-01", true];
            yield return ["date", "lt2020-01-01", false];
            yield return ["date", "2021-06", true];
            yield return ["_id", "o1", true];
            yield return ["_id", "o2", false];
            yield return ["performer:missing", "true", true];
            yield return ["subject:missing", "true", false];
        }

        [Theory]
        [MemberData(nameof(MatchingCases))]
        public void GivenAnObservation_WhenEvaluatingSupportedCriteria_ThenTheResultMatchesSearchSemantics(string key, string value, bool expected)
        {
            // Arrange
            ResourceSnapshot snapshot = SubscriptionTestFixture.Snapshot(CreateObservation());

            // Act
            bool result = _evaluator.Matches(snapshot, _evaluator.Parse("Observation", [new KeyValuePair<string, string>(key, value)]));

            // Assert
            Assert.Equal(expected, result);
        }

        [Fact]
        public void GivenMultipleParameters_WhenOneDoesNotMatch_ThenTheResourceDoesNotMatch()
        {
            ResourceSnapshot snapshot = SubscriptionTestFixture.Snapshot(CreateObservation());

            bool result = _evaluator.Matches(snapshot, _evaluator.Parse("Observation", [new("status", "final"), new("subject", "Patient/other")]));

            Assert.False(result);
        }

        [Fact]
        public void GivenAStringParameter_WhenMatching_ThenItUsesCaseAndAccentInsensitivePrefixMatching()
        {
            ResourceSnapshot patient = SubscriptionTestFixture.Snapshot(new Patient { Id = "p1", Name = [new HumanName { Family = "Müller" }] });

            Assert.True(_evaluator.Matches(patient, _evaluator.Parse("Patient", [new("family", "mul")])));
            Assert.True(_evaluator.Matches(patient, _evaluator.Parse("Patient", [new("family:contains", "LLE")])));
            Assert.False(_evaluator.Matches(patient, _evaluator.Parse("Patient", [new("family:exact", "muller")])));
            Assert.True(_evaluator.Matches(patient, _evaluator.Parse("Patient", [new("family:exact", "Müller")])));
        }

        [Theory]
        [InlineData("subject.name", "x")]
        [InlineData("_has:Observation:subject:code", "1234")]
        [InlineData("code:in", "http://example.org/vs")]
        [InlineData("code:below", "1234")]
        [InlineData("_text", "x")]
        [InlineData("_count", "1")]
        [InlineData("_include", "Observation:subject")]
        [InlineData("not-a-parameter", "x")]
        [InlineData("code-value-quantity", "1234$gt5")]
        public void GivenCriteriaThatNeedADatabaseQuery_WhenParsing_ThenTheyAreRejected(string key, string value)
        {
            Assert.Throws<SubscriptionCriteriaException>(() => _evaluator.Parse("Observation", [new KeyValuePair<string, string>(key, value)]));
        }

        [Fact]
        public void GivenAResourceInACompartment_WhenCheckingMembership_ThenOnlyItsOwnCompartmentMatches()
        {
            ResourceSnapshot observation = SubscriptionTestFixture.Snapshot(CreateObservation());
            ResourceSnapshot patient = SubscriptionTestFixture.Snapshot(new Patient { Id = "p1" });

            Assert.True(_evaluator.IsInCompartment(observation, "Patient", "p1"));
            Assert.False(_evaluator.IsInCompartment(observation, "Patient", "p2"));
            Assert.True(_evaluator.IsInCompartment(patient, "Patient", "p1"));
            Assert.False(_evaluator.IsInCompartment(patient, "Patient", "p2"));
        }

        internal static Observation CreateObservation() => new()
        {
            Id = "o1",
            Meta = new Meta { VersionId = "1", LastUpdated = new System.DateTimeOffset(2026, 1, 1, 0, 0, 0, System.TimeSpan.Zero) },
            Status = ObservationStatus.Final,
            Code = new CodeableConcept("http://loinc.org", "1234-5"),
            Subject = new ResourceReference("Patient/p1"),
            Effective = new FhirDateTime("2021-06-15"),
            Value = new Quantity(7.5m, "mg"),
        };
    }
}
