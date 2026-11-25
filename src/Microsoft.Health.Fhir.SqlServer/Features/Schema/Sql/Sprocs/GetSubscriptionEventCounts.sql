--DROP PROCEDURE dbo.GetSubscriptionEventCounts
GO
CREATE PROCEDURE dbo.GetSubscriptionEventCounts @SubscriptionId varchar(128)
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')
       ,@st datetime = getUTCdate()

BEGIN TRY
  SELECT Pending = isnull(sum(CASE WHEN Status = 0 THEN 1 ELSE 0 END),0)
        ,Unevaluable = isnull(sum(CASE WHEN Status = 2 THEN 1 ELSE 0 END),0)
    FROM dbo.SubscriptionEvents
    WHERE SubscriptionId = @SubscriptionId
      AND Status IN (0, 2)

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO
