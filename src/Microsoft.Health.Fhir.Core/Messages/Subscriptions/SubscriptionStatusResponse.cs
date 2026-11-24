// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using EnsureThat;
using Microsoft.Health.Fhir.Core.Models;

namespace Microsoft.Health.Fhir.Core.Messages.Subscriptions
{
    /// <summary>
    /// Response of the Subscription $status operation: a searchset bundle of status resources.
    /// </summary>
    public sealed class SubscriptionStatusResponse
    {
        public SubscriptionStatusResponse(ResourceElement bundle)
        {
            Bundle = EnsureArg.IsNotNull(bundle, nameof(bundle));
        }

        public ResourceElement Bundle { get; }
    }
}
