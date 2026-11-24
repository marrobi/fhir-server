--DROP PROCEDURE dbo.DeleteSubscriptionState
GO
CREATE PROCEDURE dbo.DeleteSubscriptionState @SubscriptionId varchar(64)
AS
set nocount on
set xact_abort on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')
       ,@st datetime = getUTCdate()
       ,@Rows int = 0

BEGIN TRY
  BEGIN TRANSACTION

  DELETE FROM dbo.SubscriptionEvents WHERE SubscriptionId = @SubscriptionId
  SET @Rows += @@rowcount

  DELETE FROM dbo.SubscriptionStates WHERE SubscriptionId = @SubscriptionId
  SET @Rows += @@rowcount

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
