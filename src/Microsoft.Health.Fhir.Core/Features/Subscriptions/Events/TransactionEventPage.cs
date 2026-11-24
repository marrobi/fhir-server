// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Events
{
    /// <summary>
    /// A page of committed changes read from an <see cref="ITransactionEventSource"/>.
    /// </summary>
    public sealed class TransactionEventPage
    {
        /// <summary>
        /// Gets the changes read, in commit order, with their previous versions loaded.
        /// </summary>
        public IReadOnlyList<ResourceChange> Changes { get; init; } = [];

        /// <summary>
        /// Gets the number of positions read. Zero means there was nothing to read.
        /// </summary>
        public int PositionCount { get; init; }

        /// <summary>
        /// Gets the last position read, which the pump commits as its cursor with the resulting events.
        /// </summary>
        public long LastPosition { get; init; }

        /// <summary>
        /// Gets when the change at <see cref="LastPosition"/> was made, if known.
        /// </summary>
        public DateTimeOffset? LastPositionTime { get; init; }

        /// <summary>
        /// Creates an empty page.
        /// </summary>
        /// <param name="position">The unchanged position.</param>
        /// <returns>The page.</returns>
        public static TransactionEventPage Empty(long position) => new() { LastPosition = position };
    }
}
