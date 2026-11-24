// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence
{
    /// <summary>
    /// Outbox counts reported by $status.
    /// </summary>
    public sealed record SubscriptionEventCounts(long Pending, long Unevaluable);
}
