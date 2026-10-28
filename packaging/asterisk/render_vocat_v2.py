"""Pure projection of a v2 deployment into the VoCat SIP Config request.

The returned object is the body for the authenticated ``PUT /api/settings/sip``
configuration lifecycle, NOT the private stored setting and NOT an instruction
to start a listener, IMS registration, call, SMS, or other service. ``enabled``
is always false. Explicit feature and directional grants remain in the stopped
configuration for review; nothing is inferred from a SIM or a PBX account.

Only an enabled GatewayLocalRegistrar contributes local accounts. Otherwise
both its accounts and the line-local receiver lists are projected empty: Go's
presence of an account is itself registrar access when the service is later
enabled. A completely blank disabled peer is omitted; a configured disabled
peer keeps its independent credentials and disabled switch. PBX Extensions are
never copied into the local registrar.

This function neither reads nor writes files and does not create credentials,
HA1 values, recipient epochs, config revisions, or the private stored wrapper.
The platform's durable admission generates those identities. Its return value
contains resolved passwords and must be handled as a secret, never displayed
or logged. Secrets are checked through the shared strict resolver, including
declared references belonging to the separately rendered PBX side.

VoCat has no Config DialRules/SmsDialRules fields. The PBX renderer separately
consumes those independent rules and must retain the selected line's Prefix
before the normalized E.164 user; it must not strip the selected line and rely
on a default SIM. Opaque reply users are not rewritten here.
"""

from deployment_v2 import (
    DeploymentV2Error,
    normalize_deployment_v2,
    resolve_deployment_secrets,
)


def _fail(path, reason):
    raise DeploymentV2Error(path + ": " + reason) from None


def _endpoint(address, port):
    # The model already admits only canonical, unscoped IP literals.
    host = "[" + address + "]" if ":" in address else address
    return host + ":" + str(port)


def _configured_peer(peer):
    return (
        peer["Enabled"]
        or any(peer[key] for key in (
            "AllowCallsFromPeer", "AllowCallsToPeer",
            "AllowSmsFromPeer", "AllowSmsToPeer",
        ))
        or bool(peer["OutgoingLines"] or peer["SmsSendLines"])
        or any(role[field]
               for role in (peer["InboundAuth"], peer["OutboundAuth"])
               for field in ("Username", "SecretRef"))
    )


def render_vocat_settings(model: dict, secrets_by_ref: dict) -> dict:
    """Return a newly allocated, stopped Go Config without I/O or side effects.

    Schema and secret failures use DeploymentV2Error with fixed, safe paths;
    no input value, password, unknown property, or complete config is echoed.
    Config import still requires the real authenticated settings API, and all
    existing LICENSE, geographic, device, SIM and carrier gates remain intact.
    """
    deployment = normalize_deployment_v2(model)
    if deployment["GatewayPlatform"] != "vocat-go":
        _fail("GatewayPlatform", "Go settings require the vocat-go platform")
    if not deployment["GatewayTrustedNetworks"]:
        # Never manufacture an all-private allowlist for an empty request.
        _fail("GatewayTrustedNetworks", "Go settings require explicit trusted networks")
    if (deployment["CarrierEnabled"] or deployment["SmsEnabled"]) and not deployment["Lines"]:
        _fail("Lines", "enabled carrier or SMS features require a configured line")
    secrets = resolve_deployment_secrets(deployment, secrets_by_ref)
    registrar = deployment["GatewayLocalRegistrar"]
    local_enabled = registrar["Enabled"]
    accounts = []
    if local_enabled:
        for account in registrar["Accounts"]:
            accounts.append({
                "username": account["Username"],
                "display_name": account["DisplayName"],
                "password": secrets[account["SecretRef"]],
                "allow_calls": account["AllowCalls"],
                "allow_messages": account["AllowMessages"],
                "outgoing_lines": list(account["OutgoingLines"]),
                "sms_send_lines": list(account["SmsSendLines"]),
            })
    lines = []
    for line in deployment["Lines"]:
        lines.append({
            "device_id": line["Id"],
            "label": line["Label"],
            "prefix": line["Prefix"],
            "default": line["Default"],
            "allow_outgoing": line["AllowOutgoingCalls"],
            "allow_incoming": line["AllowIncomingCalls"],
            "incoming_extensions": list(line["LocalIncomingExtensions"]) if local_enabled else [],
            "country_code": line["CountryCode"],
            "national_number_length": line["NationalNumberLength"],
            "allow_sms_send": line["AllowSmsSend"],
            "allow_sms_receive": line["AllowSmsReceive"],
            "sms_recipients": list(line["LocalSmsRecipients"]) if local_enabled else [],
        })
    result = {
        "enabled": False,
        "listen": _endpoint(deployment["GatewayBindAddress"], deployment["GatewayPort"]),
        "realm": deployment["GatewayRealm"],
        "identity_domain": deployment["MessageDomain"],
        "messages_enabled": deployment["MessagesEnabled"],
        "calls_enabled": deployment["CallsEnabled"],
        "algorithm": deployment["GatewayAlgorithm"],
        "allowed_cidrs": list(deployment["GatewayTrustedNetworks"]),
        "accounts": accounts,
        "carrier_enabled": deployment["CarrierEnabled"],
        "carrier_lines": lines,
        "sms_enabled": deployment["SmsEnabled"],
    }
    peer = deployment["Peer"]
    if _configured_peer(peer):
        incoming = []
        for line in deployment["Lines"]:
            if line["CallTarget"] or line["SmsTarget"]:
                incoming.append({
                    "device_id": line["Id"],
                    "call_target": line["CallTarget"],
                    "sms_target": line["SmsTarget"],
                })
        result["peer"] = {
            "enabled": peer["Enabled"],
            "endpoint": _endpoint(deployment["ServerIp"], deployment["SipPort"]),
            "inbound_auth": {
                "username": peer["InboundAuth"]["Username"],
                "password": secrets[peer["InboundAuth"]["SecretRef"]],
            },
            "outbound_auth": {
                "username": peer["OutboundAuth"]["Username"],
                "password": secrets[peer["OutboundAuth"]["SecretRef"]],
                "realm": deployment["PbxRealm"],
                "algorithms": [deployment["PbxAlgorithm"]],
            },
            "allow_calls_from_peer": peer["AllowCallsFromPeer"],
            "allow_sms_from_peer": peer["AllowSmsFromPeer"],
            "allow_calls_to_peer": peer["AllowCallsToPeer"],
            "allow_sms_to_peer": peer["AllowSmsToPeer"],
            "outgoing_lines": list(peer["OutgoingLines"]),
            "sms_send_lines": list(peer["SmsSendLines"]),
            "incoming_routes": incoming,
        }
    return result
