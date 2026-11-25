--DROP PROCEDURE dbo.CleanupSubscriptionEvents
GO
-- Removes delivered, unevaluable and not-authorized events older than the retention period. Pending events are kept.
-- Deletes in batches below the lock escalation threshold, and stops starting batches after @MaxSeconds when given.
CREATE PROCEDURE dbo.CleanupSubscriptionEvents @RetentionDays int, @MaxSeconds int = NULL, @DeletedRows int = 0 OUT, @IsComplete bit = 0 OUT
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(200) = 'R='+isnull(convert(varchar,@RetentionDays),'NULL')+' MS='+isnull(convert(varchar,@MaxSeconds),'NULL')
       ,@st datetime = getUTCdate()
       ,@Cutoff datetime = dateadd(day, -@RetentionDays, getUTCdate())
       ,@Rows int = 1

BEGIN TRY
  SET @DeletedRows = 0
  SET @IsComplete = 0
  WHILE @Rows > 0 AND (@MaxSeconds IS NULL OR datediff(second, @st, getUTCdate()) < @MaxSeconds)
  BEGIN
    DELETE TOP (4000) FROM dbo.SubscriptionEvents WHERE StatusDate < @Cutoff AND Status <> 0
    SET @Rows = @@rowcount
    SET @DeletedRows += @Rows
  END
  SET @IsComplete = CASE WHEN @Rows = 0 THEN 1 ELSE 0 END

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@DeletedRows
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO
