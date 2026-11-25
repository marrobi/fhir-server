// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

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
    public class SubscriptionTopicProfileTests
    {
        private const string TopicUrl = "http://example.org/fhir/SubscriptionTopic/encounter-cancelled";

        [Fact]
        public void GivenTheServerFhirVersion_WhenBuiltInProfilesAreLoaded_ThenOnlyR4ShipsTheTopicProfile()
        {
#if R4 && !R4B
            StructureDefinition profile = Assert.Single(SubscriptionTopicProfile.All);
            Assert.Equal(SubscriptionsBackport.R4TopicProfile, profile.Url);
            Assert.Equal("Basic", profile.Type);
#else
            Assert.Empty(SubscriptionTopicProfile.All);
#endif
        }

#if R4 && !R4B
        [Fact]
        public void GivenATopicAcceptedByTheServer_WhenValidatedAgainstTheProfile_ThenThereAreNoErrors()
        {
            OperationOutcomeIssue[] issues = Validate(SubscriptionResources.Topic(TopicUrl));

            Assert.DoesNotContain(issues, IsError);
        }

        [Theory]
        [InlineData("missing-url")]
        [InlineData("event-trigger")]
        [InlineData("wrong-code")]
        [InlineData("trigger-without-resource")]
        [InlineData("status-not-a-modifier")]
        public void GivenAnInvalidTopic_WhenValidatedAgainstTheProfile_ThenErrorsAreReported(string problem)
        {
            var basic = (Basic)new FhirJsonParser().Parse<Resource>(SubscriptionResources.Topic(TopicUrl));
            switch (problem)
            {
                case "missing-url":
                    basic.Extension.RemoveAll(e => e.Url == SubscriptionsBackport.TopicExtensionPrefix + "url");
                    break;
                case "event-trigger":
                    basic.Extension.Add(new Extension(SubscriptionsBackport.TopicBackboneExtensionPrefix + "eventTrigger", null) { Extension = [new Extension("event", new CodeableConcept("http://example.org", "x"))] });
                    break;
                case "wrong-code":
                    basic.Code = new CodeableConcept("http://example.org", "not-a-topic");
                    break;
                case "status-not-a-modifier":
                    basic.Extension.AddRange(basic.ModifierExtension);
                    basic.ModifierExtension.Clear();
                    break;
                default:
                    basic.Extension.First(e => e.Url == SubscriptionsBackport.TopicBackboneExtensionPrefix + "resourceTrigger").Extension.RemoveAll(e => e.Url == "resource");
                    break;
            }

            OperationOutcomeIssue[] issues = Validate(new FhirJsonSerializer().SerializeToString(basic));

            Assert.Contains(issues, IsError);
        }

        private static OperationOutcomeIssue[] Validate(string json)
        {
            var validator = new ProfileValidator(
                Substitute.For<IProvideProfilesForValidation>(),
                Options.Create(new ValidateOperationConfiguration()),
                NullLogger<ProfileValidator>.Instance,
                ModelInfoProvider.Instance);
            ITypedElement topic = new FhirJsonParser().Parse<Resource>(json).ToTypedElement();
            return validator.TryValidate(topic, SubscriptionsBackport.R4TopicProfile);
        }

        private static bool IsError(OperationOutcomeIssue issue) => issue.Severity is "Error" or "Fatal";
#endif
    }
}
