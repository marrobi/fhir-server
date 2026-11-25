// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Extensions.Options;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Events;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.SqlServer.Features.ChangeFeed;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;

namespace Microsoft.Health.Fhir.SqlServer.Features.Subscriptions
{
    /// <summary>
    /// Reads the <c>ResourceChangeData</c> change feed. Positions are change record ids. It needs
    /// <c>CoreFeatures:SupportsResourceChangeCapture</c>, covers only the writes the change feed records, and its reader takes a
    /// table lock that can block concurrent writes (ADR-2611, option 2).
    /// </summary>
    internal sealed class ResourceChangeDataEventSource : ITransactionEventSource
    {
        public const string ChangeFeedCursorId = "SubscriptionEventWatchdog.ChangeFeedCursor";

        private const byte DeletionChangeType = 2;

        // Changes are recorded when a transaction writes, which may be later than the transaction began, so start a margin earlier.
        // Starting early only re-reads changes that the activation watermark and the outbox key already exclude.
        private static readonly TimeSpan StartMargin = TimeSpan.FromMinutes(10);

        private readonly SqlServerFhirResourceChangeDataStore _changeFeed;
        private readonly SqlResourceChangeReader _reader;
        private readonly CoreFeatureConfiguration _coreFeatures;

        // The timestamp of the last record read, and its id. Only valid as a partition hint when the next read starts there: a page
        // that is read again (not committed, or stale) starts earlier, and records before this timestamp would be skipped.
        private (long Position, DateTime Timestamp)? _lastRead;

        public ResourceChangeDataEventSource(SqlServerFhirResourceChangeDataStore changeFeed, SqlResourceChangeReader reader, IOptions<CoreFeatureConfiguration> coreFeatures)
        {
            _changeFeed = EnsureArg.IsNotNull(changeFeed, nameof(changeFeed));
            _reader = EnsureArg.IsNotNull(reader, nameof(reader));
            _coreFeatures = EnsureArg.IsNotNull(coreFeatures?.Value, nameof(coreFeatures));
        }

        public SubscriptionEventSource Kind => SubscriptionEventSource.ResourceChangeData;

        public string CursorId => ChangeFeedCursorId;

        public string GetUnavailableReason() =>
            _coreFeatures.SupportsResourceChangeCapture ? null : "The ResourceChangeData event source requires FhirServer:CoreFeatures:SupportsResourceChangeCapture to be true.";

        public async Task<long> GetStartPositionAsync(long? earliestActivationTransactionId, CancellationToken cancellationToken)
        {
            if (earliestActivationTransactionId == null)
            {
                return await _reader.Store.GetResourceChangeDataIdBeforeAsync(null, cancellationToken);
            }

            if (earliestActivationTransactionId.Value <= 0)
            {
                return 0;
            }

            DateTime before = earliestActivationTransactionId.Value.ToLastUpdated().UtcDateTime - StartMargin;
            return await _reader.Store.GetResourceChangeDataIdBeforeAsync(before, cancellationToken);
        }

        public async Task<TransactionEventPage> ReadAsync(long position, int maxPositions, Func<string, bool> isCovered, CancellationToken cancellationToken)
        {
            short pageSize = (short)Math.Clamp(maxPositions, 1, short.MaxValue);

            // The start id is inclusive. The last timestamp only narrows which partitions are searched.
            IReadOnlyCollection<ResourceChangeData> records = _lastRead?.Position == position
                ? await _changeFeed.GetRecordsAsync(position + 1, _lastRead.Value.Timestamp, pageSize, cancellationToken)
                : await _changeFeed.GetRecordsAsync(position + 1, pageSize, cancellationToken);
            if (records.Count == 0)
            {
                return TransactionEventPage.Empty(position);
            }

            List<ResourceChangeData> ordered = records.OrderBy(r => r.Id).ToList();
            var changes = new List<ResourceChange>();
            if (isCovered != null)
            {
                List<ResourceChangeData> covered = ordered.Where(r => isCovered(r.ResourceTypeName)).ToList();
                List<(short ResourceTypeId, string ResourceId, int Version)> keys = covered.Select(r => (r.ResourceTypeId, r.ResourceId, r.ResourceVersion)).Distinct().ToList();
                Dictionary<(string ResourceType, string ResourceId, int Version), ResourceWrapper> versions = await _reader.GetVersionsAsync(keys, cancellationToken);
                IReadOnlyDictionary<(short ResourceTypeId, string ResourceId, int Version), (long? TransactionId, bool WrittenAsHistory)> writeInfo = await _reader.GetWriteInfoAsync(keys, cancellationToken);

                foreach (ResourceChangeData record in covered)
                {
                    writeInfo.TryGetValue((record.ResourceTypeId, record.ResourceId, record.ResourceVersion), out (long? TransactionId, bool WrittenAsHistory) info);
                    if (versions.TryGetValue((record.ResourceTypeName, record.ResourceId, record.ResourceVersion), out ResourceWrapper wrapper))
                    {
                        changes.Add(_reader.CreateChange(wrapper, info.TransactionId ?? wrapper.ResourceSurrogateId, info.WrittenAsHistory));
                    }
                    else
                    {
                        // The version is gone, for example overwritten on a no-version type. Keep it so it is recorded as unevaluable.
                        // The time it was recorded stands in for its transaction. Every record of one write shares that time, so the
                        // change record id, which is unique, keeps the outbox key of each record distinct.
                        long recordedAt = new DateTimeOffset(record.Timestamp, TimeSpan.Zero).ToSurrogateId();
                        changes.Add(new ResourceChange
                        {
                            TransactionId = recordedAt,
                            ResourceTypeId = record.ResourceTypeId,
                            ResourceSurrogateId = record.Id,
                            ResourceType = record.ResourceTypeName,
                            ResourceId = record.ResourceId,
                            Version = record.ResourceVersion,
                            IsDeleted = record.ResourceChangeTypeId == DeletionChangeType,
                            LastModified = new DateTimeOffset(record.Timestamp, TimeSpan.Zero),
                        });
                    }
                }

                await _reader.LoadPreviousVersionsAsync(changes, cancellationToken);
            }

            ResourceChangeData last = ordered[^1];
            _lastRead = (last.Id, last.Timestamp);
            return new TransactionEventPage
            {
                Changes = changes,
                PositionCount = ordered.Count,
                LastPosition = last.Id,
                LastPositionTime = new DateTimeOffset(last.Timestamp, TimeSpan.Zero),
            };
        }
    }
}
