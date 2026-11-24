// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

#if R4 || R4B
using System;
using System.Collections.Generic;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Hl7.Fhir.ElementModel;
using Hl7.Fhir.Model;
using Hl7.Fhir.Serialization;
using Hl7.Fhir.Specification.Source;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Validation;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using NSubstitute;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    /// <summary>
    /// Validates notification bundles against the Subscriptions R5 Backport IG 1.1.0 profiles, loaded from the published package.
    /// </summary>
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public class SubscriptionNotificationProfileTests
    {
#if R4 && !R4B
        private const string NotificationProfile = SubscriptionsBackport.Base + "backport-subscription-notification-r4";
#else
        private const string NotificationProfile = SubscriptionsBackport.Base + "backport-subscription-notification";
#endif

        private static readonly Lazy<IReadOnlyList<Resource>> PackageResources = new(LoadPackage);

        private static readonly Lazy<ProfileValidator> Validator = new(CreateValidator);

        private readonly SubscriptionNotificationFactory _factory = new();

        public static TheoryData<SubscriptionNotificationType, SubscriptionContentLevel, int> Notifications => new()
        {
            { SubscriptionNotificationType.Handshake, SubscriptionContentLevel.IdOnly, 0 },
            { SubscriptionNotificationType.Heartbeat, SubscriptionContentLevel.IdOnly, 0 },
            { SubscriptionNotificationType.EventNotification, SubscriptionContentLevel.Empty, 2 },
            { SubscriptionNotificationType.EventNotification, SubscriptionContentLevel.IdOnly, 2 },
            { SubscriptionNotificationType.EventNotification, SubscriptionContentLevel.FullResource, 2 },
        };

        [Fact]
        public void GivenTheBackportPackage_WhenLoaded_ThenTheNotificationProfilesAreAvailable()
        {
            Assert.Contains(PackageResources.Value, r => r is StructureDefinition sd && sd.Url == NotificationProfile);
            Assert.Contains(PackageResources.Value, r => r is StructureDefinition sd && sd.Url == SubscriptionsBackport.StatusR4Profile);
        }

        [Theory]
        [MemberData(nameof(Notifications))]
        public void GivenANotification_WhenValidatedAgainstTheBackportProfile_ThenThereAreNoErrors(SubscriptionNotificationType type, SubscriptionContentLevel content, int events)
        {
            // Arrange
            Bundle bundle = Build(type, content, events, errors: []);

            // Act
            OperationOutcomeIssue[] issues = Validator.Value.TryValidate(bundle.ToTypedElement(), NotificationProfile);

            // Assert
            Assert.True(!issues.Any(IsError), Describe(issues));
        }

        [Fact]
        public void GivenANotificationWithErrors_WhenValidatedAgainstTheBackportProfile_ThenThereAreNoErrors()
        {
            Bundle bundle = Build(SubscriptionNotificationType.Heartbeat, SubscriptionContentLevel.IdOnly, 0, errors: ["endpoint returned 500"]);

            OperationOutcomeIssue[] issues = Validator.Value.TryValidate(bundle.ToTypedElement(), NotificationProfile);

            Assert.True(!issues.Any(IsError), Describe(issues));
        }

#if R4 && !R4B
        [Fact]
        public void GivenAStatusResource_WhenValidatedAgainstTheStatusProfile_ThenThereAreNoErrors()
        {
            Bundle bundle = Build(SubscriptionNotificationType.EventNotification, SubscriptionContentLevel.IdOnly, 2, errors: []);

            OperationOutcomeIssue[] issues = Validator.Value.TryValidate(bundle.Entry[0].Resource.ToTypedElement(), SubscriptionsBackport.StatusR4Profile);

            Assert.True(!issues.Any(IsError), Describe(issues));
        }
#endif

        [Fact]
        public void GivenANotificationWhoseFirstEntryIsNotTheStatus_WhenValidated_ThenTheProfileReportsAnError()
        {
            // A control, so that a validator that resolves nothing cannot pass the tests above.
            Bundle bundle = Build(SubscriptionNotificationType.EventNotification, SubscriptionContentLevel.IdOnly, 2, errors: []);
            bundle.Entry.Reverse();

            OperationOutcomeIssue[] issues = Validator.Value.TryValidate(bundle.ToTypedElement(), NotificationProfile);

            Assert.Contains(issues, IsError);
        }

        private Bundle Build(SubscriptionNotificationType type, SubscriptionContentLevel content, int events, string[] errors)
        {
            var request = new SubscriptionNotificationRequest
            {
                Subscription = new SubscriptionInfo { Id = "s1", TopicCanonical = "http://example.org/topic", Content = content, Status = SubscriptionStatusCode.Active },
                Type = type,
                Status = SubscriptionStatusCode.Active,
                EventsSinceSubscriptionStart = 8,
                Events = Enumerable.Range(1, events).Select(i => new SubscriptionNotificationEvent
                {
                    EventNumber = 6 + i,
                    Timestamp = DateTimeOffset.UtcNow,
                    ResourceType = "Encounter",
                    ResourceId = "e" + i,
                    ResourceVersion = i,
                    Interaction = i == 1 ? SubscriptionInteraction.Create : SubscriptionInteraction.Update,
                    Resource = new Encounter
                    {
                        Id = "e" + i,
                        Meta = new Meta { VersionId = i.ToString(System.Globalization.CultureInfo.InvariantCulture) },
                        Status = Encounter.EncounterStatus.Cancelled,
                        Class = new Coding("http://terminology.hl7.org/CodeSystem/v3-ActCode", "AMB"),
                    }.ToResourceElement(),
                }).ToArray(),
                Errors = errors,
                BaseUri = new Uri("https://fhir.example/"),
            };

            string json = _factory.Serialize(_factory.CreateNotification(request), "application/fhir+json");
            return new FhirJsonParser().Parse<Bundle>(json);
        }

        private static ProfileValidator CreateValidator()
        {
            var package = new InMemoryResourceResolver(PackageResources.Value);
            IProvideProfilesForValidation profiles = Substitute.For<IProvideProfilesForValidation>();
            profiles.ResolveByCanonicalUriAsync(Arg.Any<string>()).Returns(call => System.Threading.Tasks.Task.FromResult(package.ResolveByCanonicalUri(call.Arg<string>())));
            profiles.ResolveByUriAsync(Arg.Any<string>()).Returns(call => System.Threading.Tasks.Task.FromResult(package.ResolveByUri(call.Arg<string>())));

            return new ProfileValidator(profiles, Options.Create(new ValidateOperationConfiguration()), NullLogger<ProfileValidator>.Instance, ModelInfoProvider.Instance);
        }

        private static List<Resource> LoadPackage()
        {
            byte[] tarball = EmbeddedResourceManager.GetBytesContent("TestFiles", "FhirPackage-SubscriptionsBackport", "tgz");
            using var gzip = new GZipStream(new MemoryStream(tarball), CompressionMode.Decompress);
            using var tar = new TarReader(gzip);
            var parser = new FhirJsonParser();
            var resources = new List<Resource>();
            while (tar.GetNextEntry() is TarEntry entry)
            {
                string name = Path.GetFileName(entry.Name);
                bool conformance = entry.Name == "package/" + name && name.EndsWith(".json", StringComparison.Ordinal) &&
                    (name.StartsWith("StructureDefinition-", StringComparison.Ordinal) || name.StartsWith("ValueSet-", StringComparison.Ordinal) || name.StartsWith("CodeSystem-", StringComparison.Ordinal));
                if (entry.DataStream == null || !conformance)
                {
                    continue;
                }

                using var reader = new StreamReader(entry.DataStream);
                resources.Add(parser.Parse<Resource>(reader.ReadToEnd()));
            }

            return resources;
        }

        private static bool IsError(OperationOutcomeIssue issue) => issue.Severity is "Error" or "Fatal";

        private static string Describe(OperationOutcomeIssue[] issues) =>
            string.Join(Environment.NewLine, issues.Where(IsError).Select(i => $"{i.Severity} {string.Join(",", i.Expression ?? [])}: {i.Diagnostics} {i.DetailsText}"));
    }
}
#endif
