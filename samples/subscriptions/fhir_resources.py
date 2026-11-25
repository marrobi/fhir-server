"""FHIR resource builders shared by the subscription samples: R4 topics defined as Basic resources and Subscriptions
Backport IG subscriptions."""

TOPIC_PREFIX = "http://hl7.org/fhir/5.0/StructureDefinition/extension-SubscriptionTopic."
TOPIC_BACKBONE_PREFIX = "http://hl7.org/fhir/4.3/StructureDefinition/extension-SubscriptionTopic."
BACKPORT = "http://hl7.org/fhir/uv/subscriptions-backport/StructureDefinition/"
ACT_CODE = "http://terminology.hl7.org/CodeSystem/v3-ActCode"


def ext(url, **value):
    return {"url": url, **value}


def status_transition_topic(url, status, topic_id=None, current=None):
    """An R4 topic that notifies when an Encounter is created in, or updated into, `status`, and can be filtered by subject.
    `current` replaces the current-state criteria, for example to build a topic the server must reject."""
    topic = {
        "resourceType": "Basic",
        "modifierExtension": [ext(TOPIC_PREFIX + "status", valueCode="active")],
        "code": {"coding": [{"system": "http://hl7.org/fhir/fhir-types", "code": "SubscriptionTopic"}]},
        "extension": [
            ext(TOPIC_PREFIX + "url", valueUri=url),
            {"url": TOPIC_BACKBONE_PREFIX + "resourceTrigger", "extension": [
                ext("resource", valueUri="Encounter"),
                ext("supportedInteraction", valueCode="create"),
                ext("supportedInteraction", valueCode="update"),
                {"url": "queryCriteria", "extension": [
                    ext("previous", valueString=f"status:not={status}"),
                    ext("resultForCreate", valueCode="test-passes"),
                    ext("current", valueString=current or f"status={status}"),
                    ext("requireBoth", valueBoolean=True),
                ]},
            ]},
            {"url": TOPIC_BACKBONE_PREFIX + "canFilterBy", "extension": [ext("resource", valueUri="Encounter"), ext("filterParameter", valueString="subject")]},
        ],
    }
    if topic_id:
        topic["id"] = topic_id
    return topic


def backport_subscription(topic_url, endpoint, patient, content=None, max_count=10, headers=(), status="requested", reason="sample", subscription_id=None):
    """A Backport rest-hook subscription to `topic_url` for the Encounters of `patient`."""
    channel = {
        "extension": [ext(BACKPORT + "backport-heartbeat-period", valueUnsignedInt=3600), ext(BACKPORT + "backport-max-count", valuePositiveInt=max_count)],
        "type": "rest-hook",
        "endpoint": endpoint,
        "payload": "application/fhir+json",
    }
    if headers:
        channel["header"] = list(headers)
    if content:
        channel["_payload"] = {"extension": [ext(BACKPORT + "backport-payload-content", valueCode=content)]}
    subscription = {
        "resourceType": "Subscription",
        "meta": {"profile": [BACKPORT + "backport-subscription"]},
        "status": status,
        "reason": reason,
        "criteria": topic_url,
        "_criteria": {"extension": [ext(BACKPORT + "backport-filter-criteria", valueString=f"Encounter?subject=Patient/{patient}")]},
        "channel": channel,
    }
    if subscription_id:
        subscription["id"] = subscription_id
    return subscription


def encounter(enc_id, patient, status):
    return {"resourceType": "Encounter", "id": enc_id, "status": status,
            "class": {"system": ACT_CODE, "code": "AMB"},
            "subject": {"reference": f"Patient/{patient}"}}
