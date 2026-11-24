// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using EnsureThat;
using Medino;

namespace Microsoft.Health.Fhir.Core.Messages.Subscriptions
{
    /// <summary>
    /// Request for the Subscription $status operation.
    /// </summary>
    public sealed class SubscriptionStatusRequest : IRequest<SubscriptionStatusResponse>
    {
        public SubscriptionStatusRequest(IReadOnlyList<string> subscriptionIds)
        {
            SubscriptionIds = EnsureArg.IsNotNull(subscriptionIds, nameof(subscriptionIds));
        }

        public IReadOnlyList<string> SubscriptionIds { get; }
    }
}
