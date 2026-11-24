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
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Fhir.Tests.Common.FixtureParameters;
using Microsoft.Health.Test.Utilities;
using Newtonsoft.Json.Linq;
using Xunit;

namespace Microsoft.Health.Fhir.Tests.E2E.Rest.Subscriptions
{
    /// <summary>
    /// With <c>FhirServer:Subscriptions:Enabled</c> false (the default), Subscription and topic resources are stored as written,
    /// as before the feature existed, and nothing subscription-specific is advertised or processed.
    /// </summary>
    [Trait(Traits.Category, Categories.Subscriptions)]
    [HttpIntegrationFixtureArgumentSets(DataStore.SqlServer, Format.Json)]
    public class SubscriptionsDisabledTests : IClassFixture<HttpIntegrationTestFixture>
    {
#if R4 && !R4B
        private const string TopicType = "Basic";
#else
        private const string TopicType = "SubscriptionTopic";
#endif

        private readonly HttpIntegrationTestFixture _fixture;
        private readonly HttpClient _client;

        public SubscriptionsDisabledTests(HttpIntegrationTestFixture fixture)
        {
            _fixture = fixture;
            _client = fixture.TestFhirClient.HttpClient;
        }

        [Fact]
        public async Task GivenSubscriptionsAreDisabled_WhenCapabilitiesAreRead_ThenNothingSubscriptionSpecificIsAdvertised()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "The feature setting of a remote server is not known.");

            (HttpStatusCode code, JObject capabilities) = await SendAsync(HttpMethod.Get, "metadata");

            Assert.Equal(HttpStatusCode.OK, code);
            JToken subscription = capabilities["rest"][0]["resource"].Single(r => (string)r["type"] == "Subscription");
            Assert.DoesNotContain(subscription["operation"] ?? new JArray(), o => (string)o["name"] == "status");
            Assert.DoesNotContain(SubscriptionsBackport.SubscriptionProfile, capabilities.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(SubscriptionsBackport.R4TopicProfile, capabilities.ToString(), StringComparison.Ordinal);
        }

        [Fact]
        public async Task GivenSubscriptionsAreDisabled_WhenASubscriptionIsWritten_ThenItIsStoredAsWrittenAndNeverProcessed()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "The feature setting of a remote server is not known.");
            DateTimeOffset end = DateTimeOffset.UtcNow.AddYears(5);

            // An unknown topic, an unreachable endpoint, a client-set active status and an end beyond the lifetime limit would each be
            // rejected or changed with the feature on.
            (HttpStatusCode code, JObject created) = await SendAsync(
                HttpMethod.Post,
                "Subscription",
                SubscriptionResources.Subscription($"http://example.org/topics/missing-{Guid.NewGuid():N}", "http://127.0.0.1:9/hook", content: null, status: "active", end: end));

            Assert.True(code == HttpStatusCode.Created, $"Expected Created but got {code}: {created}");
            string id = (string)created["id"];
            try
            {
                Assert.Equal("active", (string)created["status"]);
                Assert.Equal(end.ToUniversalTime().Year, DateTimeOffset.Parse((string)created["end"], System.Globalization.CultureInfo.InvariantCulture).Year);
                Assert.DoesNotContain(SubscriptionsBackport.PayloadContent, created.ToString(), StringComparison.Ordinal);

                await Task.Delay(TimeSpan.FromSeconds(5));
                (_, JObject current) = await SendAsync(HttpMethod.Get, $"Subscription/{id}");
                Assert.Equal("active", (string)current["status"]);
                Assert.Equal((string)created["meta"]["versionId"], (string)current["meta"]["versionId"]);

                // The server's mapping for a disabled operation (OperationNotImplementedException).
                (code, JObject outcome) = await SendAsync(HttpMethod.Get, $"Subscription/{id}/$status");
                Assert.Equal(HttpStatusCode.MethodNotAllowed, code);
                Assert.Equal("OperationOutcome", (string)outcome["resourceType"]);
            }
            finally
            {
                await SendAsync(HttpMethod.Delete, $"Subscription/{id}");
            }
        }

        [Fact]
        public async Task GivenSubscriptionsAreDisabled_WhenATopicWithCriteriaTheFeatureRejectsIsWritten_ThenItIsStored()
        {
            Assert.SkipWhen(!_fixture.IsUsingInProcTestServer, "The feature setting of a remote server is not known.");
            string body = SubscriptionResources.Topic($"http://example.org/topics/off-{Guid.NewGuid():N}", Guid.NewGuid().ToString("N"))
                .Replace("status=cancelled\"", "subject:Patient.name=x\"", StringComparison.Ordinal);

            (HttpStatusCode code, JObject topic) = await SendAsync(HttpMethod.Post, TopicType, body);

            Assert.True(code == HttpStatusCode.Created, $"Expected Created but got {code}: {topic}");
            await SendAsync(HttpMethod.Delete, $"{TopicType}/{topic["id"]}");
        }

        private async Task<(HttpStatusCode Code, JObject Body)> SendAsync(HttpMethod method, string relativeUrl, string body = null)
        {
            using var request = new HttpRequestMessage(method, relativeUrl);
            if (body != null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/fhir+json");
            }

            request.Headers.Accept.ParseAdd("application/fhir+json");
            using HttpResponseMessage response = await _client.SendAsync(request);
            string text = await response.Content.ReadAsStringAsync();
            return (response.StatusCode, string.IsNullOrWhiteSpace(text) ? null : JObject.Parse(text));
        }
    }
}
#endif
