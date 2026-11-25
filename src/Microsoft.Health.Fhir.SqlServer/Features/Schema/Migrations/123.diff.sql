IF type_id('dbo.SubscriptionEventList') IS NULL
BEGIN
  CREATE TYPE dbo.SubscriptionEventList AS TABLE
  (
       SubscriptionId      varchar(128)  COLLATE Latin1_General_100_CS_AS NOT NULL
      ,TransactionId       bigint       NOT NULL
      ,ResourceTypeId      smallint     NOT NULL
      ,ResourceSurrogateId bigint       NOT NULL
      ,ResourceId          varchar(128)  COLLATE Latin1_General_100_CS_AS NOT NULL
      ,ResourceVersion     int          NOT NULL
      ,Interaction         tinyint      NOT NULL
      ,Status              tinyint      NOT NULL
      ,EventDate           datetime2(7) NOT NULL
      ,Ordinal             int          NOT NULL

       PRIMARY KEY (SubscriptionId, TransactionId, ResourceTypeId, ResourceSurrogateId)
  )
END
GO

-- Server-owned runtime state of FHIR subscriptions (ADR-2611): the activation grant bound to the subscription content,
-- the activation watermark (only later transactions are matched), the event counter, and the per-subscription delivery lease.
IF object_id('dbo.SubscriptionStates') IS NULL
BEGIN
  CREATE TABLE dbo.SubscriptionStates
    (
       SubscriptionId          varchar(128)  COLLATE Latin1_General_100_CS_AS NOT NULL
      ,ContentHash             varchar(64)   NULL
      ,GrantJson               nvarchar(max) NULL
      ,ActivationTransactionId bigint        NULL
      ,LastEventNumber         bigint        NOT NULL CONSTRAINT DF_SubscriptionStates_LastEventNumber DEFAULT 0
      ,LastNotificationDate    datetime      NULL
      ,ErrorText               nvarchar(1000) NULL
      ,LeaseHolder             varchar(100)  NULL
      ,LeaseEndDate            datetime      NULL
      ,CreatedDate             datetime      NOT NULL CONSTRAINT DF_SubscriptionStates_CreatedDate DEFAULT getUTCdate()
      ,UpdatedDate             datetime      NOT NULL CONSTRAINT DF_SubscriptionStates_UpdatedDate DEFAULT getUTCdate()

       CONSTRAINT PKC_SubscriptionStates_SubscriptionId PRIMARY KEY CLUSTERED (SubscriptionId)
    )
END
GO

-- Durable per-subscription outbox of matched events (ADR-2611). Event numbers are assigned when events are inserted,
-- together with the event pump cursor, and never change; retries reuse them.
-- Status: 0 = pending, 1 = delivered, 2 = unevaluable, 3 = not authorized at delivery.
IF object_id('dbo.SubscriptionEvents') IS NULL
BEGIN
  CREATE TABLE dbo.SubscriptionEvents
    (
       SubscriptionId      varchar(128)  COLLATE Latin1_General_100_CS_AS NOT NULL
      ,EventNumber         bigint         NOT NULL
      ,TransactionId       bigint         NOT NULL
      ,ResourceTypeId      smallint       NOT NULL
      ,ResourceSurrogateId bigint         NOT NULL
      ,ResourceId          varchar(128)  COLLATE Latin1_General_100_CS_AS NOT NULL
      ,ResourceVersion     int            NOT NULL
      ,Interaction         tinyint        NOT NULL
      ,Status              tinyint        NOT NULL
      ,Attempts            int            NOT NULL CONSTRAINT DF_SubscriptionEvents_Attempts DEFAULT 0
      ,NextAttemptDate     datetime       NOT NULL CONSTRAINT DF_SubscriptionEvents_NextAttemptDate DEFAULT getUTCdate()
      ,EventDate           datetime2(7)   NOT NULL
      ,StatusDate          datetime       NOT NULL CONSTRAINT DF_SubscriptionEvents_StatusDate DEFAULT getUTCdate()
      ,LastError           nvarchar(1000) NULL

       CONSTRAINT PKC_SubscriptionEvents_SubscriptionId_EventNumber PRIMARY KEY CLUSTERED (SubscriptionId, EventNumber)
    )
END
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = object_id('dbo.SubscriptionEvents') AND name = 'IXU_SubscriptionEvents_SubscriptionId_TransactionId_ResourceTypeId_ResourceSurrogateId')
  CREATE UNIQUE INDEX IXU_SubscriptionEvents_SubscriptionId_TransactionId_ResourceTypeId_ResourceSurrogateId ON dbo.SubscriptionEvents (SubscriptionId, TransactionId, ResourceTypeId, ResourceSurrogateId)
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = object_id('dbo.SubscriptionEvents') AND name = 'IX_SubscriptionEvents_SubscriptionId_Status_EventNumber')
  CREATE INDEX IX_SubscriptionEvents_SubscriptionId_Status_EventNumber ON dbo.SubscriptionEvents (SubscriptionId, Status, EventNumber) INCLUDE (NextAttemptDate)
GO

IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = object_id('dbo.SubscriptionEvents') AND name = 'IX_SubscriptionEvents_StatusDate')
  CREATE INDEX IX_SubscriptionEvents_StatusDate ON dbo.SubscriptionEvents (StatusDate) WHERE Status <> 0
GO

-- Acquires or renews the per-subscription lease that makes lifecycle and delivery work single-flight.
CREATE OR ALTER PROCEDURE dbo.AcquireSubscriptionLease @SubscriptionId varchar(128), @Worker varchar(100), @LeasePeriodSec int, @IsAcquired bit = 0 OUT
AS
set nocount on
set xact_abort on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')+' W='+isnull(@Worker,'NULL')
       ,@st datetime = getUTCdate()

BEGIN TRY
  SET @IsAcquired = 0

  BEGIN TRANSACTION

  UPDATE dbo.SubscriptionStates WITH (UPDLOCK, HOLDLOCK)
    SET LeaseHolder = @Worker
       ,LeaseEndDate = dateadd(second, @LeasePeriodSec, getUTCdate())
    WHERE SubscriptionId = @SubscriptionId
      AND (LeaseHolder IS NULL OR LeaseHolder = @Worker OR LeaseEndDate < getUTCdate())

  IF @@rowcount > 0
    SET @IsAcquired = 1
  ELSE IF NOT EXISTS (SELECT * FROM dbo.SubscriptionStates WHERE SubscriptionId = @SubscriptionId)
  BEGIN
    -- Subscriptions written without a grant (for example, by $import) have no state yet.
    INSERT INTO dbo.SubscriptionStates (SubscriptionId, LeaseHolder, LeaseEndDate) SELECT @SubscriptionId, @Worker, dateadd(second, @LeasePeriodSec, getUTCdate())
    SET @IsAcquired = 1
  END

  COMMIT TRANSACTION

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Text=@IsAcquired
END TRY
BEGIN CATCH
  IF @@trancount > 0 ROLLBACK TRANSACTION
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO

-- Sets the activation watermark to the current transaction visibility if it is not already set, and makes retained
-- pending events due again. Only transactions after the watermark are matched for the subscription. Each activation
-- increments Subscriptions.ActivationVersion, which the event pump checks so it never commits events matched without it.
CREATE OR ALTER PROCEDURE dbo.ActivateSubscription @SubscriptionId varchar(128), @ActivationTransactionId bigint = NULL OUT
AS
set nocount on
set xact_abort on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')
       ,@st datetime = getUTCdate()
       ,@Visibility bigint

BEGIN TRY
  BEGIN TRANSACTION

  -- The counter is locked first, in the same order as EnqueueSubscriptionEvents (counter, state, events), to avoid deadlocks.
  UPDATE dbo.Parameters WITH (UPDLOCK, HOLDLOCK) SET Bigint = isnull(Bigint, 0) + 1, UpdatedDate = getUTCdate() WHERE Id = 'Subscriptions.ActivationVersion'
  IF @@rowcount = 0
    INSERT INTO dbo.Parameters (Id, Bigint, UpdatedDate) SELECT 'Subscriptions.ActivationVersion', 1, getUTCdate()

  -- Read after the counter is locked, so a pump commit cannot move the cursor past the watermark without seeing this activation.
  EXECUTE dbo.MergeResourcesGetTransactionVisibility @Visibility OUT

  UPDATE dbo.SubscriptionStates
    SET ActivationTransactionId = isnull(ActivationTransactionId, @Visibility)
       ,UpdatedDate = getUTCdate()
    WHERE SubscriptionId = @SubscriptionId

  IF @@rowcount = 0
    INSERT INTO dbo.SubscriptionStates (SubscriptionId, ActivationTransactionId) SELECT @SubscriptionId, @Visibility

  UPDATE dbo.SubscriptionEvents SET Attempts = 0, NextAttemptDate = getUTCdate() WHERE SubscriptionId = @SubscriptionId AND Status = 0

  SET @ActivationTransactionId = (SELECT ActivationTransactionId FROM dbo.SubscriptionStates WHERE SubscriptionId = @SubscriptionId)

  COMMIT TRANSACTION

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Text=@ActivationTransactionId
END TRY
BEGIN CATCH
  IF @@trancount > 0 ROLLBACK TRANSACTION
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO

-- Removes delivered, unevaluable and not-authorized events older than the retention period. Pending events are kept.
-- Deletes in batches below the lock escalation threshold, and stops starting batches after @MaxSeconds when given.
CREATE OR ALTER PROCEDURE dbo.CleanupSubscriptionEvents @RetentionDays int, @MaxSeconds int = NULL, @DeletedRows int = 0 OUT, @IsComplete bit = 0 OUT
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'R='+isnull(convert(varchar,@RetentionDays),'NULL')+' MS='+isnull(convert(varchar,@MaxSeconds),'NULL')
       ,@st datetime = getUTCdate()
       ,@Cutoff datetime = dateadd(day, -@RetentionDays, getUTCdate())
       ,@Rows int = 1

BEGIN TRY
  SET @DeletedRows = 0
  SET @IsComplete = 0
  WHILE @Rows > 0 AND (@MaxSeconds IS NULL OR datediff(second, @st, getUTCdate()) < @MaxSeconds)
  BEGIN
    DELETE TOP (4000) FROM dbo.SubscriptionEvents WHERE StatusDate < @Cutoff AND Status <> 0
    SET @Rows = @@rowcount
    SET @DeletedRows += @Rows
  END
  SET @IsComplete = CASE WHEN @Rows = 0 THEN 1 ELSE 0 END

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@DeletedRows
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO

-- Clears the activation watermark. With @RevokeGrant = 1 (off, entered-in-error or ended) the grant is removed too, so the
-- subscription cannot be activated again without an authorized API write, and its pending events are marked not authorized.
CREATE OR ALTER PROCEDURE dbo.DeactivateSubscription @SubscriptionId varchar(128), @RevokeGrant bit = 0
AS
set nocount on
set xact_abort on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')
       ,@st datetime = getUTCdate()

BEGIN TRY
  BEGIN TRANSACTION

  -- The state row is updated, and so locked, before the events, in the same order as EnqueueSubscriptionEvents.
  UPDATE dbo.SubscriptionStates
    SET ActivationTransactionId = NULL
       ,ContentHash = CASE WHEN @RevokeGrant = 1 THEN NULL ELSE ContentHash END
       ,GrantJson = CASE WHEN @RevokeGrant = 1 THEN NULL ELSE GrantJson END
       ,UpdatedDate = getUTCdate()
    WHERE SubscriptionId = @SubscriptionId

  IF @RevokeGrant = 1
    UPDATE dbo.SubscriptionEvents SET Status = 3, StatusDate = getUTCdate() WHERE SubscriptionId = @SubscriptionId AND Status = 0

  COMMIT TRANSACTION

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st
END TRY
BEGIN CATCH
  IF @@trancount > 0 ROLLBACK TRANSACTION
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO

CREATE OR ALTER PROCEDURE dbo.DeleteSubscriptionState @SubscriptionId varchar(128)
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')
       ,@st datetime = getUTCdate()
       ,@Rows int = 0
       ,@Batch int

BEGIN TRY
  -- Deactivate first, so no event can be enqueued or delivered while the events are removed.
  UPDATE dbo.SubscriptionStates
    SET ActivationTransactionId = NULL, ContentHash = NULL, GrantJson = NULL, UpdatedDate = getUTCdate()
    WHERE SubscriptionId = @SubscriptionId

  -- A subscription can retain many events. Batches below the lock escalation threshold keep other subscriptions' enqueue and
  -- delivery running. If this stops part way, the state row remains, and delivery calls this again for the deleted subscription.
  WHILE 1 = 1
  BEGIN
    DELETE TOP (4000) FROM dbo.SubscriptionEvents WHERE SubscriptionId = @SubscriptionId
    SET @Batch = @@rowcount
    IF @Batch = 0 BREAK
    SET @Rows += @Batch
  END

  DELETE FROM dbo.SubscriptionStates WHERE SubscriptionId = @SubscriptionId
  SET @Rows += @@rowcount

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@Rows
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO

-- Appends matched events to subscription outboxes and advances the event pump cursor in one transaction (ADR-2611).
-- Event numbers continue each subscription's counter. Events for subscriptions that are not activated, or for transactions
-- at or before the activation watermark, are dropped. Replays are idempotent: existing events are not inserted again,
-- so they cannot consume extra event numbers, and the cursor never moves backwards.
-- @ActivationVersion is the activation counter the caller read before the subscription states it matched with. If a
-- subscription was activated since, the events may be missing it, so nothing is written and @IsStale is set: the caller
-- must match the same changes again with fresh states.
CREATE OR ALTER PROCEDURE dbo.EnqueueSubscriptionEvents @Events dbo.SubscriptionEventList READONLY, @CursorId varchar(100), @CursorTransactionId bigint, @ActivationVersion bigint = NULL, @IsStale bit = 0 OUT
AS
set nocount on
set xact_abort on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'C='+isnull(@CursorId,'NULL')+' T='+isnull(convert(varchar,@CursorTransactionId),'NULL')
       ,@st datetime = getUTCdate()
       ,@Rows int = 0

BEGIN TRY
  DECLARE @New TABLE
    (
       SubscriptionId      varchar(128)  COLLATE Latin1_General_100_CS_AS NOT NULL
      ,TransactionId       bigint       NOT NULL
      ,ResourceTypeId      smallint     NOT NULL
      ,ResourceSurrogateId bigint       NOT NULL
      ,ResourceId          varchar(128)  COLLATE Latin1_General_100_CS_AS NOT NULL
      ,ResourceVersion     int          NOT NULL
      ,Interaction         tinyint      NOT NULL
      ,Status              tinyint      NOT NULL
      ,EventDate           datetime2(7) NOT NULL
      ,Ordinal             int          NOT NULL
       PRIMARY KEY (SubscriptionId, Ordinal)
    )
  DECLARE @Base TABLE (SubscriptionId varchar(128) COLLATE Latin1_General_100_CS_AS NOT NULL PRIMARY KEY, BaseNumber bigint NOT NULL)

  SET @IsStale = 0

  BEGIN TRANSACTION

  -- The update lock serializes with ActivateSubscription, so no activation can commit between this check and the commit.
  IF @ActivationVersion IS NOT NULL
     AND isnull((SELECT Bigint FROM dbo.Parameters WITH (UPDLOCK, HOLDLOCK) WHERE Id = 'Subscriptions.ActivationVersion'), 0) <> @ActivationVersion
  BEGIN
    SET @IsStale = 1
    COMMIT TRANSACTION
    EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Text='Stale activations'
    RETURN
  END

  -- Locking the state rows serializes concurrent enqueues for a subscription.
  INSERT INTO @New
    SELECT E.SubscriptionId, E.TransactionId, E.ResourceTypeId, E.ResourceSurrogateId, E.ResourceId, E.ResourceVersion, E.Interaction, E.Status, E.EventDate, E.Ordinal
      FROM @Events E
           JOIN dbo.SubscriptionStates S WITH (UPDLOCK, HOLDLOCK) ON S.SubscriptionId = E.SubscriptionId
      WHERE S.ActivationTransactionId IS NOT NULL
        AND E.TransactionId > S.ActivationTransactionId
        AND NOT EXISTS (SELECT * FROM dbo.SubscriptionEvents X WHERE X.SubscriptionId = E.SubscriptionId AND X.TransactionId = E.TransactionId AND X.ResourceTypeId = E.ResourceTypeId AND X.ResourceSurrogateId = E.ResourceSurrogateId)

  UPDATE S
    SET LastEventNumber = S.LastEventNumber + C.Cnt
       ,UpdatedDate = getUTCdate()
    OUTPUT deleted.SubscriptionId, deleted.LastEventNumber INTO @Base
    FROM dbo.SubscriptionStates S
         JOIN (SELECT SubscriptionId, Cnt = count(*) FROM @New GROUP BY SubscriptionId) C ON C.SubscriptionId = S.SubscriptionId

  INSERT INTO dbo.SubscriptionEvents
         ( SubscriptionId,                                                            EventNumber, TransactionId, ResourceTypeId, ResourceSurrogateId, ResourceId, ResourceVersion, Interaction, Status, EventDate )
    SELECT N.SubscriptionId, B.BaseNumber + row_number() OVER (PARTITION BY N.SubscriptionId ORDER BY N.Ordinal), TransactionId, ResourceTypeId, ResourceSurrogateId, ResourceId, ResourceVersion, Interaction, Status, EventDate
      FROM @New N
           JOIN @Base B ON B.SubscriptionId = N.SubscriptionId
  SET @Rows = @@rowcount

  IF @CursorId IS NOT NULL
  BEGIN
    UPDATE dbo.Parameters SET Bigint = @CursorTransactionId, UpdatedDate = getUTCdate() WHERE Id = @CursorId AND (Bigint IS NULL OR Bigint < @CursorTransactionId)
    IF @@rowcount = 0
      INSERT INTO dbo.Parameters (Id, Bigint, UpdatedDate) SELECT @CursorId, @CursorTransactionId, getUTCdate() WHERE NOT EXISTS (SELECT * FROM dbo.Parameters WHERE Id = @CursorId)
  END

  COMMIT TRANSACTION

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@Rows
END TRY
BEGIN CATCH
  IF @@trancount > 0 ROLLBACK TRANSACTION
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO

CREATE OR ALTER PROCEDURE dbo.GetSubscriptionEventCounts @SubscriptionId varchar(128)
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')
       ,@st datetime = getUTCdate()

BEGIN TRY
  SELECT Pending = isnull(sum(CASE WHEN Status = 0 THEN 1 ELSE 0 END),0)
        ,Unevaluable = isnull(sum(CASE WHEN Status = 2 THEN 1 ELSE 0 END),0)
    FROM dbo.SubscriptionEvents
    WHERE SubscriptionId = @SubscriptionId
      AND Status IN (0, 2)

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO

-- Returns the oldest events of a subscription with the given status, in event-number order.
CREATE OR ALTER PROCEDURE dbo.GetSubscriptionEvents @SubscriptionId varchar(128), @Status tinyint = 0, @MaxCount int = 1
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')+' St='+isnull(convert(varchar,@Status),'NULL')+' C='+isnull(convert(varchar,@MaxCount),'NULL')
       ,@st datetime = getUTCdate()

BEGIN TRY
  SELECT TOP (@MaxCount)
         SubscriptionId
        ,EventNumber
        ,TransactionId
        ,ResourceTypeId
        ,ResourceId
        ,ResourceVersion
        ,Interaction
        ,Status
        ,Attempts
        ,NextAttemptDate
        ,EventDate
    FROM dbo.SubscriptionEvents
    WHERE SubscriptionId = @SubscriptionId
      AND Status = @Status
    ORDER BY EventNumber

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO

CREATE OR ALTER PROCEDURE dbo.GetSubscriptionStates @SubscriptionId varchar(128) = NULL
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')
       ,@st datetime = getUTCdate()

BEGIN TRY
  SELECT SubscriptionId
        ,ContentHash
        ,GrantJson
        ,ActivationTransactionId
        ,LastEventNumber
        ,LastNotificationDate
        ,ErrorText
    FROM dbo.SubscriptionStates
    WHERE @SubscriptionId IS NULL OR SubscriptionId = @SubscriptionId

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO

CREATE OR ALTER PROCEDURE dbo.GetSubscriptionsWithDueEvents
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = ''
       ,@st datetime = getUTCdate()

BEGIN TRY
  -- Delivery sends pending events in event-number order and backs off on the oldest one, so only the oldest pending event
  -- decides whether a subscription is due. One seek per activated subscription on IX_SubscriptionEvents_SubscriptionId_Status_EventNumber.
  SELECT S.SubscriptionId
    FROM dbo.SubscriptionStates S
         CROSS APPLY (SELECT TOP 1 E.NextAttemptDate FROM dbo.SubscriptionEvents E WHERE E.SubscriptionId = S.SubscriptionId AND E.Status = 0 ORDER BY E.EventNumber) O
    WHERE S.ActivationTransactionId IS NOT NULL
      AND O.NextAttemptDate <= getUTCdate()

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO

CREATE OR ALTER PROCEDURE dbo.ReleaseSubscriptionLease @SubscriptionId varchar(128), @Worker varchar(100)
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')+' W='+isnull(@Worker,'NULL')
       ,@st datetime = getUTCdate()

BEGIN TRY
  UPDATE dbo.SubscriptionStates SET LeaseHolder = NULL, LeaseEndDate = NULL WHERE SubscriptionId = @SubscriptionId AND LeaseHolder = @Worker

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO

-- Stores the activation grant of a subscription. A different content hash clears the activation watermark, so the
-- subscription is not matched again until it completes a new handshake. Pending events matched under the previous content
-- (filters, endpoint, authorization) are marked not authorized, so they are never sent under the new content.
CREATE OR ALTER PROCEDURE dbo.SaveSubscriptionGrant @SubscriptionId varchar(128), @ContentHash varchar(64), @GrantJson nvarchar(max)
AS
set nocount on
set xact_abort on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')
       ,@st datetime = getUTCdate()

BEGIN TRY
  BEGIN TRANSACTION

  -- Lock the state row before the events, in the same order as EnqueueSubscriptionEvents, to avoid deadlocks.
  DECLARE @PreviousHash varchar(64) = (SELECT ContentHash FROM dbo.SubscriptionStates WITH (UPDLOCK, HOLDLOCK) WHERE SubscriptionId = @SubscriptionId)

  IF isnull(@PreviousHash, '') <> @ContentHash
    UPDATE dbo.SubscriptionEvents
      SET Status = 3
         ,StatusDate = getUTCdate()
      WHERE SubscriptionId = @SubscriptionId
        AND Status = 0

  UPDATE dbo.SubscriptionStates WITH (UPDLOCK, HOLDLOCK)
    SET ActivationTransactionId = CASE WHEN ContentHash = @ContentHash THEN ActivationTransactionId ELSE NULL END
       ,ContentHash = @ContentHash
       ,GrantJson = @GrantJson
       ,UpdatedDate = getUTCdate()
    WHERE SubscriptionId = @SubscriptionId

  IF @@rowcount = 0
    INSERT INTO dbo.SubscriptionStates (SubscriptionId, ContentHash, GrantJson) SELECT @SubscriptionId, @ContentHash, @GrantJson

  COMMIT TRANSACTION

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st
END TRY
BEGIN CATCH
  IF @@trancount > 0 ROLLBACK TRANSACTION
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO

-- Stores the latest server-reported error of a subscription, reported by $status. NULL clears it.
CREATE OR ALTER PROCEDURE dbo.SetSubscriptionError @SubscriptionId varchar(128), @ErrorText nvarchar(1000) = NULL
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')
       ,@st datetime = getUTCdate()

BEGIN TRY
  UPDATE dbo.SubscriptionStates SET ErrorText = @ErrorText, UpdatedDate = getUTCdate() WHERE SubscriptionId = @SubscriptionId

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO

CREATE OR ALTER PROCEDURE dbo.SetSubscriptionLastNotification @SubscriptionId varchar(128), @LastNotificationDate datetime
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')
       ,@st datetime = getUTCdate()

BEGIN TRY
  UPDATE dbo.SubscriptionStates SET LastNotificationDate = @LastNotificationDate, UpdatedDate = getUTCdate() WHERE SubscriptionId = @SubscriptionId

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO

-- Sets the status of events, or records a failed delivery attempt when @IncrementAttempts = 1.
CREATE OR ALTER PROCEDURE dbo.UpdateSubscriptionEvents @SubscriptionId varchar(128), @EventNumbers dbo.BigintList READONLY, @Status tinyint = NULL, @IncrementAttempts bit = 0, @NextAttemptDate datetime = NULL, @Error nvarchar(1000) = NULL
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')+' St='+isnull(convert(varchar,@Status),'NULL')+' I='+convert(varchar,@IncrementAttempts)
       ,@st datetime = getUTCdate()

BEGIN TRY
  UPDATE E
    SET Status = isnull(@Status, E.Status)
       ,StatusDate = CASE WHEN @Status IS NULL THEN E.StatusDate ELSE getUTCdate() END
       ,Attempts = E.Attempts + CASE WHEN @IncrementAttempts = 1 THEN 1 ELSE 0 END
       ,NextAttemptDate = isnull(@NextAttemptDate, E.NextAttemptDate)
       ,LastError = CASE WHEN @IncrementAttempts = 1 THEN @Error ELSE E.LastError END
    FROM dbo.SubscriptionEvents E
         JOIN @EventNumbers N ON N.Id = E.EventNumber
    WHERE E.SubscriptionId = @SubscriptionId

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO

-- Returns every resource version written by the transactions in a range, including history and invisible versions, with the
-- transaction that wrote it. The subscription event pump reads a page of transactions with one seek per resource type on
-- IX_ResourceTypeId_TransactionId, instead of one seek per resource type for each transaction.
CREATE OR ALTER PROCEDURE dbo.GetResourcesByTransactionIdRange @FirstTransactionId bigint, @LastTransactionId bigint
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(100) = 'F='+convert(varchar,@FirstTransactionId)+' L='+convert(varchar,@LastTransactionId)
       ,@st datetime = getUTCdate()
       ,@DummyTop bigint = 9223372036854775807
       ,@TypeId smallint

BEGIN TRY
  DECLARE @Types TABLE (TypeId smallint PRIMARY KEY, Name varchar(100))
  INSERT INTO @Types EXECUTE dbo.GetUsedResourceTypes

  DECLARE @Keys TABLE (TypeId smallint, SurrogateId bigint, TransactionId bigint PRIMARY KEY (TypeId, SurrogateId))
  WHILE EXISTS (SELECT * FROM @Types)
  BEGIN
    SET @TypeId = (SELECT TOP 1 TypeId FROM @Types ORDER BY TypeId)

    INSERT INTO @Keys SELECT @TypeId, ResourceSurrogateId, TransactionId FROM dbo.Resource WHERE ResourceTypeId = @TypeId AND TransactionId BETWEEN @FirstTransactionId AND @LastTransactionId

    DELETE FROM @Types WHERE TypeId = @TypeId
  END

  SELECT B.ResourceTypeId
        ,B.ResourceId
        ,B.ResourceSurrogateId
        ,B.Version
        ,B.IsDeleted
        ,B.IsHistory
        ,B.RawResource
        ,B.IsRawResourceMetaSet
        ,B.SearchParamHash
        ,B.RequestMethod
        ,A.TransactionId
    FROM (SELECT TOP (@DummyTop) * FROM @Keys) A
         JOIN dbo.Resource B ON B.ResourceTypeId = A.TypeId AND B.ResourceSurrogateId = A.SurrogateId
    OPTION (MAXDOP 1, OPTIMIZE FOR (@DummyTop = 1))

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO
