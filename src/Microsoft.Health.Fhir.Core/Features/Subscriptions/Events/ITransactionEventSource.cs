// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Health.Fhir.Core.Configs;

namespace Microsoft.Health.Fhir.Core.Features.Subscriptions.Events
{
    /// <summary>
    /// A source of committed resource changes for the subscription event pump (ADR-2611). Each source has its own ordered,
    /// durable position, so matching and delivery do not depend on which source is used.
    /// </summary>
    public interface ITransactionEventSource
    {
        /// <summary>
        /// Gets the source this implementation reads.
        /// </summary>
        SubscriptionEventSource Kind { get; }

        /// <summary>
        /// Gets the identifier under which the pump stores this source's position.
        /// </summary>
        string CursorId { get; }

        /// <summary>
        /// Returns why the source cannot be used with the current configuration, or null when it can.
        /// </summary>
        /// <returns>The reason, or null.</returns>
        string GetUnavailableReason();

        /// <summary>
        /// Returns the position to start from when the pump has none. It must not be past any change made after
        /// <paramref name="earliestActivationTransactionId"/>, because those changes are owed to an active subscription.
        /// </summary>
        /// <param name="earliestActivationTransactionId">The lowest activation watermark of existing subscriptions, or null.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The start position.</returns>
        Task<long> GetStartPositionAsync(long? earliestActivationTransactionId, CancellationToken cancellationToken);

        /// <summary>
        /// Reads committed changes after <paramref name="position"/>, in order, with their previous versions loaded.
        /// </summary>
        /// <param name="position">The last position already processed.</param>
        /// <param name="maxPositions">The maximum number of positions (transactions or change records) to read.</param>
        /// <param name="isCovered">Returns whether any subscription can match a resource type; other changes may be skipped.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The page read. An empty page means there is nothing more to read now.</returns>
        Task<TransactionEventPage> ReadAsync(long position, int maxPositions, Func<string, bool> isCovered, CancellationToken cancellationToken);
    }
}
