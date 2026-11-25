--DROP TABLE dbo.SubscriptionEvents
GO
-- Durable per-subscription outbox of matched events (ADR-2611). Event numbers are assigned when events are inserted,
-- together with the event pump cursor, and never change; retries reuse them.
-- Status: 0 = pending, 1 = delivered, 2 = unevaluable, 3 = not authorized at delivery.
CREATE TABLE dbo.SubscriptionEvents
  (
     SubscriptionId      varchar(128)  COLLATE Latin1_General_100_CS_AS NOT NULL
    ,EventNumber         bigint         NOT NULL
    ,TransactionId       bigint         NOT NULL
    ,ResourceTypeId      smallint       NOT NULL
    ,ResourceSurrogateId bigint         NOT NULL
    ,ResourceId          varchar(128)  COLLATE Latin1_General_100_CS_AS NOT NULL
    ,ResourceVersion     int            NOT NULL
    ,Interaction         tinyint        NOT NULL
    ,Status              tinyint        NOT NULL
    ,Attempts            int            NOT NULL CONSTRAINT DF_SubscriptionEvents_Attempts DEFAULT 0
    ,NextAttemptDate     datetime       NOT NULL CONSTRAINT DF_SubscriptionEvents_NextAttemptDate DEFAULT getUTCdate()
    ,EventDate           datetime2(7)   NOT NULL
    ,StatusDate          datetime       NOT NULL CONSTRAINT DF_SubscriptionEvents_StatusDate DEFAULT getUTCdate()
    ,LastError           nvarchar(1000) NULL

     CONSTRAINT PKC_SubscriptionEvents_SubscriptionId_EventNumber PRIMARY KEY CLUSTERED (SubscriptionId, EventNumber)
  )
GO
CREATE UNIQUE INDEX IXU_SubscriptionEvents_SubscriptionId_TransactionId_ResourceTypeId_ResourceSurrogateId ON dbo.SubscriptionEvents (SubscriptionId, TransactionId, ResourceTypeId, ResourceSurrogateId)
GO
CREATE INDEX IX_SubscriptionEvents_SubscriptionId_Status_EventNumber ON dbo.SubscriptionEvents (SubscriptionId, Status, EventNumber) INCLUDE (NextAttemptDate)
GO
CREATE INDEX IX_SubscriptionEvents_StatusDate ON dbo.SubscriptionEvents (StatusDate) WHERE Status <> 0
GO
