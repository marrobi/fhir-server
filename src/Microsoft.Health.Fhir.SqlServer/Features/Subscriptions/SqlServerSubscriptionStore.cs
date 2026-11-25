// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EnsureThat;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Matching;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Models;
using Microsoft.Health.Fhir.Core.Features.Subscriptions.Persistence;
using Microsoft.Health.Fhir.SqlServer.Features.Schema;
using Microsoft.Health.Fhir.SqlServer.Features.Schema.Model;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.SqlServer.Features.Schema;

namespace Microsoft.Health.Fhir.SqlServer.Features.Subscriptions
{
    /// <summary>
    /// SQL Server storage for subscription state and outbox events (schema version 123).
    /// </summary>
    internal sealed class SqlServerSubscriptionStore : ISubscriptionStore
    {
        /// <summary>
        /// The <c>dbo.Parameters</c> id of the event pump cursor: the last transaction whose matches are committed.
        /// </summary>
        public const string CursorId = "SubscriptionEventWatchdog.Cursor";

        /// <summary>
        /// The <c>dbo.Parameters</c> id recording which event source the pump last used.
        /// </summary>
        public const string EventSourceId = "SubscriptionEventWatchdog.Source";

        /// <summary>
        /// The <c>dbo.Parameters</c> id of the counter that every activation increments.
        /// </summary>
        public const string ActivationVersionId = "Subscriptions.ActivationVersion";

        private readonly ISqlRetryService _sqlRetryService;
        private readonly ISqlServerFhirModel _model;
        private readonly SchemaInformation _schemaInformation;
        private readonly ILogger<SqlServerSubscriptionStore> _logger;

        public SqlServerSubscriptionStore(ISqlRetryService sqlRetryService, ISqlServerFhirModel model, SchemaInformation schemaInformation, ILogger<SqlServerSubscriptionStore> logger)
        {
            _sqlRetryService = EnsureArg.IsNotNull(sqlRetryService, nameof(sqlRetryService));
            _model = EnsureArg.IsNotNull(model, nameof(model));
            _schemaInformation = EnsureArg.IsNotNull(schemaInformation, nameof(schemaInformation));
            _logger = EnsureArg.IsNotNull(logger, nameof(logger));
        }

        /// <summary>
        /// Gets whether the database schema has the subscription tables and procedures. During a rolling upgrade the server can
        /// run before the schema reaches <see cref="SchemaVersionConstants.Subscriptions"/>.
        /// </summary>
        public SubscriptionStoreAvailability Availability =>
            _schemaInformation.Current >= SchemaVersionConstants.Subscriptions ? SubscriptionStoreAvailability.Available : SubscriptionStoreAvailability.NotReady;

        public async Task<IReadOnlyList<SubscriptionState>> GetStatesAsync(CancellationToken cancellationToken)
        {
            await using SqlCommand cmd = Procedure("dbo.GetSubscriptionStates");
            return await cmd.ExecuteReaderAsync(_sqlRetryService, ReadState, _logger, cancellationToken);
        }

        public async Task<SubscriptionState> GetStateAsync(string subscriptionId, CancellationToken cancellationToken)
        {
            await using SqlCommand cmd = Procedure("dbo.GetSubscriptionStates");
            cmd.Parameters.AddWithValue("@SubscriptionId", subscriptionId);
            IReadOnlyList<SubscriptionState> states = await cmd.ExecuteReaderAsync(_sqlRetryService, ReadState, _logger, cancellationToken);
            return states.Count > 0 ? states[0] : null;
        }

        public async Task SaveGrantAsync(string subscriptionId, string contentHash, string grant, CancellationToken cancellationToken)
        {
            await using SqlCommand cmd = Procedure("dbo.SaveSubscriptionGrant");
            cmd.Parameters.AddWithValue("@SubscriptionId", subscriptionId);
            cmd.Parameters.AddWithValue("@ContentHash", contentHash);
            cmd.Parameters.AddWithValue("@GrantJson", grant);
            await cmd.ExecuteNonQueryAsync(_sqlRetryService, _logger, cancellationToken);
        }

        public async Task DeleteStateAsync(string subscriptionId, CancellationToken cancellationToken)
        {
            await using SqlCommand cmd = Procedure("dbo.DeleteSubscriptionState");
            cmd.Parameters.AddWithValue("@SubscriptionId", subscriptionId);
            await cmd.ExecuteNonQueryAsync(_sqlRetryService, _logger, cancellationToken);
        }

        public async Task<long> ActivateAsync(string subscriptionId, CancellationToken cancellationToken)
        {
            await using SqlCommand cmd = Procedure("dbo.ActivateSubscription");
            cmd.Parameters.AddWithValue("@SubscriptionId", subscriptionId);
            SqlParameter output = cmd.Parameters.Add("@ActivationTransactionId", SqlDbType.BigInt);
            output.Direction = ParameterDirection.Output;
            await cmd.ExecuteNonQueryAsync(_sqlRetryService, _logger, cancellationToken);
            return (long)output.Value;
        }

        public async Task DeactivateAsync(string subscriptionId, bool revokeGrant, CancellationToken cancellationToken)
        {
            await using SqlCommand cmd = Procedure("dbo.DeactivateSubscription");
            cmd.Parameters.AddWithValue("@SubscriptionId", subscriptionId);
            cmd.Parameters.AddWithValue("@RevokeGrant", revokeGrant);
            await cmd.ExecuteNonQueryAsync(_sqlRetryService, _logger, cancellationToken);
        }

        public async Task<bool> TryAcquireLeaseAsync(string subscriptionId, string worker, TimeSpan leasePeriod, CancellationToken cancellationToken)
        {
            await using SqlCommand cmd = Procedure("dbo.AcquireSubscriptionLease");
            cmd.Parameters.AddWithValue("@SubscriptionId", subscriptionId);
            cmd.Parameters.AddWithValue("@Worker", worker);
            cmd.Parameters.AddWithValue("@LeasePeriodSec", (int)Math.Ceiling(leasePeriod.TotalSeconds));
            SqlParameter output = cmd.Parameters.Add("@IsAcquired", SqlDbType.Bit);
            output.Direction = ParameterDirection.Output;
            await cmd.ExecuteNonQueryAsync(_sqlRetryService, _logger, cancellationToken);
            return (bool)output.Value;
        }

        public async Task ReleaseLeaseAsync(string subscriptionId, string worker, CancellationToken cancellationToken)
        {
            await using SqlCommand cmd = Procedure("dbo.ReleaseSubscriptionLease");
            cmd.Parameters.AddWithValue("@SubscriptionId", subscriptionId);
            cmd.Parameters.AddWithValue("@Worker", worker);
            await cmd.ExecuteNonQueryAsync(_sqlRetryService, _logger, cancellationToken);
        }

        public async Task<IReadOnlyList<SubscriptionEventRecord>> GetPendingEventsAsync(string subscriptionId, int maxCount, CancellationToken cancellationToken)
        {
            return await GetEventsAsync(subscriptionId, SubscriptionEventStatus.Pending, maxCount, cancellationToken);
        }

        /// <summary>
        /// Returns the oldest events of a subscription with the given status.
        /// </summary>
        /// <param name="subscriptionId">The subscription id.</param>
        /// <param name="status">The status.</param>
        /// <param name="maxCount">The maximum number of events.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The events in event-number order.</returns>
        public async Task<IReadOnlyList<SubscriptionEventRecord>> GetEventsAsync(string subscriptionId, SubscriptionEventStatus status, int maxCount, CancellationToken cancellationToken)
        {
            await using SqlCommand cmd = Procedure("dbo.GetSubscriptionEvents");
            cmd.Parameters.AddWithValue("@SubscriptionId", subscriptionId);
            cmd.Parameters.AddWithValue("@Status", (byte)status);
            cmd.Parameters.AddWithValue("@MaxCount", maxCount);
            return await cmd.ExecuteReaderAsync(
                _sqlRetryService,
                reader => new SubscriptionEventRecord
                {
                    SubscriptionId = reader.GetString(0),
                    EventNumber = reader.GetInt64(1),
                    TransactionId = reader.GetInt64(2),
                    ResourceType = _model.GetResourceTypeName(reader.GetInt16(3)),
                    ResourceId = reader.GetString(4),
                    ResourceVersion = reader.GetInt32(5),
                    Interaction = (SubscriptionInteraction)reader.GetByte(6),
                    Status = (SubscriptionEventStatus)reader.GetByte(7),
                    Attempts = reader.GetInt32(8),
                    NextAttemptUtc = AsUtc(reader.GetDateTime(9)),
                    EventUtc = AsUtc(reader.GetDateTime(10)),
                },
                _logger,
                cancellationToken);
        }

        public async Task SetEventStatusAsync(string subscriptionId, IReadOnlyList<long> eventNumbers, SubscriptionEventStatus status, CancellationToken cancellationToken)
        {
            await UpdateEventsAsync(subscriptionId, eventNumbers, status, incrementAttempts: false, nextAttemptUtc: null, error: null, cancellationToken);
        }

        public async Task RecordFailedAttemptAsync(string subscriptionId, IReadOnlyList<long> eventNumbers, DateTimeOffset nextAttemptUtc, string error, CancellationToken cancellationToken)
        {
            await UpdateEventsAsync(subscriptionId, eventNumbers, status: null, incrementAttempts: true, nextAttemptUtc, error, cancellationToken);
        }

        public async Task SetErrorAsync(string subscriptionId, string error, CancellationToken cancellationToken)
        {
            await using SqlCommand cmd = Procedure("dbo.SetSubscriptionError");
            cmd.Parameters.AddWithValue("@SubscriptionId", subscriptionId);
            cmd.Parameters.AddWithValue("@ErrorText", (object)Truncate(error) ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(_sqlRetryService, _logger, cancellationToken);
        }

        public async Task SetLastNotificationAsync(string subscriptionId, DateTimeOffset sentUtc, CancellationToken cancellationToken)
        {
            await using SqlCommand cmd = Procedure("dbo.SetSubscriptionLastNotification");
            cmd.Parameters.AddWithValue("@SubscriptionId", subscriptionId);
            cmd.Parameters.AddWithValue("@LastNotificationDate", sentUtc.UtcDateTime);
            await cmd.ExecuteNonQueryAsync(_sqlRetryService, _logger, cancellationToken);
        }

        public async Task<SubscriptionEventCounts> GetEventCountsAsync(string subscriptionId, CancellationToken cancellationToken)
        {
            await using SqlCommand cmd = Procedure("dbo.GetSubscriptionEventCounts");
            cmd.Parameters.AddWithValue("@SubscriptionId", subscriptionId);
            return (await cmd.ExecuteReaderAsync(_sqlRetryService, reader => new SubscriptionEventCounts(reader.GetInt32(0), reader.GetInt32(1)), _logger, cancellationToken))[0];
        }

        public async Task<IReadOnlyList<string>> GetSubscriptionsWithDueEventsAsync(CancellationToken cancellationToken)
        {
            await using SqlCommand cmd = Procedure("dbo.GetSubscriptionsWithDueEvents");
            return await cmd.ExecuteReaderAsync(_sqlRetryService, reader => reader.GetString(0), _logger, cancellationToken);
        }

        /// <summary>
        /// Appends matched events and advances the named pump cursor to <paramref name="cursorPosition"/> in one transaction.
        /// </summary>
        /// <param name="events">The events in change order.</param>
        /// <param name="cursorId">The cursor of the event source the events came from.</param>
        /// <param name="cursorPosition">The last source position covered by <paramref name="events"/>.</param>
        /// <param name="activationVersion">
        /// The activation counter read before the subscription states the events were matched with, or null to skip the check.
        /// </param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>False, with nothing written, when a subscription was activated after <paramref name="activationVersion"/> was read.</returns>
        public async Task<bool> EnqueueAsync(IReadOnlyList<SubscriptionEventCandidate> events, string cursorId, long cursorPosition, long? activationVersion, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(cursorId, nameof(cursorId));
            EnsureArg.IsNotNull(events, nameof(events));

            int ordinal = 0;
            var rows = new List<SubscriptionEventListRow>(events.Count);
            var seen = new HashSet<(string, long, short, long)>();
            foreach (SubscriptionEventCandidate e in events)
            {
                if (seen.Add((e.SubscriptionId, e.Change.TransactionId, e.Change.ResourceTypeId, e.Change.ResourceSurrogateId)))
                {
                    rows.Add(new SubscriptionEventListRow(
                        e.SubscriptionId,
                        e.Change.TransactionId,
                        e.Change.ResourceTypeId,
                        e.Change.ResourceSurrogateId,
                        e.Change.ResourceId,
                        e.Change.Version,
                        (byte)e.Change.Interaction,
                        (byte)e.Status,
                        e.Change.LastModified.UtcDateTime,
                        ordinal++));
                }
            }

            await using SqlCommand cmd = Procedure("dbo.EnqueueSubscriptionEvents");
            new SubscriptionEventListTableValuedParameterDefinition("@Events").AddParameter(cmd.Parameters, rows);
            cmd.Parameters.AddWithValue("@CursorId", cursorId);
            cmd.Parameters.AddWithValue("@CursorTransactionId", cursorPosition);
            cmd.Parameters.Add(new SqlParameter("@ActivationVersion", SqlDbType.BigInt) { Value = activationVersion.HasValue ? activationVersion.Value : DBNull.Value });
            SqlParameter stale = cmd.Parameters.Add("@IsStale", SqlDbType.Bit);
            stale.Direction = ParameterDirection.Output;
            await cmd.ExecuteNonQueryAsync(_sqlRetryService, _logger, cancellationToken);
            return !(stale.Value is bool isStale && isStale);
        }

        /// <summary>
        /// Reads the activation counter. Read it before the subscription states used for matching, and pass it to
        /// <see cref="EnqueueAsync(IReadOnlyList{SubscriptionEventCandidate}, string, long, long?, CancellationToken)"/>.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The counter; zero before the first activation.</returns>
        public async Task<long> GetActivationVersionAsync(CancellationToken cancellationToken)
        {
            return await GetBigintParameterAsync(ActivationVersionId, cancellationToken) ?? 0;
        }

        /// <summary>
        /// Reads which event source the pump last used, or null if it has not recorded one.
        /// </summary>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The source name.</returns>
        public async Task<string> GetEventSourceAsync(CancellationToken cancellationToken)
        {
            await using var cmd = new SqlCommand("SELECT Char FROM dbo.Parameters WHERE Id = @Id");
            cmd.Parameters.AddWithValue("@Id", EventSourceId);
            object value = await cmd.ExecuteScalarAsync(_sqlRetryService, _logger, cancellationToken);
            return value == null || value == DBNull.Value ? null : (string)value;
        }

        /// <summary>
        /// Records the event source the pump uses and sets that source's cursor to <paramref name="position"/>. Used once, when the
        /// pump first runs.
        /// </summary>
        /// <param name="source">The source name.</param>
        /// <param name="cursorId">The source's cursor id.</param>
        /// <param name="position">The position to start after, or null to keep the cursor.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>A task.</returns>
        public async Task SetEventSourceAsync(string source, string cursorId, long? position, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(source, nameof(source));
            EnsureArg.IsNotNullOrWhiteSpace(cursorId, nameof(cursorId));

            await using var cmd = new SqlCommand(@"
set xact_abort on
BEGIN TRANSACTION
IF @Position IS NOT NULL
BEGIN
  UPDATE dbo.Parameters SET Bigint = @Position, UpdatedDate = getUTCdate() WHERE Id = @CursorId
  IF @@rowcount = 0
    INSERT INTO dbo.Parameters (Id, Bigint, UpdatedDate) VALUES (@CursorId, @Position, getUTCdate())
END
UPDATE dbo.Parameters SET Char = @Source, UpdatedDate = getUTCdate() WHERE Id = @SourceId
IF @@rowcount = 0
  INSERT INTO dbo.Parameters (Id, Char, UpdatedDate) VALUES (@SourceId, @Source, getUTCdate())
COMMIT TRANSACTION");
            cmd.Parameters.AddWithValue("@CursorId", cursorId);
            cmd.Parameters.Add(new SqlParameter("@Position", SqlDbType.BigInt) { Value = position.HasValue ? position.Value : DBNull.Value });
            cmd.Parameters.AddWithValue("@SourceId", EventSourceId);
            cmd.Parameters.Add(new SqlParameter("@Source", SqlDbType.VarChar, 4000) { Value = source });
            await cmd.ExecuteNonQueryAsync(_sqlRetryService, _logger, cancellationToken);
        }

        /// <summary>
        /// Reads the named pump cursor, or null when the pump has never run for that event source.
        /// </summary>
        /// <param name="cursorId">The cursor of an event source.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>The cursor.</returns>
        public async Task<long?> GetCursorAsync(string cursorId, CancellationToken cancellationToken)
        {
            EnsureArg.IsNotNullOrWhiteSpace(cursorId, nameof(cursorId));
            return await GetBigintParameterAsync(cursorId, cancellationToken);
        }

        private async Task<long?> GetBigintParameterAsync(string id, CancellationToken cancellationToken)
        {
            await using var cmd = new SqlCommand("SELECT Bigint FROM dbo.Parameters WHERE Id = @Id");
            cmd.Parameters.AddWithValue("@Id", id);
            object value = await cmd.ExecuteScalarAsync(_sqlRetryService, _logger, cancellationToken);
            return value == null || value == DBNull.Value ? null : (long)value;
        }

        /// <summary>
        /// Deletes delivered, unevaluable and not-authorized events older than the retention period.
        /// </summary>
        /// <param name="retentionDays">The retention period in days.</param>
        /// <param name="maxDuration">The time after which no new delete batch starts, or null to delete everything that is due.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns>True when every due event was deleted; false when <paramref name="maxDuration"/> stopped the cleanup first.</returns>
        public async Task<bool> CleanupEventsAsync(int retentionDays, TimeSpan? maxDuration, CancellationToken cancellationToken)
        {
            await using SqlCommand cmd = Procedure("dbo.CleanupSubscriptionEvents");

            // The budget only stops new batches; allow the last batch to finish.
            cmd.CommandTimeout = maxDuration.HasValue ? (int)maxDuration.Value.TotalSeconds + 60 : 600;
            cmd.Parameters.AddWithValue("@RetentionDays", retentionDays);
            cmd.Parameters.AddWithValue("@MaxSeconds", maxDuration.HasValue ? (int)Math.Ceiling(maxDuration.Value.TotalSeconds) : DBNull.Value);
            var isComplete = new SqlParameter("@IsComplete", SqlDbType.Bit) { Direction = ParameterDirection.Output };
            cmd.Parameters.Add(isComplete);
            await cmd.ExecuteNonQueryAsync(_sqlRetryService, _logger, cancellationToken);
            return isComplete.Value is bool complete && complete;
        }

        private async Task UpdateEventsAsync(string subscriptionId, IReadOnlyList<long> eventNumbers, SubscriptionEventStatus? status, bool incrementAttempts, DateTimeOffset? nextAttemptUtc, string error, CancellationToken cancellationToken)
        {
            if (eventNumbers == null || eventNumbers.Count == 0)
            {
                return;
            }

            await using SqlCommand cmd = Procedure("dbo.UpdateSubscriptionEvents");
            cmd.Parameters.AddWithValue("@SubscriptionId", subscriptionId);
            new BigintListTableValuedParameterDefinition("@EventNumbers").AddParameter(cmd.Parameters, eventNumbers.Distinct().Select(n => new BigintListRow(n)));
            cmd.Parameters.AddWithValue("@Status", status.HasValue ? (byte)status.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@IncrementAttempts", incrementAttempts);
            cmd.Parameters.AddWithValue("@NextAttemptDate", nextAttemptUtc.HasValue ? nextAttemptUtc.Value.UtcDateTime : DBNull.Value);
            cmd.Parameters.AddWithValue("@Error", (object)Truncate(error) ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(_sqlRetryService, _logger, cancellationToken);
        }

        private static SubscriptionState ReadState(SqlDataReader reader)
        {
            return new SubscriptionState
            {
                SubscriptionId = reader.GetString(0),
                ContentHash = reader.IsDBNull(1) ? null : reader.GetString(1),
                Grant = reader.IsDBNull(2) ? null : reader.GetString(2),
                ActivationTransactionId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
                LastEventNumber = reader.GetInt64(4),
                LastNotificationUtc = reader.IsDBNull(5) ? null : AsUtc(reader.GetDateTime(5)),
                Error = reader.IsDBNull(6) ? null : reader.GetString(6),
            };
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100:Review SQL queries for security vulnerabilities", Justification = "Only constant stored procedure names are passed.")]
        private static SqlCommand Procedure(string name) => new(name) { CommandType = CommandType.StoredProcedure };

        private static DateTimeOffset AsUtc(DateTime value) => new(DateTime.SpecifyKind(value, DateTimeKind.Utc));

        private static string Truncate(string value) => value == null || value.Length <= 1000 ? value : value.Substring(0, 1000);
    }
}
