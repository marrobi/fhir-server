--DROP PROCEDURE dbo.UpdateSubscriptionEvents
GO
-- Sets the status of events, or records a failed delivery attempt when @IncrementAttempts = 1.
CREATE PROCEDURE dbo.UpdateSubscriptionEvents @SubscriptionId varchar(64), @EventNumbers dbo.BigintList READONLY, @Status tinyint = NULL, @IncrementAttempts bit = 0, @NextAttemptDate datetime = NULL, @Error nvarchar(1000) = NULL
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
