// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching
{
    /// <summary>
    /// One resource version written by a committed transaction.
    /// </summary>
    public sealed class ResourceChange
    {
        public long TransactionId { get; init; }

        public short ResourceTypeId { get; init; }

        public long ResourceSurrogateId { get; init; }

        public string ResourceType { get; init; }

        public string ResourceId { get; init; }

        public int Version { get; init; }

        public bool IsDeleted { get; init; }

        public DateTimeOffset LastModified { get; init; }

        /// <summary>
        /// Gets a value indicating whether the version was already superseded when it was written: a later version of the
        /// resource was written by the same or an earlier transaction. <c>$import</c> loads a resource's history this way.
        /// </summary>
        public bool IsWrittenAsHistory { get; init; }

        /// <summary>
        /// Gets the written version, or null when its content is no longer available (for example, removed by history cleanup).
        /// </summary>
        public ResourceSnapshot Current { get; init; }

        /// <summary>
        /// Gets or sets the immediately preceding version, when it has been loaded and is available.
        /// </summary>
        public ResourceSnapshot Previous { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the preceding version was looked up.
        /// </summary>
        public bool PreviousLoaded { get; set; }

        /// <summary>
        /// Gets or sets a value indicating whether the preceding version is deleted, which makes this change a re-create.
        /// </summary>
        public bool PreviousIsDeleted { get; set; }

        public SubscriptionInteraction Interaction
        {
            get
            {
                if (IsDeleted)
                {
                    return SubscriptionInteraction.Delete;
                }

                return Version <= 1 || PreviousIsDeleted ? SubscriptionInteraction.Create : SubscriptionInteraction.Update;
            }
        }

        /// <summary>
        /// Gets the version that filters and authorization apply to: the deleted version for a delete, otherwise the written one.
        /// Null when that version is unavailable. A delete's own version is an empty tombstone, so it is never used.
        /// </summary>
        public ResourceSnapshot Target => Interaction == SubscriptionInteraction.Delete ? Previous : Current;

        /// <summary>
        /// Gets a value indicating whether a preceding version should exist.
        /// </summary>
        public bool HasPreviousVersion => Version > 1;
    }
}
