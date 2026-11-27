// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions
{
    /// <summary>
    /// Checks deletes that may remove subscription topics (ADR-2611).
    /// </summary>
    public interface ISubscriptionTopicDeletionGuard
    {
        /// <summary>
        /// Checks that the resources may be deleted, as far as subscription topics are concerned. Deleting a topic needs the
        /// subscription topic permission, and is refused while a subscription in <c>requested</c>, <c>active</c> or <c>error</c> uses it.
        /// </summary>
        /// <param name="resources">The type and id of each resource to delete.</param>
        /// <param name="checkPermission">
        /// Whether to check the caller's permission. False for background jobs, whose permission was checked when they were created.
        /// </param>
        /// <param name="removesCurrentVersion">
        /// Whether the current version is deleted. <c>$purge-history</c> keeps it, so the topic stays usable and only the permission applies.
        /// </param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task.</returns>
        Task EnsureCanDeleteAsync(IEnumerable<(string ResourceType, string Id)> resources, bool checkPermission, bool removesCurrentVersion, CancellationToken cancellationToken);
    }
}
