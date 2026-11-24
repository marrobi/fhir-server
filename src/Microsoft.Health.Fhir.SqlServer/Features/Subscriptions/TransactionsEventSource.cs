// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Events;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;

namespace Microsoft.Health.Fhir.SqlServer.Features.Subscriptions
{
    /// <summary>
    /// Reads committed transactions from <c>dbo.Transactions</c> up to the visibility watermark. Positions are transaction ids.
    /// Every write path that uses <c>MergeResources</c>, including <c>$import</c>, is covered.
    /// </summary>
    internal sealed class TransactionsEventSource : ITransactionEventSource
    {
        private readonly SqlResourceChangeReader _reader;
        private readonly object _sync = new();
        private Queue<long> _fetched = new();

        public TransactionsEventSource(SqlResourceChangeReader reader)
        {
            _reader = EnsureArg.IsNotNull(reader, nameof(reader));
        }

        public SubscriptionEventSource Kind => SubscriptionEventSource.Transactions;

        public string CursorId => SqlServerSubscriptionStore.CursorId;

        public string GetUnavailableReason() => null;

        public async Task<long> GetStartPositionAsync(long? earliestActivationTransactionId, CancellationToken cancellationToken)
        {
            long visibility = await _reader.Store.MergeResourcesGetTransactionVisibilityAsync(cancellationToken);
            return earliestActivationTransactionId.HasValue ? Math.Min(visibility, earliestActivationTransactionId.Value) : visibility;
        }

        public Task<DateTimeOffset?> GetPositionTimeAsync(long position, CancellationToken cancellationToken)
        {
            // Transaction ids are surrogate ids, which encode when the transaction began.
            return Task.FromResult<DateTimeOffset?>(position > 0 ? position.ToLastUpdated() : null);
        }

        public Task<long> GetPositionBeforeAsync(DateTimeOffset time, CancellationToken cancellationToken)
        {
            return Task.FromResult(time.ToSurrogateId());
        }

        public async Task<TransactionEventPage> ReadAsync(long position, int maxPositions, Func<string, bool> isCovered, CancellationToken cancellationToken)
        {
            List<long> transactionIds = await NextTransactionsAsync(position, Math.Max(1, maxPositions), cancellationToken);
            if (transactionIds.Count == 0)
            {
                return TransactionEventPage.Empty(position);
            }

            var changes = new List<ResourceChange>();
            if (isCovered != null)
            {
                foreach (long transactionId in transactionIds)
                {
                    IReadOnlyList<ResourceWrapper> wrappers = (await _reader.GetTransactionVersionsAsync(transactionId, cancellationToken))
                        .Where(w => isCovered(w.ResourceTypeName))
                        .OrderBy(w => w.ResourceSurrogateId)
                        .ToList();

                    // Only a version that is already history can have been written as history, so only those need the lookup.
                    List<(short, string, int)> historyKeys = wrappers
                        .Where(w => w.IsHistory)
                        .Select(w => (_reader.Model.GetResourceTypeId(w.ResourceTypeName), w.ResourceId, int.Parse(w.Version, CultureInfo.InvariantCulture)))
                        .ToList();
                    IReadOnlyDictionary<(short ResourceTypeId, string ResourceId, int Version), (long? TransactionId, bool WrittenAsHistory)> writeInfo =
                        await _reader.GetWriteInfoAsync(historyKeys, cancellationToken);

                    foreach (ResourceWrapper wrapper in wrappers)
                    {
                        var key = (_reader.Model.GetResourceTypeId(wrapper.ResourceTypeName), wrapper.ResourceId, int.Parse(wrapper.Version, CultureInfo.InvariantCulture));
                        bool writtenAsHistory = writeInfo.TryGetValue(key, out (long? TransactionId, bool WrittenAsHistory) info) && info.WrittenAsHistory;
                        changes.Add(_reader.CreateChange(wrapper, transactionId, writtenAsHistory));
                    }
                }

                await _reader.LoadPreviousVersionsAsync(changes, cancellationToken);
            }

            long last = transactionIds[^1];
            return new TransactionEventPage
            {
                Changes = changes,
                PositionCount = transactionIds.Count,
                LastPosition = last,
                LastPositionTime = last.ToLastUpdated(),
            };
        }

        private async Task<List<long>> NextTransactionsAsync(long position, int maxPositions, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                // Drop anything at or before the position, which also discards the cache if the cursor moved elsewhere.
                while (_fetched.Count > 0 && _fetched.Peek() <= position)
                {
                    _fetched.Dequeue();
                }
            }

            if (_fetched.Count == 0)
            {
                long visibility = await _reader.Store.MergeResourcesGetTransactionVisibilityAsync(cancellationToken);
                if (position >= visibility)
                {
                    return [];
                }

                // GetTransactions returns at most 10,000 transactions; keep them for the following pages.
                IReadOnlyList<(long TransactionId, DateTime? VisibleDate, DateTime? InvisibleHistoryRemovedDate)> transactions =
                    await _reader.Store.GetTransactionsAsync(position, visibility, cancellationToken);
                lock (_sync)
                {
                    _fetched = new Queue<long>(transactions.Select(t => t.TransactionId).OrderBy(t => t));
                }
            }

            lock (_sync)
            {
                var result = new List<long>(maxPositions);
                foreach (long transactionId in _fetched)
                {
                    if (result.Count == maxPositions)
                    {
                        break;
                    }

                    result.Add(transactionId);
                }

                return result;
            }
        }
    }
}
