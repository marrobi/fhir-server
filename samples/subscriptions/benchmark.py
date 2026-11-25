"""Load and latency benchmark for topic-based subscriptions (ADR-2611).

Creates an R4 topic and per-patient rest-hook subscriptions, then writes Encounters that each match exactly one subscription,
and reports write throughput and latency. Pump lag and delivery latency are read from dbo.SubscriptionEvents afterwards
(see README.md, "Benchmark").

Usage:
  python benchmark.py subscribe <fhir base> <hook base> <count> [first index]
  python benchmark.py write <fhir base> <writes> <concurrency> <patients> [bundle size]
"""
import http.client
import json
import sys
import threading
import time
import urllib.parse
import uuid

from fhir_resources import backport_subscription, encounter, status_transition_topic

TOPIC_URL = "http://example.org/fhir/SubscriptionTopic/benchmark-encounter-cancelled"
HEADERS = {"Content-Type": "application/fhir+json", "Accept": "application/fhir+json", "Prefer": "return=minimal"}


class Client:
    def __init__(self, base):
        url = urllib.parse.urlparse(base)
        self.path = url.path.rstrip("/")
        self.conn = http.client.HTTPConnection(url.hostname, url.port or 80, timeout=120)

    def send(self, method, path, body=None):
        self.conn.request(method, self.path + "/" + path, body=json.dumps(body) if body is not None else None, headers=HEADERS)
        response = self.conn.getresponse()
        text = response.read()
        return response.status, text


def topic():
    return status_transition_topic(TOPIC_URL, "cancelled", topic_id="benchmark-topic")


def subscription(hook, patient):
    return backport_subscription(TOPIC_URL, f"{hook}/bench", patient, content="id-only", max_count=20, reason="benchmark", subscription_id=f"bench-{patient}")


def new_encounter(patient):
    return encounter(uuid.uuid4().hex, patient, "cancelled")


def parallel(base, count, concurrency, work):
    """Runs work(client, i) for i in range(count) on `concurrency` threads; returns per-call latencies in ms, errors and elapsed seconds."""
    latencies, errors, lock, next_index = [], [], threading.Lock(), [0]

    def worker():
        client = Client(base)
        while True:
            with lock:
                i = next_index[0]
                next_index[0] += 1
            if i >= count:
                return
            start = time.perf_counter()
            try:
                status, text = work(client, i)
                if status >= 300:
                    errors.append(f"{status} {text[:200]!r}")
            except Exception as e:  # noqa: BLE001 - a benchmark reports failures rather than stopping
                errors.append(repr(e))
                client = Client(base)
            elapsed = (time.perf_counter() - start) * 1000
            with lock:
                latencies.append(elapsed)

    threads = [threading.Thread(target=worker) for _ in range(concurrency)]
    start = time.perf_counter()
    for t in threads:
        t.start()
    for t in threads:
        t.join()
    return latencies, errors, time.perf_counter() - start


def percentile(values, p):
    ordered = sorted(values)
    return round(ordered[min(len(ordered) - 1, int(len(ordered) * p))], 1) if ordered else None


def subscribe(base, hook, count, start=0):
    client = Client(base)
    status, text = client.send("PUT", "Basic/benchmark-topic", topic())
    assert status in (200, 201), text
    latencies, errors, seconds = parallel(base, count - start, 8, lambda c, i: c.send("PUT", f"Subscription/bench-p{start + i}", subscription(hook, f"p{start + i}")))
    deadline = time.time() + 600
    active = 0
    while time.time() < deadline:
        _, text = client.send("GET", "Subscription?status=active&_summary=count")
        active = json.loads(text)["total"]
        if active >= count:
            break
        time.sleep(2)
    print(json.dumps({"subscriptions": count, "created_seconds": round(seconds, 1), "active": active,
                      "activation_seconds": round(time.time() - (deadline - 600), 1), "errors": errors[:5], "error_count": len(errors)}))


def write(base, writes, concurrency, patients, bundle_size):
    patients = max(1, patients)
    if bundle_size <= 1:
        def work(c, i):
            resource = new_encounter(f"p{i % patients}")
            return c.send("PUT", f"Encounter/{resource['id']}", resource)
        calls = writes
    else:
        def work(c, i):
            entries = []
            for j in range(bundle_size):
                resource = new_encounter(f"p{(i * bundle_size + j) % patients}")
                entries.append({"fullUrl": f"urn:uuid:{uuid.uuid4()}", "resource": resource, "request": {"method": "PUT", "url": f"Encounter/{resource['id']}"}})
            return c.send("POST", "", {"resourceType": "Bundle", "type": "transaction", "entry": entries})
        calls = writes // bundle_size

    latencies, errors, seconds = parallel(base, calls, concurrency, work)
    print(json.dumps({"writes": calls * max(1, bundle_size), "calls": calls, "concurrency": concurrency, "bundle_size": bundle_size,
                      "seconds": round(seconds, 2), "writes_per_second": round(calls * max(1, bundle_size) / seconds, 1),
                      "p50_ms": percentile(latencies, 0.5), "p95_ms": percentile(latencies, 0.95), "p99_ms": percentile(latencies, 0.99),
                      "error_count": len(errors), "errors": errors[:3]}))


if __name__ == "__main__":
    if sys.argv[1] == "subscribe":
        subscribe(sys.argv[2], sys.argv[3], int(sys.argv[4]), int(sys.argv[5]) if len(sys.argv) > 5 else 0)
    else:
        write(sys.argv[2], int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5]), int(sys.argv[6]) if len(sys.argv) > 6 else 1)
