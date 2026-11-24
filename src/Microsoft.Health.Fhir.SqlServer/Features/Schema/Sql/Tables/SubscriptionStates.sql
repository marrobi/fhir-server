--DROP TABLE dbo.SubscriptionStates
GO
-- Server-owned runtime state of FHIR subscriptions (ADR-2611): the activation grant bound to the subscription content,
-- the activation watermark (only later transactions are matched), the event counter, and the per-subscription delivery lease.
CREATE TABLE dbo.SubscriptionStates
  (
     SubscriptionId          varchar(64)   COLLATE Latin1_General_100_CS_AS NOT NULL
    ,ContentHash             varchar(64)   NULL
    ,GrantJson               nvarchar(max) NULL
    ,ActivationTransactionId bigint        NULL
    ,LastEventNumber         bigint        NOT NULL CONSTRAINT DF_SubscriptionStates_LastEventNumber DEFAULT 0
    ,LastNotificationDate    datetime      NULL
    ,ErrorText               nvarchar(1000) NULL
    ,LeaseHolder             varchar(100)  NULL
    ,LeaseEndDate            datetime      NULL
    ,CreatedDate             datetime      NOT NULL CONSTRAINT DF_SubscriptionStates_CreatedDate DEFAULT getUTCdate()
    ,UpdatedDate             datetime      NOT NULL CONSTRAINT DF_SubscriptionStates_UpdatedDate DEFAULT getUTCdate()

     CONSTRAINT PKC_SubscriptionStates_SubscriptionId PRIMARY KEY CLUSTERED (SubscriptionId)
  )
GO
