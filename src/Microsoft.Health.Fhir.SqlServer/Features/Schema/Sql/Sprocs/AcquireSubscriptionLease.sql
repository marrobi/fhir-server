--DROP PROCEDURE dbo.AcquireSubscriptionLease
GO
-- Acquires or renews the per-subscription lease that makes lifecycle and delivery work single-flight.
CREATE PROCEDURE dbo.AcquireSubscriptionLease @SubscriptionId varchar(128), @Worker varchar(100), @LeasePeriodSec int, @IsAcquired bit = 0 OUT
AS
set nocount on
set xact_abort on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'S='+isnull(@SubscriptionId,'NULL')+' W='+isnull(@Worker,'NULL')
       ,@st datetime = getUTCdate()

BEGIN TRY
  SET @IsAcquired = 0

  BEGIN TRANSACTION

  UPDATE dbo.SubscriptionStates WITH (UPDLOCK, HOLDLOCK)
    SET LeaseHolder = @Worker
       ,LeaseEndDate = dateadd(second, @LeasePeriodSec, getUTCdate())
    WHERE SubscriptionId = @SubscriptionId
      AND (LeaseHolder IS NULL OR LeaseHolder = @Worker OR LeaseEndDate < getUTCdate())

  IF @@rowcount > 0
    SET @IsAcquired = 1
  ELSE IF NOT EXISTS (SELECT * FROM dbo.SubscriptionStates WHERE SubscriptionId = @SubscriptionId)
  BEGIN
    -- Subscriptions written without a grant (for example, by $import) have no state yet.
    INSERT INTO dbo.SubscriptionStates (SubscriptionId, LeaseHolder, LeaseEndDate) SELECT @SubscriptionId, @Worker, dateadd(second, @LeasePeriodSec, getUTCdate())
    SET @IsAcquired = 1
  END

  COMMIT TRANSACTION

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Text=@IsAcquired
END TRY
BEGIN CATCH
  IF @@trancount > 0 ROLLBACK TRANSACTION
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO
