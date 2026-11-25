--DROP PROCEDURE dbo.ReleaseSubscriptionLease
GO
CREATE PROCEDURE dbo.ReleaseSubscriptionLease @SubscriptionId varchar(128), @Worker varchar(100)
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')+' W='+isnull(@Worker,'NULL')
       ,@st datetime = getUTCdate()

BEGIN TRY
  UPDATE dbo.SubscriptionStates SET LeaseHolder = NULL, LeaseEndDate = NULL WHERE SubscriptionId = @SubscriptionId AND LeaseHolder = @Worker

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO
