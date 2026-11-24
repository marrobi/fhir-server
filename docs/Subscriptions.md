# Topic-based subscriptions

The FHIR server can notify clients over HTTPS when resources change, using the [R5 topic-based subscriptions framework](https://hl7.org/fhir/R5/subscriptions.html). On R4 and R4B it follows the [Subscriptions R5 Backport IG](https://hl7.org/fhir/uv/subscriptions-backport/). The design and its trade-offs are in [ADR-2611](arch/adr-2611-fhir-subscriptions.md).

The feature is **off by default** and needs the **SQL Server** data store. When it is off, `Subscription` and topic resources are stored like any other resource and nothing is sent.

## What is supported

| Area | Supported | Not supported |
|------|-----------|---------------|
| FHIR versions | R4 (Backport), R4B (Backport), R5 | STU3 |
| Topics | Defined through the API: `SubscriptionTopic` on R4B and R5; on R4 a `Basic` resource with code `http://hl7.org/fhir/fhir-types#SubscriptionTopic` and the R5 cross-version topic extensions | Event triggers, topics on `Subscription`, server-only topics |
| Triggers | `resourceTrigger` with `supportedInteraction` (`create`, `update`, `delete`), `queryCriteria` (`previous`, `current`, `resultForCreate`, `resultForDelete`, `requireBoth`) and `fhirPathCriteria` (with `%previous` and `%current`) | Chained, reverse-chained (`_has`), composite, terminology (`:in`, `:below` …) and result parameters; these are rejected when the topic is written |
| Filters | `canFilterBy` on the topic; `filterBy` (R5) or `_criteria` filter extension (Backport) on the subscription, evaluated in memory with the same limits as criteria | |
| Subscriptions | Topic-based only (`criteria` is the topic canonical on R4) | Classic R4 criteria-based subscriptions (rejected with 400) |
| Channels | `rest-hook` | `websocket`, `email`, `message` |
| Payload | `empty`, `id-only` (default), `full-resource`; `application/fhir+json` or `application/fhir+xml` | |
| Notifications | `handshake`, `heartbeat`, `event-notification`; R5 `subscription-notification` bundles, Backport `history` bundles | `query-status`, `query-event`, `$events` |
| Operations | `$status` on `Subscription/{id}/$status` and `Subscription/$status?id=` | `$events`, `$get-ws-binding-token` |

## How it works

1. A client writes a topic, then a `Subscription` that references it. The server validates both, sets `status` to `requested` and caps `end` at `MaxLifetimeDays`. The server owns `status`: a client may only set `off` or `entered-in-error`.
2. The server records an **authorization grant** for the subscription from the caller's identity, roles, SMART scopes and compartment. Every event is checked against this grant (and against the *current* role configuration) before it is numbered, so a subscriber never learns about a resource it could not read.
3. The server sends a **handshake**. A 2xx response makes the subscription `active`; anything else moves it to `error`.
4. A background **pump** reads committed transactions, matches each written version against the active subscriptions in memory, and queues events per subscription. Matching never adds work to the write path.
5. A delivery service sends queued events in order, up to `maxCount` per notification, retries with back-off, and moves the subscription to `error` after `MaxDeliveryAttempts`. Undelivered events are kept; set `status` back to `requested` to retry.
6. Changing the endpoint, channel, topic, filters or content of a subscription needs a new grant and a new handshake.

`$status` reports `status`, `eventsSinceSubscriptionStart` and, on failure, the error.

### History requirement

The pump matches the exact versions written, including earlier versions when a resource is updated several times before the pump reads them. That needs the resource type to keep history (`versioned` or `versioned-update`). For `no-version` types, a version replaced before the pump reads it cannot be evaluated: it is counted as unevaluable, reported by `$status`, and not sent. While subscriptions are enabled, invisible history is kept for at least `MaxHistoryCleanupHoldHours`.

## Configuration

Settings are under `FhirServer:Subscriptions`.

| Setting | Default | Description |
|---------|---------|-------------|
| `Enabled` | `false` | Turns the feature on. |
| `AllowHttpEndpoints` | `false` | Allow `http://` endpoints. Dev/test only. |
| `AllowPrivateNetworkEndpoints` | `false` | Allow endpoints that resolve to loopback, private or link-local addresses. Dev/test only. |
| `AllowedEndpointHosts` | empty | If set, only these hosts (or subdomains, for entries starting with `*.`) can receive notifications. |
| `MaxActiveSubscriptions` | `1000` | Limit on subscriptions in `requested`, `active` or `error`. |
| `MaxLifetimeDays` | `365` | Maximum time until `end`. |
| `MaxEventsPerNotification` | `20` | Upper bound on `maxCount` (which defaults to 1). |
| `MinHeartbeatPeriodSeconds` | `60` | Shortest heartbeat period a subscription may request. |
| `DefaultTimeoutSeconds` / `MaxTimeoutSeconds` | `30` / `120` | Endpoint timeout when not set, and the maximum allowed. |
| `MaxDeliveryAttempts` | `5` | Attempts before a subscription moves to `error`. |
| `RetryBaseDelaySeconds` | `2` | Base for exponential retry back-off. |
| `EventRetentionDays` | `7` | How long delivered events are kept. Pending events are kept until delivered or the subscription is deleted. |
| `MaxHistoryCleanupHoldHours` | `24` | Minimum invisible-history retention while subscriptions are enabled. |
| `PumpPeriodSeconds` / `PumpTransactionBatchSize` | `1` / `100` | Pump tick and batch size. |
| `RegistryRefreshIntervalSeconds` | `10` | How often each instance reloads subscriptions and topics. |
| `DeliveryPollingIntervalSeconds` / `DeliveryLeaseSeconds` | `1` / `60` | Delivery polling and the per-subscription lease that keeps delivery ordered across instances. |

Endpoints must be absolute `https://` URLs with no user information. Every resolved address is checked again when connecting, so DNS changes cannot redirect notifications to a blocked address. Headers that control the HTTP connection (`Host`, `Content-Type`, `Content-Length`, `Connection` and similar) cannot be set on a subscription.

## Authorization

- Creating, updating or deleting a topic needs the `subscriptionTopic` data action, in addition to `write` or `delete`. `*` includes it. See [Roles](Roles.md).
- Creating a subscription needs `write` on `Subscription` and read access to every resource type its topic triggers on.
- Events are filtered against the subscription's grant: roles (re-checked against the current `roles.json`), SMART scopes including scope search parameters, and the patient compartment for patient-scoped tokens.
- `$status` needs read access to the subscription.

## Try it locally

[samples/subscriptions](../samples/subscriptions/README.md) has a Docker Compose environment (FHIR server, SQL Server 2025 and a webhook receiver) and an end-to-end script. [docs/rest/Subscriptions.http](rest/Subscriptions.http) has example requests.
