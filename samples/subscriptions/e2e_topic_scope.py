"""End-to-end check of what a topic can trigger on and filter by, against a running server (R4, R4B or R5).

The FHIR version is read from the server's CapabilityStatement, and topics, subscriptions and resources are written in
that version's shape: on R4 a Basic topic (HAPI FHIR's layout) with a Backport subscription, on R4B a SubscriptionTopic
with a Backport subscription, and on R5 native resources. Each scenario writes its own topic and id-only rest-hook
subscription, makes changes, and checks exactly which changes were notified:

  1. supportedInteraction: a delete-only trigger ignores create and update
  2. queryCriteria with requireBoth=true and resultForCreate=test-fails: only a real in-progress -> finished transition
  3. queryCriteria with requireBoth=false and resultForDelete=test-fails: leaving or entering a state, and deletes
     judged on the deleted version
  4. fhirPathCriteria using %previous and %current
  5. Several triggers on different resource types, with a subscription filter per type
  6. canFilterBy / subscription filters: parameters are ANDed, allowed modifiers are honoured
  7. Topics and subscriptions the server cannot evaluate in memory are rejected with 400
  8. R4 and R5: a filter comparator must be one the topic's canFilterBy allows (R4 writes it as a value prefix;
     R4B topics cannot declare comparators)

Usage: python e2e_topic_scope.py [FHIR base URL] [webhook base URL as seen by the server] [webhook base URL as seen by this script]
"""
import json
import re
import time
import uuid

from e2e_r4_backport import BACKPORT, FHIR, HOOK_FOR_SERVER, HOOK_LOCAL, TOPIC_BACKBONE_PREFIX, TOPIC_PREFIX, call, ext, notifications, status_of, wait_for

QUIET_SECONDS = 8
REFERENCE = re.compile(r"([A-Z][A-Za-z]+)/([0-9a-f]{32})")


def detect_version():
    fhir_version = call("GET", f"{FHIR}/metadata", expect=200)[1]["fhirVersion"]
    return {"4.0": "R4", "4.3": "R4B", "5.0": "R5"}[fhir_version[:3]]


VERSION = detect_version()
TOPIC_TYPE = "Basic" if VERSION == "R4" else "SubscriptionTopic"
# R5 renamed the Encounter status "finished" to "completed" and made Encounter.class a list.
FINISHED = "completed" if VERSION == "R5" else "finished"
CLASS_CODE = "%current.class.coding.code" if VERSION == "R5" else "%current.class.code"
# R4B's canFilterBy.modifier binding has no "not" code; an empty list allows any modifier.
NOT_MODIFIER = [] if VERSION == "R4B" else ["not"]


def trigger(resource, interactions, query=None, fhirpath=None):
    return {"resource": resource, "interactions": interactions, "query": query, "fhirpath": fhirpath}


def can_filter_by(resource, parameter, modifiers=(), comparators=()):
    return {"resource": resource, "parameter": parameter, "modifiers": list(modifiers), "comparators": list(comparators)}


def topic(url, triggers, filters=(), event_trigger=False):
    if VERSION == "R4":
        return r4_topic(url, triggers, filters, event_trigger)
    body = {"resourceType": "SubscriptionTopic", "url": url, "status": "active", "resourceTrigger": [], "canFilterBy": []}
    for t in triggers:
        rt = {"resource": t["resource"], "supportedInteraction": t["interactions"]}
        if t["query"]:
            rt["queryCriteria"] = t["query"]
        if t["fhirpath"]:
            rt["fhirPathCriteria"] = t["fhirpath"]
        body["resourceTrigger"].append(rt)
    for f in filters:
        cf = {"resource": f["resource"], "filterParameter": f["parameter"]}
        if f["modifiers"]:
            cf["modifier"] = f["modifiers"]
        if f["comparators"]:
            cf["comparator"] = f["comparators"]
        body["canFilterBy"].append(cf)
    if event_trigger:
        body["eventTrigger"] = [{"event": {"text": "x"}, "resource": "Encounter"}]
    return body


def r4_topic(url, triggers, filters, event_trigger):
    extensions = [ext(TOPIC_PREFIX + "url", valueUri=url)]
    for t in triggers:
        parts = [ext("resource", valueUri=t["resource"])] + [ext("supportedInteraction", valueCode=i) for i in t["interactions"]]
        if t["query"]:
            criteria = [ext(k, valueBoolean=v) if isinstance(v, bool) else ext(k, valueCode=v) if k.startswith("result") else ext(k, valueString=v)
                        for k, v in t["query"].items()]
            parts.append({"url": "queryCriteria", "extension": criteria})
        if t["fhirpath"]:
            parts.append(ext("fhirPathCriteria", valueString=t["fhirpath"]))
        extensions.append({"url": TOPIC_BACKBONE_PREFIX + "resourceTrigger", "extension": parts})
    for f in filters:
        parts = [ext("resource", valueUri=f["resource"]), ext("filterParameter", valueString=f["parameter"])]
        parts += [ext("modifier", valueCode=m) for m in f["modifiers"]] + [ext("comparator", valueCode=c) for c in f["comparators"]]
        extensions.append({"url": TOPIC_BACKBONE_PREFIX + "canFilterBy", "extension": parts})
    if event_trigger:
        extensions.append({"url": TOPIC_BACKBONE_PREFIX + "eventTrigger", "extension": [ext("event", valueCodeableConcept={"text": "x"})]})
    return {
        "resourceType": "Basic",
        "modifierExtension": [ext(TOPIC_PREFIX + "status", valueCode="active")],
        "code": {"coding": [{"system": "http://hl7.org/fhir/fhir-types", "code": "SubscriptionTopic"}]},
        "extension": extensions,
    }


def subscription(topic_url, path, filters=()):
    """Filters are query strings such as 'Encounter?subject=Patient/1&status:not=cancelled', or R5 filterBy objects."""
    if VERSION == "R5":
        filter_by = []
        for f in filters:
            if isinstance(f, dict):
                filter_by.append(f)
                continue
            resource_type, query = f.split("?", 1)
            for pair in query.split("&"):
                key, value = pair.split("=", 1)
                name, _, modifier = key.partition(":")
                filter_by.append({"resourceType": resource_type, "filterParameter": name, "value": value, **({"modifier": modifier} if modifier else {})})
        return {
            "resourceType": "Subscription",
            "status": "requested",
            "topic": topic_url,
            "filterBy": filter_by,
            "channelType": {"system": "http://terminology.hl7.org/CodeSystem/subscription-channel-type", "code": "rest-hook"},
            "endpoint": HOOK_FOR_SERVER + path,
            "contentType": "application/fhir+json",
            "content": "id-only",
        }
    return {
        "resourceType": "Subscription",
        "meta": {"profile": [BACKPORT + "backport-subscription"]},
        "status": "requested",
        "reason": "topic scope sample",
        "criteria": topic_url,
        "_criteria": {"extension": [ext(BACKPORT + "backport-filter-criteria", valueString=f) for f in filters]},
        "channel": {"type": "rest-hook", "endpoint": HOOK_FOR_SERVER + path, "payload": "application/fhir+json"},
    }


def new_id():
    return uuid.uuid4().hex


def put_encounter(enc_id, patient, status, klass="AMB"):
    coding = {"system": "http://terminology.hl7.org/CodeSystem/v3-ActCode", "code": klass}
    body = {"resourceType": "Encounter", "id": enc_id, "status": status,
            "class": [{"coding": [coding]}] if VERSION == "R5" else coding,
            "subject": {"reference": f"Patient/{patient}"}}
    call("PUT", f"{FHIR}/Encounter/{enc_id}", body)
    return f"Encounter/{enc_id}"


def put_observation(obs_id, patient, status):
    body = {"resourceType": "Observation", "id": obs_id, "status": status,
            "code": {"coding": [{"system": "http://loinc.org", "code": "8867-4"}]},
            "subject": {"reference": f"Patient/{patient}"}}
    call("PUT", f"{FHIR}/Observation/{obs_id}", body)
    return f"Observation/{obs_id}"


def delete(reference):
    call("DELETE", f"{FHIR}/{reference}", expect=204)
    return reference


def events_for(path):
    """Returns (event number, focus reference, request method) for each event delivered to path, in event order.

    Read from the status part of each notification: R5 id-only notifications list a resource changed several times once.
    """
    found = []
    for n in notifications():
        if n["path"] != path or '"event-notification"' not in json.dumps(n["body"]):
            continue
        bundle = n["body"]
        status = bundle["entry"][0]["resource"]
        if VERSION == "R4":
            events = [({p["name"]: p for p in e["part"]}) for e in status["parameter"] if e["name"] == "notification-event"]
            events = [(e["event-number"]["valueString"], e["focus"]["valueReference"]["reference"]) for e in events]
        else:
            events = [(e["eventNumber"], e["focus"]["reference"]) for e in status["notificationEvent"]]
        methods = {"/".join(REFERENCE.search(e["fullUrl"]).groups()): e.get("request", {}).get("method") for e in reversed(bundle["entry"][1:])}
        for number, focus in events:
            ref = "/".join(REFERENCE.search(focus).groups())
            found.append((int(number), ref, methods.get(ref)))
    return sorted(found)


def events_since_start(sub_id):
    _, status_bundle = call("GET", f"{FHIR}/Subscription/{sub_id}/$status", expect=200)
    status = status_bundle["entry"][0]["resource"]
    if VERSION == "R4":
        return next(p for p in status["parameter"] if p["name"] == "events-since-subscription-start")["valueString"]
    return str(status["eventsSinceSubscriptionStart"])


def scenario(name, triggers, changes, expected, filters=(), subscription_filters=()):
    """Creates a topic and subscription, runs changes(), and asserts the delivered events equal expected.

    changes() returns a dict of labels to references; expected is a list of (label, method or None) in event order.
    """
    print(f"- {name}")
    path = f"/scope-{new_id()[:8]}"
    topic_url = f"http://example.org/fhir/SubscriptionTopic/scope-{new_id()[:8]}"
    _, created_topic = call("POST", f"{FHIR}/{TOPIC_TYPE}", topic(topic_url, triggers, filters), expect=201)
    _, sub = call("POST", f"{FHIR}/Subscription", subscription(topic_url, path, subscription_filters), expect=201)
    try:
        wait_for(lambda: status_of(sub["id"]) == "active", f"{name}: active")
        refs = changes()
        if expected:
            wait_for(lambda: len(events_for(path)) >= len(expected), f"{name}: {len(expected)} event(s)")
        time.sleep(QUIET_SECONDS)
        got = events_for(path)
        want = [refs[label] for label, _ in expected]
        assert [r for _, r, _ in got] == want, f"{name}: expected {want}, got {got}"
        assert [n for n, _, _ in got] == list(range(1, len(want) + 1)), f"{name}: event numbers {got}"
        for (_, _, method), (label, expected_method) in zip(got, expected):
            assert expected_method is None or method == expected_method, f"{name}: {label} was {method}, expected {expected_method}"
        count = events_since_start(sub["id"])
        assert count == str(len(want)), f"{name}: $status reports {count} events, expected {len(want)}"
        print(f"    {len(want)} event(s) as expected: {', '.join(label for label, _ in expected) or 'none'}")
    finally:
        call("DELETE", f"{FHIR}/Subscription/{sub['id']}")
        call("DELETE", f"{FHIR}/{TOPIC_TYPE}/{created_topic['id']}")


def interactions():
    patient = new_id()
    enc = new_id()

    def changes():
        put_encounter(enc, patient, "planned")
        put_encounter(enc, patient, "cancelled")
        return {"delete": delete(f"Encounter/{enc}")}

    scenario("supportedInteraction=delete: create and update are ignored, the delete is notified",
             [trigger("Encounter", ["delete"])], changes, [("delete", "DELETE")],
             [can_filter_by("Encounter", "subject")], [f"Encounter?subject=Patient/{patient}"])


def require_both():
    patient = new_id()
    created_finished, transitioned, skipped = new_id(), new_id(), new_id()

    def changes():
        put_encounter(created_finished, patient, FINISHED)
        put_encounter(transitioned, patient, "in-progress")
        ref = put_encounter(transitioned, patient, FINISHED)
        put_encounter(transitioned, patient, FINISHED)
        put_encounter(skipped, patient, "planned")
        put_encounter(skipped, patient, FINISHED)
        return {f"in-progress -> {FINISHED}": ref}

    scenario(f"queryCriteria requireBoth=true, resultForCreate=test-fails: only in-progress -> {FINISHED}",
             [trigger("Encounter", ["create", "update"],
                      {"previous": "status=in-progress", "resultForCreate": "test-fails", "current": f"status={FINISHED}", "requireBoth": True})],
             changes, [(f"in-progress -> {FINISHED}", None)],
             [can_filter_by("Encounter", "subject")], [f"Encounter?subject=Patient/{patient}"])


def either_criterion():
    patient = new_id()
    e1, e2, e3 = new_id(), new_id(), new_id()

    def changes():
        put_encounter(e1, patient, "planned")
        put_encounter(e1, patient, "in-progress")
        left = put_encounter(e1, patient, "cancelled")
        entered = put_encounter(e1, patient, FINISHED)
        put_encounter(e2, patient, "in-progress")
        deleted_in_progress = delete(f"Encounter/{e2}")
        put_encounter(e3, patient, "planned")
        delete(f"Encounter/{e3}")
        return {"left in-progress": left, f"entered {FINISHED}": entered, "deleted while in-progress": deleted_in_progress}

    scenario("queryCriteria requireBoth=false, resultForDelete=test-fails: either test passing is enough",
             [trigger("Encounter", ["update", "delete"],
                      {"previous": "status=in-progress", "current": f"status={FINISHED}", "resultForDelete": "test-fails", "requireBoth": False})],
             changes, [("left in-progress", "PUT"), (f"entered {FINISHED}", "PUT"), ("deleted while in-progress", "DELETE")],
             [can_filter_by("Encounter", "subject")], [f"Encounter?subject=Patient/{patient}"])


def fhirpath():
    patient = new_id()
    ambulatory, emergency = new_id(), new_id()

    def changes():
        put_encounter(ambulatory, patient, "in-progress", "AMB")
        put_encounter(ambulatory, patient, "cancelled", "AMB")
        put_encounter(emergency, patient, "in-progress", "EMER")
        ref = put_encounter(emergency, patient, "cancelled", "EMER")
        put_encounter(emergency, patient, "cancelled", "EMER")
        return {"emergency cancelled": ref}

    scenario("fhirPathCriteria with %previous and %current: only an emergency encounter becoming cancelled",
             [trigger("Encounter", ["create", "update"],
                      fhirpath=f"%previous.status != 'cancelled' and %current.status = 'cancelled' and {CLASS_CODE} = 'EMER'")],
             changes, [("emergency cancelled", None)],
             [can_filter_by("Encounter", "subject")], [f"Encounter?subject=Patient/{patient}"])


def several_triggers():
    patient, other = new_id(), new_id()

    def changes():
        enc = put_encounter(new_id(), patient, "planned")
        put_encounter(new_id(), other, "planned")
        obs = put_observation(new_id(), patient, "final")
        put_observation(new_id(), patient, "preliminary")
        put_observation(new_id(), other, "final")
        return {"Encounter for patient": enc, "final Observation for patient": obs}

    scenario("Two triggers (Encounter create, final Observation create) with a filter per resource type",
             [trigger("Encounter", ["create"]), trigger("Observation", ["create"], {"current": "status=final"})],
             changes, [("Encounter for patient", None), ("final Observation for patient", None)],
             [can_filter_by("Encounter", "subject"), can_filter_by("Observation", "subject")],
             [f"Encounter?subject=Patient/{patient}", f"Observation?subject=Patient/{patient}"])


def combined_filters():
    patient, other = new_id(), new_id()

    def changes():
        put_encounter(new_id(), patient, "cancelled")
        kept = put_encounter(new_id(), patient, FINISHED)
        put_encounter(new_id(), other, FINISHED)
        return {f"{FINISHED} for patient": kept}

    scenario("Filters subject AND status:not are both applied",
             [trigger("Encounter", ["create", "update"])],
             changes, [(f"{FINISHED} for patient", None)],
             [can_filter_by("Encounter", "subject"), can_filter_by("Encounter", "status", modifiers=NOT_MODIFIER)],
             [f"Encounter?subject=Patient/{patient}&status:not=cancelled"])


def rejections():
    print("- Topics and subscriptions that cannot be evaluated in memory are rejected")
    url = f"http://example.org/fhir/SubscriptionTopic/scope-{new_id()[:8]}"
    bad_topics = {
        "chained parameter": dict(triggers=[trigger("Encounter", ["update"], {"current": "subject.name=x"})]),
        "reverse chain (_has)": dict(triggers=[trigger("Encounter", ["update"], {"current": "_has:Observation:encounter:status=final"})]),
        "terminology modifier (:in)": dict(triggers=[trigger("Encounter", ["update"], {"current": "type:in=http://example.org/vs"})]),
        "criteria for another resource type": dict(triggers=[trigger("Encounter", ["update"], {"current": "Patient?gender=male"})]),
        "invalid FHIRPath": dict(triggers=[trigger("Encounter", ["update"], fhirpath="%current.status = = 'x'")]),
        "unknown resource type": dict(triggers=[trigger("NotAType", ["update"])]),
        "canFilterBy an unknown parameter": dict(triggers=[trigger("Encounter", ["update"])], filters=[can_filter_by("Encounter", "not-a-parameter")]),
        "eventTrigger": dict(triggers=[trigger("Encounter", ["update"])], event_trigger=True),
    }
    for reason, args in bad_topics.items():
        call("POST", f"{FHIR}/{TOPIC_TYPE}", topic(url, **args), expect=400)
        print(f"    topic rejected: {reason}")

    # R4B cannot declare "not" (see NOT_MODIFIER), so it declares "in" to make other modifiers disallowed.
    _, created = call("POST", f"{FHIR}/{TOPIC_TYPE}", topic(url, [trigger("Encounter", ["update"])], [can_filter_by("Encounter", "status", modifiers=NOT_MODIFIER or ["in"])]), expect=201)
    try:
        for reason, criteria in {
            "parameter not in canFilterBy": "Encounter?class=AMB",
            "modifier not allowed by canFilterBy": "Encounter?status:missing=true",
            "filter on a resource type the topic does not allow": "Observation?status=final",
        }.items():
            call("POST", f"{FHIR}/Subscription", subscription(url, "/never", [criteria]), expect=400)
            print(f"    subscription rejected: {reason}")
    finally:
        call("DELETE", f"{FHIR}/{TOPIC_TYPE}/{created['id']}")


def comparators():
    print("- Filter comparators must be allowed by canFilterBy")
    url = f"http://example.org/fhir/SubscriptionTopic/scope-{new_id()[:8]}"
    _, created = call("POST", f"{FHIR}/{TOPIC_TYPE}", topic(url, [trigger("Encounter", ["update"])], [can_filter_by("Encounter", "date", comparators=["gt", "lt"])]), expect=201)
    try:
        def date_filter(comparator):
            if VERSION == "R5":
                return subscription(url, "/never", [{"resourceType": "Encounter", "filterParameter": "date", "comparator": comparator, "value": "2026-01-01"}])
            return subscription(url, "/never", [f"Encounter?date={comparator}2026-01-01"])

        _, sub = call("POST", f"{FHIR}/Subscription", date_filter("gt"), expect=201)
        call("DELETE", f"{FHIR}/Subscription/{sub['id']}")
        print("    subscription accepted: comparator gt is allowed")
        call("POST", f"{FHIR}/Subscription", date_filter("ge"), expect=400)
        print("    subscription rejected: comparator ge is not allowed")
    finally:
        call("DELETE", f"{FHIR}/{TOPIC_TYPE}/{created['id']}")


def main():
    print(f"FHIR {VERSION} server at {FHIR}")
    call("DELETE", HOOK_LOCAL + "/notifications")
    checks = [interactions, require_both, either_criterion, fhirpath, several_triggers, combined_filters, rejections]
    if VERSION != "R4B":
        checks.append(comparators)
    for check in checks:
        check()
    print("ALL TOPIC SCOPE CHECKS PASSED")


if __name__ == "__main__":
    main()
