--DROP PROCEDURE dbo.GetResourcesByTransactionIdRange
GO
-- Returns every resource version written by the transactions in a range, including history and invisible versions, with the
-- transaction that wrote it. The subscription event pump reads a page of transactions with one seek per resource type on
-- IX_ResourceTypeId_TransactionId, instead of one seek per resource type for each transaction.
CREATE PROCEDURE dbo.GetResourcesByTransactionIdRange @FirstTransactionId bigint, @LastTransactionId bigint
AS
set nocount on
DECLARE @SP varchar(100) = object_name(@@procid)
       ,@Mode varchar(100) = 'F='+convert(varchar,@FirstTransactionId)+' L='+convert(varchar,@LastTransactionId)
       ,@st datetime = getUTCdate()
       ,@DummyTop bigint = 9223372036854775807
       ,@TypeId smallint

BEGIN TRY
  DECLARE @Types TABLE (TypeId smallint PRIMARY KEY, Name varchar(100))
  INSERT INTO @Types EXECUTE dbo.GetUsedResourceTypes

  DECLARE @Keys TABLE (TypeId smallint, SurrogateId bigint, TransactionId bigint PRIMARY KEY (TypeId, SurrogateId))
  WHILE EXISTS (SELECT * FROM @Types)
  BEGIN
    SET @TypeId = (SELECT TOP 1 TypeId FROM @Types ORDER BY TypeId)

    INSERT INTO @Keys SELECT @TypeId, ResourceSurrogateId, TransactionId FROM dbo.Resource WHERE ResourceTypeId = @TypeId AND TransactionId BETWEEN @FirstTransactionId AND @LastTransactionId

    DELETE FROM @Types WHERE TypeId = @TypeId
  END

  SELECT B.ResourceTypeId
        ,B.ResourceId
        ,B.ResourceSurrogateId
        ,B.Version
        ,B.IsDeleted
        ,B.IsHistory
        ,B.RawResource
        ,B.IsRawResourceMetaSet
        ,B.SearchParamHash
        ,B.RequestMethod
        ,A.TransactionId
    FROM (SELECT TOP (@DummyTop) * FROM @Keys) A
         JOIN dbo.Resource B ON B.ResourceTypeId = A.TypeId AND B.ResourceSurrogateId = A.SurrogateId
    OPTION (MAXDOP 1, OPTIMIZE FOR (@DummyTop = 1))

  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='End',@Start=@st,@Rows=@@rowcount
END TRY
BEGIN CATCH
  IF error_number() = 1750 THROW -- Real error is before 1750, cannot trap in SQL.
  EXECUTE dbo.LogEvent @Process=@SP,@Mode=@Mode,@Status='Error';
  THROW
END CATCH
GO
