// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

#if !Stu3
using System;
using System.Collections.Generic;
using System.Linq;
using Hl7.Fhir.Model;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;

namespace Microsoft.Health.Fhir.Core.UnitTests.Features.Subscriptions
{
    /// <summary>
    /// Notification requests and events shared by the notification factory and profile tests.
    /// </summary>
    internal static class SubscriptionNotificationTestData
    {
        public static readonly Uri BaseUri = new("https://fhir.example/");

        public static SubscriptionNotificationRequest Request(
            SubscriptionContentLevel content,
            IReadOnlyList<SubscriptionNotificationEvent> events,
            SubscriptionNotificationType type,
            IReadOnlyList<string> errors = null) => new()
        {
            Subscription = new SubscriptionInfo { Id = "s1", TopicCanonical = "http://example.org/topic", Content = content, Status = SubscriptionStatusCode.Active },
            Type = type,
            Status = SubscriptionStatusCode.Active,
            EventsSinceSubscriptionStart = 8,
            Events = events,
            Errors = errors ?? [],
            BaseUri = BaseUri,
        };

        /// <summary>
        /// Creates Encounter events numbered from 7; the first is a create, the others updates.
        /// </summary>
        /// <param name="count">The number of events.</param>
        /// <param name="resource">Creates the resource of event <c>i</c> (1-based) for full-resource content, or null for no resource.</param>
        /// <returns>The events.</returns>
        public static SubscriptionNotificationEvent[] Events(int count, Func<int, Resource> resource = null) => Enumerable.Range(1, count).Select(i => new SubscriptionNotificationEvent
        {
            EventNumber = 6 + i,
            Timestamp = DateTimeOffset.UtcNow,
            ResourceType = "Encounter",
            ResourceId = "e" + i,
            ResourceVersion = i,
            Interaction = i == 1 ? SubscriptionInteraction.Create : SubscriptionInteraction.Update,
            Resource = resource?.Invoke(i).ToResourceElement(),
        }).ToArray();
    }
}
#endif
