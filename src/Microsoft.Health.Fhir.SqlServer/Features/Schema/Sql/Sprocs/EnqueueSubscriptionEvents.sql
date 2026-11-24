--DROP PROCEDURE dbo.EnqueueSubscriptionEvents
GO
-- Appends matched events to subscription outboxes and advances the event pump cursor in one transaction (ADR-2611).
-- Event numbers continue each subscription's counter. Events for subscriptions that are not activated, or for transactions
-- at or before the activation watermark, are dropped. Replays are idempotent: existing events are not inserted again,
-- so they cannot consume extra event numbers, and the cursor never moves backwards.
-- @ActivationVersion is the activation counter the caller read before the subscription states it matched with. If a
-- subscription was activated since, the events may be missing it, so nothing is written and @IsStale is set: the caller
-- must match the same changes again with fresh states.
CREATE PROCEDURE dbo.EnqueueSubscriptionEvents @Events dbo.SubscriptionEventList READONLY, @CursorId varchar(100), @CursorTransactionId bigint, @ActivationVersion bigint = NULL, @IsStale bit = 0 OUT
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
       SubscriptionId      varchar(64)  COLLATE Latin1_General_100_CS_AS NOT NULL
      ,TransactionId       bigint       NOT NULL
      ,ResourceTypeId      smallint     NOT NULL
      ,ResourceSurrogateId bigint       NOT NULL
      ,ResourceId          varchar(64)  COLLATE Latin1_General_100_CS_AS NOT NULL
      ,ResourceVersion     int          NOT NULL
      ,Interaction         tinyint      NOT NULL
      ,Status              tinyint      NOT NULL
      ,EventDate           datetime2(7) NOT NULL
      ,Ordinal             int          NOT NULL
       PRIMARY KEY (SubscriptionId, Ordinal)
    )
  DECLARE @Base TABLE (SubscriptionId varchar(64) COLLATE Latin1_General_100_CS_AS NOT NULL PRIMARY KEY, BaseNumber bigint NOT NULL)

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
