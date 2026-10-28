"""Pure, versioned input model for a future optional fixed-peer deployment.

This module does not render or apply Asterisk/VoWin/VoCat configuration. It does
not read files, access the network, run processes, or create/read secrets. The
two public entry points return newly allocated dicts/lists and never mutate an
input. Validation errors contain only known schema paths and fixed reasons;
even an unknown property name is untrusted and is not included in an error.

Required top-level fields are SchemaVersion (2), GatewayPlatform (vocat-go or
vowin-windows), ServerIp, BindAddress, SipPort, MessageDomain, PbxRealm,
PbxAlgorithm, GatewayIp, GatewayBindAddress, GatewayPort, GatewayRealm and
GatewayAlgorithm. Algorithms are exactly SHA-256 or explicitly selected MD5.
Go GatewayRealm admits at most 64 ASCII bytes and starts with a letter/digit;
Go PbxRealm admits 128, and Windows admits 253 for either realm. Realm spelling
is preserved, with only safe letters/digits/dot/underscore/hyphen accepted.
Addresses are private/loopback literals of one family; only the PBX BindAddress
may be unspecified, and then explicit TrustedNetworks are required. The two
network lists default to [], accept at most 32 strict same-family private or
loopback CIDRs, and must cover the opposite endpoint for a configured peer.

All other top-level fields and their defaults are:
  TrustedNetworks=[], GatewayTrustedNetworks=[];
  CallsEnabled=False, CarrierEnabled=False, MessagesEnabled=False,
  SmsEnabled=False; MessagesEnabled means internal text, not paid SMS;
  Peer={Enabled:False, InboundAuth:{Username:'',SecretRef:''},
        OutboundAuth:{Username:'',SecretRef:''}, AllowCallsFromPeer:False,
        AllowCallsToPeer:False, AllowSmsFromPeer:False, AllowSmsToPeer:False,
        OutgoingLines:[], SmsSendLines:[]};
  GatewayLocalRegistrar={Enabled:False,PrimaryUsername:'',Accounts:[]};
  Extensions=[], ExtensionOptions={}, Lines=[], DialRules=[], SmsDialRules=[].

Peer auth is relative to the gateway: InboundAuth is PBX -> gateway, using
GatewayRealm/GatewayAlgorithm; OutboundAuth is gateway -> PBX, using
PbxRealm/PbxAlgorithm. Any peer activation, grant, nonempty auth data or line
allowlist requires BOTH Username/SecretRef roles. A fully blank disabled peer
is valid. SecretRef is a role identifier, never a password. All nonempty refs
are distinct case-insensitively across peer roles, PBX and local accounts.
Actual secret values are not accepted or compared here. Legacy shared-secret
objects, unknown fields, nulls and case/escaped duplicate keys are rejected.

Each PBX Extensions item is a 2..8 digit username and requires its own
ExtensionOptions item with SecretRef. Remaining option defaults are Name:'',
Enabled:False, Ring:False, MaxContacts:4, AllowOutgoing:False,
AllowMessages:False, AllowMobileSmsSend:False, AllowMobileSmsReceive:False.
PBX and gateway-local accounts are independent namespaces, never copied.
Each registrar Accounts item has required Username/SecretRef and defaults
DisplayName:'', AllowCalls:False, AllowMessages:False, OutgoingLines:[],
SmsSendLines:[]. Go local usernames contain 2..12 digits; Windows uses safe
ASCII identifiers. Enabled registrar requires an account; Windows additionally
requires an explicit member PrimaryUsername. Merely declaring an account does
not enable the registrar or authorize paid operations.

Each Lines item requires Id; defaults are Label:'', Prefix:'', Default:False,
CountryCode:'', NationalNumberLength:0, AllowIncomingCalls:False,
AllowOutgoingCalls:False, AllowSmsSend:False, AllowSmsReceive:False,
CallTarget:'', SmsTarget:'', LocalIncomingExtensions:[], LocalSmsRecipients:[].
CallTarget/SmsTarget are service users, not account references; local target
lists must name registrar members and have at most 16 items. Line/account/
prefix identities and role refs reject casefold collisions without changing
their original spelling. At most one line is Default. National normalization
requires an explicit country code/length pair; nothing is inferred from a SIM.
Go reserves a recipient slot whenever SmsTarget is nonempty, so that line's
LocalSmsRecipients then admits at most 15, even with its features disabled.

DialRules (voice only) and SmsDialRules are independent lists, at most 64 each.
Each item requires Pattern/LineId and defaults Strip:0, Prepend:''. Patterns
use the existing restricted numeric Asterisk subset, never an arbitrary
dialplan expression; strip is 0..20 and prepend is optional '+' plus digits.
Every selected line must exist. For Go every rule's line needs an explicit
nonempty Prefix so a later renderer can send Prefix + normalized E.164 instead
of stripping selection and silently relying on the default SIM. This module
does not rewrite opaque SMS reply users or synthesize routes for them.

Validation is NOT a claim of platform expressibility or successful rendering.
Known Windows support gaps are recorded in WINDOWS_PLATFORM_SUPPORT_GAPS:
existing local/peer receive switches, per-account internal call/message grants,
line allowlists and the primary account's paid-call switch cannot express every
independent policy admitted by this model. Those
combinations remain valid requested policies for a later runtime extension;
a renderer must report unsupported combinations, never broaden permissions.
No LICENSE, geographic/device/SIM admission or runtime authorization gate is
changed or bypassed by this schema. Outputs must still be reviewed/applied via
the platform's authenticated configuration lifecycle; no auto-start is implied.
"""

import ipaddress
import json
import re
import unicodedata


MAX_DEPLOYMENT_BYTES = 65536
WINDOWS_PLATFORM_SUPPORT_GAPS = (
    "independent-local-and-peer-incoming-call-recipients",
    "independent-local-and-peer-incoming-sms-recipients",
    "independent-primary-local-and-peer-paid-call-grants",
    "per-local-account-line-allowlists",
    "per-peer-line-allowlists",
    "per-local-account-internal-call-grants",
    "per-local-account-internal-message-grants",
)


class DeploymentV2Error(ValueError):
    """Safe schema failure; messages never include input values or unknown keys."""


_IDENTIFIER = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\Z")
_PBX_USER = re.compile(r"[0-9]{2,8}\Z")
_GO_USER = re.compile(r"[0-9]{2,12}\Z")
_REALM = re.compile(r"[A-Za-z0-9_.-]+\Z")
_GO_GATEWAY_REALM = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]*\Z")
_TARGET = re.compile(r"[A-Za-z0-9+][A-Za-z0-9_.+\-]{0,63}\Z")
_PREFIX = re.compile(r"[0-9]{1,8}\Z")
_COUNTRY = re.compile(r"[1-9][0-9]{0,2}\Z")
_PREPEND = re.compile(r"\+?[0-9]{0,12}\Z")
_PATTERN = re.compile(r"_(?:[0-9XZN+]|\[[0-9]+(?:-[0-9]+)?\])+\.?\Z")
_HOSTNAME = re.compile(
    r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?"
    r"(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*\Z"
)
_PRIVATE_V4 = tuple(ipaddress.ip_network(value) for value in (
    "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "127.0.0.0/8"
))
_PRIVATE_V6 = tuple(ipaddress.ip_network(value) for value in ("fc00::/7", "::1/128"))
_REQUIRED = {
    "SchemaVersion", "GatewayPlatform", "ServerIp", "BindAddress", "SipPort",
    "MessageDomain", "PbxRealm", "PbxAlgorithm", "GatewayIp",
    "GatewayBindAddress", "GatewayPort", "GatewayRealm", "GatewayAlgorithm",
}
_FEATURES = ("CallsEnabled", "CarrierEnabled", "MessagesEnabled", "SmsEnabled")
_TOP = _REQUIRED | set(_FEATURES) | {
    "TrustedNetworks", "GatewayTrustedNetworks", "Peer",
    "GatewayLocalRegistrar", "Extensions", "ExtensionOptions", "Lines",
    "DialRules", "SmsDialRules",
}
_PEER_GRANTS = (
    "AllowCallsFromPeer", "AllowCallsToPeer", "AllowSmsFromPeer", "AllowSmsToPeer"
)
_PEER_FIELDS = {
    "Enabled", "InboundAuth", "OutboundAuth", "OutgoingLines", "SmsSendLines",
} | set(_PEER_GRANTS)
_EXTENSION_FLAGS = (
    "Enabled", "Ring", "AllowOutgoing", "AllowMessages", "AllowMobileSmsSend",
    "AllowMobileSmsReceive",
)
_EXTENSION_FIELDS = {"Name", "SecretRef", "MaxContacts"} | set(_EXTENSION_FLAGS)
_ACCOUNT_FIELDS = {
    "Username", "DisplayName", "SecretRef", "AllowCalls", "AllowMessages",
    "OutgoingLines", "SmsSendLines",
}
_LINE_FLAGS = (
    "Default", "AllowIncomingCalls", "AllowOutgoingCalls", "AllowSmsSend",
    "AllowSmsReceive",
)
_LINE_FIELDS = {
    "Id", "Label", "Prefix", "CountryCode", "NationalNumberLength",
    "CallTarget", "SmsTarget", "LocalIncomingExtensions", "LocalSmsRecipients",
} | set(_LINE_FLAGS)


def _fail(path, reason):
    # Parser/Unicode exceptions can retain the original document as context.
    # Keep ordinary traceback rendering from exposing that untrusted input.
    raise DeploymentV2Error(path + ": " + reason) from None


def _object(value, path, allowed=None, required=()):
    if type(value) is not dict:
        _fail(path, "expected object")
    seen = set()
    for key in value:
        if type(key) is not str:
            _fail(path, "expected string property names")
        folded = key.casefold()
        if folded in seen:
            _fail(path, "duplicate property names")
        seen.add(folded)
        if allowed is not None and key not in allowed:
            _fail(path, "unknown property")
    if any(key not in value for key in required):
        _fail(path, "missing required property")
    return value


def _string(value, path, maximum=256, empty=False):
    if type(value) is not str:
        _fail(path, "expected string")
    try:
        size = len(value.encode("utf-8", "strict"))
    except UnicodeError:
        _fail(path, "invalid Unicode")
    if size > maximum or not empty and not value:
        _fail(path, "invalid string length")
    if any(unicodedata.category(character) == "Cc" for character in value):
        _fail(path, "control characters are not allowed")
    return value


def _matching(value, path, expression, empty=False, maximum=256):
    result = _string(value, path, maximum=maximum, empty=empty)
    if not result and empty:
        return result
    if expression.fullmatch(result) is None:
        _fail(path, "invalid identifier or format")
    return result


def _boolean(value, path):
    if type(value) is not bool:
        _fail(path, "expected boolean")
    return value


def _integer(value, path, minimum, maximum):
    if type(value) is not int:
        _fail(path, "expected integer")
    if not minimum <= value <= maximum:
        _fail(path, "integer out of range")
    return value


def _list(value, path, maximum):
    if type(value) is not list:
        _fail(path, "expected array")
    if len(value) > maximum:
        _fail(path, "too many entries")
    return value


def _unique(value, seen, path):
    folded = value.casefold()
    if folded in seen:
        _fail(path, "duplicate identity")
    seen.add(folded)


def _identifiers(value, path, maximum, expression=_IDENTIFIER):
    result, seen = [], set()
    for index, item in enumerate(_list(value, path, maximum)):
        item_path = path + "[" + str(index) + "]"
        name = _matching(item, item_path, expression, maximum=64)
        _unique(name, seen, item_path)
        result.append(name)
    return result


def _safe_name(value, path):
    result = _string(value, path, maximum=80, empty=True)
    if any(character in ';<>"\\$' for character in result):
        _fail(path, "unsafe display name")
    return result


def _algorithm(value, path):
    result = _string(value, path, maximum=16)
    if result not in ("SHA-256", "MD5"):
        _fail(path, "unsupported algorithm")
    return result


def _private_address(address):
    if isinstance(address, ipaddress.IPv6Address) and address.ipv4_mapped is not None:
        return False
    ranges = _PRIVATE_V4 if address.version == 4 else _PRIVATE_V6
    return any(address in network for network in ranges)


def _address(value, path, wildcard=False):
    value = _string(value, path, maximum=45)
    if "%" in value:
        _fail(path, "scoped addresses are not allowed")
    try:
        address = ipaddress.ip_address(value)
    except ValueError:
        _fail(path, "expected IP literal")
    if not _private_address(address) and not (wildcard and address.is_unspecified):
        _fail(path, "expected private or loopback IP")
    return address


def _networks(value, path, family):
    result, parsed, seen = [], [], set()
    ranges = _PRIVATE_V4 if family == 4 else _PRIVATE_V6
    for index, item in enumerate(_list(value, path, 32)):
        item_path = path + "[" + str(index) + "]"
        text = _string(item, item_path, maximum=49)
        if "%" in text or "/" not in text:
            _fail(item_path, "expected strict private CIDR")
        try:
            network = ipaddress.ip_network(text, strict=True)
        except ValueError:
            _fail(item_path, "expected strict private CIDR")
        if network.version != family or not any(network.subnet_of(parent) for parent in ranges):
            _fail(item_path, "expected same-family private or loopback CIDR")
        canonical = str(network)
        _unique(canonical, seen, item_path)
        result.append(canonical)
        parsed.append(network)
    return result, parsed


def _message_domain(value, path):
    result = _string(value, path, maximum=253)
    if "%" in result:
        _fail(path, "scoped addresses are not allowed")
    try:
        address = ipaddress.ip_address(result)
    except ValueError:
        # A malformed all-numeric address must not be reclassified as a host.
        if re.fullmatch(r"[0-9.]+", result) or _HOSTNAME.fullmatch(result) is None:
            _fail(path, "expected safe hostname or private IP")
        return result
    if not _private_address(address):
        _fail(path, "expected safe hostname or private IP")
    return str(address)


def _secret_ref(value, path, seen, empty=False):
    result = _matching(value, path, _IDENTIFIER, empty=empty, maximum=64)
    if result:
        _unique(result, seen, path)
    return result


def _auth(value, path, refs):
    value = _object(value, path, {"Username", "SecretRef"})
    return {
        "Username": _matching(value.get("Username", ""), path + ".Username", _IDENTIFIER, empty=True, maximum=64),
        "SecretRef": _secret_ref(value.get("SecretRef", ""), path + ".SecretRef", refs, empty=True),
    }


def _line_references(values, members, path):
    for value in values:
        if value not in members:
            _fail(path, "reference is not a configured member")


def _check_numeric_pattern(pattern, path):
    # The supported grammar has literal digits, X/Z/N and simple digit classes.
    # Canonicalize a single class before applying the one-digit catch-all rule:
    # [0-9], [0123456789] and even [01-89] all mean X to Asterisk.
    tokens = re.findall(r"\[([^]]+)\]|([0-9XZN+])|(\.)", pattern[1:])
    digit_sets = []
    for character_class, literal, suffix in tokens:
        if character_class:
            digits, index = set(), 0
            while index < len(character_class):
                start = character_class[index]
                if index + 2 < len(character_class) and character_class[index + 1] == "-":
                    end = character_class[index + 2]
                    if start > end:
                        _fail(path, "reversed digit range is not allowed")
                    digits.update(chr(value) for value in range(ord(start), ord(end) + 1))
                    index += 3
                else:
                    digits.add(start)
                    index += 1
            digit_sets.append(digits)
        elif literal:
            digit_sets.append(set("0123456789") if literal == "X" else None)
        elif suffix:
            continue
    if len(digit_sets) == 1 and digit_sets[0] == set("0123456789"):
        _fail(path, "unrestricted rule is not allowed")


def _rules(value, path, lines, platform):
    result, seen = [], set()
    for index, raw in enumerate(_list(value, path, 64)):
        item_path = path + "[" + str(index) + "]"
        raw = _object(raw, item_path, {"Pattern", "LineId", "Strip", "Prepend"}, {"Pattern", "LineId"})
        pattern = _matching(raw["Pattern"], item_path + ".Pattern", _PATTERN, maximum=64)
        if pattern in ("_.", "_X.", "_X", "_!"):
            _fail(item_path + ".Pattern", "unrestricted rule is not allowed")
        _check_numeric_pattern(pattern, item_path + ".Pattern")
        _unique(pattern, seen, item_path + ".Pattern")
        line_id = _matching(raw["LineId"], item_path + ".LineId", _IDENTIFIER, maximum=64)
        if line_id not in lines:
            _fail(item_path + ".LineId", "reference is not a configured member")
        if platform == "vocat-go" and not lines[line_id]["Prefix"]:
            _fail(item_path + ".LineId", "Go rules require an explicit selected-line prefix")
        result.append({
            "Pattern": pattern,
            "LineId": line_id,
            "Strip": _integer(raw.get("Strip", 0), item_path + ".Strip", 0, 20),
            "Prepend": _matching(raw.get("Prepend", ""), item_path + ".Prepend", _PREPEND, empty=True, maximum=13),
        })
    return result


def normalize_deployment_v2(value: dict) -> dict:
    """Validate an in-memory v2 object and return an independent normalized copy.

    No permissive legacy conversion is performed. Platform support gaps remain
    requested policies, not promises that an existing renderer can apply them.
    """
    value = _object(value, "$", _TOP, _REQUIRED)
    if _integer(value["SchemaVersion"], "SchemaVersion", 2, 2) != 2:
        _fail("SchemaVersion", "unsupported schema version")
    platform = _string(value["GatewayPlatform"], "GatewayPlatform", maximum=32)
    if platform not in ("vocat-go", "vowin-windows"):
        _fail("GatewayPlatform", "unsupported platform")
    server = _address(value["ServerIp"], "ServerIp")
    pbx_bind = _address(value["BindAddress"], "BindAddress", wildcard=True)
    gateway = _address(value["GatewayIp"], "GatewayIp")
    gateway_bind = _address(value["GatewayBindAddress"], "GatewayBindAddress")
    if any(address.version != server.version for address in (pbx_bind, gateway, gateway_bind)):
        _fail("GatewayIp", "endpoints must use one address family")
    trusted, pbx_networks = _networks(value.get("TrustedNetworks", []), "TrustedNetworks", server.version)
    gateway_trusted, gateway_networks = _networks(value.get("GatewayTrustedNetworks", []), "GatewayTrustedNetworks", server.version)
    if pbx_bind.is_unspecified and not trusted:
        _fail("TrustedNetworks", "wildcard PBX binding requires explicit trusted networks")
    result = {
        "SchemaVersion": 2,
        "GatewayPlatform": platform,
        "ServerIp": str(server),
        "BindAddress": str(pbx_bind),
        "SipPort": _integer(value["SipPort"], "SipPort", 1, 65535),
        "MessageDomain": _message_domain(value["MessageDomain"], "MessageDomain"),
        "TrustedNetworks": trusted,
        "PbxRealm": _matching(value["PbxRealm"], "PbxRealm", _REALM, maximum=128 if platform == "vocat-go" else 253),
        "PbxAlgorithm": _algorithm(value["PbxAlgorithm"], "PbxAlgorithm"),
        "GatewayIp": str(gateway),
        "GatewayBindAddress": str(gateway_bind),
        "GatewayPort": _integer(value["GatewayPort"], "GatewayPort", 1, 65535),
        "GatewayRealm": _matching(value["GatewayRealm"], "GatewayRealm", _GO_GATEWAY_REALM if platform == "vocat-go" else _REALM, maximum=64 if platform == "vocat-go" else 253),
        "GatewayAlgorithm": _algorithm(value["GatewayAlgorithm"], "GatewayAlgorithm"),
        "GatewayTrustedNetworks": gateway_trusted,
    }
    for key in _FEATURES:
        result[key] = _boolean(value.get(key, False), key)
    refs = set()
    peer_raw = _object(value.get("Peer", {}), "Peer", _PEER_FIELDS)
    peer = {
        "Enabled": _boolean(peer_raw.get("Enabled", False), "Peer.Enabled"),
        "InboundAuth": _auth(peer_raw.get("InboundAuth", {}), "Peer.InboundAuth", refs),
        "OutboundAuth": _auth(peer_raw.get("OutboundAuth", {}), "Peer.OutboundAuth", refs),
        "OutgoingLines": _identifiers(peer_raw.get("OutgoingLines", []), "Peer.OutgoingLines", 32),
        "SmsSendLines": _identifiers(peer_raw.get("SmsSendLines", []), "Peer.SmsSendLines", 32),
    }
    for key in _PEER_GRANTS:
        peer[key] = _boolean(peer_raw.get(key, False), "Peer." + key)
    configured_peer = peer["Enabled"] or any(peer[key] for key in _PEER_GRANTS) or bool(peer["OutgoingLines"] or peer["SmsSendLines"]) or any(
        role[field] for role in (peer["InboundAuth"], peer["OutboundAuth"]) for field in ("Username", "SecretRef")
    )
    if configured_peer:
        if not all(role[field] for role in (peer["InboundAuth"], peer["OutboundAuth"]) for field in ("Username", "SecretRef")):
            _fail("Peer", "configured peer requires both independent authentication roles")
        if not any(server in network for network in gateway_networks):
            _fail("GatewayTrustedNetworks", "networks must cover the fixed PBX endpoint")
        if not any(gateway in network for network in pbx_networks):
            _fail("TrustedNetworks", "networks must cover the fixed gateway endpoint")
    if configured_peer and result["SipPort"] == result["GatewayPort"] and server in (gateway, gateway_bind):
        _fail("Peer", "peer must not point to the gateway listener")
    result["Peer"] = peer

    registrar_raw = _object(value.get("GatewayLocalRegistrar", {}), "GatewayLocalRegistrar", {"Enabled", "PrimaryUsername", "Accounts"})
    registrar = {
        "Enabled": _boolean(registrar_raw.get("Enabled", False), "GatewayLocalRegistrar.Enabled"),
        "PrimaryUsername": _string(registrar_raw.get("PrimaryUsername", ""), "GatewayLocalRegistrar.PrimaryUsername", maximum=64, empty=True),
        "Accounts": [],
    }
    account_names, account_seen = set(), set()
    for index, raw in enumerate(_list(registrar_raw.get("Accounts", []), "GatewayLocalRegistrar.Accounts", 64)):
        path = "GatewayLocalRegistrar.Accounts[" + str(index) + "]"
        raw = _object(raw, path, _ACCOUNT_FIELDS, {"Username", "SecretRef"})
        username = _matching(raw["Username"], path + ".Username", _GO_USER if platform == "vocat-go" else _IDENTIFIER, maximum=64)
        _unique(username, account_seen, path + ".Username")
        account_names.add(username)
        registrar["Accounts"].append({
            "Username": username,
            "DisplayName": _safe_name(raw.get("DisplayName", ""), path + ".DisplayName"),
            "SecretRef": _secret_ref(raw["SecretRef"], path + ".SecretRef", refs),
            "AllowCalls": _boolean(raw.get("AllowCalls", False), path + ".AllowCalls"),
            "AllowMessages": _boolean(raw.get("AllowMessages", False), path + ".AllowMessages"),
            "OutgoingLines": _identifiers(raw.get("OutgoingLines", []), path + ".OutgoingLines", 32),
            "SmsSendLines": _identifiers(raw.get("SmsSendLines", []), path + ".SmsSendLines", 32),
        })
    if registrar["PrimaryUsername"] and registrar["PrimaryUsername"] not in account_names:
        _fail("GatewayLocalRegistrar.PrimaryUsername", "primary account must be a configured member")
    if registrar["Enabled"] and (not account_names or platform == "vowin-windows" and not registrar["PrimaryUsername"]):
        _fail("GatewayLocalRegistrar", "enabled registrar requires accounts and an explicit Windows primary")
    result["GatewayLocalRegistrar"] = registrar

    extensions = _identifiers(value.get("Extensions", []), "Extensions", 64, _PBX_USER)
    options_raw = _object(value.get("ExtensionOptions", {}), "ExtensionOptions")
    if set(options_raw) != set(extensions):
        _fail("ExtensionOptions", "options must match the configured PBX accounts exactly")
    options = {}
    for username in extensions:
        path = "ExtensionOptions.*"
        raw = _object(options_raw[username], path, _EXTENSION_FIELDS, {"SecretRef"})
        normalized = {
            "SecretRef": _secret_ref(raw["SecretRef"], path + ".SecretRef", refs),
            "Name": _safe_name(raw.get("Name", ""), path + ".Name"),
            "MaxContacts": _integer(raw.get("MaxContacts", 4), path + ".MaxContacts", 1, 8),
        }
        for key in _EXTENSION_FLAGS:
            normalized[key] = _boolean(raw.get(key, False), path + "." + key)
        options[username] = normalized
    result["Extensions"], result["ExtensionOptions"] = extensions, options

    lines, line_map, line_seen, prefixes = [], {}, set(), set()
    default_count = 0
    for index, raw in enumerate(_list(value.get("Lines", []), "Lines", 32)):
        path = "Lines[" + str(index) + "]"
        raw = _object(raw, path, _LINE_FIELDS, {"Id"})
        line_id = _matching(raw["Id"], path + ".Id", _IDENTIFIER, maximum=64)
        _unique(line_id, line_seen, path + ".Id")
        prefix = _matching(raw.get("Prefix", ""), path + ".Prefix", _PREFIX, empty=True, maximum=8)
        if prefix:
            _unique(prefix, prefixes, path + ".Prefix")
        country = _matching(raw.get("CountryCode", ""), path + ".CountryCode", _COUNTRY, empty=True, maximum=3)
        national_length = _integer(raw.get("NationalNumberLength", 0), path + ".NationalNumberLength", 0, 14)
        if not country and national_length != 0 or country and not 6 <= national_length <= 15 - len(country):
            _fail(path + ".NationalNumberLength", "country code and national length must be an explicit valid pair")
        line = {
            "Id": line_id,
            "Label": _safe_name(raw.get("Label", ""), path + ".Label"),
            "Prefix": prefix,
            "CountryCode": country,
            "NationalNumberLength": national_length,
            "CallTarget": _matching(raw.get("CallTarget", ""), path + ".CallTarget", _TARGET, empty=True, maximum=64),
            "SmsTarget": _matching(raw.get("SmsTarget", ""), path + ".SmsTarget", _TARGET, empty=True, maximum=64),
            "LocalIncomingExtensions": _identifiers(raw.get("LocalIncomingExtensions", []), path + ".LocalIncomingExtensions", 16, _GO_USER if platform == "vocat-go" else _IDENTIFIER),
            "LocalSmsRecipients": _identifiers(raw.get("LocalSmsRecipients", []), path + ".LocalSmsRecipients", 16, _GO_USER if platform == "vocat-go" else _IDENTIFIER),
        }
        for key in _LINE_FLAGS:
            line[key] = _boolean(raw.get(key, False), path + "." + key)
        default_count += int(line["Default"])
        _line_references(line["LocalIncomingExtensions"], account_names, path + ".LocalIncomingExtensions")
        _line_references(line["LocalSmsRecipients"], account_names, path + ".LocalSmsRecipients")
        if platform == "vocat-go" and line["SmsTarget"] and len(line["LocalSmsRecipients"]) > 15:
            _fail(path + ".LocalSmsRecipients", "Go local and peer SMS recipients exceed the combined limit")
        lines.append(line)
        line_map[line_id] = line
    if default_count > 1:
        _fail("Lines", "at most one default line is allowed")
    for key in ("OutgoingLines", "SmsSendLines"):
        _line_references(peer[key], line_map, "Peer." + key)
        for index, account in enumerate(registrar["Accounts"]):
            _line_references(account[key], line_map, "GatewayLocalRegistrar.Accounts[" + str(index) + "]." + key)
    result["Lines"] = lines
    result["DialRules"] = _rules(value.get("DialRules", []), "DialRules", line_map, platform)
    result["SmsDialRules"] = _rules(value.get("SmsDialRules", []), "SmsDialRules", line_map, platform)
    return result


def _json_pairs(pairs):
    result, seen = {}, set()
    for key, value in pairs:
        _unique(key, seen, "$")
        result[key] = value
    return result


def _reject_number(_value):
    _fail("$", "only bounded integer numbers are allowed")


def _json_integer(value):
    if len(value) > 10:
        _fail("$", "integer out of range")
    return int(value)


def parse_deployment_v2(raw: bytes) -> dict:
    """Parse at most 65536 strict UTF-8 JSON bytes, then normalize the v2 model."""
    if type(raw) is not bytes or not 0 < len(raw) <= MAX_DEPLOYMENT_BYTES:
        _fail("$", "expected 1..65536 UTF-8 JSON bytes")
    try:
        text = raw.decode("utf-8", "strict")
        value = json.loads(text, object_pairs_hook=_json_pairs, parse_int=_json_integer,
                           parse_float=_reject_number, parse_constant=_reject_number)
    except DeploymentV2Error:
        raise
    except (UnicodeError, ValueError, RecursionError):
        _fail("$", "invalid UTF-8 JSON")
    return normalize_deployment_v2(value)


def resolve_deployment_secrets(model: dict, secrets_by_ref: dict) -> dict:
    """Resolve explicit role refs, never usernames or a legacy shared password.

    Values are intentionally restricted to a safe common subset for PJSIP,
    Go's 12..128 byte credential input and Windows typed credentials. Declared
    inactive roles still require their own ref; projection never enables them
    merely to retain a password. Equal chosen values across roles are allowed.
    Neither this function nor its errors print or persist any secret.
    """
    model = normalize_deployment_v2(model)
    if type(secrets_by_ref) is not dict:
        _fail("Secrets", "expected an explicit role-reference map")
    refs = []
    for role in (model["Peer"]["InboundAuth"], model["Peer"]["OutboundAuth"]):
        if role["SecretRef"]:
            refs.append(role["SecretRef"])
    refs.extend(account["SecretRef"] for account in model["GatewayLocalRegistrar"]["Accounts"])
    refs.extend(model["ExtensionOptions"][username]["SecretRef"] for username in model["Extensions"])
    if len(secrets_by_ref) != len(refs) or any(type(key) is not str for key in secrets_by_ref):
        _fail("Secrets", "keys must exactly match all declared role references")
    if set(secrets_by_ref) != set(refs):
        _fail("Secrets", "keys must exactly match all declared role references")
    values = {}
    for ref in refs:
        value = secrets_by_ref[ref]
        if type(value) is not str or not re.fullmatch(r"[A-Za-z0-9_-]{12,128}", value):
            _fail("Secrets", "each role requires a safe 12..128 byte credential")
        values[ref] = value
    return values


def parse_deployment_secrets(raw: bytes, model: dict) -> dict:
    """Bounded, duplicate-aware private input; exception chains never echo it."""
    if type(raw) is not bytes or not 0 < len(raw) <= MAX_DEPLOYMENT_BYTES:
        _fail("Secrets", "expected 1..65536 UTF-8 JSON bytes")
    try:
        value = json.loads(raw.decode("utf-8", "strict"), object_pairs_hook=_json_pairs,
                           parse_int=_json_integer, parse_float=_reject_number,
                           parse_constant=_reject_number)
    except (UnicodeError, ValueError, RecursionError):
        _fail("Secrets", "invalid UTF-8 JSON")
    return resolve_deployment_secrets(model, value)


def deployment_json_text(value: dict) -> str:
    """One bounded, reimportable JSON representation for every v2 artifact.

    Measure the actual UTF-8 output, not the input object or character count.
    The fixed diagnostic deliberately does not contain serialization errors,
    whose messages could include a private credential.
    """
    try:
        result = json.dumps(value, ensure_ascii=False, separators=(",", ":"),
                            allow_nan=False) + "\n"
        size = len(result.encode("utf-8", "strict"))
    except (UnicodeError, ValueError, TypeError, RecursionError):
        _fail("Output", "invalid deployment JSON")
    if size > MAX_DEPLOYMENT_BYTES:
        _fail("Output", "deployment JSON exceeds 65536 UTF-8 bytes")
    return result
