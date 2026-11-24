--DROP PROCEDURE dbo.GetSubscriptionsWithDueEvents
GO
CREATE PROCEDURE dbo.GetSubscriptionsWithDueEvents
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = ''
       ,@st datetime = getUTCdate()

BEGIN TRY
  SELECT DISTINCT SubscriptionId
    FROM dbo.SubscriptionEvents
    WHERE Status = 0
      AND NextAttemptDate <= getUTCdate()

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO
