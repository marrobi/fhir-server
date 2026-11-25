--DROP PROCEDURE dbo.SetSubscriptionLastNotification
GO
-- Records a successful notification: clears the scheduled contact and the reported error, and sets the consecutive failure
-- count. Delivered events and heartbeats pass 0; a recovery handshake keeps the count, so repeated failures keep backing off.
CREATE PROCEDURE dbo.SetSubscriptionLastNotification @SubscriptionId varchar(128), @LastNotificationDate datetime, @ContactFailures int = 0
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')
       ,@st datetime = getUTCdate()

BEGIN TRY
  UPDATE dbo.SubscriptionStates SET LastNotificationDate = @LastNotificationDate, ContactFailures = @ContactFailures, NextContactDate = NULL, ErrorText = NULL, UpdatedDate = getUTCdate() WHERE SubscriptionId = @SubscriptionId

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO
