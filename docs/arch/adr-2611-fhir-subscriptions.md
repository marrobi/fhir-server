# ADR-2611: FHIR Topic-Based Subscriptions

**Status**: Proposed
**Date**: 2026-11-25
**Feature**: FHIR Subscriptions and notifications

## Summary

Add standard FHIR subscriptions, so that clients are notified when resources change. The first release targets R4 with the [Subscriptions R5 Backport IG](https://hl7.org/fhir/uv/subscriptions-backport/), plus R4B and R5, on SQL Server only. It is off by default. A background worker reads committed changes from the existing SQL transaction log, matches them in memory, and sends notifications to client web hooks. The write path does not change. A working prototype exists on [`marrobi/fhir-server` `feature/fhir-subscriptions`](https://github.com/marrobi/fhir-server/tree/feature/fhir-subscriptions). The [design proposal](Proposals/fhir-subscriptions-design.md) has the full scope, design and release gate.

## Decisions requested

The product group (PG) and maintainers must settle these before this ADR is accepted. Each one has a recommended answer.

| # | Decision | Recommendation | If we choose differently |
|---|---|---|---|
| 1 | Which change source the worker reads (options A4 and A2) | `Transactions` | `ResourceChangeData` adds a write to each resource write and a table lock on reads. It also makes the feature depend on change capture retention. |
| 2 | Whether v1 ships both change sources | Ship `Transactions` only. Add the change-feed source later if it is needed. | Shipping both adds about 330 feature lines and 160 test lines, a second test matrix, and a setting that cannot change after first use. |
| 3 | How the code is delivered (options D1–D4) | D2: a series of PRs merged into `main`, each one off by default | See [PR phasing](#d-pr-phasing). |
| 4 | Whether historical versions loaded by `$import` send notifications (`NotifyHistoricalVersions`) | No (`false`) | `true` can send a burst of notifications about old changes. |
| 5 | Whether topics are allowed on resource types that keep no history (`UnversionedResourceTypes`) | Allow, with documented gaps (`AllowWithGaps`) | `RequireHistory` rejects these topics. It is safer, but less useful. |
| 6 | Whether subscriptions expire automatically (`MaxLifetimeDays`) | Never expire (`0`). A subscription runs until the client sets an `end`, or someone switches it off or deletes it. Nothing is ever deleted automatically. | A limit (for example 365 days) switches subscriptions `off`, but does not delete them. It also limits stale access, because the server cannot see access removed in the identity provider. The risk is that a client which never renews stops receiving notifications without noticing. For clinical workflows, this is a worse failure than stale access, which other controls cover. |
| 7 | How webhook credentials in subscription headers are handled (options C1–C4) | C1 for v1: allow headers, and document that they are not secure storage | See [webhook credentials](#c-webhook-credentials). |
| 8 | Whether to block deleting a topic that active subscriptions use | Block | Allowing it stops those subscriptions without warning. |
| 9 | Confirm that Azure Health Data Services events read `ResourceChangeData` | Owner: managed service team | If the events service uses something else, re-check [section 9 of the proposal](Proposals/fhir-subscriptions-design.md#9-impact-on-azure-health-data-services-events). |
| 10 | What happens when a client's webhook stays unreachable (`MaxErrorHours`) | Decide before release. The prototype switches the subscription `off` and discards waiting notifications after 72 hours. | `0` (never) loses nothing and waits for the webhook to be fixed, but waiting notifications build up without limit and need monitoring. A time limit caps database growth, but an outage longer than the limit loses notifications and stops the subscription without the client noticing. A cap on the number of waiting notifications, instead of time, would need a design change. |

## Context

Customers want to know when FHIR data changes without polling. FHIR defines this as [topic-based subscriptions](https://hl7.org/fhir/R5/subscriptions.html). A *topic* says what kind of change matters. A *subscription* is a client's request for notifications on one topic, with filters and an endpoint. The managed Azure Health Data Services FHIR service runs R4, so **R4 with the Backport IG is the main target**. R4 has no `SubscriptionTopic` resource type, but clients must still be able to define topics through the API.

The FHIR minimum is:

- `Subscription` read and write
- `SubscriptionTopic` read and search on R4B and R5
- The `$status` operation
- One channel type and one payload type

For rest-hook, the client endpoint must accept a handshake before the subscription becomes `active`.

The SQL data store already records every write in `dbo.Transactions`. A *visibility watermark* marks the point below which all transactions have finished. Transaction IDs are assigned when a transaction starts, from the SQL Server clock.

Constraints:

- **Writes are not slowed down.** Matching and sending happen in the background. With the feature off, behaviour does not change.
- **Existing events are not changed.** The `ResourceChangeData` change feed, which is presumed to feed [Azure Health Data Services events](https://learn.microsoft.com/azure/healthcare-apis/events/events-overview), is not modified. Reading it is allowed.
- **All write paths are covered.** This includes REST, bundles, `$import` and bulk operations. Each match gets exactly one event number, including after a crash and restart. Sending is best-effort.
- **Access is respected.** Notifications respect the subscriber's access, even without an HTTP request. Checks *fail closed*: if access cannot be confirmed, nothing is sent ([ADR-2609](adr-2609-smart-authorization-fail-closed.md)). Background work uses caller-owned service scopes ([ADR-2610](adr-2610-caller-owned-scopes.md)).

Three earlier attempts were closed without public review: #4474, #4628 and #5478. Each was a large, multi-layer PR with no agreed design. [Appendix A of the proposal](Proposals/fhir-subscriptions-design.md#appendix-a-lessons-from-earlier-attempts) records what is known about them.

## Options Considered

### A. Where changes come from, and how they are matched

| # | Option | Result |
|---|---|---|
| A1 | Match inside the API request pipeline | **Rejected.** It runs before commit, `$import` and bulk jobs bypass it, and it slows every write. |
| A2 | Read the `ResourceChangeData` change feed | **Viable alternative.** It records create, update and soft delete. However, it is off by default and its reader takes a table lock (`TABLOCK, HOLDLOCK`) that can block writes. Its retention is managed outside this repository, and it does not record imported history. |
| A3 | Queue one job per committed transaction (an earlier prototype) | **Rejected.** It adds one job per write and needs coordination to keep one cursor across workers. It also read only current rows, so it lost versions after rapid updates. |
| A4 | One background worker reads `dbo.Transactions` from a saved position (the *cursor*) and matches in memory | **Chosen.** It is always on and adds nothing to writes. Results and the cursor are saved together, so a restart cannot create duplicates. One long transaction delays every subscription, and exact matching needs resource history. |
| A5 | Evaluate criteria with database queries (chained parameters, `_has`, terminology) | **Rejected.** It needs one query per change per subscription. These criteria are rejected when a topic or subscription is written. |
| A6 | Match in a separate service that consumes Azure Health Data Services events through Event Grid | **Not chosen.** It works only for the managed service, needs extra Azure resources, and cannot support `$status` or manage subscription status without a gateway. Details are in [proposal section 6.2](Proposals/fhir-subscriptions-design.md#62-change-source-options). |

### B. How R4 clients define topics

| # | Option | Result |
|---|---|---|
| B1 | Also support classic R4 subscriptions, where `criteria` is a search URL | **Rejected.** Topic-based subscriptions replace this model. Supporting both doubles the validation rules and notification formats. |
| B2 | Store R4 topics as `Basic` resources with code `fhir-types#SubscriptionTopic`, with topic elements carried as extensions under a published profile | **Chosen.** Topics stay valid R4 resources and reuse REST, search, history and authorization. The layout follows HAPI FHIR. |
| B3 | Add a non-standard `/SubscriptionTopic` endpoint to R4, or allow only server-configured topics | **Rejected.** The endpoint is not R4 standard, and configured-only topics do not meet the requirement to define topics through the API. |

### C. Webhook credentials

A subscription can carry HTTP headers that the server adds to every notification it sends to the client's webhook: `Subscription.parameter` on R5, and `channel.header` on R4 and R4B. Their only purpose is to let the webhook check that the call is genuine. Typical examples are `Authorization: Bearer <token>`, an API gateway key, or an Azure Functions `x-functions-key`. The webhook owner issues the value, and the FHIR server forwards it unchanged.

The spec requires the server to send these headers. However, it also says that `Subscription` resources are not secure storage for secrets, and allows only limited-use tokens ([R5 Subscriptions, security](https://hl7.org/fhir/R5/subscriptions.html)).

In the prototype:

- Values are stored as plain text in the `Subscription` resource.
- Anyone with `Read` on `Subscription` can see them, through read, search and `_history`.
- They are included in a system `$export`.
- They are never logged.

The same applies to secrets in the endpoint URL, such as Logic Apps or Storage SAS keys.

| # | Option | For | Against |
|---|---|---|---|
| C1 | **Allow headers, and document that they are not secure storage.** Advise a limited-use, revocable value per subscription, not a shared or long-lived credential. | Matches the spec. Interoperable with standard clients. No extra work. | Relies on customers following guidance. Values are visible to readers and in exports. Short-lived OAuth tokens expire, which drives clients towards long-lived keys. |
| C2 | **As C1, plus hide header values** in read, search, history and export for callers without `Write` on `Subscription` | Fewer people see the values | Non-standard. A client that reads, edits and writes back would overwrite the real value with the masked one, so this needs merge rules. Export round trips lose the values. |
| C3 | **Store values in a secret store**, such as Key Vault, and keep only a reference in the resource | Real secret storage, with rotation and audit | Larger design, not in the spec, and adds a new dependency to the OSS server. Import and export need special handling. |
| C4 | **Do not accept credential headers.** The server proves its identity instead, by signing each notification (HMAC or signed JWT) or with mutual TLS. Managed-identity Azure channels follow later. | No customer secret stored | Non-standard for rest-hook. Existing webhooks that expect a bearer token or key would need changes. |

**Recommended: C1 for v1.** Write up C2 and C4 as follow-ups once customer needs are known. C2 and C4 can be combined: hide headers, and also offer signed notifications.

### D. PR phasing

The prototype adds about 11,000 lines of feature code and 6,700 lines of tests, plus 1,200 lines of docs and samples, in about 190 files. The feature code alone is about three times the size of #4628, which never got a review. Line counts below are measured on the prototype branch, grouped by the PR each file would belong to, and rounded.

| # | Option | For | Against |
|---|---|---|---|
| D1 | **One feature PR**, off by default, with commits grouped by component | One review covers the whole design. No partly built feature on `main`. | About 11,000 feature and 6,700 test lines in one PR. This is the same pattern that stalled #4474 and #4628. Reviewers for SQL, security and FHIR must all review one PR. |
| D2 | **A series of PRs into `main`**, each off by default and each with its own tests (see below) | Each PR has at most about 4,000 feature lines and can be reviewed by the right owners. Problems are found early. Prerequisites help other features. | `main` holds an incomplete feature until the last PR. The feature must stay off and unadvertised until then. The schema change ships to all databases, unused, from PR 4. |
| D3 | **Feature branch in `microsoft/fhir-server`**, with reviewed PRs into the branch and one final merge into `main` | Pieces are reviewed, and `main` is untouched until the feature is done. | The final merge is still about 11,000 feature and 6,700 test lines. The branch drifts from `main`, and schema version numbers can clash. CI must run on the branch. |
| D4 | **Thin end-to-end slice first**: R4 Backport, `Transactions` source, `id-only`, rest-hook and `$status`. R4B, R5, other payloads and the second source follow. | Customer value and load measurements come earliest. Later PRs are small. | The first PR still crosses every layer (about 7,000–8,000 feature lines plus tests). Some code is reworked when the other versions are added. |

**Recommended: D2**, combined with D4's narrower first scope where it is cheap. In particular, defer the `ResourceChangeData` source (decision 2). Proposed sequence:

| PR | Content | Feature lines | Test lines | Main reviewers |
|---|---|---|---|---|
| 1 | `DataActions.SubscriptionTopic` permission, `Subscriptions` settings, feature flag | 200 | Small; tests to be added | Security |
| 2 | In-memory search and FHIRPath evaluator (standalone, reusable) | 650 | 200 | Search |
| 3 | Subscription and topic models, version converters, R4 `Basic` topic profile, validation on API writes, authorization grant capture | 3,900 (including 1,000 of profile JSON and 700 of generated resource strings) | 1,600 | FHIR, Security |
| 4 | SQL schema, `Transactions` source, event worker, event store, registry, matching | 3,600 | 2,100 | SQL |
| 5 | Delivery service, rest-hook channel, handshake, notification bundles, `$status`, CapabilityStatement, metrics, E2E tests | 2,300 | 2,600 | FHIR, API |
| Docs | User guide, example requests and local sample, with PR 5 | 1,200 | – | FHIR |
| Deferred | `ResourceChangeData` source (decision 2) | 330 | 160 | SQL |

Until PR 5 merges, `Subscriptions:Enabled=true` is unsupported. The CapabilityStatement does not advertise subscriptions, and subscription writes are handled as today. The release gate in [proposal section 7](Proposals/fhir-subscriptions-design.md#7-v1-release-gate) applies after PR 5.

## Proposed decision

Accepting this ADR means agreeing to build FHIR topic-based subscriptions as follows. Answers to the [decisions requested](#decisions-requested) may change individual rows.

| Question | Choice | Why |
|---|---|---|
| What do we build? | Topic-based subscriptions for R4 (Backport IG), R4B and R5 on SQL Server, behind `FhirServer:Subscriptions:Enabled` (default `false`) | Standard FHIR, and R4 is the managed service's version. Off by default, so nothing changes for existing customers. |
| How are changes detected and matched? (A) | **A4:** one background worker reads `dbo.Transactions` and matches in memory | Adds nothing to writes and covers every write path. |
| How do R4 clients define topics? (B) | **B2:** `Basic` resources with a published profile | Valid R4, and compatible with HAPI FHIR. |
| How are webhook credentials handled? (C) | **C1:** allow headers, and document that they are not secure storage | Matches the spec. Stronger options can follow. |
| How is the code delivered? (D) | **D2:** five PRs into `main`, each off by default | Each PR is small enough to review. |

The v1 scope is in [proposal section 3](Proposals/fhir-subscriptions-design.md#3-feature-support-matrix), and the design in [proposal section 6](Proposals/fhir-subscriptions-design.md#6-implementation-design).

## Consequences

### Benefits

- Standard FHIR clients, starting with R4 Backport clients, get filtered notifications in event-number order and can detect numbered gaps.
- Write performance and existing Azure Health Data Services events do not change.
- The in-memory evaluator depends only on Core abstractions. Other features, or an external service (option A6), can reuse it.
- Under D2, each PR is small enough for the owning team to review.

### Costs and limits

- **New components:** two SQL tables (`SubscriptionEvents`, `SubscriptionStates`), the event worker, the delivery service, the evaluator, version converters and one data action. `DataActions.All` grows to include the new action.
- **Delay:** one long `$import` batch or a stuck transaction delays every subscription, because the watermark cannot pass the oldest unfinished transaction.
- **Throughput:** one worker limits matching speed. If load tests require it, the fallback is to split the work by resource type.
- **Some changes cannot be notified:**
  - If history is turned off or cleaned up before the worker reads it, a change may be skipped with no numbered gap. While the feature is on, the server keeps invisible history for at least 24 hours (`MaxHistoryCleanupHoldHours`).
  - Transaction IDs come from the SQL clock. If the clock steps backwards, a transaction can land behind the cursor. The worker reads 5 seconds behind (`TransactionReadLagSeconds`) to reduce this risk, and logs any late transaction it misses.
- **Access limits:** the server cannot see access removed in the identity provider. With no automatic expiry (decision 6), a subscription created by someone who later loses access keeps running until an administrator stops it. Controls:
  - Delivery checks the subscriber's current server roles.
  - Administrators can find subscriptions with search and switch them `off` or delete them.
  - Operators can set `MaxLifetimeDays` if their policy requires expiry. Expired subscriptions are switched `off`, not deleted.
- **Unreachable webhooks:** pending events wait while the subscription is in `error`. The time limit after which they are discarded and the subscription is switched `off` is decision 10.
- **R4 topics are a server extension.** The Backport IG leaves R4 topic definition out of scope. Interoperability with HAPI FHIR 8.12.0 was checked, but other servers are not guaranteed to work.
- **Schema:** a new schema version is required. Under D2, it ships with PR 4, before the feature is usable.

### Follow-ups

- The PG and maintainers resolve the decisions requested above.
- The release gate requires measured throughput, database load and notification delay, plus a latency target ([proposal section 7](Proposals/fhir-subscriptions-design.md#7-v1-release-gate)).
- A tracking issue, an owner and named reviewers are needed for each PR in the chosen phasing option.
- No existing ADR becomes obsolete. The subscription registry reuses the cache-sync pattern from [ADR-2603](adr-2603-load-independent-search-param-cache-sync.md).
