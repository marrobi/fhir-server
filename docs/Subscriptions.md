# Topic-based subscriptions

The FHIR server can notify clients over HTTPS when resources change, using the [R5 topic-based subscriptions framework](https://hl7.org/fhir/R5/subscriptions.html). On R4 and R4B it follows the [Subscriptions R5 Backport IG](https://hl7.org/fhir/uv/subscriptions-backport/). The design and its trade-offs are in [ADR-2611](arch/adr-2611-fhir-subscriptions.md).

**Design status:** the ADR is proposed. This guide describes the feature-branch implementation, not a released feature. Product group decisions and the [release gate](arch/Proposals/fhir-subscriptions-design.md#7-v1-release-gate) remain open.

The feature is **off by default**. It needs the **SQL Server** data store with schema version 123 or later. When off, `Subscription` and topic resources use normal resource storage, no notifications are sent, and `$status` returns 405 Method Not Allowed. When enabled on another data store, subscription writes and `$status` return 405. While a schema upgrade is pending, they return 503 Service Unavailable. The CapabilityStatement advertises subscriptions when the feature is enabled on a data store that supports them, including while a schema upgrade is pending. On R4, the topic-canonical extensions are refreshed when the CapabilityStatement is rebuilt.

## What is supported

| Area | Supported | Not supported |
|------|-----------|---------------|
| FHIR versions | R4 (Backport), R4B (Backport), R5 | STU3 |
| Topics | Defined through the API: `SubscriptionTopic` on R4B/R5; a `Basic` resource on R4 (see [R4 topic profile](#r4-topic-profile)) | Event triggers, topics on `Subscription`, topics loaded only from configuration |
| Triggers | `resourceTrigger` with `supportedInteraction` (`create`, `update`, soft `delete`), `queryCriteria` (`previous`, `current`, `resultForCreate`, `resultForDelete`, `requireBoth`) and `fhirPathCriteria` (`%previous`, `%current`) | Chained, reverse-chained (`_has`), composite, terminology (`:in`, `:below`) and result parameters; unsupported criteria are rejected when written |
| Filters | Topic `canFilterBy`; subscription `filterBy` on R5 or `backport-filter-criteria` extension on R4/R4B. Filters run in memory and must use parameters, modifiers and comparators allowed by the topic. Backport comparators use search prefixes, for example `date=gt2026-01-01`. | Same criteria limits as triggers |
| Subscriptions | Topic-based only (`criteria` is the topic canonical on R4) | Classic R4 criteria-based subscriptions (rejected with 400) |
| Channels | `rest-hook` | `websocket`, `email`, `message` |
| Payload | `empty`, `id-only` (default), `full-resource`; `application/fhir+json` or `application/fhir+xml` | |
| Notifications | `handshake`, `heartbeat`, `event-notification`; R5 `subscription-notification` bundles, Backport `history` bundles; `query-status` through `$status` | `query-event`, `$events` |
| Operations | `$status` on `Subscription/{id}/$status` and `Subscription/$status?id=` | `$events`, `$get-ws-binding-token` |

## How it works

1. A client writes a topic, then a `Subscription` that references it. The server validates both, sets a new activation request to `requested`, and caps `end` at `MaxLifetimeDays`. Clients may request activation or retry with `requested`, or stop with `off` or `entered-in-error`. Stopping is accepted even when the topic has been retired or deleted, or the endpoint is no longer allowed; requesting activation again checks both. Only the server sets `active` or `error`.
2. The server records an **authorization grant** from the caller's identity, roles, SMART scopes and compartment. It checks each event against this grant and the current role configuration before assigning an event number.
3. The server sends a **handshake**. A 2xx response makes the subscription `active`. A failed handshake is retried with increasing delays; after `MaxDeliveryAttempts` failures the subscription moves to `error`. Only changes after a successful handshake are matched, so if the endpoint cannot answer at first, changes made before the handshake succeeds are **not notified**. Run a catch-up search after the subscription becomes `active` (for example with `_lastUpdated` from when you created it) if you need them.
4. A background **pump** reads the configured event source, matches available committed versions in memory, and queues events per subscription. Matching and delivery add no synchronous step to other resource writes. Subscription and topic API writes still need validation and grant handling.
5. A background service sends events in event-number order, up to `maxCount` per notification. It retries failed sends, and failed heartbeats, with increasing delays. After `MaxDeliveryAttempts`, it sets `error` and pauses delivery; later events cannot pass the failed event, and new changes are still matched. Pending events stay until recovery or deactivation.
   - **Recovery is automatic.** In `error`, the server keeps sending a recovery handshake, doubling the delay after each failure up to `MaxRetryDelaySeconds` (15 minutes). When one succeeds, the subscription returns to `active` and the pending events are sent in order with their original event numbers. Failed checks update only the error reported by `$status`; they do not write a new `Subscription` version. Any update to the subscription through the API, such as setting `status` to `requested`, resets the delay so the handshake is sent straight away.
   - By default (`MaxErrorHours` = `0`) a subscription is never switched `off` because of errors, and its pending events are never discarded. If `MaxErrorHours` is set, the server sets `off` and discards them once the subscription has been in `error` that long. Subscriptions that are invalid, whose topic is missing, or whose grant is no longer valid are not retried.
6. Changing the endpoint, channel, topic, filters or content needs a new grant and handshake. Old pending events are retired because they were matched and authorized for different content. The grant is bound to the accepted content hash and endpoint, not to every resource version created by server status updates.
7. Setting `off` or `entered-in-error`, deleting the subscription, or reaching `end` revokes the grant and retires pending events. An imported resource cannot create a new grant. The registry validates stored resources again, including their lifetime. Without a valid grant, no handshake or notification is sent; background validation corrects status to `off` or `error`. A raw resource read may show stale status until that check runs.
8. Each subscription has one delivery worker at a time. Before every send, the worker renews its lease for longer than the send timeout. It checks authorization again to catch local revocation.

`$status` reports `status`, `eventsSinceSubscriptionStart` and, on failure, the error. The type-level form accepts at most `CoreFeatures:MaxItemCountPerSearch` ids.

Delivery is best-effort. Retries reuse event numbers, but a client may receive a notification more than once or not at all. Clients remove duplicates by event number and re-query resources to recover from gaps. `$status` does not replay events; `$events` is planned for later.

### History requirement

The pump needs resource history to match exact versions after rapid updates. Resource types should keep history (`versioned` or `versioned-update`). For `no-version` types, `UnversionedResourceTypes` controls whether topics are accepted:

- `AllowWithGaps` (default): topics may trigger on these types. If a change record remains but a required version is missing, the pump can record an unevaluable event. The topic must cover the change, and the grant must allow reading that resource type without compartment or search-parameter scope restrictions. `$status` reports the failure. The event takes a number but is not sent, so clients can detect a gap.
- `RequireHistory`: topics on `no-version` types are rejected. This includes `Resource`/`DomainResource` triggers while any covered type is `no-version`. Stored topics are checked again on load, and affected subscriptions move to `error`.

Neither setting guarantees complete history. If a write or cleanup deletes the change row itself, the source cannot report it and no numbered gap is guaranteed. Metadata-only updates without history can also remove required versions. Restricted subscribers receive no numbered gap for an unevaluable change because it could reveal a resource they cannot read.

While enabled, the feature keeps invisible history for at least `MaxHistoryCleanupHoldHours` (default 24). Cleanup does not wait for the pump cursor. A longer backlog can therefore lose versions. This hold does not preserve rows deleted during a `no-version` write.

### Historical versions from `$import`

`$import` can store older resource versions directly as history. These describe past changes and do not notify by default. With the `Transactions` source, set `NotifyHistoricalVersions=true` to include them. The change feed does not record these versions, so the setting has no effect with `ResourceChangeData`.

A version counts as written as history when a later version of the same resource was already written by the same or an earlier transaction. Ordinary writes that become history after a later update are not excluded by this setting.

### Event source

The pump reads committed changes through an event source, selected by `EventSource`:

| Source | Reads | Trade-offs |
|--------|-------|------------|
| `Transactions` (default) | `dbo.Transactions` up to the visibility watermark and configured read delay, then available versions for each transaction | Covers REST, bundle, `$import` and bulk write paths. History and clock limits still apply. Notifications wait for the oldest incomplete transaction. |
| `ResourceChangeData` | The `ResourceChangeData` change feed | Needs `CoreFeatures:SupportsResourceChangeCapture`; without it the pump logs an error and does not run. Covers only what the change feed records (it omits versions written as history). Its reader takes a table lock that can block concurrent writes. The feed itself is only read, never changed. |

Transaction IDs use the SQL Server clock at transaction start. A backward clock step can give a later write an ID below the saved cursor. The `Transactions` source delays reads by `TransactionReadLagSeconds` (default 5) to reduce this risk. The same margin applies before activation, so changes shortly before activation may notify.

A transaction that commits below a cursor already passed is not notified. The pump counts it in `Subscriptions.LateTransactions` and logs a warning. A normal long-running transaction holds back visibility; it is not skipped simply because it runs longer than the read delay.

On first run, the pump starts at visibility, or at the earliest existing activation watermark if lower. It then saves its progress after each matched page.

The source is fixed on first use of a database. A different source setting stops matching and logs an error. Restoring the original setting resumes from the saved cursor, but only records and versions still retained can be matched. Changing the source of an existing database is not supported.

## Configuration

Settings are under `FhirServer:Subscriptions`.

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `false` | Turns the feature on. |
| `AllowHttpEndpoints` | `false` | Allow `http://` endpoints. Dev/test only. |
| `AllowedPrivateNetworks` | empty | CIDR ranges of loopback or private networks that may receive notifications, for example `10.20.0.0/16`, `127.0.0.0/8` or `::1/128`. By default only public addresses can. Link-local (including cloud metadata endpoints), `168.63.129.16`, multicast and reserved addresses are always blocked. An invalid entry makes subscription writes and deliveries fail with an error that names it. |
| `AllowedEndpointHosts` | empty | If set, only these hosts (or subdomains, for entries starting with `*.`) can receive notifications. |
| `MaxActiveSubscriptions` | `1000` | Limit on subscriptions in `requested`, `active` or `error`. |
| `MaxLifetimeDays` | `365` | Maximum time until `end`. `0` disables the limit. |
| `MaxEventsPerNotification` | `20` | Upper bound on `maxCount` (which defaults to 1). |
| `MinHeartbeatPeriodSeconds` | `60` | Shortest heartbeat period a subscription may request. |
| `DefaultTimeoutSeconds` / `MaxTimeoutSeconds` | `30` / `120` | Endpoint timeout when not set, and the maximum allowed. |
| `MaxDeliveryAttempts` | `5` | Attempts for a notification, handshake or heartbeat before a subscription moves to `error`. |
| `RetryBaseDelaySeconds` | `2` | Base for exponential retry back-off. |
| `MaxRetryDelaySeconds` | `900` | Longest back-off delay. A subscription in `error` gets a recovery handshake at least this often. |
| `EventRetentionDays` | `7` | How long completed event records are kept, including delivered, unevaluable and no-longer-authorized events. Pending events stay until delivery or deactivation; content changes also retire old pending events. |
| `MaxErrorHours` | `0` | How long a subscription may stay in `error` before it is set to `off` and its pending events are discarded. `0`, the default, never switches it off: recovery checks continue and pending events are kept until the endpoint answers. Monitor the backlog metrics. |
| `MaxHistoryCleanupHoldHours` | `24` | Minimum invisible-history retention while subscriptions are enabled. |
| `PumpPeriodSeconds` / `PumpTransactionBatchSize` | `1` / `100` | Pump tick, and the number of transactions or change records per batch. |
| `TransactionReadLagSeconds` | `5` | Read delay for `Transactions` and the margin before activation. Adds this delay to notifications. See [Event source](#event-source). |
| `EventSource` | `Transactions` | Where the pump reads committed changes: `Transactions` or `ResourceChangeData`. See [Event source](#event-source). |
| `NotifyHistoricalVersions` | `false` | Whether versions written directly as history, such as `$import` loading earlier versions, notify. |
| `UnversionedResourceTypes` | `AllowWithGaps` | `AllowWithGaps` or `RequireHistory` for topics on `no-version` resource types. See [History requirement](#history-requirement). |
| `RegistryRefreshIntervalSeconds` | `10` | How often each instance reloads subscriptions and topics. |
| `DeliveryPollingIntervalSeconds` / `DeliveryLeaseSeconds` | `1` / `60` | Delivery polling, and the per-subscription lease that keeps delivery ordered across instances. Before each send the lease is renewed for at least the send's timeout plus 30 seconds. |

Endpoints must be absolute `https://` URLs with no user information. They must resolve to public addresses, or to a private network listed in `AllowedPrivateNetworks`. Every resolved address is checked again when connecting, so DNS changes cannot redirect notifications to a blocked address. List only the networks your webhooks are on, never the server's own infrastructure. On a hosted service whose network is not yours, keep the list empty. Headers that control the HTTP connection (`Host`, `Content-Type`, `Content-Length`, `Connection` and similar) cannot be set on a subscription.

The capacity defaults (`MaxActiveSubscriptions`, `PumpPeriodSeconds`, `PumpTransactionBatchSize`, `MaxEventsPerNotification`, `TransactionReadLagSeconds`) are provisional: they have not been sized by a benchmark on a representative deployment.

The product group must confirm the defaults for `EventSource`, `NotifyHistoricalVersions`, `UnversionedResourceTypes` and `MaxLifetimeDays` before accepting [ADR-2611](arch/adr-2611-fhir-subscriptions.md). These are settings, but `EventSource` cannot change after first use of a database.

## Notification payloads

Each notification event names one version of one resource. `focus` and each entry's `fullUrl` are version-independent URLs, as in the Backport IG examples and as HAPI FHIR sends them. Each entry's `response` gives the exact version:

| Element | Value |
|---|---|
| `response.location` | The version's URL, for example `https://fhir.example/Encounter/123/_history/2`. Read it directly to get that version. Absent for deletes, because a deleted version cannot be read. |
| `response.etag` | The version, for example `W/"2"`. |
| `response.lastModified` | When the version was written. It equals the event's `timestamp`, so a client can match each event to its entry. |

Entries follow the events in event-number order. With `maxCount` above 1, one notification can hold several versions of the same resource. On R4 and R4B each has its own entry. On R5, an `id-only` notification has one entry per resource, because the R5 Bundle rules do not allow entries with the same `fullUrl` and no resource; use `full-resource`, where each resource carries `meta.versionId`, or `maxCount` 1 if you need every version.

## Built-in profiles

On R4 and R4B the CapabilityStatement lists the Backport IG 1.1.0 profile `http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/backport-subscription` in `Subscription.supportedProfile`. The server ships that profile, the Backport extensions it uses and the payload content value set, so `$validate` and profile validation on write resolve it without anything being uploaded. Do not upload them yourself.

## R4 topic profile

R4 has no `SubscriptionTopic` resource type. This server uses `Basic` with code `http://hl7.org/fhir/fhir-types#SubscriptionTopic` and the StructureDefinition `http://fhir.microsoft.com/StructureDefinition/subscription-topic-basic` ([source](../src/Microsoft.Health.Fhir.R4.Core/Features/Subscriptions/StructureDefinition-subscription-topic-basic.json)). This is a server-specific extension, not a Backport IG requirement. The layout follows HAPI FHIR, but compatibility with every server is not guaranteed:

| Topic element | Where it goes on `Basic` |
|---------------|--------------------------|
| `url`, `version`, `title`, `description` | `extension`, URL `http://hl7.org/fhir/5.0/StructureDefinition/extension-SubscriptionTopic.<element>` |
| `status` | `modifierExtension`, URL `http://hl7.org/fhir/5.0/StructureDefinition/extension-SubscriptionTopic.status` |
| `resourceTrigger`, `canFilterBy` | `extension`, URL `http://hl7.org/fhir/4.3/StructureDefinition/extension-SubscriptionTopic.<element>` |

The server also accepts topics with `5.0` URLs for every element and `status` as a plain extension. It ships the profile and lists it in `Basic.supportedProfile` when subscriptions are available. `$validate` resolves the profile without an upload:

```http
POST /Basic/$validate?profile=http://fhir.microsoft.com/StructureDefinition/subscription-topic-basic
```

## Metrics

The server emits these instruments on the `FhirServer` meter:

| Instrument | Type | Tags | Meaning |
|------------|------|------|---------|
| `Subscriptions.PumpPositions` | counter | | Transactions or change records the pump read |
| `Subscriptions.PumpLag` | histogram (s) | | Age of the newest change read; how far notifications trail writes |
| `Subscriptions.Events` | counter | `outcome` (`matched`, `unevaluable`) | Events numbered for subscriptions |
| `Subscriptions.Notifications` | counter | `type`, `outcome` (`success`, `failure`) | Handshake, heartbeat and event notification attempts |
| `Subscriptions.NotificationDuration` | histogram (ms) | `type`, `outcome` | Duration of each attempt |
| `Subscriptions.StatusChanges` | counter | `status` | Status changes made by the server |
| `Subscriptions.LateTransactions` | counter | | Transactions that committed below a cursor already passed, so their changes did not notify. Should be zero; see [Event source](#event-source) |
| `Subscriptions.InError` | gauge | | Subscriptions in `error`, whose endpoint the server keeps checking. Alert when above zero for longer than you can tolerate |
| `Subscriptions.PendingEvents` | gauge | | Events waiting to be delivered |
| `Subscriptions.OldestPendingEventAge` | gauge (s) | | Age of the oldest waiting event. Alert when it exceeds your notification latency target |

## Authorization

- Creating, updating or deleting a topic needs the `subscriptionTopic` data action, in addition to `write` or `delete`. `*` includes it. See [Roles](Roles.md). With subscriptions enabled, a `$bulk-update` that may reach topics also needs it: one on the topic resource type (every `Basic` on R4), one across all types, or one with `_include` or `_revinclude`. `$import` and `$bulk-delete` do not check it, so grant those roles only to trusted administrators.
- Editing a topic changes what every subscription on it receives straight away, limited by each subscription's grant. Publish changes that widen a topic as a new topic resource, with a new version or canonical, and keep the old one. A subscription that names a versioned canonical (`url|version`) stays on that version.
- Creating a subscription needs `write` on `Subscription` and search access (`read` or `search`; SMART `s`) to every resource type its topic triggers on. A subscription reports every matching resource, so read-by-id access alone (`readById`; SMART v2 `r` without `s`) is not enough.
- The creator's grant governs delivery. The grant is captured from the identity that sets the subscription's content (topic, filters, channel, endpoint, headers or payload), not from the owner of the endpoint, and it is used for every notification. A write by another identity that leaves the content unchanged, for example extending `end` or requesting a handshake again, keeps the stored grant. Only the same identity can refresh it. The maximum lifetime counts from when the grant was captured. A service that creates subscriptions for other parties under its own identity must enforce each party's access itself. For example, it can receive notifications at its own endpoint and forward only what each party may see, or pass that party's own token to the server. Delegated subscription management is not supported.
- Events are filtered against the subscription's grant: roles (re-checked against the current `roles.json`), SMART scopes including scope search parameters, and the patient compartment for patient-scoped tokens.
- The grant cannot detect identity-provider revocation. Stop or delete the subscription to revoke it locally. The maximum lifetime forces periodic renewal unless that limit is disabled.
- `$status` needs read access to the subscription.

## Try it locally

[samples/subscriptions](../samples/subscriptions/README.md) has a Docker Compose environment (FHIR server, SQL Server 2025 and a webhook receiver) and an end-to-end script. [docs/rest/Subscriptions.http](rest/Subscriptions.http) has example requests.
