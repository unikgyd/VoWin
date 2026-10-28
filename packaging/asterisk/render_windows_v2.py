"""Pure projection of the public v2 model into a private Windows import.

The returned object is an INPUT-ONLY, secret-bearing deployment envelope, not
public diagnostic JSON. Serialize it with deployment_json_text, store it
privately and import it through VoWin's authenticated configuration lifecycle.
This module does not read/write files, inspect adapters, start a listener, bind
modems, generate credentials, contact a PBX or invoke any process.

PBX account policies and its independent DialRules/SmsDialRules belong to the
PBX renderer. Windows receives explicit line identities from that PBX and also
gets each line's native prefix/country normalization for direct local clients.
All gateway permissions are explicit, including empty deny-all principal ACLs
and independent local/peer receive recipients. Declared disabled roles remain
disabled; their unused secrets are not copied into the Windows import.
"""

from deployment_v2 import (
    DeploymentV2Error,
    deployment_json_text,
    normalize_deployment_v2,
    resolve_deployment_secrets,
)


def _fail(path, reason):
    # Paths and reasons below are fixed schema text, never input values or refs.
    raise DeploymentV2Error(path + ": " + reason) from None


def _local_policy(account, calls_enabled, messages_enabled):
    if account is None:
        return {
            "AllowCalls": False,
            "AllowSms": False,
            "AllowInternalCalls": False,
            "AllowMessages": False,
            "OutgoingLines": [],
            "SmsSendLines": [],
        }
    return {
        # The public local AllowCalls gate covers both internal and paid voice.
        "AllowCalls": account["AllowCalls"],
        # In the public model a nonempty, explicit SMS line list is the paid
        # send grant. Internal AllowMessages never authorizes or denies SMS.
        "AllowSms": bool(account["SmsSendLines"]),
        "AllowInternalCalls": calls_enabled and account["AllowCalls"],
        "AllowMessages": messages_enabled and account["AllowMessages"],
        "OutgoingLines": list(account["OutgoingLines"]),
        "SmsSendLines": list(account["SmsSendLines"]),
    }


def render_windows_profile(model, secrets_by_ref) -> dict:
    """Return a newly allocated, disabled Version:2 Windows import envelope.

    Both the public model and complete secret-ref map are revalidated. Secrets
    are resolved only by their explicit role refs, never by username, the
    primary account, another role or a legacy shared-trunk password. Equal
    actual values chosen for independent refs remain valid.

    This projection targets the native TrustedNetworks, LocalRegistrarAlgorithm,
    IdentityDomain and line-normalization capabilities, not older Windows
    releases that cannot represent them. Nonempty configured local display
    names are rejected until the Windows account model can preserve them.
    """
    normalized = normalize_deployment_v2(model)
    if normalized["GatewayPlatform"] != "vowin-windows":
        _fail("GatewayPlatform", "Windows profile requires the Windows target")
    secrets = resolve_deployment_secrets(normalized, secrets_by_ref)
    peer = normalized["Peer"]
    registrar = normalized["GatewayLocalRegistrar"]
    accounts = registrar["Accounts"]
    for account in accounts:
        if account["DisplayName"]:
            _fail("GatewayLocalRegistrar.Accounts.DisplayName", "Windows account display names are not representable")

    local_enabled = registrar["Enabled"]
    peer_enabled = peer["Enabled"]
    calls_enabled = normalized["CallsEnabled"]
    sms_enabled = normalized["SmsEnabled"]
    messages_enabled = normalized["MessagesEnabled"]
    primary = next((account for account in accounts if account["Username"] == registrar["PrimaryUsername"]), None)
    active_primary = primary if local_enabled else None
    active_extras = [account for account in accounts if account is not primary] if local_enabled else []
    allowed_local_callers = {account["Username"] for account in accounts if account["AllowCalls"]}

    settings = {
        "CredentialVersion": 2,
        "UseTrunk": peer_enabled,
        "LocalRegistrarEnabled": local_enabled,
        "BindAddress": normalized["GatewayBindAddress"],
        "Port": normalized["GatewayPort"],
        "Realm": normalized["GatewayRealm"],
        "IdentityDomain": normalized["MessageDomain"],
        "TrustedNetworks": list(normalized["GatewayTrustedNetworks"]),
        "LocalRegistrarAlgorithm": normalized["GatewayAlgorithm"],
        "Extension": active_primary["Username"] if active_primary is not None else "",
        "PrimaryPolicy": _local_policy(active_primary, calls_enabled, messages_enabled),
        "Extensions": [
            {"Username": account["Username"], "Policy": _local_policy(account, calls_enabled, messages_enabled)}
            for account in active_extras
        ],
        "ServerAddress": normalized["ServerIp"],
        "ServerPort": normalized["SipPort"],
        "TrunkUsername": peer["InboundAuth"]["Username"],
        "PeerInboundAlgorithm": normalized["GatewayAlgorithm"],
        "PeerOutboundUsername": peer["OutboundAuth"]["Username"],
        "PeerExpectedRealm": normalized["PbxRealm"],
        "PeerAllowedAlgorithms": [normalized["PbxAlgorithm"]],
        "PeerAllowOutboundCalls": peer["AllowCallsFromPeer"],
        "PeerAllowOutboundMessages": peer["AllowSmsFromPeer"],
        "PeerAllowIncomingCalls": peer["AllowCallsToPeer"],
        "PeerAllowIncomingMessages": peer["AllowSmsToPeer"],
        "PeerOutgoingLines": list(peer["OutgoingLines"]),
        "PeerSmsSendLines": list(peer["SmsSendLines"]),
        # Required legacy scalar is intentionally inert: every route below has
        # explicit local lists and explicit peer-forward decisions/own targets.
        "IncomingTarget": "vowin-unused",
        "AllowOutboundCalls": calls_enabled and normalized["CarrierEnabled"],
        "MessagesEnabled": messages_enabled or sms_enabled,
        "ForwardIncomingSms": sms_enabled,
        "AllowOutgoingSms": sms_enabled,
        "DefaultOutboundLineId": next((line["Id"] for line in normalized["Lines"] if line["Default"]), None),
        "DialRules": [],
        "LineRoutes": {},
        "WasEnabled": False,
        "RestoreOnStartup": False,
    }
    for line in normalized["Lines"]:
        settings["LineRoutes"][line["Id"]] = {
            "Enabled": True,
            "AllowIncomingCalls": calls_enabled and normalized["CarrierEnabled"] and line["AllowIncomingCalls"],
            "AllowOutgoingCalls": line["AllowOutgoingCalls"],
            "ForwardIncomingSms": sms_enabled and line["AllowSmsReceive"],
            "AllowOutgoingSms": line["AllowSmsSend"],
            "IncomingTarget": line["CallTarget"],
            "SmsTarget": line["SmsTarget"],
            "LocalIncomingExtensions": [
                username for username in line["LocalIncomingExtensions"] if username in allowed_local_callers
            ] if local_enabled else [],
            "LocalSmsRecipients": list(line["LocalSmsRecipients"]) if local_enabled else [],
            "ForwardCallsToPeer": peer_enabled and peer["AllowCallsToPeer"] and bool(line["CallTarget"]),
            "ForwardSmsToPeer": peer_enabled and peer["AllowSmsToPeer"] and bool(line["SmsTarget"]),
            "Prefix": line["Prefix"],
            "CountryCode": line["CountryCode"],
            "NationalNumberLength": line["NationalNumberLength"],
            "NormalizeE164": True,
        }

    profile = {
        "Version": 2,
        "Settings": settings,
        "Credentials": {
            "MainPassword": secrets[active_primary["SecretRef"]] if active_primary is not None else "",
            "Extensions": {account["Username"]: secrets[account["SecretRef"]] for account in active_extras},
            "PeerInboundPassword": secrets[peer["InboundAuth"]["SecretRef"]] if peer_enabled else "",
            "PeerOutboundPassword": secrets[peer["OutboundAuth"]["SecretRef"]] if peer_enabled else "",
        },
    }
    # This is the same compact UTF-8 wire serializer as the artifact writer.
    # Adding credentials can exceed the public input budget; do not return an
    # oversized secret-bearing import even when its public model was valid.
    deployment_json_text(profile)
    return profile
