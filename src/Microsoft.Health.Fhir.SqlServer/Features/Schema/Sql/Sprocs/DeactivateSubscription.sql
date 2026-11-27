--DROP PROCEDURE dbo.DeactivateSubscription
GO
-- Clears the activation watermark. With @RevokeGrant = 1 (off, entered-in-error or ended) the grant is removed too, so the
-- subscription cannot be activated again without an authorized API write, and its pending events are marked not authorized.
CREATE PROCEDURE dbo.DeactivateSubscription @SubscriptionId varchar(128), @RevokeGrant bit = 0
AS
set nocount on
set xact_abort on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')
       ,@st datetime = getUTCdate()
       ,@Rows int = 0
       ,@Batch int

BEGIN TRY
  -- The retained backlog can be large. Most of it is retired first, in batches below the lock escalation threshold, so the
  -- event pump and other subscriptions keep running. If this stops part way, the state is unchanged and the delivery service
  -- calls this again, because the subscription still has its grant.
  IF @RevokeGrant = 1
    WHILE 1 = 1
    BEGIN
      UPDATE TOP (4000) dbo.SubscriptionEvents SET Status = 3, StatusDate = getUTCdate() WHERE SubscriptionId = @SubscriptionId AND Status = 0
      SET @Batch = @@rowcount
      IF @Batch = 0 BREAK
      SET @Rows += @Batch
    END

  BEGIN TRANSACTION

  -- The state row is updated, and so locked, before the events, in the same order as EnqueueSubscriptionEvents.
  UPDATE dbo.SubscriptionStates
    SET ActivationTransactionId = NULL
       ,ContentHash = CASE WHEN @RevokeGrant = 1 THEN NULL ELSE ContentHash END
       ,GrantJson = CASE WHEN @RevokeGrant = 1 THEN NULL ELSE GrantJson END
       ,UpdatedDate = getUTCdate()
    WHERE SubscriptionId = @SubscriptionId

  -- Only events enqueued while the batches ran remain.
  IF @RevokeGrant = 1
  BEGIN
    UPDATE dbo.SubscriptionEvents SET Status = 3, StatusDate = getUTCdate() WHERE SubscriptionId = @SubscriptionId AND Status = 0
    SET @Rows += @@rowcount
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
