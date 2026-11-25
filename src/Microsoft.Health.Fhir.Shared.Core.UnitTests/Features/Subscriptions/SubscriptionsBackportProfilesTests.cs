// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

#if !Stu3
using System.Linq;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Validation;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SubscriptionsBackportProfilesTests
    {
#if R4
        private const string TopicUrl = "http://example.org/fhir/SubscriptionTopic/encounter-cancelled";

        [Fact]
        public void GivenR4OrR4B_WhenBuiltInBackportResourcesAreLoaded_ThenTheAdvertisedProfileAndWhatItUsesAreIncludedWithSnapshots()
        {
            // Arrange
            string[] expected =
            [
                SubscriptionsBackport.SubscriptionProfile,
                SubscriptionsBackport.FilterCriteria,
                SubscriptionsBackport.HeartbeatPeriod,
                SubscriptionsBackport.Timeout,
                SubscriptionsBackport.MaxCount,
                SubscriptionsBackport.ChannelType,
                SubscriptionsBackport.PayloadContent,
            ];

            // Act
            var definitions = SubscriptionsBackportProfiles.All.OfType<StructureDefinition>().ToDictionary(d => d.Url);

            // Assert
            Assert.All(expected, url => Assert.True(definitions.ContainsKey(url), $"{url} is not built in."));
            Assert.All(definitions.Values, d => Assert.True(d.HasSnapshot, $"{d.Url} has no snapshot."));
            Assert.Contains(SubscriptionsBackportProfiles.All, r => r is ValueSet);
            Assert.Contains(SubscriptionsBackportProfiles.All, r => r is CodeSystem);
        }

        [Fact]
        public void GivenASubscriptionClaimingTheAdvertisedProfile_WhenValidated_ThenThereAreNoErrors()
        {
            OperationOutcomeIssue[] issues = Validate(SubscriptionResources.Subscription(TopicUrl, "https://hooks.example.com/notify", filter: "Encounter?patient=Patient/123"));

            Assert.DoesNotContain(issues, IsError);
        }

        [Fact]
        public void GivenASubscriptionWithAPayloadContentOutsideTheValueSet_WhenValidated_ThenAnErrorIsReported()
        {
            OperationOutcomeIssue[] issues = Validate(SubscriptionResources.Subscription(TopicUrl, "https://hooks.example.com/notify", content: "everything"));

            Assert.Contains(issues, IsError);
        }

        private static OperationOutcomeIssue[] Validate(string json)
        {
            var validator = new ProfileValidator(
                Substitute.For<IProvideProfilesForValidation>(),
                Options.Create(new ValidateOperationConfiguration()),
                NullLogger<ProfileValidator>.Instance,
                ModelInfoProvider.Instance);
            ITypedElement subscription = new FhirJsonParser().Parse<Resource>(json).ToTypedElement();
            return validator.TryValidate(subscription, SubscriptionsBackport.SubscriptionProfile);
        }

        private static bool IsError(OperationOutcomeIssue issue) => issue.Severity is "Error" or "Fatal";
#else
        [Fact]
        public void GivenR5_WhenBuiltInBackportResourcesAreLoaded_ThenThereAreNone()
        {
            Assert.Empty(SubscriptionsBackportProfiles.All);
        }
#endif
    }
}
#endif
