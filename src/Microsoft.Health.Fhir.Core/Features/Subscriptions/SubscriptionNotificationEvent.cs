// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// One event included in a notification.
    /// </summary>
    public sealed class SubscriptionNotificationEvent
    {
        public long EventNumber { get; init; }

        public DateTimeOffset Timestamp { get; init; }

        public string ResourceType { get; init; }

        public string ResourceId { get; init; }

        public int ResourceVersion { get; init; }

        public SubscriptionInteraction Interaction { get; init; }

        /// <summary>
        /// Gets the resource content, for <c>full-resource</c> notifications. Null when the content cannot be included.
        /// </summary>
        public ResourceElement Resource { get; init; }
    }
}
