"""End-to-end check of FHIR topic-based subscriptions (R4 Backport) against a running server.

Runs the ADR-2611 first milestone: an API-defined R4 topic (Basic), an id-only rest-hook subscription,
the handshake, an event notification, $status, and delivery failure handling.

Usage: python e2e_r4_backport.py [FHIR base URL] [webhook base URL as seen by the server] [webhook base URL as seen by this script]
"""
import json
import sys
import time
import urllib.error
import urllib.request
import uuid

FHIR = sys.argv[1] if len(sys.argv) > 1 else "http://localhost:8080"
HOOK_FOR_SERVER = sys.argv[2] if len(sys.argv) > 2 else "http://webhook:8080"
HOOK_LOCAL = sys.argv[3] if len(sys.argv) > 3 else HOOK_FOR_SERVER
# R4 topic layout used by HAPI FHIR: metadata on the 5.0 prefix (status as a modifier extension), backbone elements on 4.3.
TOPIC_PREFIX = "http://hl7.org/fhir/5.0/StructureDefinition/extension-SubscriptionTopic."
TOPIC_BACKBONE_PREFIX = "http://hl7.org/fhir/4.3/StructureDefinition/extension-SubscriptionTopic."
BACKPORT = "http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/"
TOPIC_URL = f"http://example.org/fhir/SubscriptionTopic/encounter-finished-{uuid.uuid4().hex[:8]}"


def call(method, url, body=None, expect=None):
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(url, data=data, method=method, headers={"Content-Type": "application/fhir+json", "Accept": "application/fhir+json"})
    try:
        with urllib.request.urlopen(req, timeout=60) as resp:
            status, text = resp.status, resp.read().decode()
    except urllib.error.HTTPError as e:
        status, text = e.code, e.read().decode()
    if expect is not None and status != expect:
        raise AssertionError(f"{method} {url} returned {status}, expected {expect}: {text[:2000]}")
    return status, (json.loads(text) if text[:1] in ("{", "[") else text)


def notifications():
    return call("GET", HOOK_LOCAL + "/notifications")[1]


def wait_for(predicate, what, timeout=60):
    deadline = time.time() + timeout
    while time.time() < deadline:
        value = predicate()
        if value:
            return value
        time.sleep(1)
    raise AssertionError(f"timed out waiting for {what}")


def param(parameters, name):
    return next(p for p in parameters["parameter"] if p["name"] == name)


def status_of(sub_id):
    return call("GET", f"{FHIR}/Subscription/{sub_id}", expect=200)[1]["status"]


def ext(url, **value):
    return {"url": url, **value}


def topic():
    return {
        "resourceType": "Basic",
        "modifierExtension": [ext(TOPIC_PREFIX + "status", valueCode="active")],
        "code": {"coding": [{"system": "http://hl7.org/fhir/fhir-types", "code": "SubscriptionTopic"}]},
        "extension": [
            ext(TOPIC_PREFIX + "url", valueUri=TOPIC_URL),
            {"url": TOPIC_BACKBONE_PREFIX + "resourceTrigger", "extension": [
                ext("resource", valueUri="Encounter"),
                ext("supportedInteraction", valueCode="create"),
                ext("supportedInteraction", valueCode="update"),
                {"url": "queryCriteria", "extension": [
                    ext("previous", valueString="status:not=finished"),
                    ext("resultForCreate", valueCode="test-passes"),
                    ext("current", valueString="status=finished"),
                    ext("requireBoth", valueBoolean=True),
                ]},
            ]},
            {"url": TOPIC_BACKBONE_PREFIX + "canFilterBy", "extension": [ext("resource", valueUri="Encounter"), ext("filterParameter", valueString="subject")]},
        ],
    }


def subscription(endpoint, patient_id, content=None):
    channel = {
        "extension": [ext(BACKPORT + "backport-heartbeat-period", valueUnsignedInt=3600), ext(BACKPORT + "backport-max-count", valuePositiveInt=10)],
        "type": "rest-hook",
        "endpoint": endpoint,
        "payload": "application/fhir+json",
        "header": ["Authorization: Bearer subscriber-secret"],
    }
    if content:
        channel["_payload"] = {"extension": [ext(BACKPORT + "backport-payload-content", valueCode=content)]}
    return {
        "resourceType": "Subscription",
        "meta": {"profile": [BACKPORT + "backport-subscription"]},
        "status": "active",
        "reason": "e2e test",
        "criteria": TOPIC_URL,
        "_criteria": {"extension": [ext(BACKPORT + "backport-filter-criteria", valueString=f"Encounter?subject=Patient/{patient_id}")]},
        "channel": channel,
    }


def encounter(enc_id, patient_id, status):
    return {"resourceType": "Encounter", "id": enc_id, "status": status,
            "class": {"system": "http://terminology.hl7.org/CodeSystem/v3-ActCode", "code": "AMB"},
            "subject": {"reference": f"Patient/{patient_id}"}}


def main():
    call("DELETE", HOOK_LOCAL + "/notifications")
    patient_id, other_patient = uuid.uuid4().hex, uuid.uuid4().hex

    print("1. Topic is defined through the API as an R4 Basic resource")
    _, created_topic = call("POST", f"{FHIR}/Basic", topic(), expect=201)
    _, found = call("GET", f"{FHIR}/Basic?code=http://hl7.org/fhir/fhir-types|SubscriptionTopic", expect=200)
    assert any(e["resource"]["id"] == created_topic["id"] for e in found.get("entry", [])), "topic not found by search"

    print("2. Unsupported topics and subscriptions are rejected")
    bad = topic()
    bad["extension"][1]["extension"][3]["extension"][2]["valueString"] = "subject.name=x"
    call("POST", f"{FHIR}/Basic", bad, expect=400)
    classic = subscription(HOOK_FOR_SERVER + "/hook", patient_id)
    classic["criteria"] = "Encounter?status=finished"
    call("POST", f"{FHIR}/Subscription", classic, expect=400)
    call("POST", f"{FHIR}/Subscription", subscription("ftp://example.org", patient_id), expect=400)

    print("3. id-only subscription is created; status is server-owned and the content extension is added")
    _, sub = call("POST", f"{FHIR}/Subscription", subscription(HOOK_FOR_SERVER + "/hook", patient_id), expect=201)
    sub_id = sub["id"]
    assert sub["status"] == "requested", sub["status"]
    payload_ext = sub["channel"]["_payload"]["extension"]
    assert any(e["url"] == BACKPORT + "backport-payload-content" and e["valueCode"] == "id-only" for e in payload_ext), payload_ext
    assert "end" in sub, "end was not capped"

    print("4. Handshake: requested -> active")
    handshake = wait_for(lambda: [n for n in notifications() if "handshake" in json.dumps(n["body"])], "handshake")[0]
    assert handshake["authorization"] == "Bearer subscriber-secret"
    assert handshake["body"]["type"] == "history"
    wait_for(lambda: status_of(sub_id) == "active", "active status")

    print("5. A matching transition produces one id-only notification; non-matching changes do not")
    enc_id = uuid.uuid4().hex
    call("PUT", f"{FHIR}/Encounter/{enc_id}", encounter(enc_id, patient_id, "in-progress"), expect=201)
    call("PUT", f"{FHIR}/Encounter/{enc_id}", encounter(enc_id, patient_id, "finished"), expect=200)
    call("PUT", f"{FHIR}/Encounter/{enc_id}", encounter(enc_id, patient_id, "finished"), expect=200)
    other = uuid.uuid4().hex
    call("PUT", f"{FHIR}/Encounter/{other}", encounter(other, other_patient, "finished"), expect=201)

    def events():
        return [n for n in notifications() if '"event-notification"' in json.dumps(n["body"])]

    note = wait_for(events, "event notification")[0]["body"]
    time.sleep(5)
    assert len(events()) == 1, f"expected exactly one notification, got {len(events())}"
    status_params = note["entry"][0]["resource"]
    assert status_params["resourceType"] == "Parameters"
    assert BACKPORT + "backport-subscription-status-r4" in status_params["meta"]["profile"]
    event = param(status_params, "notification-event")
    assert next(p for p in event["part"] if p["name"] == "event-number")["valueString"] == "1"
    focus = next(p for p in event["part"] if p["name"] == "focus")["valueReference"]["reference"]
    assert focus.endswith(f"Encounter/{enc_id}"), focus
    assert note["entry"][1]["fullUrl"].endswith(f"Encounter/{enc_id}")
    assert "resource" not in note["entry"][1], "id-only must not include the resource"
    assert note["entry"][1]["request"]["method"] == "PUT"

    print("6. $status reports the event count")
    _, status_bundle = call("GET", f"{FHIR}/Subscription/{sub_id}/$status", expect=200)
    assert status_bundle["type"] == "searchset"
    params = status_bundle["entry"][0]["resource"]
    assert param(params, "events-since-subscription-start")["valueString"] == "1"
    assert param(params, "status")["valueCode"] == "active"

    print("7. A failing endpoint moves the subscription to error; events are retained")
    _, failing = call("POST", f"{FHIR}/Subscription", subscription(HOOK_FOR_SERVER + "/fail", patient_id), expect=201)
    wait_for(lambda: status_of(failing["id"]) == "error", "handshake failure -> error")
    _, failed_status = call("GET", f"{FHIR}/Subscription/{failing['id']}/$status", expect=200)
    assert "500" in json.dumps(failed_status), failed_status

    print("8. Changing the endpoint requires a new handshake")
    sub_now = call("GET", f"{FHIR}/Subscription/{sub_id}", expect=200)[1]
    sub_now["channel"]["endpoint"] = HOOK_FOR_SERVER + "/hook2"
    _, updated = call("PUT", f"{FHIR}/Subscription/{sub_id}", sub_now, expect=200)
    assert updated["status"] == "requested", updated["status"]
    wait_for(lambda: any(n["path"] == "/hook2" and "handshake" in json.dumps(n["body"]) for n in notifications()), "new handshake")

    print("9. Deleting the subscription stops notifications")
    call("DELETE", f"{FHIR}/Subscription/{sub_id}", expect=204)
    before = len(events())
    enc2 = uuid.uuid4().hex
    call("PUT", f"{FHIR}/Encounter/{enc2}", encounter(enc2, patient_id, "finished"), expect=201)
    time.sleep(8)
    assert len(events()) == before, "notification sent after delete"

    print("ALL CHECKS PASSED")


if __name__ == "__main__":
    main()
