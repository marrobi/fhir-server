// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

#if !Stu3
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
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
        [InlineData("unknown-topic")]
        [InlineData("ftp-endpoint")]
        [InlineData("unsupported-filter")]
        public async Task GivenAnInvalidSubscription_WhenCreated_ThenBadRequestIsReturned(string problem)
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled.");
            string topicUrl = await CreateTopicAsync();
            string body = problem switch
            {
                "unknown-topic" => SubscriptionResources.Subscription($"http://example.org/topics/missing-{Guid.NewGuid():N}", Webhook.BaseUrl + NewPath()),
                "ftp-endpoint" => SubscriptionResources.Subscription(topicUrl, "ftp://example.org/hook"),
                _ => SubscriptionResources.Subscription(topicUrl, Webhook.BaseUrl + NewPath(), filter: "Encounter?status=planned"),
            };

            (HttpStatusCode code, JObject outcome) = await SendAsync(HttpMethod.Post, "Subscription", body);

            Assert.Equal(HttpStatusCode.BadRequest, code);
            Assert.Equal("OperationOutcome", (string)outcome["resourceType"]);
        }

        [Fact]
        public async Task GivenATopicWithUnsupportedCriteria_WhenCreated_ThenBadRequestIsReturned()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "Requires an in-proc server with subscriptions enabled.");
            string body = SubscriptionResources.Topic(NewTopicUrl(), Guid.NewGuid().ToString("N")).Replace("status=cancelled\"", "subject:Patient.name=x\"", StringComparison.Ordinal);

            (HttpStatusCode code, JObject outcome) = await SendAsync(HttpMethod.Post, TopicType, body);

            Assert.Equal(HttpStatusCode.BadRequest, code);
            Assert.Equal("OperationOutcome", (string)outcome["resourceType"]);
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

        private static bool IsEvent(ReceivedNotification notification) => notification.Body.Contains("\"event-notification\"", StringComparison.Ordinal);

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
