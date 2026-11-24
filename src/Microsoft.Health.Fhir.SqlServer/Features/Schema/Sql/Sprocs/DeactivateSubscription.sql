--DROP PROCEDURE dbo.DeactivateSubscription
GO
-- Clears the activation watermark. With @RevokeGrant = 1 (off, entered-in-error or ended) the grant is removed too, so the
-- subscription cannot be activated again without an authorized API write, and its pending events are marked not authorized.
CREATE PROCEDURE dbo.DeactivateSubscription @SubscriptionId varchar(64), @RevokeGrant bit = 0
AS
set nocount on
set xact_abort on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')
       ,@st datetime = getUTCdate()

BEGIN TRY
  BEGIN TRANSACTION

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
