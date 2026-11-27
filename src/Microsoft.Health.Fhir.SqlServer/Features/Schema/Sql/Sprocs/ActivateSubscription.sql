--DROP PROCEDURE dbo.ActivateSubscription
GO
-- Sets the activation watermark to the current transaction visibility if it is not already set, and makes retained
-- pending events due again. Only transactions after the watermark are matched for the subscription. Each activation
-- increments Subscriptions.ActivationVersion, which the event pump checks so it never commits events matched without it.
CREATE PROCEDURE dbo.ActivateSubscription @SubscriptionId varchar(128), @ActivationTransactionId bigint = NULL OUT
AS
set nocount on
set xact_abort on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')
       ,@st datetime = getUTCdate()
       ,@Visibility bigint
       ,@Now datetime
       ,@Rows int = 0
       ,@Batch int

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

  SET @ActivationTransactionId = (SELECT ActivationTransactionId FROM dbo.SubscriptionStates WHERE SubscriptionId = @SubscriptionId)

  COMMIT TRANSACTION

  -- The retained backlog can be large after a long error period. It is made due again after the counter lock is released, in
  -- batches below the lock escalation threshold, so the event pump and other subscriptions keep running. Each batch only takes
  -- rows not yet reset, so the loop ends; a failure part way leaves the remaining events on their previous schedule.
  SET @Now = getUTCdate()
  WHILE 1 = 1
  BEGIN
    UPDATE TOP (4000) dbo.SubscriptionEvents
      SET Attempts = 0, NextAttemptDate = @Now
      WHERE SubscriptionId = @SubscriptionId AND Status = 0 AND (Attempts <> 0 OR NextAttemptDate > @Now)
    SET @Batch = @@rowcount
    IF @Batch = 0 BREAK
    SET @Rows += @Batch
  END

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@Rows,@Text=@ActivationTransactionId
END TRY
BEGIN CATCH
  IF @@trancount > 0 ROLLBACK TRANSACTION
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO
