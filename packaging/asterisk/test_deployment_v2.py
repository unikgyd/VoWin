"""Pure V2 deployment-schema controls, not a generator or live-PBX acceptance.

Fixtures are synthetic. These tests use no files, network, modems or credentials
from a running deployment. Expected defaults are fixed here, not obtained from
the implementation under test. The pre-V2 generator is deliberately not used as
an artificial RED oracle for a new, previously missing schema module.
"""
from contextlib import ExitStack
from copy import deepcopy
import json
import traceback
import unittest
from unittest.mock import patch

from deployment_v2 import (
    DeploymentV2Error,
    normalize_deployment_v2,
    parse_deployment_v2,
)


SECRET_SENTINEL = "synthetic-secret-never-echo-this"


def minimal_deployment(platform="vowin-windows"):
    return {
        "SchemaVersion": 2,
        "GatewayPlatform": platform,
        "ServerIp": "127.0.0.2",
        "BindAddress": "127.0.0.2",
        "SipPort": 5060,
        "MessageDomain": "pbx.example.invalid",
        "PbxRealm": "pbx.example.invalid",
        "PbxAlgorithm": "SHA-256",
        "GatewayIp": "127.0.0.3",
        "GatewayBindAddress": "127.0.0.3",
        "GatewayPort": 5062,
        "GatewayRealm": "gateway.example.invalid",
        "GatewayAlgorithm": "SHA-256",
    }


def full_deployment(platform="vowin-windows"):
    value = minimal_deployment(platform)
    local_main, local_extra = (("3101", "3102") if platform == "vocat-go"
                               else ("desk-main", "desk-extra"))
    value.update({
        "TrustedNetworks": ["127.0.0.3/32"],
        "GatewayTrustedNetworks": ["127.0.0.2/32"],
        "CallsEnabled": True,
        "CarrierEnabled": True,
        "MessagesEnabled": True,
        "SmsEnabled": True,
        "Peer": {
            "Enabled": True,
            "InboundAuth": {"Username": "pbx-to-gateway", "SecretRef": "peer-in-ref"},
            "OutboundAuth": {"Username": "gateway-to-pbx", "SecretRef": "peer-out-ref"},
            "AllowCallsFromPeer": True,
            "AllowCallsToPeer": True,
            "AllowSmsFromPeer": True,
            "AllowSmsToPeer": True,
            "OutgoingLines": ["synthetic-a"],
            "SmsSendLines": ["synthetic-a"],
        },
        "Extensions": ["2101", "2102"],
        "ExtensionOptions": {
            "2101": {
                "SecretRef": "pbx-first-ref", "Name": "Synthetic first",
                "Enabled": True, "Ring": True, "MaxContacts": 2,
                "AllowOutgoing": True, "AllowMessages": True,
                "AllowMobileSmsSend": True, "AllowMobileSmsReceive": True,
            },
            "2102": {
                "SecretRef": "pbx-second-ref", "Name": "Synthetic second",
                "Enabled": True, "Ring": True, "MaxContacts": 3,
                "AllowOutgoing": True, "AllowMessages": True,
                "AllowMobileSmsSend": True, "AllowMobileSmsReceive": True,
            },
        },
        "GatewayLocalRegistrar": {
            "Enabled": True, "PrimaryUsername": local_main,
            "Accounts": [
                {"Username": local_main, "SecretRef": "local-first-ref",
                 "DisplayName": "Synthetic local first", "AllowCalls": True,
                 "AllowMessages": True, "OutgoingLines": ["synthetic-a"],
                 "SmsSendLines": ["synthetic-a"]},
                {"Username": local_extra, "SecretRef": "local-second-ref",
                 "DisplayName": "Synthetic local second", "AllowCalls": True,
                 "AllowMessages": True, "OutgoingLines": ["synthetic-a"],
                 "SmsSendLines": ["synthetic-a"]},
            ],
        },
        "Lines": [{
            "Id": "synthetic-a", "Label": "Synthetic line", "Prefix": "81",
            "Default": False, "CountryCode": "", "NationalNumberLength": 0,
            "AllowIncomingCalls": True, "AllowOutgoingCalls": True,
            "AllowSmsSend": True, "AllowSmsReceive": True,
            "CallTarget": "ring-service", "SmsTarget": "sms-service",
            "LocalIncomingExtensions": [local_main], "LocalSmsRecipients": [local_extra],
        }],
        "DialRules": [{"Pattern": "_81X.", "LineId": "synthetic-a", "Strip": 2, "Prepend": "+"}],
        "SmsDialRules": [{"Pattern": "_81X.", "LineId": "synthetic-a", "Strip": 2, "Prepend": "+"}],
    })
    return value


def encoded(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def descend(value, path):
    for item in path:
        value = value[item]
    return value


def assign(value, path, replacement):
    descend(value, path[:-1])[path[-1]] = replacement


def with_extra_json_member(value, path, key_json, extra):
    """Encode one duplicate member without relying on Python dict overwriting."""
    target = descend(value, path)

    def render(node):
        if isinstance(node, dict):
            members = [json.dumps(key) + ":" + render(item) for key, item in node.items()]
            if node is target:
                members.insert(0, key_json + ":" + json.dumps(extra))
            return "{" + ",".join(members) + "}"
        if isinstance(node, list):
            return "[" + ",".join(render(item) for item in node) + "]"
        return json.dumps(node)

    return render(value).encode("utf-8")


class DeploymentV2Tests(unittest.TestCase):
    def assert_invalid(self, value):
        before = deepcopy(value)
        with self.assertRaises(DeploymentV2Error) as caught:
            normalize_deployment_v2(value)
        self.assertIsInstance(caught.exception, ValueError)
        self.assertEqual(before, value, "rejected input must not be mutated")
        return caught.exception

    def test_error_has_value_error_contract(self):
        self.assertTrue(issubclass(DeploymentV2Error, ValueError))

    def test_minimal_defaults_are_disabled_and_explicit(self):
        expected = minimal_deployment()
        expected.update({
            "TrustedNetworks": [], "GatewayTrustedNetworks": [],
            "CallsEnabled": False, "CarrierEnabled": False,
            "MessagesEnabled": False, "SmsEnabled": False,
            "Peer": {
                "Enabled": False,
                "InboundAuth": {"Username": "", "SecretRef": ""},
                "OutboundAuth": {"Username": "", "SecretRef": ""},
                "AllowCallsFromPeer": False, "AllowCallsToPeer": False,
                "AllowSmsFromPeer": False, "AllowSmsToPeer": False,
                "OutgoingLines": [], "SmsSendLines": [],
            },
            "GatewayLocalRegistrar": {"Enabled": False, "PrimaryUsername": "", "Accounts": []},
            "Extensions": [], "ExtensionOptions": {}, "Lines": [],
            "DialRules": [], "SmsDialRules": [],
        })
        self.assertEqual(expected, normalize_deployment_v2(minimal_deployment()))

    def test_parse_and_normalize_accept_independent_synthetic_roles(self):
        for platform in ("vocat-go", "vowin-windows"):
            with self.subTest(platform=platform):
                value = full_deployment(platform)
                self.assertEqual(value, normalize_deployment_v2(value))
                self.assertEqual(value, parse_deployment_v2(encoded(value)))
                self.assertEqual("peer-in-ref", value["Peer"]["InboundAuth"]["SecretRef"])
                self.assertEqual("peer-out-ref", value["Peer"]["OutboundAuth"]["SecretRef"])

    def test_same_username_across_namespaces_and_peer_roles_keeps_distinct_refs(self):
        for platform in ("vocat-go", "vowin-windows"):
            with self.subTest(platform=platform):
                value = full_deployment(platform)
                value["Peer"]["InboundAuth"]["Username"] = "2101"
                value["Peer"]["OutboundAuth"]["Username"] = "2101"
                value["GatewayLocalRegistrar"]["PrimaryUsername"] = "2101"
                value["GatewayLocalRegistrar"]["Accounts"][0]["Username"] = "2101"
                value["Lines"][0]["LocalIncomingExtensions"] = ["2101"]
                self.assertEqual(value, normalize_deployment_v2(value))
                self.assertEqual(value, parse_deployment_v2(encoded(value)))

    def test_identity_and_secret_reference_spelling_is_preserved_not_casefold_rewritten(self):
        value = full_deployment()
        value["Peer"]["InboundAuth"].update(Username="SharedPeer", SecretRef="Peer-In-Ref")
        value["Peer"]["OutboundAuth"].update(Username="SharedPeer", SecretRef="Peer-Out-Ref")
        self.assertEqual(value, normalize_deployment_v2(value))

    def test_normalization_is_idempotent_and_preserves_order(self):
        value = full_deployment()
        value["Extensions"].reverse()
        value["GatewayLocalRegistrar"]["Accounts"].reverse()
        first = normalize_deployment_v2(value)
        self.assertEqual(value, first)
        self.assertEqual(first, normalize_deployment_v2(first))

    def test_success_returns_a_deep_copy_without_mutating_caller(self):
        original = full_deployment()
        before = deepcopy(original)
        result = normalize_deployment_v2(original)
        self.assertEqual(before, original)
        result["Peer"]["OutgoingLines"].append("mutated-output")
        result["ExtensionOptions"]["2101"]["Name"] = "Changed output"
        result["GatewayLocalRegistrar"]["Accounts"][0]["SmsSendLines"].clear()
        result["Lines"][0]["LocalSmsRecipients"].clear()
        self.assertEqual(before, original)

    def test_default_nested_objects_are_not_shared_between_results(self):
        first = normalize_deployment_v2(minimal_deployment())
        second = normalize_deployment_v2(minimal_deployment())
        first["Peer"]["OutgoingLines"].append("output-only")
        first["GatewayLocalRegistrar"]["Accounts"].append({"output": True})
        first["TrustedNetworks"].append("127.0.0.1/32")
        self.assertEqual([], second["Peer"]["OutgoingLines"])
        self.assertEqual([], second["GatewayLocalRegistrar"]["Accounts"])
        self.assertEqual([], second["TrustedNetworks"])

    def test_parse_and_normalize_do_not_open_files_network_or_processes(self):
        value = full_deployment()
        raw = encoded(value)
        forbidden = (
            "builtins.open", "os.open", "os.mkdir", "os.replace", "os.remove",
            "pathlib.Path.open", "socket.socket", "socket.getaddrinfo",
            "subprocess.Popen", "secrets.token_hex", "secrets.token_bytes",
        )
        with ExitStack() as stack:
            for target in forbidden:
                stack.enter_context(patch(target, side_effect=AssertionError("schema must remain pure")))
            self.assertEqual(value, normalize_deployment_v2(value))
            self.assertEqual(value, parse_deployment_v2(raw))

    def test_json_byte_limit_includes_the_exact_65536_boundary(self):
        raw = encoded(minimal_deployment())
        at_limit = raw + b" " * (65536 - len(raw))
        self.assertEqual(65536, len(at_limit))
        self.assertEqual(normalize_deployment_v2(minimal_deployment()), parse_deployment_v2(at_limit))
        for rejected in (b"", at_limit + b" "):
            with self.subTest(length=len(rejected)), self.assertRaises(DeploymentV2Error):
                parse_deployment_v2(rejected)

    def test_json_limit_counts_utf8_bytes_not_unicode_characters(self):
        value = full_deployment()
        value["ExtensionOptions"]["2101"]["Name"] = "研究分机"
        raw = encoded(value)
        self.assertGreater(len(raw), len(raw.decode("utf-8")))
        at_limit = raw + b" " * (65536 - len(raw))
        self.assertEqual(value, parse_deployment_v2(at_limit))
        with self.assertRaises(DeploymentV2Error):
            parse_deployment_v2(at_limit + b" ")

    def test_parser_rejects_nonbytes_malformed_utf8_and_nonobjects(self):
        invalid = [None, "{}", bytearray(b"{}"), memoryview(b"{}"),
                   b"\xff", b"{", b"[]", b"null", b"true", b"2", b"{} {}",
                   b'{"SchemaVersion":NaN}', b'{"SchemaVersion":Infinity}']
        for raw in invalid:
            with self.subTest(kind=type(raw).__name__, raw=repr(raw)), self.assertRaises(DeploymentV2Error):
                parse_deployment_v2(raw)

    def test_malicious_nested_json_under_byte_limit_is_a_safe_schema_error(self):
        raw = (b'{"SchemaVersion":2,"UnknownField":' + b"[" * 2000
               + b"0" + b"]" * 2000 + b"}")
        self.assertLess(len(raw), 65536)
        with self.assertRaises(DeploymentV2Error) as caught:
            parse_deployment_v2(raw)
        self.assertNotIn("UnknownField", str(caught.exception))

    def test_escaped_unpaired_surrogates_in_safe_text_are_rejected_without_secret_echo(self):
        paths = [("ExtensionOptions", "2101", "Name"),
                 ("GatewayLocalRegistrar", "Accounts", 0, "DisplayName"),
                 ("Lines", 0, "Label")]
        for path in paths:
            for surrogate in ("\ud800", "\udfff"):
                with self.subTest(path=path, surrogate_code=ord(surrogate)):
                    value = full_deployment()
                    assign(value, path, SECRET_SENTINEL + surrogate)
                    error = self.assert_invalid(value)
                    self.assertNotIn(SECRET_SENTINEL, str(error))
                    raw = json.dumps(value, ensure_ascii=True).encode("ascii")
                    with self.assertRaises(DeploymentV2Error) as caught:
                        parse_deployment_v2(raw)
                    self.assertNotIn(SECRET_SENTINEL, str(caught.exception))

    def test_normalizer_rejects_nonobjects(self):
        for value in (None, [], (), "{}", True, 2):
            with self.subTest(kind=type(value).__name__):
                self.assert_invalid(value)

    def test_version_is_required_exact_integer_two_not_bool(self):
        for version in (None, False, True, 0, 1, 3, 2.0, "2"):
            with self.subTest(version=version):
                value = minimal_deployment()
                value["SchemaVersion"] = version
                self.assert_invalid(value)
        value = minimal_deployment()
        del value["SchemaVersion"]
        self.assert_invalid(value)

    def test_every_core_network_realm_algorithm_and_platform_field_is_required(self):
        for field in minimal_deployment():
            with self.subTest(field=field):
                value = minimal_deployment()
                del value[field]
                self.assert_invalid(value)

    def test_unknown_platforms_and_legacy_projection_are_rejected(self):
        for platform in ("go", "windows", "rust", "", None, True):
            with self.subTest(platform=platform):
                value = minimal_deployment()
                value["GatewayPlatform"] = platform
                self.assert_invalid(value)
        self.assert_invalid({"ServerIp": "127.0.0.2", "WindowsIp": "127.0.0.3",
                             "TrunkUsername": "legacy-peer", "Realm": "legacy.example.invalid"})

    def test_duplicate_exact_escaped_and_casefold_keys_are_rejected_recursively(self):
        cases = [
            ((), "SchemaVersion", r'"\u0053chemaVersion"', 2),
            (("Peer",), "Enabled", '"enabled"', True),
            (("Peer", "InboundAuth"), "Username", r'"\u0055sername"', "other-user"),
            (("ExtensionOptions",), "2101", r'"\u0032\u0031\u0030\u0031"', {"SecretRef": "other-ref"}),
            (("ExtensionOptions", "2101"), "SecretRef", '"secretref"', "other-ref"),
            (("Lines", 0), "Id", '"ID"', "other-line"),
            (("GatewayLocalRegistrar", "Accounts", 0), "Username", '"username"', "other-user"),
        ]
        for path, key, alias, extra in cases:
            for key_json in (json.dumps(key), alias):
                with self.subTest(path=path, key=key_json), self.assertRaises(DeploymentV2Error):
                    parse_deployment_v2(with_extra_json_member(full_deployment(), path, key_json, extra))

    def test_unknown_fields_are_rejected_at_every_schema_object(self):
        paths = [(), ("Peer",), ("Peer", "InboundAuth"), ("Peer", "OutboundAuth"),
                 ("GatewayLocalRegistrar",), ("GatewayLocalRegistrar", "Accounts", 0),
                 ("ExtensionOptions", "2101"), ("Lines", 0),
                 ("DialRules", 0), ("SmsDialRules", 0)]
        for path in paths:
            with self.subTest(path=path):
                value = full_deployment()
                descend(value, path)["UnknownField"] = "never-interpreted"
                self.assert_invalid(value)
                with self.assertRaises(DeploymentV2Error):
                    parse_deployment_v2(encoded(value))

    def test_unknown_secret_key_and_invalid_secret_values_never_appear_in_errors(self):
        cases = [
            ((), SECRET_SENTINEL, "unknown-value"),
            (("Peer", "InboundAuth"), "SecretRef", SECRET_SENTINEL + "\n"),
            (("Peer", "OutboundAuth"), "Password", SECRET_SENTINEL),
            (("ExtensionOptions", "2101"), "Password", SECRET_SENTINEL),
            (("GatewayLocalRegistrar", "Accounts", 0), "Password", SECRET_SENTINEL),
        ]
        for path, key, item in cases:
            with self.subTest(path=path, key_kind="secret" if key == SECRET_SENTINEL else key):
                value = full_deployment()
                descend(value, path)[key] = item
                error = self.assert_invalid(value)
                self.assertNotIn(SECRET_SENTINEL, str(error))
                self.assertNotIn(SECRET_SENTINEL, repr(error))
                with self.assertRaises(DeploymentV2Error) as caught:
                    parse_deployment_v2(encoded(value))
                self.assertNotIn(SECRET_SENTINEL, str(caught.exception))
                self.assertNotIn(SECRET_SENTINEL, repr(caught.exception))

    def test_formatted_tracebacks_suppress_raw_parser_and_unicode_exception_context(self):
        surrogate = full_deployment()
        surrogate["ExtensionOptions"]["2101"]["Name"] = SECRET_SENTINEL + "\ud800"
        invalid = [
            b'{"untrusted":"' + SECRET_SENTINEL.encode("ascii") + b'\xff"}',
            encoded(minimal_deployment())[:-1] + b',"untrusted":"' + SECRET_SENTINEL.encode("ascii") + b'"',
            json.dumps(surrogate, ensure_ascii=True).encode("ascii"),
        ]
        for index, raw in enumerate(invalid):
            with self.subTest(kind=index):
                try:
                    parse_deployment_v2(raw)
                except DeploymentV2Error as error:
                    # Format while the actual traceback is attached. assertRaises
                    # clears it, which would make this privacy check too weak.
                    rendered = "".join(traceback.format_exception(type(error), error, error.__traceback__))
                    self.assertIn("DeploymentV2Error", rendered)
                    for hidden in (SECRET_SENTINEL, "UnicodeDecodeError", "UnicodeEncodeError", "JSONDecodeError",
                                   "During handling of the above exception", "The above exception was the direct cause",
                                   "invalid start byte", "surrogates not allowed", "can't decode byte", "can't encode character"):
                        self.assertNotIn(hidden, rendered)
                else:
                    self.fail("malformed input unexpectedly accepted")

    def test_known_nulls_and_wrong_container_types_do_not_mean_defaults(self):
        paths = [("Peer",), ("Peer", "InboundAuth"), ("Peer", "OutgoingLines"),
                 ("GatewayLocalRegistrar",), ("GatewayLocalRegistrar", "Accounts"),
                 ("Extensions",), ("ExtensionOptions",), ("ExtensionOptions", "2101"),
                 ("Lines",), ("Lines", 0), ("DialRules",), ("SmsDialRules",),
                 ("TrustedNetworks",), ("GatewayTrustedNetworks",)]
        for path in paths:
            for replacement in (None, "not-a-container"):
                with self.subTest(path=path, kind=type(replacement).__name__):
                    value = full_deployment()
                    assign(value, path, replacement)
                    self.assert_invalid(value)

    def test_features_and_permission_flags_are_strict_booleans(self):
        paths = [(key,) for key in ("CallsEnabled", "CarrierEnabled", "MessagesEnabled", "SmsEnabled")]
        paths += [("Peer", key) for key in ("Enabled", "AllowCallsFromPeer", "AllowCallsToPeer",
                                            "AllowSmsFromPeer", "AllowSmsToPeer")]
        paths += [("GatewayLocalRegistrar", "Enabled")]
        paths += [("GatewayLocalRegistrar", "Accounts", 0, key) for key in ("AllowCalls", "AllowMessages")]
        paths += [("ExtensionOptions", "2101", key) for key in
                  ("Enabled", "Ring", "AllowOutgoing", "AllowMessages", "AllowMobileSmsSend", "AllowMobileSmsReceive")]
        paths += [("Lines", 0, key) for key in
                  ("Default", "AllowIncomingCalls", "AllowOutgoingCalls", "AllowSmsSend", "AllowSmsReceive")]
        for path in paths:
            for replacement in (0, 1, "true", None):
                with self.subTest(path=path, replacement=replacement):
                    value = full_deployment()
                    assign(value, path, replacement)
                    self.assert_invalid(value)

    def test_port_bounds_are_integers_and_never_boolean(self):
        for key in ("SipPort", "GatewayPort"):
            for port in (0, -1, 65536, True, False, 5060.0, "5060", None):
                with self.subTest(key=key, port=port):
                    value = minimal_deployment()
                    value[key] = port
                    self.assert_invalid(value)
            for port in (1, 65535):
                with self.subTest(key=key, accepted_port=port):
                    value = minimal_deployment()
                    value[key] = port
                    self.assertEqual(port, normalize_deployment_v2(value)[key])

    def test_explicit_endpoints_reject_public_multicast_hostnames_and_unspecified(self):
        for key in ("ServerIp", "GatewayIp", "GatewayBindAddress"):
            for address in ("8.8.8.8", "224.0.0.1", "0.0.0.0", "example.invalid", "127.0.0.2\n", None, True):
                with self.subTest(key=key, address=address):
                    value = minimal_deployment()
                    value[key] = address
                    self.assert_invalid(value)
        for address in ("8.8.8.8", "224.0.0.1", "example.invalid"):
            value = minimal_deployment()
            value["BindAddress"] = address
            self.assert_invalid(value)

    def test_ipv6_private_endpoints_are_supported_without_silent_stack_conversion(self):
        value = full_deployment()
        value.update(ServerIp="fd00:77::2", BindAddress="fd00:77::2", GatewayIp="fd00:77::3",
                     GatewayBindAddress="fd00:77::3", TrustedNetworks=["fd00:77::3/128"],
                     GatewayTrustedNetworks=["fd00:77::2/128"])
        self.assertEqual(value, normalize_deployment_v2(value))
        for key in ("ServerIp", "BindAddress", "GatewayIp", "GatewayBindAddress"):
            with self.subTest(key=key):
                # No configured peer or CIDRs: only the endpoint-family rule
                # can reject this otherwise valid mixed-family configuration.
                mixed = minimal_deployment()
                mixed.update(ServerIp="fd00:77::2", BindAddress="fd00:77::2",
                             GatewayIp="fd00:77::3", GatewayBindAddress="fd00:77::3")
                mixed[key] = "127.0.0.2"
                self.assert_invalid(mixed)

    def test_pbx_wildcard_requires_explicit_trusted_cidrs(self):
        value = minimal_deployment()
        value["BindAddress"] = "0.0.0.0"
        self.assert_invalid(value)
        value["TrustedNetworks"] = ["127.0.0.3/32"]
        self.assertEqual("0.0.0.0", normalize_deployment_v2(value)["BindAddress"])

    def test_gateway_wildcard_is_never_enabled_by_trusted_networks(self):
        for address in ("0.0.0.0", "::"):
            with self.subTest(address=address):
                value = minimal_deployment()
                value.update(TrustedNetworks=["127.0.0.3/32"], GatewayTrustedNetworks=["127.0.0.2/32"])
                if address == "::":
                    value.update(ServerIp="fd00:77::2", BindAddress="fd00:77::2", GatewayIp="fd00:77::3",
                                 GatewayBindAddress="fd00:77::3", TrustedNetworks=["fd00:77::3/128"],
                                 GatewayTrustedNetworks=["fd00:77::2/128"])
                value["GatewayBindAddress"] = address
                self.assert_invalid(value)

    def test_trusted_networks_are_strict_private_same_stack_cidrs(self):
        invalid = ["0.0.0.0/0", "8.8.8.0/24", "224.0.0.0/24", "127.0.0.3/24",
                   "127.0.0.2", "::1/128", "127.0.0.3/32\n", None, True]
        for key in ("TrustedNetworks", "GatewayTrustedNetworks"):
            for network in invalid:
                with self.subTest(key=key, network=network):
                    # Blank peer avoids missing-opposing-endpoint coverage
                    # masking the CIDR's own format/family validation.
                    value = minimal_deployment()
                    value[key] = [network]
                    self.assert_invalid(value)
            for count in (32, 33):
                with self.subTest(key=key, count=count):
                    value = full_deployment()
                    networks = ["127.0.0." + str(index) + "/32" for index in range(1, count + 1)]
                    value[key] = networks
                    # Distinct canonical /32s include both fixed endpoints;
                    # neither duplicates nor coverage can mask the size limit.
                    if count == 32:
                        self.assertEqual(networks, normalize_deployment_v2(value)[key])
                    else:
                        self.assert_invalid(value)

    def test_configured_peer_requires_opposing_endpoint_in_each_trust_list(self):
        for key in ("TrustedNetworks", "GatewayTrustedNetworks"):
            for networks in ([], ["127.0.0.9/32"]):
                with self.subTest(key=key, networks=networks):
                    value = full_deployment()
                    value[key] = networks
                    self.assert_invalid(value)

    def test_peer_cannot_target_its_own_address_and_port(self):
        for enabled in (False, True):
            with self.subTest(peer_enabled=enabled):
                value = full_deployment()
                value["Peer"]["Enabled"] = enabled
                value.update(GatewayIp="127.0.0.2", GatewayBindAddress="127.0.0.2", GatewayPort=5060,
                             TrustedNetworks=["127.0.0.2/32"])
                self.assert_invalid(value)
        for platform in ("vocat-go", "vowin-windows"):
            value = minimal_deployment(platform)
            value.update(GatewayIp="127.0.0.2", GatewayBindAddress="127.0.0.2", GatewayPort=5060)
            result = normalize_deployment_v2(value)
            self.assertFalse(result["Peer"]["Enabled"])

    def test_realms_and_digest_algorithms_are_explicit_and_not_auto_learned(self):
        for key in ("PbxRealm", "GatewayRealm"):
            for realm in ("", "*", " ", "bad\r\nrealm", "${SHELL(id)}", None):
                with self.subTest(key=key, realm=realm):
                    value = minimal_deployment()
                    value[key] = realm
                    self.assert_invalid(value)
        for key in ("PbxAlgorithm", "GatewayAlgorithm"):
            for algorithm in ("", "sha-256", "SHA-512", "AUTO", ["SHA-256"], None):
                with self.subTest(key=key, algorithm=algorithm):
                    value = minimal_deployment()
                    value[key] = algorithm
                    self.assert_invalid(value)
            value = minimal_deployment()
            value[key] = "MD5"
            self.assertEqual("MD5", normalize_deployment_v2(value)[key])

    def test_realm_role_and_platform_byte_boundaries_preserve_original_case(self):
        limits = [("vocat-go", "GatewayRealm", 64), ("vocat-go", "PbxRealm", 128),
                  ("vowin-windows", "GatewayRealm", 253), ("vowin-windows", "PbxRealm", 253)]
        for platform, key, limit in limits:
            with self.subTest(platform=platform, key=key, limit=limit):
                value = minimal_deployment(platform)
                realm = "Realm" + "a" * (limit - 5)
                value[key] = realm
                self.assertEqual(limit, len(realm.encode("ascii")))
                self.assertEqual(realm, normalize_deployment_v2(value)[key])
                self.assertEqual(realm, parse_deployment_v2(encoded(value))[key])
                value[key] = realm + "A"
                self.assert_invalid(value)

    def test_any_peer_activation_grant_or_partial_auth_requires_both_roles(self):
        def trusted_minimal():
            value = minimal_deployment()
            value.update(TrustedNetworks=["127.0.0.3/32"], GatewayTrustedNetworks=["127.0.0.2/32"])
            return value

        activations = ["Enabled", "AllowCallsFromPeer", "AllowCallsToPeer", "AllowSmsFromPeer", "AllowSmsToPeer"]
        for flag in activations:
            with self.subTest(flag=flag):
                value = trusted_minimal()
                value["Peer"] = {flag: True}
                self.assert_invalid(value)
        for role in ("InboundAuth", "OutboundAuth"):
            for auth in ({"Username": "synthetic-peer"}, {"SecretRef": "synthetic-peer-ref"},
                         {"Username": "synthetic-peer", "SecretRef": "synthetic-peer-ref"}):
                with self.subTest(role=role, auth_keys=sorted(auth)):
                    value = trusted_minimal()
                    value["Peer"] = {role: auth}
                    self.assert_invalid(value)
        for grant in ("OutgoingLines", "SmsSendLines"):
            with self.subTest(grant=grant):
                value = trusted_minimal()
                value["Lines"] = [{"Id": "synthetic-a"}]
                value["Peer"] = {grant: ["synthetic-a"]}
                self.assert_invalid(value)
        for role in ("InboundAuth", "OutboundAuth"):
            for key in ("Username", "SecretRef"):
                value = full_deployment()
                value["Peer"][role][key] = ""
                self.assert_invalid(value)

    def test_empty_peer_and_empty_auth_objects_are_disabled_not_legacy_fallback(self):
        for peer in ({}, {"Enabled": False}, {"InboundAuth": {}, "OutboundAuth": {}}):
            with self.subTest(peer=peer):
                value = minimal_deployment()
                value["Peer"] = peer
                result = normalize_deployment_v2(value)
                self.assertFalse(result["Peer"]["Enabled"])
                self.assertEqual({"Username": "", "SecretRef": ""}, result["Peer"]["InboundAuth"])
                self.assertEqual({"Username": "", "SecretRef": ""}, result["Peer"]["OutboundAuth"])

    def test_role_secret_references_cannot_alias_across_peer_pbx_or_local_accounts(self):
        collisions = [
            (("Peer", "OutboundAuth", "SecretRef"), "PEER-IN-REF"),
            (("ExtensionOptions", "2101", "SecretRef"), "peer-out-ref"),
            (("ExtensionOptions", "2102", "SecretRef"), "PBX-FIRST-REF"),
            (("GatewayLocalRegistrar", "Accounts", 0, "SecretRef"), "peer-in-ref"),
            (("GatewayLocalRegistrar", "Accounts", 0, "SecretRef"), "PBX-FIRST-REF"),
            (("GatewayLocalRegistrar", "Accounts", 1, "SecretRef"), "LOCAL-FIRST-REF"),
        ]
        for path, ref in collisions:
            with self.subTest(path=path):
                value = full_deployment()
                assign(value, path, ref)
                self.assert_invalid(value)

    def test_secret_refs_are_mandatory_safe_references_not_inline_password_fields(self):
        paths = [("Peer", "InboundAuth", "SecretRef"), ("Peer", "OutboundAuth", "SecretRef"),
                 ("ExtensionOptions", "2101", "SecretRef"),
                 ("GatewayLocalRegistrar", "Accounts", 0, "SecretRef")]
        for path in paths:
            for ref in ("", "../secret", "${SHELL(id)}", "bad\nref", None, True):
                with self.subTest(path=path, ref=ref):
                    value = full_deployment()
                    assign(value, path, ref)
                    self.assert_invalid(value)

    def test_pbx_account_membership_and_numeric_length_are_enforced(self):
        for username in ("1", "123456789", "desk", "+2101", "21\n01"):
            with self.subTest(username=username):
                value = minimal_deployment()
                value.update(Extensions=[username], ExtensionOptions={username: {"SecretRef": "pbx-first-ref"}})
                self.assert_invalid(value)
        for username in ("21", "12345678"):
            with self.subTest(valid_boundary_username=username):
                value = minimal_deployment()
                value.update(Extensions=[username], ExtensionOptions={username: {"SecretRef": "pbx-first-ref"}})
                result = normalize_deployment_v2(value)
                self.assertEqual([username], result["Extensions"])
                self.assertEqual({username}, set(result["ExtensionOptions"]))
        # Non-string account items are a separate type control: JSON object
        # property names cannot be made non-string to match those invalid items.
        for username in (True, 2101, None):
            with self.subTest(nonstring_username_type=type(username).__name__):
                value = minimal_deployment()
                value.update(Extensions=[username], ExtensionOptions={})
                self.assert_invalid(value)
        for mutation in ("missing-options", "extra-options", "missing-ref", "duplicate-account"):
            with self.subTest(mutation=mutation):
                value = full_deployment()
                if mutation == "missing-options":
                    del value["ExtensionOptions"]["2101"]
                elif mutation == "extra-options":
                    value["ExtensionOptions"]["2199"] = {"SecretRef": "unknown-account-ref"}
                elif mutation == "missing-ref":
                    del value["ExtensionOptions"]["2101"]["SecretRef"]
                else:
                    value["Extensions"][1] = "2101"
                    del value["ExtensionOptions"]["2102"]
                self.assert_invalid(value)

    def test_pbx_account_permissions_default_off_including_mobile_sms(self):
        value = minimal_deployment()
        value.update(Extensions=["2101"], ExtensionOptions={"2101": {"SecretRef": "pbx-first-ref"}})
        result = normalize_deployment_v2(value)
        self.assertEqual({"SecretRef": "pbx-first-ref", "Name": "", "Enabled": False,
                          "Ring": False, "MaxContacts": 4, "AllowOutgoing": False,
                          "AllowMessages": False, "AllowMobileSmsSend": False,
                          "AllowMobileSmsReceive": False}, result["ExtensionOptions"]["2101"])

    def test_declared_local_accounts_and_lines_do_not_infer_permissions_or_targets(self):
        value = minimal_deployment()
        value["GatewayLocalRegistrar"] = {"Accounts": [{"Username": "desk-main", "SecretRef": "local-first-ref"}]}
        value["Lines"] = [{"Id": "synthetic-a"}]
        result = normalize_deployment_v2(value)
        self.assertEqual({"Enabled": False, "PrimaryUsername": "", "Accounts": [
            {"Username": "desk-main", "DisplayName": "", "SecretRef": "local-first-ref",
             "AllowCalls": False, "AllowMessages": False, "OutgoingLines": [], "SmsSendLines": []}
        ]}, result["GatewayLocalRegistrar"])
        self.assertEqual([{
            "Id": "synthetic-a", "Label": "", "Prefix": "", "Default": False,
            "CountryCode": "", "NationalNumberLength": 0,
            "AllowIncomingCalls": False, "AllowOutgoingCalls": False,
            "AllowSmsSend": False, "AllowSmsReceive": False,
            "CallTarget": "", "SmsTarget": "", "LocalIncomingExtensions": [], "LocalSmsRecipients": [],
        }], result["Lines"])

    def test_gateway_registrar_requires_accounts_and_windows_primary_membership(self):
        go_empty = minimal_deployment("vocat-go")
        go_empty["GatewayLocalRegistrar"] = {"Enabled": True, "Accounts": []}
        self.assert_invalid(go_empty)
        value = full_deployment()
        value["GatewayLocalRegistrar"]["Accounts"] = []
        self.assert_invalid(value)
        for primary in ("", "missing-local", None):
            with self.subTest(primary=primary):
                value = full_deployment()
                value["GatewayLocalRegistrar"]["PrimaryUsername"] = primary
                self.assert_invalid(value)
        go = full_deployment("vocat-go")
        go["GatewayLocalRegistrar"]["PrimaryUsername"] = ""
        self.assertEqual("", normalize_deployment_v2(go)["GatewayLocalRegistrar"]["PrimaryUsername"])

    def test_go_local_accounts_are_numeric_but_windows_can_use_safe_identifiers(self):
        self.assertEqual("desk-main", normalize_deployment_v2(full_deployment())["GatewayLocalRegistrar"]["PrimaryUsername"])
        for username in ("1", "1234567890123", "desk-main", "+3101"):
            with self.subTest(username=username):
                value = minimal_deployment("vocat-go")
                value["GatewayLocalRegistrar"] = {"Accounts": [{"Username": username, "SecretRef": "local-first-ref"}]}
                self.assert_invalid(value)
        for username in ("31", "123456789012"):
            with self.subTest(valid_boundary_username=username):
                value = minimal_deployment("vocat-go")
                value["GatewayLocalRegistrar"] = {"Accounts": [{"Username": username, "SecretRef": "local-first-ref"}]}
                result = normalize_deployment_v2(value)
                self.assertEqual(username, result["GatewayLocalRegistrar"]["Accounts"][0]["Username"])
                self.assertFalse(result["GatewayLocalRegistrar"]["Enabled"])

    def test_local_identities_and_account_target_membership_are_casefold_checked(self):
        value = full_deployment()
        value["GatewayLocalRegistrar"]["Accounts"][1]["Username"] = "DESK-MAIN"
        value["Lines"][0]["LocalSmsRecipients"] = ["DESK-MAIN"]
        self.assert_invalid(value)
        for key in ("LocalIncomingExtensions", "LocalSmsRecipients"):
            for targets in (["unknown-local"], ["desk-main", "desk-main"], ["desk-main", "DESK-MAIN"]):
                with self.subTest(key=key, targets=targets):
                    value = full_deployment()
                    value["Lines"][0][key] = targets
                    self.assert_invalid(value)

    def test_line_grants_are_explicit_existing_members_not_default_fallback(self):
        paths = [("Peer", "OutgoingLines"), ("Peer", "SmsSendLines"),
                 ("GatewayLocalRegistrar", "Accounts", 0, "OutgoingLines"),
                 ("GatewayLocalRegistrar", "Accounts", 0, "SmsSendLines")]
        for path in paths:
            for lines in (["missing-line"], ["synthetic-a", "synthetic-a"],
                          ["synthetic-a", "SYNTHETIC-A"], "synthetic-a"):
                with self.subTest(path=path, lines=lines):
                    value = full_deployment()
                    assign(value, path, lines)
                    self.assert_invalid(value)

    def test_peer_and_local_call_and_sms_line_policies_remain_four_independent_lists(self):
        value = full_deployment()
        for line_id, prefix in (("synthetic-b", "82"), ("synthetic-c", "83"), ("synthetic-d", "84")):
            line = deepcopy(value["Lines"][0])
            line.update(Id=line_id, Prefix=prefix)
            value["Lines"].append(line)
        value["Peer"]["OutgoingLines"] = ["synthetic-a"]
        value["Peer"]["SmsSendLines"] = ["synthetic-b"]
        value["GatewayLocalRegistrar"]["Accounts"][0]["OutgoingLines"] = ["synthetic-c"]
        value["GatewayLocalRegistrar"]["Accounts"][0]["SmsSendLines"] = ["synthetic-d"]
        result = normalize_deployment_v2(value)
        self.assertEqual(value, result)
        self.assertEqual(["synthetic-a"], result["Peer"]["OutgoingLines"])
        self.assertEqual(["synthetic-b"], result["Peer"]["SmsSendLines"])
        self.assertEqual(["synthetic-c"], result["GatewayLocalRegistrar"]["Accounts"][0]["OutgoingLines"])
        self.assertEqual(["synthetic-d"], result["GatewayLocalRegistrar"]["Accounts"][0]["SmsSendLines"])

    def test_lines_have_unique_casefold_ids_and_service_targets_are_not_account_ids(self):
        value = full_deployment()
        self.assertEqual("ring-service", normalize_deployment_v2(value)["Lines"][0]["CallTarget"])
        duplicate = deepcopy(value["Lines"][0])
        duplicate.update(Id="SYNTHETIC-A", Prefix="82")
        value["Lines"].append(duplicate)
        self.assert_invalid(value)
        for key in ("CallTarget", "SmsTarget"):
            for target in ("bad\r\ntarget", "${SHELL(id)}", "x" * 65):
                with self.subTest(key=key):
                    value = full_deployment()
                    value["Lines"][0][key] = target
                    self.assert_invalid(value)

    def test_go_mt_recipient_budget_is_sixteen_total_with_optional_peer_target(self):
        def receivers(platform):
            value = full_deployment(platform)
            template = value["GatewayLocalRegistrar"]["Accounts"][0]
            accounts = []
            for index in range(16):
                account = deepcopy(template)
                account.update(Username=str(4100 + index), SecretRef="receiver-" + str(index) + "-ref")
                accounts.append(account)
            value["GatewayLocalRegistrar"]["Accounts"] = accounts
            value["GatewayLocalRegistrar"]["PrimaryUsername"] = "4100"
            value["Lines"][0]["LocalIncomingExtensions"] = ["4100"]
            value["Lines"][0]["LocalSmsRecipients"] = [str(4100 + index) for index in range(16)]
            return value

        go = receivers("vocat-go")
        go["Lines"][0]["SmsTarget"] = ""
        self.assertEqual([str(4100 + index) for index in range(16)],
                         normalize_deployment_v2(go)["Lines"][0]["LocalSmsRecipients"])
        go["Lines"][0]["SmsTarget"] = "peer-service"
        self.assert_invalid(go)
        disabled = deepcopy(go)
        disabled["Peer"] = {}
        for feature in ("CallsEnabled", "CarrierEnabled", "MessagesEnabled", "SmsEnabled"):
            disabled[feature] = False
        self.assert_invalid(disabled)
        go["Lines"][0]["LocalSmsRecipients"] = [str(4100 + index) for index in range(15)]
        self.assertEqual([str(4100 + index) for index in range(15)],
                         normalize_deployment_v2(go)["Lines"][0]["LocalSmsRecipients"])
        windows = receivers("vowin-windows")
        windows["Lines"][0]["SmsTarget"] = "peer-service"
        self.assertEqual([str(4100 + index) for index in range(16)],
                         normalize_deployment_v2(windows)["Lines"][0]["LocalSmsRecipients"])

    def test_go_rules_require_explicit_line_prefix_and_never_choose_first_line(self):
        for key in ("DialRules", "SmsDialRules"):
            with self.subTest(rule_kind=key):
                value = full_deployment("vocat-go")
                value["Lines"][0]["Prefix"] = ""
                value["DialRules"] = []
                value["SmsDialRules"] = []
                value[key] = [{"Pattern": "_81X.", "LineId": "synthetic-a", "Strip": 2, "Prepend": "+"}]
                self.assert_invalid(value)
        for key in ("DialRules", "SmsDialRules"):
            value = full_deployment()
            value[key][0]["LineId"] = "missing-line"
            self.assert_invalid(value)

    def test_call_and_sms_rules_are_independent_and_can_share_a_pattern(self):
        value = full_deployment()
        value["DialRules"][0]["Prepend"] = "+63"
        value["SmsDialRules"][0]["Prepend"] = "+31"
        result = normalize_deployment_v2(value)
        self.assertEqual("+63", result["DialRules"][0]["Prepend"])
        self.assertEqual("+31", result["SmsDialRules"][0]["Prepend"])
        self.assertEqual("_81X.", result["DialRules"][0]["Pattern"])
        self.assertEqual("_81X.", result["SmsDialRules"][0]["Pattern"])
        for key in ("DialRules", "SmsDialRules"):
            with self.subTest(rule_kind=key):
                duplicate = deepcopy(value)
                duplicate[key].append(deepcopy(duplicate[key][0]))
                self.assert_invalid(duplicate)

    def test_only_one_rule_kind_or_internal_message_feature_does_not_enable_the_other(self):
        for retained, omitted in (("DialRules", "SmsDialRules"), ("SmsDialRules", "DialRules")):
            with self.subTest(retained=retained):
                value = full_deployment()
                value[omitted] = []
                result = normalize_deployment_v2(value)
                self.assertEqual([], result[omitted])
                self.assertEqual([{"Pattern": "_81X.", "LineId": "synthetic-a", "Strip": 2, "Prepend": "+"}], result[retained])
        value = minimal_deployment()
        value["MessagesEnabled"] = True
        result = normalize_deployment_v2(value)
        self.assertTrue(result["MessagesEnabled"])
        for key in ("CallsEnabled", "CarrierEnabled", "SmsEnabled"):
            self.assertFalse(result[key])

    def test_rule_fields_cannot_inject_dialplan_or_enable_unrestricted_patterns(self):
        invalid = [
            ("Pattern", "_."), ("Pattern", "_X."), ("Pattern", "_!"),
            ("Pattern", "_81X.\ninclude=evil"), ("Pattern", "${SHELL(id)}"),
            ("Strip", True), ("Strip", -1), ("Strip", 21), ("Strip", "2"),
            ("Prepend", "${SHELL(id)}"), ("Prepend", "+1\nmalicious"), ("Prepend", "+" + "1" * 13),
        ]
        for rules in ("DialRules", "SmsDialRules"):
            for field, item in invalid:
                with self.subTest(rules=rules, field=field, item=item):
                    value = full_deployment()
                    value[rules][0][field] = item
                    self.assert_invalid(value)

    def test_digit_classes_cannot_disguise_a_single_unrestricted_numeric_token(self):
        rejected = ["_[0-9].", "_[0123456789].", "_[01-89].",
                    "_[0-9]", "_[0123456789]", "_[01-89]", "_[9-0].", "_[9-0]"]
        accepted = ["_[1-9].", "_81[0-9].", "_81X."]
        for rules in ("DialRules", "SmsDialRules"):
            for pattern in rejected:
                with self.subTest(rules=rules, rejected_pattern=pattern):
                    value = full_deployment()
                    value[rules][0]["Pattern"] = pattern
                    self.assert_invalid(value)
            for pattern in accepted:
                with self.subTest(rules=rules, accepted_pattern=pattern):
                    value = full_deployment()
                    value[rules][0]["Pattern"] = pattern
                    self.assertEqual(value, normalize_deployment_v2(value))
                    self.assertEqual(pattern, parse_deployment_v2(encoded(value))[rules][0]["Pattern"])

    def test_country_code_and_national_length_are_a_consistent_explicit_pair(self):
        value = full_deployment()
        value["Lines"][0].update(CountryCode="63", NationalNumberLength=10)
        self.assertEqual("63", normalize_deployment_v2(value)["Lines"][0]["CountryCode"])
        for code, length in (("", 10), ("63", 0), ("+63", 10), ("0", 10), ("1234", 10),
                             ("63", 14), ("63", True), ("63", "10")):
            with self.subTest(code=code, length=length):
                value = full_deployment()
                value["Lines"][0].update(CountryCode=code, NationalNumberLength=length)
                self.assert_invalid(value)

    def test_display_and_routing_text_cannot_inject_generated_configuration(self):
        paths = [("ExtensionOptions", "2101", "Name"),
                 ("GatewayLocalRegistrar", "Accounts", 0, "DisplayName"),
                 ("Lines", 0, "Label"), ("Lines", 0, "Id"), ("Lines", 0, "Prefix"),
                 ("Peer", "InboundAuth", "Username"), ("Peer", "OutboundAuth", "Username"),
                 ("MessageDomain",)]
        for path in paths:
            for injection in ("x\r\nmalicious=1", "${SHELL(id)}", "x\x00y"):
                with self.subTest(path=path, injection_kind="control" if "\x00" in injection else "dialplan"):
                    if path in (("Lines", 0, "Id"), ("Lines", 0, "Prefix")):
                        value = minimal_deployment()
                        value["Lines"] = [{"Id": "synthetic-a"}]
                    else:
                        value = full_deployment()
                    assign(value, path, injection)
                    self.assert_invalid(value)


if __name__ == "__main__":
    unittest.main()
