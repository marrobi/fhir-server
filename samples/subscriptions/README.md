# Topic-based subscriptions: local environment

*IMPORTANT:* For dev/test only. Security is disabled, the SQL connection is not encrypted, and the server is allowed
to notify HTTP and private-network endpoints so it can reach the local receiver. See [ADR-2611](../../docs/arch/adr-2611-fhir-subscriptions.md)
and [Subscriptions](../../docs/Subscriptions.md).

The compose file starts:

| Service | Port | Purpose |
|---------|------|---------|
| `fhir-api` | 8080 | FHIR server (SQL Server data store) with `FhirServer:Subscriptions:Enabled=true` |
| `sql` | — | SQL Server 2025 (the schema needs the `vector` type) |
| `webhook` | 8081 | [webhook_receiver.py](webhook_receiver.py): records POSTs; `GET /notifications` lists them, `DELETE /notifications` clears them, paths starting with `/fail` return HTTP 500 |

## Run

```bash
export SAPASSWORD='<a strong password>'
docker compose up -d --build          # R4 by default; FHIR_VERSION=R4B or R5 to change
```

Wait until `curl http://localhost:8080/metadata` returns the CapabilityStatement, then run the R4 end-to-end check from
the receiver container (it sees the server as `fhir-api` and itself as `webhook`):

```bash
docker compose exec webhook python /app/e2e_r4_backport.py http://fhir-api:8080 http://webhook:8080 http://localhost:8080
```

It creates a topic (an R4 `Basic` resource), an `id-only` rest-hook subscription, and checks the handshake,
one notification for a matching Encounter transition, `$status`, a failing endpoint moving to `error`, a new handshake
after an endpoint change, and that deleting the subscription stops notifications. It deletes the topic and subscriptions
it created when it finishes. The scripts share their topic, subscription and Encounter builders in [fhir_resources.py](fhir_resources.py).

To check what a topic can trigger on and filter by, run (works with `FHIR_VERSION` R4, R4B or R5):

```bash
docker compose exec webhook python /app/e2e_topic_scope.py http://fhir-api:8080 http://webhook:8080 http://localhost:8080
```

It reads the server's FHIR version and writes resources in that version's shape, then runs one topic per case and checks
exactly which changes are notified: `supportedInteraction` (including `delete`), `queryCriteria` with `requireBoth`,
`resultForCreate` and `resultForDelete`, `fhirPathCriteria` with `%previous` and `%current`, several triggers with a
filter per resource type, combined `canFilterBy` filters with modifiers, and the topics and filters that are rejected
because they cannot be evaluated in memory. On R4 and R5 it also checks that filter comparators must be allowed by the topic (R4B topics cannot declare them).

The receiver replies to notifications with an empty `Bundle`, so it can also be used with HAPI FHIR, which retries
deliveries whose response it cannot parse as a Bundle.

To try it by hand, see [Subscriptions.http](../../docs/rest/Subscriptions.http) and use `http://webhook:8080/<path>` as
the subscription endpoint. View what was received with `curl http://localhost:8081/notifications`.

To try the change-feed event source instead of `Transactions`, add these to the `fhir-api` environment (see [Event source](../../docs/Subscriptions.md#event-source)):

```yaml
      FhirServer__CoreFeatures__SupportsResourceChangeCapture: "true"
      FhirServer__Subscriptions__EventSource: "ResourceChangeData"
```

## Benchmark

[benchmark.py](benchmark.py) measures write throughput with subscriptions active, and the events it causes give the pump lag
and delivery latency. Run it from the receiver container, first to create subscriptions (one per patient, all on one R4 topic),
then to write Encounters that each match exactly one subscription:

```bash
docker compose exec webhook python /app/benchmark.py subscribe http://fhir-api:8080 http://webhook:8080 100
docker compose exec webhook python /app/benchmark.py write http://fhir-api:8080 5000 16 100      # writes, concurrency, patients [, bundle size]
```

Both print JSON: `subscribe` reports how long creation and activation took, and `write` reports writes per second and call
latency percentiles. When the receiver has the notifications, read the lags from the database. `EventDate` is when the resource
was written, `NextAttemptDate` is when the pump recorded the event (it only changes after a failed attempt, so only first-attempt
deliveries are counted), and `StatusDate` is when it was delivered:

```sql
SELECT count(*) AS Delivered
      ,avg(datediff_big(millisecond, EventDate, NextAttemptDate)) AS AvgPumpLagMs
      ,max(datediff_big(millisecond, EventDate, NextAttemptDate)) AS MaxPumpLagMs
      ,avg(datediff_big(millisecond, EventDate, StatusDate)) AS AvgDeliveryLatencyMs
      ,max(datediff_big(millisecond, EventDate, StatusDate)) AS MaxDeliveryLatencyMs
  FROM dbo.SubscriptionEvents
  WHERE Status = 1 AND Attempts = 0
```

For example, `docker compose exec sql /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P "$SAPASSWORD" -C -d FHIR -Q "<query>"`.

## Clean up

```bash
docker compose down -v
```
