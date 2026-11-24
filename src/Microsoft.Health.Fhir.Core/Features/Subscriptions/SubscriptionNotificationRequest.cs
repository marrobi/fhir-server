// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// The content of a notification or status bundle.
    /// </summary>
    public sealed class SubscriptionNotificationRequest
    {
        public SubscriptionInfo Subscription { get; init; }

        public SubscriptionNotificationType Type { get; init; }

        /// <summary>
        /// Gets the status to report. Usually the subscription's status; <c>active</c> for a handshake.
        /// </summary>
        public SubscriptionStatusCode Status { get; init; }

        public long EventsSinceSubscriptionStart { get; init; }

        public IReadOnlyList<SubscriptionNotificationEvent> Events { get; init; } = [];

        public IReadOnlyList<string> Errors { get; init; } = [];

        /// <summary>
        /// Gets the base URL used to build absolute references.
        /// </summary>
        public Uri BaseUri { get; init; }
    }
}
