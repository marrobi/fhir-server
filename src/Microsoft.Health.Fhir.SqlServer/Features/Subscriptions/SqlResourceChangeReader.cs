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
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;

namespace Microsoft.Health.Fhir.SqlServer.Features.Subscriptions
{
    /// <summary>
    /// Turns stored resource versions into <see cref="ResourceChange"/>s for the subscription event sources, loading previous versions
    /// and detecting versions that were written directly as history.
    /// </summary>
    internal sealed class SqlResourceChangeReader
    {
        private readonly SqlStoreClient _store;
        private readonly ISqlServerFhirModel _model;
        private readonly ICompressedRawResourceConverter _rawResourceConverter;
        private readonly IResourceDeserializer _deserializer;
        private readonly ILogger<SqlResourceChangeReader> _logger;

        public SqlResourceChangeReader(
            SqlStoreClient store,
            ISqlServerFhirModel model,
            ICompressedRawResourceConverter rawResourceConverter,
            IResourceDeserializer deserializer,
            ILogger<SqlResourceChangeReader> logger)
        {
            _store = EnsureArg.IsNotNull(store, nameof(store));
            _model = EnsureArg.IsNotNull(model, nameof(model));
            _rawResourceConverter = EnsureArg.IsNotNull(rawResourceConverter, nameof(rawResourceConverter));
            _deserializer = EnsureArg.IsNotNull(deserializer, nameof(deserializer));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        internal SqlStoreClient Store => _store;

        internal ISqlServerFhirModel Model => _model;

        /// <summary>
        /// Reads every version a transaction wrote, including versions that are already history.
        /// </summary>
        /// <param name="transactionId">The transaction.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The versions.</returns>
        public Task<IReadOnlyList<ResourceWrapper>> GetTransactionVersionsAsync(long transactionId, CancellationToken cancellationToken)
        {
            return _store.GetAllResourceVersionsByTransactionIdAsync(transactionId, _rawResourceConverter.ReadCompressedRawResource, _model.GetResourceTypeName, cancellationToken);
        }

        /// <summary>
        /// Reads specific resource versions, including invisible ones.
        /// </summary>
        /// <param name="keys">The versions.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The versions found, by type name, id and version.</returns>
        public async Task<Dictionary<(string ResourceType, string ResourceId, int Version), ResourceWrapper>> GetVersionsAsync(IEnumerable<(short ResourceTypeId, string ResourceId, int Version)> keys, CancellationToken cancellationToken)
        {
            List<ResourceDateKey> dateKeys = keys
                .Distinct()
                .Select(k => new ResourceDateKey(k.ResourceTypeId, k.ResourceId, 0, k.Version.ToString(CultureInfo.InvariantCulture)))
                .ToList();
            IReadOnlyList<ResourceWrapper> wrappers = await _store.GetAsync(dateKeys, _rawResourceConverter.ReadCompressedRawResource, _model.GetResourceTypeName, isReadOnly: false, cancellationToken, includeInvisible: true);
            return wrappers
                .GroupBy(w => (w.ResourceTypeName, w.ResourceId, int.Parse(w.Version, CultureInfo.InvariantCulture)))
                .ToDictionary(g => g.Key, g => g.First());
        }

        /// <summary>
        /// Returns the writing transaction of each version and whether it was written directly as history.
        /// </summary>
        /// <param name="keys">The versions.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The write information by version.</returns>
        public Task<IReadOnlyDictionary<(short ResourceTypeId, string ResourceId, int Version), (long? TransactionId, bool WrittenAsHistory)>> GetWriteInfoAsync(
            IReadOnlyCollection<(short ResourceTypeId, string ResourceId, int Version)> keys,
            CancellationToken cancellationToken)
        {
            return _store.GetVersionWriteInfoAsync(keys, cancellationToken);
        }

        /// <summary>
        /// Creates a change for a stored version.
        /// </summary>
        /// <param name="wrapper">The stored version.</param>
        /// <param name="transactionId">The transaction that wrote it.</param>
        /// <param name="writtenAsHistory">Whether it was already superseded when written.</param>
        /// <returns>The change.</returns>
        public ResourceChange CreateChange(ResourceWrapper wrapper, long transactionId, bool writtenAsHistory)
        {
            EnsureArg.IsNotNull(wrapper, nameof(wrapper));

            return new ResourceChange
            {
                TransactionId = transactionId,
                ResourceTypeId = _model.GetResourceTypeId(wrapper.ResourceTypeName),
                ResourceSurrogateId = wrapper.ResourceSurrogateId,
                ResourceType = wrapper.ResourceTypeName,
                ResourceId = wrapper.ResourceId,
                Version = int.Parse(wrapper.Version, CultureInfo.InvariantCulture),
                IsDeleted = wrapper.IsDeleted,
                LastModified = wrapper.LastModified,
                IsWrittenAsHistory = writtenAsHistory,
                Current = ToSnapshot(wrapper),
            };
        }

        /// <summary>
        /// Loads the immediately preceding version of each change that has one.
        /// </summary>
        /// <param name="changes">The changes.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task.</returns>
        public async Task LoadPreviousVersionsAsync(IReadOnlyList<ResourceChange> changes, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNull(changes, nameof(changes));

            List<ResourceChange> needPrevious = changes.Where(c => c.HasPreviousVersion).ToList();
            if (needPrevious.Count == 0)
            {
                return;
            }

            Dictionary<(string ResourceType, string ResourceId, int Version), ResourceWrapper> previous =
                await GetVersionsAsync(needPrevious.Select(c => (c.ResourceTypeId, c.ResourceId, c.Version - 1)), cancellationToken);

            foreach (ResourceChange change in needPrevious)
            {
                change.PreviousLoaded = true;
                if (previous.TryGetValue((change.ResourceType, change.ResourceId, change.Version - 1), out ResourceWrapper wrapper))
                {
                    change.PreviousIsDeleted = wrapper.IsDeleted;
                    change.Previous = ToSnapshot(wrapper);
                }
            }
        }

        private ResourceSnapshot ToSnapshot(ResourceWrapper wrapper)
        {
            if (SqlStoreClient.IsInvisible(wrapper))
            {
                return null;
            }

            try
            {
                return new ResourceSnapshot(_deserializer.Deserialize(wrapper));
            }
            catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
            {
                _logger.LogWarning(ex, "Subscription event source could not read {ResourceType}/{ResourceId}/_history/{Version}.", wrapper.ResourceTypeName, wrapper.ResourceId, wrapper.Version);
                return null;
            }
        }
    }
}
