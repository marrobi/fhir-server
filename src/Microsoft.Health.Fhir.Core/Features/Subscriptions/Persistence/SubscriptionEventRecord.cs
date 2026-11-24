// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence
{
    /// <summary>
    /// An event in a subscription's durable outbox.
    /// </summary>
    public sealed class SubscriptionEventRecord
    {
        public string SubscriptionId { get; init; }

        public long EventNumber { get; init; }

        public long TransactionId { get; init; }

        public string ResourceType { get; init; }

        public string ResourceId { get; init; }

        public int ResourceVersion { get; init; }

        public SubscriptionInteraction Interaction { get; init; }

        public SubscriptionEventStatus Status { get; init; }

        public int Attempts { get; init; }

        public DateTimeOffset NextAttemptUtc { get; init; }

        public DateTimeOffset EventUtc { get; init; }
    }
}
