// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching
{
    /// <summary>
    /// An event to append to a subscription's outbox.
    /// </summary>
    public sealed record SubscriptionEventCandidate(string SubscriptionId, ResourceChange Change, SubscriptionEventStatus Status);
}
