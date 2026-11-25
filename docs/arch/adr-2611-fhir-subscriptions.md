# ADR-2611: FHIR Topic-Based Subscriptions

**Status**: Proposed
**Date**: 2026-11-23
**Feature**: FHIR Subscriptions and notifications

## Context

Clients need notifications when FHIR resources change. The design uses the [R5 topic-based subscriptions framework](https://hl7.org/fhir/R5/subscriptions.html) and, for R4 and R4B, the [Subscriptions R5 Backport IG](https://hl7.org/fhir/uv/subscriptions-backport/). R4 with the Backport IG is the primary target for the managed Azure Health Data Services FHIR service. Clients must be able to define topics through the API on every supported version, including R4, which has no `SubscriptionTopic` resource type. The minimum support is `Subscription` read/write, `SubscriptionTopic` read/search on R4B and R5, `$status`, one channel and one payload type. The rest-hook channel requires a successful handshake before a subscription becomes `active`.

The existing SQL Server transaction log provides `dbo.Transactions`, `Resource.TransactionId` and a visibility watermark. The watermark marks how far all transactions have completed. IDs are assigned when transactions begin, not when they commit. The feature adds a subscription engine and an in-memory search evaluator; the limits of clock-based IDs and resource history are stated below.

Constraints:

- **No synchronous matching on resource writes:** matching and delivery run in the background. API writes to subscriptions and topics still need validation and grant storage. Existing behaviour must not change when the feature is off.
- **No impact on existing events:** the `ResourceChangeData` change feed, presumed to feed [Azure Health Data Services events](https://learn.microsoft.com/azure/healthcare-apis/events/events-overview), must not be modified. Reading it is acceptable.
- **Write-path coverage:** REST, bundle, `$import` and bulk writes are in scope. Each match recorded by the pump must have one logical event and one event number, including after a crash or replay. This does not guarantee that every change can be read: source coverage, missing history and clock changes impose the limits below. Delivery is a separate, best-effort step.
- **Authorization:** notifications must respect the subscriber's access, even without an HTTP request context. Authorization checks must fail closed, as in [ADR-2609](adr-2609-smart-authorization-fail-closed.md). Background work uses the caller-owned service lifetimes from [ADR-2610](adr-2610-caller-owned-scopes.md); that ADR does not define authorization policy.

Three earlier attempts (#4474, #4628, #5478) were closed with no public review recorded. They were large, multi-layer PRs with no agreed design.

## Options Considered

**Event source and matching**

1. **Match in the MediatR request pipeline.** *(rejected)* It runs before commit, `$import` and bulk jobs bypass it, and it delays writes.
2. **Consume the `ResourceChangeData` change feed.** *(viable alternative)* It records create, update and soft delete with resource versions. Its reader takes a table lock (`TABLOCK, HOLDLOCK`) that can block writes; the impact has not been measured. Feed retention is not managed here and may belong to the managed events pipeline. This needs confirmation: a reader that falls behind could lose events. The feed omits versions imported directly as history. Enabling capture also adds an insert to each write if capture was previously off.
3. **One queued job per committed transaction (the prior prototype).** *(rejected)* It adds a job for every transaction and needs coordination to save one cursor across parallel workers. The prototype also read only current rows, losing earlier versions after rapid updates.
4. **One leased pump over `Transactions`.** The pump reads pages from a saved cursor up to the visibility watermark. It matches available committed versions in memory, then saves events and the cursor in one SQL transaction. A delivery service sends queued notifications. *(viable, preferred)* Existing SQL writes already record transactions. No new locks are added to writes. Long transactions delay all subscriptions, and exact matching depends on keeping resource history.
5. **Evaluate criteria with database queries** (chained parameters, `_has`, terminology). *(rejected)* It needs one query per event per subscription. The design avoids this query count by rejecting such criteria when a subscription or topic is written.
6. **Match outside the server, consuming [Azure Health Data Services events](https://learn.microsoft.com/azure/healthcare-apis/events/events-overview) through Event Grid and Service Bus.** *(viable for the managed service only, not chosen)* A separate service receives the resource created, updated and deleted events through a Service Bus queue. It reads each version an event names, plus the version before it for `previous` criteria, matches in memory, and delivers notifications itself. It could reuse this design's in-memory evaluator and R4 topic conversion. It needs no change to the managed service. However:
   - **Managed service only:** this server does not emit Event Grid events, so the option does not apply to it.
   - **Additional services:** it needs an Event Grid subscription, a Service Bus queue, a compute host and a store for subscription state and events, each with its own deployment, monitoring and cost.
   - **Weaker contract:** the service reads with its own identity, rather than a grant captured from the subscriber's API request. It cannot provide `$status`, control `Subscription.status`, or validate API writes unless a gateway is added in front of the API.
   - **Storage and order:** Service Bus keeps each event until the service saves its results and marks it complete. Event Grid and Service Bus can move failed messages to dead-letter storage. The service must restore version order itself and keep any source events it needs to replay. Coverage depends on the managed events source, presumed to be `ResourceChangeData`, with option 2's limits.
   - **Search parameters:** custom search parameters must be loaded into the external evaluator.

**Subscription model and topics**

7. **Also support classic R4 criteria-based subscriptions.** *(rejected)* Topic-based subscriptions replace this model. Supporting both would require two sets of validation rules and notification formats.
8. **R4 topics as `Basic` resources** (code `fhir-types#SubscriptionTopic`, with R5 topic elements carried as extensions under a published, versioned profile). *(viable, chosen)*
9. **R4 topics through an R4B-shaped `/SubscriptionTopic` endpoint, or server-configured topics only.** *(rejected)* The endpoint is non-standard on an R4 server, and server-only topics do not meet the requirement to define topics through the API.

## Decision

Implement topic-based subscriptions (R4 Backport, R4B Backport, R5) on SQL Server, behind `FhirServer:Subscriptions:Enabled` (default `false`, independent of `SupportsResourceChangeCapture`). A prototype tested this design. Product group decisions and the release gate are still open.

**Event pipeline** (option 4, with option 2 as the alternative):

- Matching and delivery use `ITransactionEventSource`. Both sources are implemented, with `Transactions` as the provisional default. The product group must confirm the default before this ADR is accepted. Both use the same event uniqueness key and activation watermark; change-feed records are mapped to their writing transaction. The pump records its source on first use of a database. It stops if a different source is configured and resumes when the original setting is restored, subject to source retention. Switching sources on an existing database is not supported. The product group must also decide whether imported history should notify and whether change capture and its retention are supported dependencies.
- A leased `SubscriptionEventWatchdog` pages `dbo.Transactions` from a durable cursor up to the visibility watermark.
- It reads transactions **including historical rows** and evaluates the exact version when available. `previous` criteria use the immediately preceding version. If a change record remains but a required version is missing, the pump can record `Unevaluable`. This requires a relevant topic and read access to that resource type, with no compartment or search-parameter scope restrictions. Restricted subscribers get no numbered gap because it could reveal a change they cannot read. An `Unevaluable` record takes an event number but is not delivered. If the change row itself was deleted, no numbered gap is guaranteed for any subscriber.
- The activation watermark excludes earlier transactions, except for the configured clock margin described below.
- Matches go to `dbo.SubscriptionEvents`, a stored queue also called an outbox. Event rows and the cursor commit together. A unique key prevents replay from creating duplicate events or extra event numbers.
- A background delivery service allows one notification at a time per subscription, with bounded retries and stable event numbers. Receipt is best-effort: a notification may arrive more than once, or not at all. Clients remove duplicates by event number.
- `ResourceChangeData` is not changed.

**Contract:**

- **Topics:** definable through the API on all versions (option 8 on R4). Writes require a new `DataActions.SubscriptionTopic` permission whenever the old or the new resource is a topic.
- **Channel and status:** v1 supports rest-hook only, with a mandatory handshake. Clients request activation or retry with `requested`, and stop with `off` or `entered-in-error`. Only the server sets `active` or `error`.
- **Payload:** `id-only` is the default. The server adds the content extension when the client omits it.
- **Operations:** `$status` is in v1. `$events`, `notificationShape` and the Azure channels follow later.
- **Authorization:** the server captures the caller's access in a grant when the subscription is created or renewed through the API. For every payload level, it checks that grant **before assigning an event number**. Changes the subscriber cannot read leave no numbered gap (R5 §3.9.8). Delivery checks the grant again for local revocation; that later denial may leave a gap. The server cannot detect identity-provider revocation. Local controls are `off`, deletion, a maximum lifetime and checks against current roles.
- **Activation grant:** the authorization snapshot is a server-owned record bound to the accepted subscription content and endpoint. A content change needs a new grant and handshake, and retires old pending events. A subscription without a matching grant, including one imported with changed content, is not activated. An authorized caller must activate it through the API. The server corrects its status to `off`, or to `error` with a reason reported by `$status`. Stored status may be stale until background validation runs; it is not proof of a valid grant.
- **Revalidation:** the registry also validates every subscription and topic again when it loads them, so imported resources cannot bypass content and endpoint checks, for example to reach internal endpoints.
- **Loop prevention:** the server's own `Subscription` bookkeeping writes are excluded from matching.

**Not implemented:**

- `eventTrigger` topics.
- Websocket, email and message channels.
- Classic R4 subscriptions.
- Cross-version notifications.
- Hard delete and `$purge-history` events.
- Cosmos DB and STU3.

Full scope, rationale and the delivery plan are in the [design proposal](Proposals/fhir-subscriptions-design.md).

## Consequences

- **New components:** `SubscriptionEvents` and `SubscriptionStates` tables, source and activation keys in `dbo.Parameters`, an event watchdog, a delivery service, an in-memory evaluator in Core, version converters and a new data action. `DataActions.All` must include the new action. Other resource write paths and `ResourceChangeData` stay unchanged.
- **Open defaults:** the product group must confirm `EventSource` (currently `Transactions`), `NotifyHistoricalVersions` (currently `false`), `UnversionedResourceTypes` (currently `AllowWithGaps`, with `RequireHistory` as the alternative) and `MaxLifetimeDays` (currently 365). These are settings, but `EventSource` cannot change after first use of a database.
- **Client support:** standard FHIR clients, starting with R4 Backport, can receive filtered notifications in event-number order and detect numbered gaps. Missing source records remain a limit. The evaluator and pump may be reused by other features.
- **External matching:** the in-memory evaluator depends only on Core abstractions, not on SQL Server or the HTTP layer, so a service that consumes Azure Health Data Services events outside the server (option 6) can reuse it, with the limits noted there.
- **Delays:** a long `$import` batch or a stalled transaction delays every subscription. Visibility cannot advance past the oldest incomplete transaction. The release gate requires measuring this delay and setting a latency target.
- **History cleanup:** while the feature is on, `InvisibleHistoryCleanupWatchdog` keeps invisible history for at least `MaxHistoryCleanupHoldHours` (default 24). Cleanup does not wait for the subscription cursor. This hold does not preserve versions deleted during a `no-version` write or guarantee recovery after a longer backlog.
- **Transaction ordering:** IDs come from the SQL Server clock. A backward clock step can place a later transaction below the saved cursor. The `Transactions` source uses `TransactionReadLagSeconds` (default 5) as a read delay and a margin before activation. A transaction that commits below a cursor already passed is counted and logged, but not notified. Changes just before activation may notify. A normal long-running transaction holds back visibility; it is not skipped merely because it exceeds this delay. Strictly increasing IDs would need a separate decision by the SQL storage owners.
- **Unreachable endpoints:** events stay pending in `error`. After `MaxErrorHours` (default 72), the server sets the subscription to `off` and discards them. `EventRetentionDays` applies to completed event records, not pending events.
- **Event table locking:** deleting a subscription and cleaning up completed events remove rows in batches below SQL Server's lock escalation threshold, so they do not block the pump or other subscriptions' delivery; an interrupted deletion is finished by the delivery service. A content change or revocation retires a subscription's pending events in one transaction so that none are sent under the new content. A very large pending backlog can therefore briefly escalate to a table lock.
- **Capability advertisement:** the CapabilityStatement is built once per process, so subscription support is advertised whenever the feature is enabled on a store that supports it, including while a schema upgrade is pending (subscription writes and `$status` return 503 until it completes). The R4 topic-canonical list is refreshed when the statement is periodically rebuilt.
- **Throughput:** a single pump bounds matching throughput. Partitioning by resource type is the fallback if benchmarks require it.
- **Authorization limits:** the snapshot cannot observe identity-provider revocation. The maximum subscription lifetime is configurable (default 365 days); the product group should confirm the default.
- **R4 compatibility:** the Backport IG leaves R4 topic definitions out of scope. It records that HL7 dropped an earlier `Basic` profile because it was difficult to use. This server's API-defined R4 topics are therefore an extension, not a standard guarantee. Standard clients use canonical URLs and CapabilityStatement topic advertisements. The `Basic` layout follows HAPI FHIR. The recorded check with HAPI FHIR 8.12.0 (R4) read and matched this server's topics. HAPI's example also matched here after removing its `_in` filter, which is not supported on R4. These checks do not guarantee compatibility with every server.
- **Review and release:** this ADR must be accepted before the feature PR opens for review. The feature is submitted as one PR, disabled by default, with commits grouped by component. Separately useful prerequisites may merge first. Release requires the proposal's end-to-end, handshake, crash, backlog, load, latency and feature-off checks.
- **Related ADRs:** no existing ADR is made obsolete. The registry reuses the cache-sync pattern from [ADR-2603 load-independent search parameter cache sync](adr-2603-load-independent-search-param-cache-sync.md).
