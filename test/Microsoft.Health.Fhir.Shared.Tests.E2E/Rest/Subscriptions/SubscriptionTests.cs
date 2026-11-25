// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

#if !Stu3
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Client;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.Tests.Common.FixtureParameters;
using Microsoft.Health.Fhir.Tests.E2E.Common;
using Microsoft.Health.Test.Utilities;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.Health.Fhir.Tests.E2E.Rest.Subscriptions
{
    /// <summary>
    /// End-to-end tests for topic-based subscriptions with rest-hook delivery to a local receiver.
    /// The topic matches Encounter changes whose new status is <c>cancelled</c> and allows filtering by <c>subject</c>.
    /// </summary>
    [Trait(Traits.Category, Categories.Subscriptions)]
    [HttpIntegrationFixtureArgumentSets(DataStore.SqlServer, Format.Json)]
    public class SubscriptionTests : IClassFixture<SubscriptionsTestFixture>
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);
        private static readonly TimeSpan QuietPeriod = TimeSpan.FromSeconds(6);
        private static readonly Regex FocusPattern = new("[A-Z][A-Za-z]+/[0-9a-f]{32}", RegexOptions.Compiled);

#if R4B
        // R4B's canFilterBy.modifier binding (SubscriptionSearchModifier) has no "not"; an empty list allows any modifier.
        private static readonly string[] StatusModifiers = [];
        private static readonly string[] RestrictedStatusModifiers = ["in"];
#else
        private static readonly string[] StatusModifiers = ["not"];
        private static readonly string[] RestrictedStatusModifiers = ["not"];
#endif

        private readonly SubscriptionsTestFixture _fixture;
        private readonly HttpClient _client;

#if R4 && !R4B
        private const string TopicType = "Basic";
#else
        private const string TopicType = "SubscriptionTopic";
#endif

#if R5
        private const string NotificationBundleType = "subscription-notification";
#else
        private const string NotificationBundleType = "history";
#endif

        public SubscriptionTests(SubscriptionsTestFixture fixture)
        {
            _fixture = fixture;
            _client = fixture.TestFhirClient.HttpClient;
        }

        private WebhookReceiver Webhook => _fixture.Webhook;

        [Fact]
        public async Task GivenAnIdOnlySubscription_WhenAMatchingChangeOccurs_ThenHandshakeActivatesAndOneNotificationIsDelivered()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled and a local receiver.");
            string path = NewPath();
            string topicUrl = await CreateTopicAsync();
            string patient = Guid.NewGuid().ToString("N");

            (HttpStatusCode code, JObject subscription) = await SendAsync(HttpMethod.Post, "Subscription", SubscriptionResources.Subscription(topicUrl, Webhook.BaseUrl + path, filter: $"Encounter?subject=Patient/{patient}"));
            AssertStatus(HttpStatusCode.Created, code, subscription);
            Assert.Equal("requested", (string)subscription["status"]);
            Assert.NotNull(subscription["end"]);
            string subscriptionId = (string)subscription["id"];

            try
            {
                ReceivedNotification handshake = await Webhook.WaitForAsync(path, n => n.Body.Contains("\"handshake\"", StringComparison.Ordinal), Timeout);
                Assert.Equal("Bearer secret", handshake.Authorization);
                Assert.StartsWith("application/fhir+json", handshake.ContentType, StringComparison.Ordinal);
                await WaitForStatusAsync(subscriptionId, "active");

                string encounterId = Guid.NewGuid().ToString("N");
                await PutEncounterAsync(encounterId, patient, "planned");
                await PutEncounterAsync(encounterId, patient, "cancelled");
                await PutEncounterAsync(encounterId, patient, "cancelled");

                ReceivedNotification notification = await Webhook.WaitForAsync(path, IsEvent, Timeout);
                await Task.Delay(QuietPeriod);
                Assert.Single(Webhook.For(path), IsEvent);

                var bundle = JObject.Parse(notification.Body);
                Assert.Equal(NotificationBundleType, (string)bundle["type"]);
                JToken focusEntry = bundle["entry"].Skip(1).Single();
                Assert.EndsWith($"Encounter/{encounterId}", (string)focusEntry["fullUrl"], StringComparison.Ordinal);
                Assert.Null(focusEntry["resource"]);
                Assert.Equal("1", EventNumbers(bundle).Single());

                (code, JObject statusBundle) = await SendAsync(HttpMethod.Get, $"Subscription/{subscriptionId}/$status");
                Assert.Equal(HttpStatusCode.OK, code);
                Assert.Equal("searchset", (string)statusBundle["type"]);
                Assert.Equal("1", StatusValue(statusBundle, "eventsSinceSubscriptionStart", "events-since-subscription-start"));
                Assert.Equal("active", StatusValue(statusBundle, "status", "status"));
            }
            finally
            {
                await SendAsync(HttpMethod.Delete, $"Subscription/{subscriptionId}");
            }
        }

        [Fact]
        public async Task GivenAFullResourceSubscriptionWithAFilter_WhenEncountersChange_ThenOnlyTheFilteredEncounterIsDeliveredWithItsResource()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled and a local receiver.");
            string path = NewPath();
            string topicUrl = await CreateTopicAsync();
            string patient = Guid.NewGuid().ToString("N");
            string otherPatient = Guid.NewGuid().ToString("N");

            string subscriptionId = await CreateActiveSubscriptionAsync(topicUrl, path, "full-resource", $"Encounter?subject=Patient/{patient}");
            try
            {
                string otherEncounter = Guid.NewGuid().ToString("N");
                await PutEncounterAsync(otherEncounter, otherPatient, "cancelled");
                string encounterId = Guid.NewGuid().ToString("N");
                await PutEncounterAsync(encounterId, patient, "cancelled");

                ReceivedNotification notification = await Webhook.WaitForAsync(path, IsEvent, Timeout);
                await Task.Delay(QuietPeriod);
                Assert.Single(Webhook.For(path), IsEvent);
                Assert.DoesNotContain(otherEncounter, notification.Body, StringComparison.Ordinal);

                JToken resource = JObject.Parse(notification.Body)["entry"].Skip(1).Single()["resource"];
                Assert.Equal("Encounter", (string)resource["resourceType"]);
                Assert.Equal(encounterId, (string)resource["id"]);
                Assert.Equal("cancelled", (string)resource["status"]);
            }
            finally
            {
                await SendAsync(HttpMethod.Delete, $"Subscription/{subscriptionId}");
            }
        }

        [Fact]
        public async Task GivenAnActiveSubscription_WhenTheEndpointChangesAndItIsThenDeleted_ThenANewHandshakeIsRequiredAndNotificationsStop()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled and a local receiver.");
            string path = NewPath();
            string newPath = NewPath();
            string topicUrl = await CreateTopicAsync();
            string patient = Guid.NewGuid().ToString("N");
            string subscriptionId = await CreateActiveSubscriptionAsync(topicUrl, path, "id-only", $"Encounter?subject=Patient/{patient}");

            (HttpStatusCode code, JObject current) = await SendAsync(HttpMethod.Get, $"Subscription/{subscriptionId}");
            Assert.Equal(HttpStatusCode.OK, code);
            SetEndpoint(current, Webhook.BaseUrl + newPath);

            (code, JObject updated) = await SendAsync(HttpMethod.Put, $"Subscription/{subscriptionId}", current.ToString());
            Assert.Equal(HttpStatusCode.OK, code);
            Assert.Equal("requested", (string)updated["status"]);
            await Webhook.WaitForAsync(newPath, n => n.Body.Contains("\"handshake\"", StringComparison.Ordinal), Timeout);
            await WaitForStatusAsync(subscriptionId, "active");

            (code, _) = await SendAsync(HttpMethod.Delete, $"Subscription/{subscriptionId}");
            Assert.Equal(HttpStatusCode.NoContent, code);

            // Allow the registry to observe the delete before the change.
            await Task.Delay(TimeSpan.FromSeconds(3));
            await PutEncounterAsync(Guid.NewGuid().ToString("N"), patient, "cancelled");
            await Task.Delay(QuietPeriod);

            Assert.DoesNotContain(Webhook.For(path), IsEvent);
            Assert.DoesNotContain(Webhook.For(newPath), IsEvent);
        }

        [Fact]
        public async Task GivenAnEmptyContentSubscription_WhenAMatchingChangeOccurs_ThenTheNotificationHasOnlyTheStatusWithoutFocus()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled and a local receiver.");
            string path = NewPath();
            string topicUrl = await CreateTopicAsync();
            string patient = Guid.NewGuid().ToString("N");
            string subscriptionId = await CreateActiveSubscriptionAsync(topicUrl, path, "empty", $"Encounter?subject=Patient/{patient}");

            try
            {
                string encounterId = Guid.NewGuid().ToString("N");
                await PutEncounterAsync(encounterId, patient, "cancelled");

                ReceivedNotification notification = await Webhook.WaitForAsync(path, IsEvent, Timeout);

                var bundle = JObject.Parse(notification.Body);
                Assert.Equal(NotificationBundleType, (string)bundle["type"]);
                Assert.Single(bundle["entry"]);
                Assert.Equal("1", EventNumbers(bundle).Single());
                Assert.DoesNotContain("focus", notification.Body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(encounterId, notification.Body, StringComparison.Ordinal);
            }
            finally
            {
                await SendAsync(HttpMethod.Delete, $"Subscription/{subscriptionId}");
            }
        }

        [Fact]
        public async Task GivenAnXmlSubscription_WhenItIsActivatedAndAChangeMatches_ThenHandshakeAndNotificationAreFhirXml()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled and a local receiver.");
            string path = NewPath();
            string topicUrl = await CreateTopicAsync();
            string patient = Guid.NewGuid().ToString("N");

            (HttpStatusCode code, JObject subscription) = await SendAsync(
                HttpMethod.Post,
                "Subscription",
                SubscriptionResources.Subscription(topicUrl, Webhook.BaseUrl + path, content: "full-resource", filter: $"Encounter?subject=Patient/{patient}", contentType: "application/fhir+xml"));
            AssertStatus(HttpStatusCode.Created, code, subscription);
            string subscriptionId = (string)subscription["id"];

            try
            {
                ReceivedNotification handshake = await Webhook.WaitForAsync(path, n => n.Body.Contains("handshake", StringComparison.Ordinal), Timeout);
                Assert.StartsWith("application/fhir+xml", handshake.ContentType, StringComparison.Ordinal);
                Assert.Equal(NotificationBundleType, Hl7.Fhir.Utility.EnumUtility.GetLiteral(ParseXml(handshake.Body).Type));
                await WaitForStatusAsync(subscriptionId, "active");

                string encounterId = Guid.NewGuid().ToString("N");
                await PutEncounterAsync(encounterId, patient, "cancelled");

                ReceivedNotification notification = await Webhook.WaitForAsync(path, n => n.Body.Contains("event-notification", StringComparison.Ordinal), Timeout);
                Assert.StartsWith("application/fhir+xml", notification.ContentType, StringComparison.Ordinal);
                Hl7.Fhir.Model.Bundle bundle = ParseXml(notification.Body);
                Assert.Equal(NotificationBundleType, Hl7.Fhir.Utility.EnumUtility.GetLiteral(bundle.Type));
                Hl7.Fhir.Model.Bundle.EntryComponent focus = bundle.Entry.Skip(1).Single();
                Assert.EndsWith($"Encounter/{encounterId}", focus.FullUrl, StringComparison.Ordinal);
                Assert.Equal(encounterId, focus.Resource.Id);
            }
            finally
            {
                await SendAsync(HttpMethod.Delete, $"Subscription/{subscriptionId}");
            }
        }

        [Fact]
        public async Task GivenAFailingEndpoint_WhenTheSubscriptionIsCreated_ThenItMovesToErrorAndStatusReportsTheFailure()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled and a local receiver.");
            string topicUrl = await CreateTopicAsync();

            (HttpStatusCode code, JObject subscription) = await SendAsync(HttpMethod.Post, "Subscription", SubscriptionResources.Subscription(topicUrl, Webhook.BaseUrl + "/fail" + NewPath()));
            AssertStatus(HttpStatusCode.Created, code, subscription);
            string subscriptionId = (string)subscription["id"];

            try
            {
                await WaitForStatusAsync(subscriptionId, "error");

                (code, JObject statusBundle) = await SendAsync(HttpMethod.Get, $"Subscription/$status?id={subscriptionId}");
                Assert.Equal(HttpStatusCode.OK, code);
                Assert.Equal("error", StatusValue(statusBundle, "status", "status"));
                Assert.Contains("500", statusBundle.ToString(), StringComparison.Ordinal);
            }
            finally
            {
                await SendAsync(HttpMethod.Delete, $"Subscription/{subscriptionId}");
            }
        }

        [Fact]
        public async Task GivenASubscriptionCreatedAsOff_WhenItIsStored_ThenItStaysOffAndNoHandshakeIsSent()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled and a local receiver.");
            string path = NewPath();
            string topicUrl = await CreateTopicAsync();

            (HttpStatusCode code, JObject subscription) = await SendAsync(HttpMethod.Post, "Subscription", SubscriptionResources.Subscription(topicUrl, Webhook.BaseUrl + path, status: "off"));
            AssertStatus(HttpStatusCode.Created, code, subscription);
            Assert.Equal("off", (string)subscription["status"]);

            try
            {
                await Task.Delay(QuietPeriod);
                Assert.Empty(Webhook.For(path));
                (_, JObject current) = await SendAsync(HttpMethod.Get, $"Subscription/{subscription["id"]}");
                Assert.Equal("off", (string)current["status"]);
            }
            finally
            {
                await SendAsync(HttpMethod.Delete, $"Subscription/{subscription["id"]}");
            }
        }

        [Theory]
        [InlineData("unknown-topic", "is not defined on this server")]
        [InlineData("ftp-endpoint", null)]
        [InlineData("parameter-not-in-can-filter-by", "The filter parameter 'class' is not allowed")]
        [InlineData("modifier-not-allowed", "The modifier 'missing' is not allowed")]
        [InlineData("resource-type-not-allowed", "The filter parameter 'status' is not allowed")]
        public async Task GivenAnInvalidSubscription_WhenCreated_ThenBadRequestIsReturned(string problem, string reason)
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled.");
            string topicUrl = await CreateTopicAsync(
                [new TopicTrigger("Encounter", ["update"])],
                [("Encounter", "subject"), new TopicFilter("Encounter", "status") { Modifiers = RestrictedStatusModifiers }]);
            string body = problem switch
            {
                "unknown-topic" => SubscriptionResources.Subscription($"http://example.org/topics/missing-{Guid.NewGuid():N}", Webhook.BaseUrl + NewPath()),
                "ftp-endpoint" => SubscriptionResources.Subscription(topicUrl, "ftp://example.org/hook"),
                "parameter-not-in-can-filter-by" => SubscriptionResources.Subscription(topicUrl, Webhook.BaseUrl + NewPath(), filter: "Encounter?class=AMB"),
                "modifier-not-allowed" => SubscriptionResources.Subscription(topicUrl, Webhook.BaseUrl + NewPath(), filter: "Encounter?status:missing=true"),
                _ => SubscriptionResources.Subscription(topicUrl, Webhook.BaseUrl + NewPath(), filter: "Observation?status=final"),
            };

            (HttpStatusCode code, JObject outcome) = await SendAsync(HttpMethod.Post, "Subscription", body);

            Assert.Equal(HttpStatusCode.BadRequest, code);
            Assert.Equal("OperationOutcome", (string)outcome["resourceType"]);
            if (reason != null)
            {
                Assert.Contains(reason, Diagnostics(outcome), StringComparison.Ordinal);
            }
        }

        [Theory]
        [InlineData("chained", "is not supported in subscription criteria")]
        [InlineData("reverse-chained", "is not supported in subscription criteria")]
        [InlineData("terminology-modifier", "is not supported in subscription criteria")]
        [InlineData("criteria-for-another-type", "does not apply to resource type")]
        [InlineData("invalid-fhirpath", "is not valid")]
        [InlineData("unknown-resource-type", "is not a known resource type")]
        [InlineData("unknown-filter-parameter", "is not a search parameter")]
        [InlineData("event-trigger", "event triggers are not supported")]
        public async Task GivenATopicTheServerCannotEvaluate_WhenCreated_ThenBadRequestIsReturned(string problem, string reason)
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled.");
            TopicTrigger trigger = problem switch
            {
                "chained" => new TopicTrigger("Encounter", ["update"]) { Current = "subject:Patient.name=x" },
                "reverse-chained" => new TopicTrigger("Encounter", ["update"]) { Current = "_has:Observation:encounter:status=final" },
                "terminology-modifier" => new TopicTrigger("Encounter", ["update"]) { Current = "type:in=http://example.org/vs" },
                "criteria-for-another-type" => new TopicTrigger("Encounter", ["update"]) { Current = "Patient?gender=male" },
                "invalid-fhirpath" => new TopicTrigger("Encounter", ["update"]) { FhirPathCriteria = "%current.status = = 'x'" },
                "unknown-resource-type" => new TopicTrigger("NotAType", ["update"]),
                _ => new TopicTrigger("Encounter", ["update"]),
            };
            TopicFilter filter = problem == "unknown-filter-parameter" ? new TopicFilter("Encounter", "not-a-parameter") : new TopicFilter("Encounter", "subject");
            string body = SubscriptionResources.Topic(NewTopicUrl(), Guid.NewGuid().ToString("N"), [trigger], [filter], eventTrigger: problem == "event-trigger");

            (HttpStatusCode code, JObject outcome) = await SendAsync(HttpMethod.Post, TopicType, body);

            Assert.Equal(HttpStatusCode.BadRequest, code);
            Assert.Contains(reason, Diagnostics(outcome), StringComparison.Ordinal);
        }

        [Fact]
        public async Task GivenAReadOnlyUser_WhenCreatingATopic_ThenForbiddenIsReturned()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires the development identity provider.");
            TestFhirClient reader = _fixture.TestFhirServer.GetTestFhirClient(_fixture.TestFhirClient.Format, TestApplications.ReadOnlyUser, user: null, reusable: false);

            (HttpStatusCode code, _) = await SendAsync(HttpMethod.Post, TopicType, SubscriptionResources.Topic(NewTopicUrl(), Guid.NewGuid().ToString("N")), reader.HttpClient);

            Assert.Equal(HttpStatusCode.Forbidden, code);
        }

        [Fact]
        public async Task GivenADeleteOnlyTrigger_WhenAnEncounterIsCreatedUpdatedAndDeleted_ThenOnlyTheDeleteIsNotified()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled and a local receiver.");
            string path = NewPath();
            string patient = Guid.NewGuid().ToString("N");
            string topicUrl = await CreateTopicAsync([new TopicTrigger("Encounter", ["delete"])], [("Encounter", "subject")]);
            string subscriptionId = await CreateActiveSubscriptionAsync(topicUrl, path, "id-only", $"Encounter?subject=Patient/{patient}");
            try
            {
                string encounter = Guid.NewGuid().ToString("N");
                await PutEncounterAsync(encounter, patient, "planned");
                await PutEncounterAsync(encounter, patient, "cancelled");
                await DeleteAsync($"Encounter/{encounter}");

                (int Number, string Focus)[] events = await WaitForEventsAsync(path, 1);

                Assert.Equal([(1, $"Encounter/{encounter}")], events);
                JToken entry = JObject.Parse(Webhook.For(path).Single(IsEvent).Body)["entry"].Skip(1).Single();
                Assert.Equal("DELETE", (string)entry["request"]["method"]);
            }
            finally
            {
                await SendAsync(HttpMethod.Delete, $"Subscription/{subscriptionId}");
            }
        }

        [Fact]
        public async Task GivenQueryCriteriaWhereEitherTestMayPass_WhenEncountersChangeAndAreDeleted_ThenLeavingOrEnteringAStateIsNotified()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled and a local receiver.");
            string path = NewPath();
            string patient = Guid.NewGuid().ToString("N");
            var trigger = new TopicTrigger("Encounter", ["update", "delete"])
            {
                Previous = "status=in-progress",
                Current = "status=cancelled",
                ResultForDelete = "test-fails",
                RequireBoth = false,
            };
            string topicUrl = await CreateTopicAsync([trigger], [("Encounter", "subject")]);
            string subscriptionId = await CreateActiveSubscriptionAsync(topicUrl, path, "id-only", $"Encounter?subject=Patient/{patient}");
            try
            {
                string changed = Guid.NewGuid().ToString("N");
                await PutEncounterAsync(changed, patient, "planned");
                await PutEncounterAsync(changed, patient, "in-progress");
                await PutEncounterAsync(changed, patient, "unknown");
                await PutEncounterAsync(changed, patient, "cancelled");
                string deletedInProgress = Guid.NewGuid().ToString("N");
                await PutEncounterAsync(deletedInProgress, patient, "in-progress");
                await DeleteAsync($"Encounter/{deletedInProgress}");
                string deletedPlanned = Guid.NewGuid().ToString("N");
                await PutEncounterAsync(deletedPlanned, patient, "planned");
                await DeleteAsync($"Encounter/{deletedPlanned}");

                (int Number, string Focus)[] events = await WaitForEventsAsync(path, 3);

                // Leaving in-progress passes the previous test, entering cancelled passes the current test, and a delete is judged on
                // the deleted version because resultForDelete fails the current test.
                Assert.Equal([(1, $"Encounter/{changed}"), (2, $"Encounter/{changed}"), (3, $"Encounter/{deletedInProgress}")], events);
            }
            finally
            {
                await SendAsync(HttpMethod.Delete, $"Subscription/{subscriptionId}");
            }
        }

        [Fact]
        public async Task GivenFhirPathCriteriaUsingPreviousAndCurrent_WhenEncountersChange_ThenOnlyTheTransitionIntoCancelledIsNotified()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled and a local receiver.");
            string path = NewPath();
            string patient = Guid.NewGuid().ToString("N");
            var trigger = new TopicTrigger("Encounter", ["create", "update"]) { FhirPathCriteria = "%previous.status != 'cancelled' and %current.status = 'cancelled'" };
            string topicUrl = await CreateTopicAsync([trigger], [("Encounter", "subject")]);
            string subscriptionId = await CreateActiveSubscriptionAsync(topicUrl, path, "id-only", $"Encounter?subject=Patient/{patient}");
            try
            {
                await PutEncounterAsync(Guid.NewGuid().ToString("N"), patient, "cancelled");
                string transitioned = Guid.NewGuid().ToString("N");
                await PutEncounterAsync(transitioned, patient, "planned");
                await PutEncounterAsync(transitioned, patient, "cancelled");
                await PutEncounterAsync(transitioned, patient, "cancelled");

                (int Number, string Focus)[] events = await WaitForEventsAsync(path, 1);

                Assert.Equal([(1, $"Encounter/{transitioned}")], events);
            }
            finally
            {
                await SendAsync(HttpMethod.Delete, $"Subscription/{subscriptionId}");
            }
        }

        [Fact]
        public async Task GivenTwoTriggersWithAFilterPerResourceType_WhenEncountersAndObservationsAreCreated_ThenOnlyMatchingResourcesForThePatientAreNotified()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled and a local receiver.");
            string path = NewPath();
            string patient = Guid.NewGuid().ToString("N");
            string otherPatient = Guid.NewGuid().ToString("N");
            string topicUrl = await CreateTopicAsync(
                [new TopicTrigger("Encounter", ["create"]), new TopicTrigger("Observation", ["create"]) { Current = "status=final" }],
                [("Encounter", "subject"), ("Observation", "subject")]);
            (HttpStatusCode code, JObject subscription) = await SendAsync(
                HttpMethod.Post,
                "Subscription",
                SubscriptionResources.Subscription(topicUrl, Webhook.BaseUrl + path, filters: [$"Encounter?subject=Patient/{patient}", $"Observation?subject=Patient/{patient}"]));
            AssertStatus(HttpStatusCode.Created, code, subscription);
            string subscriptionId = (string)subscription["id"];
            try
            {
                await WaitForStatusAsync(subscriptionId, "active");
                string encounter = Guid.NewGuid().ToString("N");
                await PutEncounterAsync(encounter, patient, "planned");
                await PutEncounterAsync(Guid.NewGuid().ToString("N"), otherPatient, "planned");
                string observation = Guid.NewGuid().ToString("N");
                await PutObservationAsync(observation, patient, "final");
                await PutObservationAsync(Guid.NewGuid().ToString("N"), patient, "preliminary");
                await PutObservationAsync(Guid.NewGuid().ToString("N"), otherPatient, "final");

                (int Number, string Focus)[] events = await WaitForEventsAsync(path, 2);

                Assert.Equal([(1, $"Encounter/{encounter}"), (2, $"Observation/{observation}")], events);
            }
            finally
            {
                await SendAsync(HttpMethod.Delete, $"Subscription/{subscriptionId}");
            }
        }

        [Fact]
        public async Task GivenQueryCriteriaWhereCreatesFailThePreviousTest_WhenEncountersAreCreatedAndUpdated_ThenOnlyAnUpdateFromTheRequiredStateIsNotified()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled and a local receiver.");
            string path = NewPath();
            string patient = Guid.NewGuid().ToString("N");
            var trigger = new TopicTrigger("Encounter", ["create", "update"])
            {
                Previous = "status=in-progress",
                ResultForCreate = "test-fails",
                Current = "status=cancelled",
                RequireBoth = true,
            };
            string topicUrl = await CreateTopicAsync([trigger], [("Encounter", "subject")]);
            string subscriptionId = await CreateActiveSubscriptionAsync(topicUrl, path, "id-only", $"Encounter?subject=Patient/{patient}");
            try
            {
                await PutEncounterAsync(Guid.NewGuid().ToString("N"), patient, "cancelled");
                string transitioned = Guid.NewGuid().ToString("N");
                await PutEncounterAsync(transitioned, patient, "in-progress");
                await PutEncounterAsync(transitioned, patient, "cancelled");
                await PutEncounterAsync(transitioned, patient, "cancelled");
                string skipped = Guid.NewGuid().ToString("N");
                await PutEncounterAsync(skipped, patient, "planned");
                await PutEncounterAsync(skipped, patient, "cancelled");

                (int Number, string Focus)[] events = await WaitForEventsAsync(path, 1);

                // Created as cancelled fails the previous test (resultForCreate), and planned -> cancelled fails it on the stored version.
                Assert.Equal([(1, $"Encounter/{transitioned}")], events);
            }
            finally
            {
                await SendAsync(HttpMethod.Delete, $"Subscription/{subscriptionId}");
            }
        }

        [Fact]
        public async Task GivenFiltersOnTwoParametersWithAnAllowedModifier_WhenEncountersAreCreated_ThenOnlyEncountersMatchingBothAreNotified()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled and a local receiver.");
            string path = NewPath();
            string patient = Guid.NewGuid().ToString("N");
            string otherPatient = Guid.NewGuid().ToString("N");
            string topicUrl = await CreateTopicAsync(
                [new TopicTrigger("Encounter", ["create"])],
                [("Encounter", "subject"), new TopicFilter("Encounter", "status") { Modifiers = StatusModifiers }]);
            string subscriptionId = await CreateActiveSubscriptionAsync(topicUrl, path, "id-only", $"Encounter?subject=Patient/{patient}&status:not=cancelled");
            try
            {
                await PutEncounterAsync(Guid.NewGuid().ToString("N"), patient, "cancelled");
                string kept = Guid.NewGuid().ToString("N");
                await PutEncounterAsync(kept, patient, "planned");
                await PutEncounterAsync(Guid.NewGuid().ToString("N"), otherPatient, "planned");

                (int Number, string Focus)[] events = await WaitForEventsAsync(path, 1);

                Assert.Equal([(1, $"Encounter/{kept}")], events);
            }
            finally
            {
                await SendAsync(HttpMethod.Delete, $"Subscription/{subscriptionId}");
            }
        }

#if !R4B
        // R4B topics cannot declare comparators. R5 states the comparator separately; R4 Backport filters carry it as a value prefix.
        [Theory]
        [InlineData("gt", HttpStatusCode.Created)]
        [InlineData("ge", HttpStatusCode.BadRequest)]
        public async Task GivenATopicThatAllowsSomeComparators_WhenSubscribingWithAComparator_ThenOnlyAllowedComparatorsAreAccepted(string comparator, HttpStatusCode expected)
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled.");
            string topicUrl = await CreateTopicAsync([new TopicTrigger("Encounter", ["update"])], [new TopicFilter("Encounter", "date") { Comparators = ["gt", "lt"] }]);
#if R5
            JObject subscription = JObject.Parse(SubscriptionResources.Subscription(topicUrl, Webhook.BaseUrl + NewPath(), filter: "Encounter?date=2026-01-01"));
            subscription["filterBy"][0]["comparator"] = comparator;
#else
            JObject subscription = JObject.Parse(SubscriptionResources.Subscription(topicUrl, Webhook.BaseUrl + NewPath(), filter: $"Encounter?date={comparator}2026-01-01"));
#endif

            (HttpStatusCode code, JObject body) = await SendAsync(HttpMethod.Post, "Subscription", subscription.ToString());

            AssertStatus(expected, code, body);
            if (code == HttpStatusCode.Created)
            {
                await SendAsync(HttpMethod.Delete, $"Subscription/{(string)body["id"]}");
            }
            else
            {
                Assert.Contains($"The comparator '{comparator}' is not allowed", Diagnostics(body), StringComparison.Ordinal);
            }
        }
#endif

        [Fact]
        public async Task GivenSubscriptionsAreEnabled_WhenCapabilitiesAreRead_ThenTheStatusOperationIsAdvertised()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled.");

            (HttpStatusCode code, JObject capabilities) = await SendAsync(HttpMethod.Get, "metadata");

            Assert.Equal(HttpStatusCode.OK, code);
            JToken subscription = capabilities["rest"][0]["resource"].Single(r => (string)r["type"] == "Subscription");
            Assert.Contains(subscription["operation"], o => (string)o["name"] == "status");
#if R4 || R4B
            Assert.Contains(subscription["supportedProfile"], p => (string)p == SubscriptionsBackport.SubscriptionProfile);
#endif
#if R4 && !R4B
            JToken basic = capabilities["rest"][0]["resource"].Single(r => (string)r["type"] == "Basic");
            Assert.Contains(basic["supportedProfile"], p => (string)p == SubscriptionsBackport.R4TopicProfile);
#endif
        }

#if R4 && !R4B
        [Fact]
        public async Task GivenTheBuiltInTopicProfile_WhenATopicIsValidatedAgainstIt_ThenNoErrorsAreReported()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled.");

            (HttpStatusCode code, JObject outcome) = await SendAsync(
                HttpMethod.Post,
                $"Basic/$validate?profile={Uri.EscapeDataString(SubscriptionsBackport.R4TopicProfile)}",
                SubscriptionResources.Topic(NewTopicUrl(), Guid.NewGuid().ToString("N")));

            AssertStatus(HttpStatusCode.OK, code, outcome);
            Assert.DoesNotContain(outcome["issue"], i => (string)i["severity"] is "error" or "fatal");
        }
#endif

        private static void AssertStatus(HttpStatusCode expected, HttpStatusCode actual, JObject body)
        {
            Assert.True(expected == actual, $"Expected {expected} but got {actual}: {body}");
        }

        private static string Diagnostics(JObject outcome) => string.Join(" | ", outcome["issue"].Select(i => (string)i["diagnostics"]));

        private static bool IsEvent(ReceivedNotification notification) => notification.Body.Contains("\"event-notification\"", StringComparison.Ordinal);

        private static Hl7.Fhir.Model.Bundle ParseXml(string body) => new Hl7.Fhir.Serialization.FhirXmlParser().Parse<Hl7.Fhir.Model.Bundle>(body);

        private static string NewPath() => "/" + Guid.NewGuid().ToString("N");

        private static string NewTopicUrl() => $"http://example.org/topics/e2e-{Guid.NewGuid():N}";

        private static void SetEndpoint(JObject subscription, string endpoint)
        {
#if R5
            subscription["endpoint"] = endpoint;
#else
            subscription["channel"]["endpoint"] = endpoint;
#endif
        }

        private static string[] EventNumbers(JObject bundle)
        {
            JObject status = (JObject)bundle["entry"][0]["resource"];
#if R4 && !R4B
            return status["parameter"].Where(p => (string)p["name"] == "notification-event")
                .Select(p => (string)p["part"].Single(part => (string)part["name"] == "event-number")["valueString"]).ToArray();
#else
            return status["notificationEvent"].Select(e => e["eventNumber"].ToString()).ToArray();
#endif
        }

        private static string StatusValue(JObject statusBundle, string r5Name, string r4Name)
        {
            JObject status = (JObject)statusBundle["entry"][0]["resource"];
#if R4 && !R4B
            JObject parameter = (JObject)status["parameter"].Single(p => (string)p["name"] == r4Name);
            return parameter.Properties().Single(p => p.Name.StartsWith("value", StringComparison.Ordinal)).Value.ToString();
#else
            return status[r5Name].ToString();
#endif
        }

        private async Task<string> CreateTopicAsync()
        {
            string url = NewTopicUrl();
            (HttpStatusCode code, JObject topic) = await SendAsync(HttpMethod.Post, TopicType, SubscriptionResources.Topic(url, Guid.NewGuid().ToString("N")));
            AssertStatus(HttpStatusCode.Created, code, topic);
            return url;
        }

        private async Task<string> CreateTopicAsync(IReadOnlyList<TopicTrigger> triggers, IReadOnlyList<TopicFilter> canFilterBy)
        {
            string url = NewTopicUrl();
            (HttpStatusCode code, JObject topic) = await SendAsync(HttpMethod.Post, TopicType, SubscriptionResources.Topic(url, Guid.NewGuid().ToString("N"), triggers, canFilterBy));
            AssertStatus(HttpStatusCode.Created, code, topic);
            return url;
        }

        /// <summary>
        /// Waits for at least <paramref name="count"/> events on <paramref name="path"/>, then for a quiet period, and returns every
        /// delivered event as its number and focus (<c>Type/id</c>) in event order. Read from the status part of each notification,
        /// because R5 id-only notifications list a resource changed several times only once.
        /// </summary>
        private async Task<(int Number, string Focus)[]> WaitForEventsAsync(string path, int count)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (Events(path).Length < count && DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
            }

            await Task.Delay(QuietPeriod);
            return Events(path);
        }

        private (int Number, string Focus)[] Events(string path)
        {
            return Webhook.For(path).Where(IsEvent).SelectMany(n =>
            {
                JObject status = (JObject)JObject.Parse(n.Body)["entry"][0]["resource"];
#if R4 && !R4B
                return status["parameter"].Where(p => (string)p["name"] == "notification-event").Select(p => (
                    int.Parse((string)p["part"].Single(part => (string)part["name"] == "event-number")["valueString"], CultureInfo.InvariantCulture),
                    (string)p["part"].Single(part => (string)part["name"] == "focus")["valueReference"]["reference"]));
#else
                return status["notificationEvent"].Select(e => (int.Parse(e["eventNumber"].ToString(), CultureInfo.InvariantCulture), (string)e["focus"]["reference"]));
#endif
            })
            .Select(e => (e.Item1, FocusPattern.Match(e.Item2).Value))
            .OrderBy(e => e.Item1)
            .ToArray();
        }

        private async Task DeleteAsync(string relativeUrl)
        {
            (HttpStatusCode code, _) = await SendAsync(HttpMethod.Delete, relativeUrl);
            Assert.Equal(HttpStatusCode.NoContent, code);
        }

        private async Task PutObservationAsync(string id, string patient, string status)
        {
            var observation = new JObject
            {
                ["resourceType"] = "Observation",
                ["id"] = id,
                ["status"] = status,
                ["code"] = new JObject { ["coding"] = new JArray(new JObject { ["system"] = "http://loinc.org", ["code"] = "8867-4" }) },
                ["subject"] = new JObject { ["reference"] = $"Patient/{patient}" },
            };

            (HttpStatusCode code, _) = await SendAsync(HttpMethod.Put, $"Observation/{id}", observation.ToString());
            Assert.True(code is HttpStatusCode.OK or HttpStatusCode.Created, $"PUT Observation returned {code}");
        }

        private async Task<string> CreateActiveSubscriptionAsync(string topicUrl, string path, string content, string filter)
        {
            (HttpStatusCode code, JObject subscription) = await SendAsync(HttpMethod.Post, "Subscription", SubscriptionResources.Subscription(topicUrl, Webhook.BaseUrl + path, content: content, filter: filter));
            AssertStatus(HttpStatusCode.Created, code, subscription);
            string id = (string)subscription["id"];
            await WaitForStatusAsync(id, "active");
            return id;
        }

        private async Task WaitForStatusAsync(string subscriptionId, string expected)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            string status = null;
            while (DateTime.UtcNow < deadline)
            {
                (_, JObject subscription) = await SendAsync(HttpMethod.Get, $"Subscription/{subscriptionId}");
                status = (string)subscription?["status"];
                if (status == expected)
                {
                    return;
                }

                await Task.Delay(500);
            }

            Assert.Fail($"Subscription {subscriptionId} did not reach '{expected}' within {Timeout}; last status '{status}'.");
        }

        private async Task PutEncounterAsync(string id, string patient, string status)
        {
#if R5
            var encounterClass = new JArray(new JObject { ["coding"] = new JArray(new JObject { ["system"] = "http://terminology.hl7.org/CodeSystem/v3-ActCode", ["code"] = "AMB" }) });
#else
            var encounterClass = new JObject { ["system"] = "http://terminology.hl7.org/CodeSystem/v3-ActCode", ["code"] = "AMB" };
#endif
            var encounter = new JObject
            {
                ["resourceType"] = "Encounter",
                ["id"] = id,
                ["status"] = status,
                ["class"] = encounterClass,
                ["subject"] = new JObject { ["reference"] = $"Patient/{patient}" },
            };

            (HttpStatusCode code, _) = await SendAsync(HttpMethod.Put, $"Encounter/{id}", encounter.ToString());
            Assert.True(code is HttpStatusCode.OK or HttpStatusCode.Created, $"PUT Encounter returned {code}");
        }

        private async Task<(HttpStatusCode Code, JObject Body)> SendAsync(HttpMethod method, string relativeUrl, string body = null, HttpClient client = null)
        {
            using var request = new HttpRequestMessage(method, relativeUrl);
            if (body != null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/fhir+json");
            }

            request.Headers.Accept.ParseAdd("application/fhir+json");
            using HttpResponseMessage response = await (client ?? _client).SendAsync(request);
            string text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JObject.Parse(text));
        }
    }
}
#endif
