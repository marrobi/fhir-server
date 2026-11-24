// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence
{
    /// <summary>
    /// Server-owned runtime state of a subscription: its grant, activation watermark and event counter.
    /// </summary>
    public sealed class SubscriptionState
    {
        public string SubscriptionId { get; init; }

        public string ContentHash { get; init; }

        /// <summary>
        /// Gets the serialized <see cref="Authorization.SubscriptionGrant"/>.
        /// </summary>
        public string Grant { get; init; }

        /// <summary>
        /// Gets the visible transaction at activation. Only later transactions are matched. Null when not activated.
        /// </summary>
        public long? ActivationTransactionId { get; init; }

        /// <summary>
        /// Gets the highest event number assigned so far (R5 <c>eventsSinceSubscriptionStart</c>).
        /// </summary>
        public long LastEventNumber { get; init; }

        public DateTimeOffset? LastNotificationUtc { get; init; }

        /// <summary>
        /// Gets the latest server-reported error, reported by $status.
        /// </summary>
        public string Error { get; init; }
    }
}
