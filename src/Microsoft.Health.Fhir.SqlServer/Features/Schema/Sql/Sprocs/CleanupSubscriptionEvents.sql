--DROP PROCEDURE dbo.CleanupSubscriptionEvents
GO
-- Removes delivered, unevaluable and not-authorized events older than the retention period. Pending events are kept.
CREATE PROCEDURE dbo.CleanupSubscriptionEvents @RetentionDays int, @DeletedRows int = 0 OUT
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'R='+isnull(convert(varchar,@RetentionDays),'NULL')
       ,@st datetime = getUTCdate()
       ,@Cutoff datetime = dateadd(day, -@RetentionDays, getUTCdate())
       ,@Rows int = 1

BEGIN TRY
  SET @DeletedRows = 0
  WHILE @Rows > 0
  BEGIN
    DELETE TOP (10000) FROM dbo.SubscriptionEvents WHERE StatusDate < @Cutoff AND Status <> 0
    SET @Rows = @@rowcount
    SET @DeletedRows += @Rows
  END

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@DeletedRows
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO
