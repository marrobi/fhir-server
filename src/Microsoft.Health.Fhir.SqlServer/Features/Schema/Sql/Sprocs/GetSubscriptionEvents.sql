--DROP PROCEDURE dbo.GetSubscriptionEvents
GO
-- Returns the oldest events of a subscription with the given status, in event-number order.
CREATE PROCEDURE dbo.GetSubscriptionEvents @SubscriptionId varchar(64), @Status tinyint = 0, @MaxCount int = 1
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')+' St='+isnull(convert(varchar,@Status),'NULL')+' C='+isnull(convert(varchar,@MaxCount),'NULL')
       ,@st datetime = getUTCdate()

BEGIN TRY
  SELECT TOP (@MaxCount)
         SubscriptionId
        ,EventNumber
        ,TransactionId
        ,ResourceTypeId
        ,ResourceId
        ,ResourceVersion
        ,Interaction
        ,Status
        ,Attempts
        ,NextAttemptDate
        ,EventDate
    FROM dbo.SubscriptionEvents
    WHERE SubscriptionId = @SubscriptionId
      AND Status = @Status
    ORDER BY EventNumber

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO
