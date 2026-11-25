--DROP PROCEDURE dbo.GetSubscriptionsWithDueEvents
GO
CREATE PROCEDURE dbo.GetSubscriptionsWithDueEvents
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = ''
       ,@st datetime = getUTCdate()

BEGIN TRY
  -- Delivery sends pending events in event-number order and backs off on the oldest one, so only the oldest pending event
  -- decides whether a subscription is due. One seek per activated subscription on IX_SubscriptionEvents_SubscriptionId_Status_EventNumber.
  SELECT S.SubscriptionId
    FROM dbo.SubscriptionStates S
         CROSS APPLY (SELECT TOP 1 E.NextAttemptDate FROM dbo.SubscriptionEvents E WHERE E.SubscriptionId = S.SubscriptionId AND E.Status = 0 ORDER BY E.EventNumber) O
    WHERE S.ActivationTransactionId IS NOT NULL
      AND O.NextAttemptDate <= getUTCdate()

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO
