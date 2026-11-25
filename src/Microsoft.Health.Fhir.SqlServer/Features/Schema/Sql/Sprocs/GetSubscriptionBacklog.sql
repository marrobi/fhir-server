--DROP PROCEDURE dbo.GetSubscriptionBacklog
GO
-- Returns the number of pending events across all subscriptions and the time of the oldest one, for monitoring.
-- Seeks the pending range of each subscription on IX_SubscriptionEvents_SubscriptionId_Status_EventNumber, so the cost
-- grows with the pending events, not with retained delivered ones.
CREATE PROCEDURE dbo.GetSubscriptionBacklog
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = ''
       ,@st datetime = getUTCdate()

BEGIN TRY
  SELECT PendingEvents = isnull(sum(P.Pending),0)
        ,OldestPendingEventDate = min(O.EventDate)
    FROM dbo.SubscriptionStates S
         CROSS APPLY (SELECT Pending = count_big(*) FROM dbo.SubscriptionEvents E WHERE E.SubscriptionId = S.SubscriptionId AND E.Status = 0) P
         OUTER APPLY (SELECT TOP 1 E.EventDate FROM dbo.SubscriptionEvents E WHERE E.SubscriptionId = S.SubscriptionId AND E.Status = 0 ORDER BY E.EventNumber) O

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO
