--DROP TYPE dbo.SubscriptionEventList
GO
CREATE TYPE dbo.SubscriptionEventList AS TABLE
(
     SubscriptionId      varchar(128)  COLLATE Latin1_General_100_CS_AS NOT NULL
    ,TransactionId       bigint       NOT NULL
    ,ResourceTypeId      smallint     NOT NULL
    ,ResourceSurrogateId bigint       NOT NULL
    ,ResourceId          varchar(128)  COLLATE Latin1_General_100_CS_AS NOT NULL
    ,ResourceVersion     int          NOT NULL
    ,Interaction         tinyint      NOT NULL
    ,Status              tinyint      NOT NULL
    ,EventDate           datetime2(7) NOT NULL
    ,Ordinal             int          NOT NULL

     PRIMARY KEY (SubscriptionId, TransactionId, ResourceTypeId, ResourceSurrogateId)
)
GO
