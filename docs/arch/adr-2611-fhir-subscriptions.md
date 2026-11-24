# ADR-2611: FHIR Topic-Based Subscriptions

**Status**: Proposed
**Date**: 2026-11-23
**Feature**: FHIR Subscriptions and notifications

## Context

Clients need server-pushed notifications when FHIR resources change, using the standard [R5 topic-based Subscriptions framework](https://hl7.org/fhir/R5/subscriptions.html) and, for R4 and R4B, the [Subscriptions R5 Backport IG](https://hl7.org/fhir/uv/subscriptions-backport/). The managed Azure Health Data Services FHIR service runs R4, so R4 with the Backport IG is the primary target. Topics must be definable through the API on every supported version, even though R4 has no `SubscriptionTopic` resource type. To conform, a server must support `Subscription` read/write, `SubscriptionTopic` read/search (R4B and R5), `$status`, at least one channel and at least one payload type. The rest-hook channel also requires a handshake before a subscription becomes `active`. The server already has an ordered transaction log on SQL Server: `dbo.Transactions`, the `Resource.TransactionId` column, and a visibility watermark. Transaction IDs are allocated when a transaction begins, and the watermark advances only past transactions that have all completed, so the order is consistent but is not strictly commit order. It has no subscription engine and no in-memory search evaluator.

Constraints:

- **No synchronous cost on writes:** notifications must not add a synchronous step to the write path or change existing behaviour when the feature is off.
- **No impact on existing events:** the `ResourceChangeData` change feed, presumed to feed [Azure Health Data Services events](https://learn.microsoft.com/azure/healthcare-apis/events/events-overview), must not be modified. Reading it is acceptable.
- **Every write path covered:** a matching change must produce exactly one logical event (one event number), across crashes, backlogs, and rapid successive updates, and `$import` and bulk paths must be covered. Delivery of that event is a separate, best-effort concern.
- **Authorization:** notifications must respect authorization ([ADR-2609 SMART fail-closed](adr-2609-smart-authorization-fail-closed.md), [ADR-2610 caller-owned scopes](adr-2610-caller-owned-scopes.md)), even though no HTTP request context exists when a notification is sent.

Three earlier attempts (#4474, #4628, #5478) were closed with no public review recorded. They were large, multi-layer PRs with no agreed design.

## Options Considered

**Event source and matching**

1. **Match synchronously in the MediatR request pipeline.** *(rejected)* It runs before commit, `$import` and bulk jobs bypass it, and it adds latency to writes.
2. **Consume the `ResourceChangeData` change feed.** *(viable alternative, not chosen)* It already records create, update and soft delete with the resource version, and avoids adding a second change source. However, its reader holds a table lock (`TABLOCK, HOLDLOCK`) that can block concurrent writes; the impact has not been measured. Its retention does not appear to be managed in this repository and is presumed to be owned by the managed events pipeline, so a lagging subscription reader could lose events. It records only current versions, so historical versions loaded by `$import` are not captured. Its insert also adds work inside every write wherever capture is not already on.
3. **One queued job per committed transaction (the prior prototype).** *(rejected)* Job overhead scales with write volume. Parallel jobs cannot advance a single cursor atomically. It also read only current rows, so earlier versions were dropped when a resource was updated again before matching ran.
4. **Leased singleton pump over `Transactions`.** The pump pages from a cursor to the visibility watermark, matches the exact committed versions in memory, and commits the resulting events and the cursor in one SQL transaction. A per-subscription delivery queue then sends notifications. *(viable, preferred)* `Transactions` is written on every SQL write already, its visibility watermark gives a consistent order without new locks on writes, and its retention can be held back by the subscription cursor. Its main cost is head-of-line latency during long transactions.
5. **Evaluate criteria with database queries** (chained parameters, `_has`, terminology). *(rejected)* It needs one query per event per subscription, which does not scale. Such criteria are rejected when the subscription or topic is created.

**Subscription model and topics**

6. **Also support classic R4 criteria-based subscriptions.** *(rejected)* That model is superseded by topic-based subscriptions and would double validation and notification formats.
7. **R4 topics as `Basic` resources** (code `fhir-types#SubscriptionTopic`, with R5 topic elements carried as extensions under a published, versioned profile). *(viable, chosen)*
8. **R4 topics through an R4B-shaped `/SubscriptionTopic` endpoint, or server-configured topics only.** *(rejected)* The endpoint is non-standard on an R4 server, and server-only topics do not meet the requirement to define topics through the API.

## Decision

Implement topic-based subscriptions (R4 Backport, R4B, R5) on SQL Server, behind `Subscriptions:Enabled` (default `false`, independent of `SupportsResourceChangeCapture`).

**Event pipeline** (option 4, with option 2 as the alternative):

- The event source sits behind a narrow abstraction (`ITransactionEventSource`), so matching and delivery do not depend on which source is used. `Transactions` is the preferred source. The choice between the two is open for product group guidance, and is recorded here before this ADR is accepted. Both share the deduplication key and activation watermark: change-feed records are mapped back to the transaction that wrote them. The source is chosen once per database: the pump records the source it first ran with and does not run if another is configured later, because the expectation is that one source is chosen on product group guidance rather than switched in production. Product group guidance is also needed on whether historical versions loaded by `$import` should notify, and whether change capture and its retention are a supported dependency.
- A leased `SubscriptionEventWatchdog` pages `dbo.Transactions` from a durable cursor up to the visibility watermark.
- It reads each transaction **including historical rows** and evaluates the exact version written. `previous` criteria use the immediately preceding version. A missing version is recorded as `Unevaluable`, never silently dropped. An `Unevaluable` event is created only for subscriptions whose topic covers that resource type and interaction and whose authorization covers that resource type. It takes an event number but is not delivered, so the client detects a gap and recovers, as the framework expects. This depends on history being kept: where a resource type uses `no-version`, or a metadata-only update is not versioned, the earlier row is overwritten or deleted during the next write. Version-exact matching therefore requires history for subscribed resource types, or a weaker guarantee for those types; the product group decides which.
- Each subscription matches only transactions after its activation watermark.
- Matches go to a durable `dbo.SubscriptionEvents` outbox. Event rows and the cursor commit together, and a unique key makes replays idempotent: each match produces one logical event with one event number.
- Delivery is single-flight per subscription, with bounded retries and stable event numbers. Receipt is best-effort, as the spec defines: a notification may be delivered more than once (for example, when a response is lost) or not at all, and clients deduplicate by event number.
- `ResourceChangeData` is not changed.

**Contract:**

- **Topics:** definable through the API on all versions (option 7 on R4). Writes require a new `DataActions.SubscriptionTopic` permission whenever the old or the new resource is a topic.
- **Channel:** v1 ships the rest-hook channel only, with a mandatory handshake.
- **Payload:** `id-only` is the default. The server adds the content extension when the client omits it.
- **Operations:** `$status` is in v1. `$events`, `notificationShape` and the Azure channels follow later.
- **Authorization:** each event, at every payload level, is checked against an authorization snapshot captured when the subscription is created. The check runs **at matching, before an event number is assigned**, so events the subscriber may not see leave no gap in the numbering (R5 §3.9.8: no indication of results the subscriber is not permitted to see). It runs again at delivery only to catch revocation; a gap caused by revocation is acceptable. The server cannot see revocation at the identity provider, so revocation works through `off`/delete, a configurable maximum lifetime, and local role re-evaluation.
- **Activation grant:** the authorization snapshot is a server-owned record bound to the accepted subscription version and its endpoint. A subscription without a matching grant, for example one written or changed through `$import` or bulk operations, is not activated: no handshake and no notifications until an authorized caller activates it through the API. Its stored status must say so: the server sets it to `off`, or to `error` with the reason in `SubscriptionStatus.error`, so that reads and `$status` never report `active` for a subscription that is not delivering. Content validation alone does not prove who authorized the subscription.
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

- **Additive change:** new `SubscriptionEvents` and `SubscriptionStates` storage, a cursor in `dbo.Parameters`, a pump watchdog, a delivery background service, an in-memory search evaluator in Core, version converters (including R4 `Basic` ↔ topic), and a new data action. `DataActions.All` must be extended. Existing write paths and `ResourceChangeData` are unchanged.
- **Event source is open:** `Transactions` is preferred and `ResourceChangeData` is a documented alternative. Both are read-only uses of existing data, and both are implemented behind `ITransactionEventSource`. The final choice awaits product group guidance and is recorded here before acceptance.
- **Open decisions are settings:** the event source (`EventSource`), whether versions `$import` writes as history notify (`NotifyHistoricalVersions`, default off), whether topics may use `no-version` resource types (`UnversionedResourceTypes`: `AllowWithGaps` or `RequireHistory`) and the maximum lifetime (`MaxLifetimeDays`) are configuration, so product group guidance changes defaults rather than code.
- **Easier:** standard FHIR clients (R4 Backport first) can subscribe and get filtered, ordered notifications with gap detection. The in-memory evaluator and the event pump can be reused for future event-driven features.
- **Head-of-line latency:** a long `$import` batch or a stalled transaction delays notifications for every subscription, because visibility cannot advance past the oldest incomplete transaction. A latency target must be set from the phase 0 benchmark.
- **History cleanup:** version-exact matching needs invisible history to outlive the pump's lag. While the feature is on, `InvisibleHistoryCleanupWatchdog` keeps history for at least a configurable hold; a version removed before the pump reads it is recorded as `Unevaluable`.
- **Transaction ordering:** transaction ids come from the SQL Server clock, so they are not strictly increasing if that clock steps back. Rather than change the shared write path, the `Transactions` source reads a configurable lag behind the newest transactions and matches the same margin before activation. Writes that commit later than the lag are counted and logged, not notified, and changes just before activation may notify. Monotonic transaction ids in `MergeResourcesBeginTransaction` would remove the limitation and are a separate decision for the SQL storage owners.
- **Unreachable endpoints:** events stay pending while a subscription is in `error`, so a configurable limit (`MaxErrorHours`) turns it `off` and discards them.
- **Throughput:** a single pump bounds matching throughput. Partitioning by resource type is the fallback if benchmarks require it.
- **Authorization limits:** the snapshot cannot observe identity-provider revocation. The maximum subscription lifetime is configurable (default 365 days); the product group should confirm the default.
- **R4 interoperability:** the Backport IG leaves R4 topic definitions out of scope, and records that HL7 tried a `Basic`-plus-extensions profile and dropped it for low usability. API-defined topics on R4 are therefore a server-specific extension. Standard R4 Backport clients discover topics only by canonical URL and the CapabilityStatement topic-canonical extension, which the server also provides. Interoperability of the `Basic` profile with other servers is a goal to be tested, not a guarantee.
- **Delivery process:** to avoid repeating the earlier stalled attempts, this ADR must be accepted before the feature PR opens for review. The feature then ships as a single PR, inert behind the flag, and v1 ships only after the release gate defined in the proposal (end-to-end tests, handshake transitions, crash and backlog tests, load and latency results, feature-off regression).
- **Related ADRs:** no existing ADR is made obsolete. The registry reuses the cache-sync pattern from [ADR-2603 load-independent search parameter cache sync](adr-2603-load-independent-search-param-cache-sync.md).
