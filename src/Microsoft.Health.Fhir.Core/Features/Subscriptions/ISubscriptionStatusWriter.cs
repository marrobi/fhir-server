// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Writes server-owned status changes to stored <c>Subscription</c> resources.
    /// </summary>
    public interface ISubscriptionStatusWriter
    {
        /// <summary>
        /// Updates status and error, provided the stored version is still <paramref name="expectedVersionId"/>.
        /// </summary>
        /// <returns>True when written; false when the subscription changed or no longer exists.</returns>
        Task<bool> SetStatusAsync(string subscriptionId, string expectedVersionId, SubscriptionStatusCode status, string error, CancellationToken cancellationToken);
    }
}
