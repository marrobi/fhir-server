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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Extensions;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Events;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Core.Logging.Metrics;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;

namespace Microsoft.Health.Fhir.SqlServer.Features.Subscriptions
{
    /// <summary>
    /// Reads committed transactions from <c>dbo.Transactions</c> up to the visibility watermark. Positions are transaction ids.
    /// Every write path that uses <c>MergeResources</c>, including <c>$import</c>, is covered.
    /// </summary>
    internal sealed class TransactionsEventSource : ITransactionEventSource
    {
        private static readonly TimeSpan LateCheckWindow = TimeSpan.FromHours(1);

        private readonly SqlResourceChangeReader _reader;
        private readonly SubscriptionsConfiguration _configuration;
        private readonly ILogger<TransactionsEventSource> _logger;
        private readonly ISubscriptionMetricHandler _metrics;
        private Queue<long> _fetched = new();
        private DateTime? _lateCheckedUntil;
        private DateTimeOffset _nextLateCheck;

        public TransactionsEventSource(SqlResourceChangeReader reader, IOptions<SubscriptionsConfiguration> configuration, ILogger<TransactionsEventSource> logger, ISubscriptionMetricHandler metrics = null)
        {
            _reader = EnsureArg.IsNotNull(reader, nameof(reader));
            _configuration = EnsureArg.IsNotNull(configuration?.Value, nameof(configuration));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
            _metrics = metrics;
        }

        public SubscriptionEventSource Kind => SubscriptionEventSource.Transactions;

        internal TimeSpan LateCheckPeriod { get; init; } = TimeSpan.FromMinutes(1);

        public string CursorId => SqlServerSubscriptionStore.CursorId;

        public string GetUnavailableReason() => null;

        public async Task<long> GetStartPositionAsync(long? earliestActivationTransactionId, CancellationToken cancellationToken)
        {
            long visibility = await _reader.Store.MergeResourcesGetTransactionVisibilityAsync(cancellationToken);
            return earliestActivationTransactionId.HasValue ? Math.Min(visibility, earliestActivationTransactionId.Value) : visibility;
        }

        public async Task<TransactionEventPage> ReadAsync(long position, int maxPositions, Func<string, bool> isCovered, CancellationToken cancellationToken)
        {
            await CheckLateTransactionsAsync(position, cancellationToken);

            List<long> transactionIds = await NextTransactionsAsync(position, Math.Max(1, maxPositions), cancellationToken);
            if (transactionIds.Count == 0)
            {
                return TransactionEventPage.Empty(position);
            }

            var changes = new List<ResourceChange>();
            if (isCovered != null)
            {
                // One read for the page. The range can include transactions that are not in the page, such as failed ones; they are skipped.
                var pageTransactions = new HashSet<long>(transactionIds);
                var versions = (await _reader.GetTransactionVersionsAsync(transactionIds[0], transactionIds[^1], cancellationToken))
                    .Where(v => pageTransactions.Contains(v.TransactionId) && isCovered(v.Wrapper.ResourceTypeName))
                    .Select(v => (v.TransactionId, v.Wrapper, Key: (_reader.Model.GetResourceTypeId(v.Wrapper.ResourceTypeName), v.Wrapper.ResourceId, int.Parse(v.Wrapper.Version, CultureInfo.InvariantCulture))))
                    .OrderBy(v => v.TransactionId)
                    .ThenBy(v => v.Wrapper.ResourceSurrogateId)
                    .ToList();

                // Only a version that is already history can have been written as history, so only those need the lookup.
                IReadOnlyDictionary<(short ResourceTypeId, string ResourceId, int Version), (long? TransactionId, bool WrittenAsHistory)> writeInfo =
                    await _reader.GetWriteInfoAsync(versions.Where(v => v.Wrapper.IsHistory).Select(v => v.Key).ToList(), cancellationToken);

                foreach ((long transactionId, ResourceWrapper wrapper, (short, string, int) key) in versions)
                {
                    bool writtenAsHistory = writeInfo.TryGetValue(key, out (long? TransactionId, bool WrittenAsHistory) info) && info.WrittenAsHistory;
                    changes.Add(_reader.CreateChange(wrapper, transactionId, writtenAsHistory));
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

        /// <summary>
        /// Reports transactions that committed below the cursor after the pump passed them, which happens when the SQL clock steps
        /// back by more than <see cref="SubscriptionsConfiguration.TransactionReadLagSeconds"/>. Their changes did not notify.
        /// </summary>
        private async Task CheckLateTransactionsAsync(long position, CancellationToken cancellationToken)
        {
            if (Clock.UtcNow < _nextLateCheck)
            {
                return;
            }

            _nextLateCheck = Clock.UtcNow + LateCheckPeriod;
            long lagIds = new DateTimeOffset(DateTime.MinValue.AddSeconds(Math.Max(0, _configuration.TransactionReadLagSeconds)), TimeSpan.Zero).ToSurrogateId();
            long fromId = _lateCheckedUntil.HasValue ? new DateTimeOffset(_lateCheckedUntil.Value - LateCheckWindow, TimeSpan.Zero).ToSurrogateId() : position;
            (int count, DateTime until) = await _reader.Store.GetLateTransactionCountAsync(fromId, position, lagIds, _lateCheckedUntil, cancellationToken);
            _lateCheckedUntil = until;
            if (count > 0)
            {
                _logger.LogWarning("Subscription event pump: {Count} transactions committed after the pump had passed them, so their changes did not notify. Increase TransactionReadLagSeconds if this recurs.", count);
                _metrics?.LateTransactions(count);
            }
        }

        private async Task<List<long>> NextTransactionsAsync(long position, int maxPositions, CancellationToken cancellationToken)
        {
            // Drop anything at or before the position, which also discards the cache if the cursor moved elsewhere.
            while (_fetched.Count > 0 && _fetched.Peek() <= position)
            {
                _fetched.Dequeue();
            }

            if (_fetched.Count == 0)
            {
                long end = await _reader.Store.MergeResourcesGetTransactionVisibilityAsync(cancellationToken);
                if (_configuration.TransactionReadLagSeconds > 0)
                {
                    end = Math.Min(end, Clock.UtcNow.AddSeconds(-_configuration.TransactionReadLagSeconds).ToSurrogateId());
                }

                if (position >= end)
                {
                    return [];
                }

                // GetTransactions returns at most 10,000 transactions; keep them for the following pages.
                IReadOnlyList<(long TransactionId, DateTime? VisibleDate, DateTime? InvisibleHistoryRemovedDate)> transactions =
                    await _reader.Store.GetTransactionsAsync(position, end, cancellationToken);
                _fetched = new Queue<long>(transactions.Select(t => t.TransactionId).OrderBy(t => t));
            }

            return _fetched.Take(maxPositions).ToList();
        }
    }
}
