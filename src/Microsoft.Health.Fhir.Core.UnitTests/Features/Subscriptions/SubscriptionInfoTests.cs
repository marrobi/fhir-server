// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.Test.Utilities;
using Xunit;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    [Trait(Traits.OwningTeam, OwningTeam.Fhir)]
    [Trait(Traits.Category, Categories.Subscriptions)]
    public sealed class SubscriptionInfoTests
    {
        private static readonly SubscriptionFilter StatusFilter = new() { ResourceType = "Encounter", FilterParameter = "status", Value = "finished" };
        private static readonly SubscriptionFilter PatientFilter = new() { ResourceType = "Encounter", FilterParameter = "patient", Value = "Patient/1" };
        private static readonly SubscriptionHeader Authorization = new("Authorization", "Bearer a");
        private static readonly SubscriptionHeader Tenant = new("X-Tenant", "t1");

        // The content hash binds a grant and pending events to what is sent, and where; each of these changes must change it.
        public static TheoryData<string, Func<SubscriptionInfo>> ContentChanges => new()
        {
            { "topic", () => Create(topic: "http://example.org/topic/other") },
            { "filter value", () => Create(filters: [new SubscriptionFilter { ResourceType = "Encounter", FilterParameter = "status", Value = "cancelled" }, PatientFilter]) },
            { "filter comparator", () => Create(filters: [new SubscriptionFilter { ResourceType = "Encounter", FilterParameter = "status", Comparator = "ne", Value = "finished" }, PatientFilter]) },
            { "filter modifier", () => Create(filters: [new SubscriptionFilter { ResourceType = "Encounter", FilterParameter = "status", Modifier = "not", Value = "finished" }, PatientFilter]) },
            { "filter added", () => Create(filters: [StatusFilter, PatientFilter, new SubscriptionFilter { ResourceType = "Encounter", FilterParameter = "class", Value = "IMP" }]) },
            { "filter removed", () => Create(filters: [StatusFilter]) },
            { "endpoint", () => Create(endpoint: "https://other.example.com/notify") },
            { "header value", () => Create(headers: [new SubscriptionHeader("Authorization", "Bearer b"), Tenant]) },
            { "header removed", () => Create(headers: [Tenant]) },
            { "content type", () => Create(contentType: "application/fhir+xml") },
            { "content", () => Create(content: SubscriptionContentLevel.FullResource) },
        };

        [Theory]
        [MemberData(nameof(ContentChanges))]
        public void GivenAContentChange_WhenHashed_ThenTheHashChanges(string change, Func<SubscriptionInfo> changed)
        {
            Assert.False(string.Equals(Create().ComputeContentHash(), changed().ComputeContentHash(), StringComparison.Ordinal), change);
        }

        [Fact]
        public void GivenOnlyOrderStatusOrBookkeepingDiffers_WhenHashed_ThenTheHashIsTheSame()
        {
            // Arrange
            SubscriptionInfo original = Create();
            SubscriptionInfo reordered = Create(
                filters: [PatientFilter, StatusFilter],
                headers: [Tenant, Authorization],
                status: SubscriptionStatusCode.Error,
                heartbeat: 600,
                end: DateTimeOffset.UtcNow.AddDays(1));

            // Act & Assert
            Assert.Equal(original.ComputeContentHash(), reordered.ComputeContentHash());
        }

        private static SubscriptionInfo Create(
            string topic = "http://example.org/topic/encounter",
            IReadOnlyList<SubscriptionFilter> filters = null,
            string endpoint = "https://hooks.example.com/notify",
            IReadOnlyList<SubscriptionHeader> headers = null,
            string contentType = "application/fhir+json",
            SubscriptionContentLevel content = SubscriptionContentLevel.IdOnly,
            SubscriptionStatusCode status = SubscriptionStatusCode.Active,
            int heartbeat = 60,
            DateTimeOffset? end = null)
        {
            return new SubscriptionInfo
            {
                Id = "s1",
                TopicCanonical = topic,
                Filters = filters ?? [StatusFilter, PatientFilter],
                ChannelType = "rest-hook",
                Endpoint = endpoint,
                Headers = headers ?? [Authorization, Tenant],
                ContentType = contentType,
                Content = content,
                Status = status,
                HeartbeatPeriodSeconds = heartbeat,
                End = end,
            };
        }
    }
}
