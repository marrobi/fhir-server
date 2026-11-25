--DROP PROCEDURE dbo.RecordSubscriptionContactFailure
GO
-- Records a failed handshake, heartbeat or recovery check: the consecutive failure count, when the endpoint is next
-- contacted, and the error reported by $status.
CREATE PROCEDURE dbo.RecordSubscriptionContactFailure @SubscriptionId varchar(128), @ContactFailures int, @NextContactDate datetime, @ErrorText nvarchar(1000) = NULL
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')+' F='+isnull(convert(varchar,@ContactFailures),'NULL')
       ,@st datetime = getUTCdate()

BEGIN TRY
  UPDATE dbo.SubscriptionStates
    SET ContactFailures = @ContactFailures
       ,NextContactDate = @NextContactDate
       ,ErrorText = @ErrorText
       ,UpdatedDate = getUTCdate()
    WHERE SubscriptionId = @SubscriptionId

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO
