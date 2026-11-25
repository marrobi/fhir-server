--DROP PROCEDURE dbo.DeleteSubscriptionState
GO
CREATE PROCEDURE dbo.DeleteSubscriptionState @SubscriptionId varchar(128)
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
