"""End-to-end check of what an R4 topic can trigger on and filter by, against a running server.

Each scenario writes its own topic (an R4 Basic resource) and id-only rest-hook subscription, makes changes, and
checks exactly which changes were notified:

  1. supportedInteraction: a delete-only trigger ignores create and update
  2. queryCriteria with requireBoth=true and resultForCreate=test-fails: only a real in-progress -> finished transition
  3. queryCriteria with requireBoth=false and resultForDelete=test-fails: leaving or entering a state, and deletes
     judged on the deleted version
  4. fhirPathCriteria using %previous and %current
  5. Several triggers on different resource types, with a subscription filter per type
  6. canFilterBy / _criteria filters: parameters are ANDed, allowed modifiers are honoured
  7. Topics and subscriptions the server cannot evaluate in memory are rejected with 400

Usage: python e2e_r4_topic_scope.py [FHIR base URL] [webhook base URL as seen by the server] [webhook base URL as seen by this script]
"""
import json
import re
import time
import uuid

from e2e_r4_backport import BACKPORT, FHIR, HOOK_FOR_SERVER, HOOK_LOCAL, TOPIC_BACKBONE_PREFIX, TOPIC_PREFIX, call, ext, notifications, param, status_of, wait_for

QUIET_SECONDS = 8
REFERENCE = re.compile(r"([A-Za-z]+)/([0-9a-f]{32})")


def trigger(resource, interactions, query=None, fhirpath=None):
    parts = [ext("resource", valueUri=resource)] + [ext("supportedInteraction", valueCode=i) for i in interactions]
    if query:
        criteria = [ext(k, valueBoolean=v) if isinstance(v, bool) else ext(k, valueCode=v) if k.startswith("result") else ext(k, valueString=v)
                    for k, v in query.items()]
        parts.append({"url": "queryCriteria", "extension": criteria})
    if fhirpath:
        parts.append(ext("fhirPathCriteria", valueString=fhirpath))
    return {"url": TOPIC_BACKBONE_PREFIX + "resourceTrigger", "extension": parts}


def can_filter_by(resource, parameter, modifiers=()):
    parts = [ext("resource", valueUri=resource), ext("filterParameter", valueString=parameter)]
    parts += [ext("modifier", valueCode=m) for m in modifiers]
    return {"url": TOPIC_BACKBONE_PREFIX + "canFilterBy", "extension": parts}


def topic(url, triggers, filters=(), extra=()):
    return {
        "resourceType": "Basic",
        "modifierExtension": [ext(TOPIC_PREFIX + "status", valueCode="active")],
        "code": {"coding": [{"system": "http://hl7.org/fhir/fhir-types", "code": "SubscriptionTopic"}]},
        "extension": [ext(TOPIC_PREFIX + "url", valueUri=url), *triggers, *filters, *extra],
    }


def subscription(topic_url, path, filters=()):
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
    body = {"resourceType": "Encounter", "id": enc_id, "status": status,
            "class": {"system": "http://terminology.hl7.org/CodeSystem/v3-ActCode", "code": klass},
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
    """Returns (event number, focus reference, request method) for each event delivered to path, in event order."""
    found = []
    for n in notifications():
        if n["path"] != path or '"event-notification"' not in json.dumps(n["body"]):
            continue
        bundle = n["body"]
        numbers = [next(p for p in e["part"] if p["name"] == "event-number")["valueString"]
                   for e in bundle["entry"][0]["resource"]["parameter"] if e["name"] == "notification-event"]
        for number, entry in zip(numbers, bundle["entry"][1:]):
            ref = "/".join(REFERENCE.search(entry["fullUrl"]).groups())
            found.append((int(number), ref, entry.get("request", {}).get("method")))
    return sorted(found)


def scenario(name, triggers, changes, expected, filters=(), subscription_filters=()):
    """Creates a topic and subscription, runs changes(), and asserts the delivered events equal expected.

    changes() returns a dict of labels to references; expected is a list of (label, method or None) in event order.
    """
    print(f"- {name}")
    path = f"/scope-{new_id()[:8]}"
    topic_url = f"http://example.org/fhir/SubscriptionTopic/scope-{new_id()[:8]}"
    _, created_topic = call("POST", f"{FHIR}/Basic", topic(topic_url, triggers, filters), expect=201)
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
        _, status_bundle = call("GET", f"{FHIR}/Subscription/{sub['id']}/$status", expect=200)
        count = param(status_bundle["entry"][0]["resource"], "events-since-subscription-start")["valueString"]
        assert count == str(len(want)), f"{name}: $status reports {count} events, expected {len(want)}"
        print(f"    {len(want)} event(s) as expected: {', '.join(label for label, _ in expected) or 'none'}")
    finally:
        call("DELETE", f"{FHIR}/Subscription/{sub['id']}")
        call("DELETE", f"{FHIR}/Basic/{created_topic['id']}")


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
        put_encounter(created_finished, patient, "finished")
        put_encounter(transitioned, patient, "in-progress")
        ref = put_encounter(transitioned, patient, "finished")
        put_encounter(transitioned, patient, "finished")
        put_encounter(skipped, patient, "planned")
        put_encounter(skipped, patient, "finished")
        return {"in-progress -> finished": ref}

    scenario("queryCriteria requireBoth=true, resultForCreate=test-fails: only in-progress -> finished",
             [trigger("Encounter", ["create", "update"],
                      {"previous": "status=in-progress", "resultForCreate": "test-fails", "current": "status=finished", "requireBoth": True})],
             changes, [("in-progress -> finished", None)],
             [can_filter_by("Encounter", "subject")], [f"Encounter?subject=Patient/{patient}"])


def either_criterion():
    patient = new_id()
    e1, e2, e3 = new_id(), new_id(), new_id()

    def changes():
        put_encounter(e1, patient, "planned")
        put_encounter(e1, patient, "in-progress")
        left = put_encounter(e1, patient, "cancelled")
        entered = put_encounter(e1, patient, "finished")
        put_encounter(e2, patient, "in-progress")
        deleted_in_progress = delete(f"Encounter/{e2}")
        put_encounter(e3, patient, "planned")
        delete(f"Encounter/{e3}")
        return {"left in-progress": left, "entered finished": entered, "deleted while in-progress": deleted_in_progress}

    scenario("queryCriteria requireBoth=false, resultForDelete=test-fails: either test passing is enough",
             [trigger("Encounter", ["update", "delete"],
                      {"previous": "status=in-progress", "current": "status=finished", "resultForDelete": "test-fails", "requireBoth": False})],
             changes, [("left in-progress", "PUT"), ("entered finished", "PUT"), ("deleted while in-progress", "DELETE")],
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
                      fhirpath="%previous.status != 'cancelled' and %current.status = 'cancelled' and %current.class.code = 'EMER'")],
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
        kept = put_encounter(new_id(), patient, "finished")
        put_encounter(new_id(), other, "finished")
        return {"finished for patient": kept}

    scenario("Filters subject AND status:not are both applied",
             [trigger("Encounter", ["create", "update"])],
             changes, [("finished for patient", None)],
             [can_filter_by("Encounter", "subject"), can_filter_by("Encounter", "status", modifiers=["not"])],
             [f"Encounter?subject=Patient/{patient}&status:not=cancelled"])


def rejections():
    print("- Topics and subscriptions that cannot be evaluated in memory are rejected")
    url = f"http://example.org/fhir/SubscriptionTopic/scope-{new_id()[:8]}"
    bad_topics = {
        "chained parameter": [trigger("Encounter", ["update"], {"current": "subject.name=x"})],
        "reverse chain (_has)": [trigger("Encounter", ["update"], {"current": "_has:Observation:encounter:status=final"})],
        "terminology modifier (:in)": [trigger("Encounter", ["update"], {"current": "type:in=http://example.org/vs"})],
        "criteria for another resource type": [trigger("Encounter", ["update"], {"current": "Patient?gender=male"})],
        "invalid FHIRPath": [trigger("Encounter", ["update"], fhirpath="%current.status = = 'x'")],
        "unknown resource type": [trigger("NotAType", ["update"])],
    }
    for reason, triggers in bad_topics.items():
        call("POST", f"{FHIR}/Basic", topic(url, triggers), expect=400)
        print(f"    topic rejected: {reason}")
    call("POST", f"{FHIR}/Basic", topic(url, [trigger("Encounter", ["update"])], [can_filter_by("Encounter", "not-a-parameter")]), expect=400)
    print("    topic rejected: canFilterBy an unknown parameter")
    call("POST", f"{FHIR}/Basic", topic(url, [trigger("Encounter", ["update"])], extra=[{"url": TOPIC_BACKBONE_PREFIX + "eventTrigger", "extension": [ext("event", valueCodeableConcept={"text": "x"})]}]), expect=400)
    print("    topic rejected: eventTrigger")

    _, created = call("POST", f"{FHIR}/Basic", topic(url, [trigger("Encounter", ["update"])], [can_filter_by("Encounter", "status", modifiers=["not"])]), expect=201)
    try:
        for reason, criteria in {
            "parameter not in canFilterBy": "Encounter?class=AMB",
            "modifier not allowed by canFilterBy": "Encounter?status:missing=true",
            "filter on a resource type the topic does not allow": "Observation?status=final",
        }.items():
            call("POST", f"{FHIR}/Subscription", subscription(url, "/never", [criteria]), expect=400)
            print(f"    subscription rejected: {reason}")
    finally:
        call("DELETE", f"{FHIR}/Basic/{created['id']}")


def main():
    call("DELETE", HOOK_LOCAL + "/notifications")
    for check in (interactions, require_both, either_criterion, fhirpath, several_triggers, combined_filters, rejections):
        check()
    print("ALL TOPIC SCOPE CHECKS PASSED")


if __name__ == "__main__":
    main()
