--DROP PROCEDURE dbo.SaveSubscriptionGrant
GO
-- Stores the activation grant of a subscription. A different content hash clears the activation watermark, so the
-- subscription is not matched again until it completes a new handshake. Pending events matched under the previous content
-- (filters, endpoint, authorization) are marked not authorized, so they are never sent under the new content.
-- Every API write resets the contact back-off, so a client that fixes its endpoint gets a handshake straight away.
CREATE PROCEDURE dbo.SaveSubscriptionGrant @SubscriptionId varchar(128), @ContentHash varchar(64), @GrantJson nvarchar(max)
AS
set nocount on
set xact_abort on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')
       ,@st datetime = getUTCdate()
       ,@PreviousHash varchar(64)
       ,@Rows int = 0
       ,@Batch int

BEGIN TRY
  -- The content was already changed by the API write, so events matched under the previous content must not be sent. A large
  -- backlog is retired first, in batches below the lock escalation threshold, so the event pump and other subscriptions keep
  -- running. The hash is checked again under the lock below.
  IF isnull((SELECT ContentHash FROM dbo.SubscriptionStates WHERE SubscriptionId = @SubscriptionId), '') <> @ContentHash
    WHILE 1 = 1
    BEGIN
      UPDATE TOP (4000) dbo.SubscriptionEvents SET Status = 3, StatusDate = getUTCdate() WHERE SubscriptionId = @SubscriptionId AND Status = 0
      SET @Batch = @@rowcount
      IF @Batch = 0 BREAK
      SET @Rows += @Batch
    END

  BEGIN TRANSACTION

  -- Lock the state row before the events, in the same order as EnqueueSubscriptionEvents, to avoid deadlocks.
  SET @PreviousHash = (SELECT ContentHash FROM dbo.SubscriptionStates WITH (UPDLOCK, HOLDLOCK) WHERE SubscriptionId = @SubscriptionId)

  -- Only events enqueued while the batches ran remain.
  IF isnull(@PreviousHash, '') <> @ContentHash
  BEGIN
    UPDATE dbo.SubscriptionEvents
      SET Status = 3
         ,StatusDate = getUTCdate()
      WHERE SubscriptionId = @SubscriptionId
        AND Status = 0
    SET @Rows += @@rowcount
  END

  UPDATE dbo.SubscriptionStates WITH (UPDLOCK, HOLDLOCK)
    SET ActivationTransactionId = CASE WHEN ContentHash = @ContentHash THEN ActivationTransactionId ELSE NULL END
       ,ContentHash = @ContentHash
       ,GrantJson = @GrantJson
       ,ContactFailures = 0
       ,NextContactDate = NULL
       ,UpdatedDate = getUTCdate()
    WHERE SubscriptionId = @SubscriptionId

  IF @@rowcount = 0
    INSERT INTO dbo.SubscriptionStates (SubscriptionId, ContentHash, GrantJson) SELECT @SubscriptionId, @ContentHash, @GrantJson

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
