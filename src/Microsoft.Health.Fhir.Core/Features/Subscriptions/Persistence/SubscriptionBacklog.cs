// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence
{
    /// <summary>
    /// Pending events across all subscriptions, reported as metrics so operators can alert on a growing backlog.
    /// </summary>
    /// <param name="PendingEvents">The number of events waiting to be delivered.</param>
    /// <param name="OldestPendingEventUtc">When the oldest waiting event happened, or null when nothing is waiting.</param>
    public sealed record SubscriptionBacklog(long PendingEvents, DateTimeOffset? OldestPendingEventUtc);
}
