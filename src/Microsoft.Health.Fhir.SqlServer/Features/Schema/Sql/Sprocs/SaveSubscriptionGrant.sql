--DROP PROCEDURE dbo.SaveSubscriptionGrant
GO
-- Stores the activation grant of a subscription. A different content hash clears the activation watermark, so the
-- subscription is not matched again until it completes a new handshake. Pending events matched under the previous content
-- (filters, endpoint, authorization) are marked not authorized, so they are never sent under the new content.
CREATE PROCEDURE dbo.SaveSubscriptionGrant @SubscriptionId varchar(64), @ContentHash varchar(64), @GrantJson nvarchar(max)
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
