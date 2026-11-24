--DROP PROCEDURE dbo.GetSubscriptionsWithDueEvents
GO
CREATE PROCEDURE dbo.GetSubscriptionsWithDueEvents
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = ''
       ,@st datetime = getUTCdate()

BEGIN TRY
  -- One probe per activated subscription on IX_SubscriptionEvents_SubscriptionId_Status_EventNumber. Pending events after
  -- the first batch keep their insert time, so the probe stops at the first unattempted event instead of reading the backlog.
  SELECT S.SubscriptionId
    FROM dbo.SubscriptionStates S
    WHERE S.ActivationTransactionId IS NOT NULL
      AND EXISTS (SELECT * FROM dbo.SubscriptionEvents E WHERE E.SubscriptionId = S.SubscriptionId AND E.Status = 0 AND E.NextAttemptDate <= getUTCdate())

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO
