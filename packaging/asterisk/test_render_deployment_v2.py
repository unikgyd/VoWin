"""Independent offline tests for the V2 deployment renderer.

Every endpoint, identity and secret below is synthetic. The public render API
returns text only: these tests never start Asterisk, a listener, a process, a
modem or a paid operation. They intentionally do not reuse the old generator's
shared-password projection or derive expected policy from a renderer helper.
"""
from contextlib import ExitStack
from copy import deepcopy
import json
import re
import secrets  # Import before the no-I/O mocks are installed.
import socket
import subprocess
import unittest
from unittest.mock import patch

from deployment_v2 import (
    DeploymentV2Error, deployment_json_text, normalize_deployment_v2, parse_deployment_secrets,
)
from render_deployment_v2 import render_deployment_v2


OUTPUTS = {
    "config/asterisk.conf", "config/modules.conf", "config/logger.conf",
    "config/rtp.conf", "config/http.conf", "config/manager.conf",
    "config/pjsip.conf", "config/extensions.conf", "gateway-client.json",
    "deployment.normalized.json", "sip-guard.nft",
}
SENTINEL = "synthetic-never-echo-secret-value"


def deployment(platform="vowin-windows"):
    main, extra = ("3101", "3102") if platform == "vocat-go" else ("desk-main", "desk-extra")
    return {
        "SchemaVersion": 2, "GatewayPlatform": platform,
        "ServerIp": "127.70.0.2", "BindAddress": "127.70.0.2", "SipPort": 5090,
        "MessageDomain": "messages.example.invalid", "PbxRealm": "pbx.realm.invalid",
        "PbxAlgorithm": "SHA-256", "GatewayIp": "127.70.0.3",
        "GatewayBindAddress": "127.70.0.3", "GatewayPort": 5092,
        "GatewayRealm": "gateway.realm.invalid", "GatewayAlgorithm": "SHA-256",
        "TrustedNetworks": ["127.70.0.3/32"],
        "GatewayTrustedNetworks": ["127.70.0.2/32"],
        "CallsEnabled": True, "CarrierEnabled": True,
        "MessagesEnabled": True, "SmsEnabled": True,
        "Peer": {
            "Enabled": True,
            "InboundAuth": {"Username": "pbx-to-gateway", "SecretRef": "peer-in-role"},
            "OutboundAuth": {"Username": "gateway-to-pbx", "SecretRef": "peer-out-role"},
            "AllowCallsFromPeer": True, "AllowCallsToPeer": False,
            "AllowSmsFromPeer": False, "AllowSmsToPeer": True,
            "OutgoingLines": ["synthetic-a"], "SmsSendLines": ["synthetic-b"],
        },
        "Extensions": ["2101", "2102", "2103"],
        "ExtensionOptions": {
            "2101": {
                "SecretRef": "pbx-one-role", "Name": "Synthetic PBX first",
                "Enabled": True, "Ring": True, "MaxContacts": 2,
                "AllowOutgoing": True, "AllowMessages": True,
                "AllowMobileSmsSend": True, "AllowMobileSmsReceive": False,
            },
            "2102": {
                "SecretRef": "pbx-two-role", "Name": "Synthetic PBX second",
                "Enabled": True, "Ring": False, "MaxContacts": 3,
                "AllowOutgoing": False, "AllowMessages": False,
                "AllowMobileSmsSend": False, "AllowMobileSmsReceive": True,
            },
            "2103": {
                "SecretRef": "pbx-disabled-role", "Name": "Synthetic disabled PBX",
                "Enabled": False, "Ring": True, "MaxContacts": 4,
                "AllowOutgoing": True, "AllowMessages": True,
                "AllowMobileSmsSend": True, "AllowMobileSmsReceive": True,
            },
        },
        "GatewayLocalRegistrar": {
            "Enabled": True, "PrimaryUsername": main,
            "Accounts": [
                {"Username": main, "SecretRef": "local-main-role", "DisplayName": "",
                 "AllowCalls": False, "AllowMessages": True,
                 "OutgoingLines": ["synthetic-a"], "SmsSendLines": ["synthetic-b"]},
                {"Username": extra, "SecretRef": "local-extra-role", "DisplayName": "",
                 "AllowCalls": True, "AllowMessages": False,
                 "OutgoingLines": ["synthetic-b"], "SmsSendLines": []},
            ],
        },
        "Lines": [
            {"Id": "synthetic-a", "Label": "Synthetic first line", "Prefix": "81",
             "Default": True, "CountryCode": "86", "NationalNumberLength": 11,
             "AllowIncomingCalls": True, "AllowOutgoingCalls": True,
             "AllowSmsSend": False, "AllowSmsReceive": True,
             "CallTarget": "ring-a", "SmsTarget": "sms-a",
             "LocalIncomingExtensions": [main], "LocalSmsRecipients": [extra]},
            {"Id": "synthetic-b", "Label": "Synthetic second line", "Prefix": "82",
             "Default": False, "CountryCode": "63", "NationalNumberLength": 10,
             "AllowIncomingCalls": False, "AllowOutgoingCalls": False,
             "AllowSmsSend": True, "AllowSmsReceive": False,
             "CallTarget": "ring-b", "SmsTarget": "sms-b",
             "LocalIncomingExtensions": [extra], "LocalSmsRecipients": [main]},
        ],
        "DialRules": [{"Pattern": "_72X.", "LineId": "synthetic-a", "Strip": 2, "Prepend": "+"}],
        "SmsDialRules": [{"Pattern": "_93X.", "LineId": "synthetic-b", "Strip": 2, "Prepend": "+"}],
    }


def role_secrets():
    return {
        "peer-in-role": "synthetic-peer-in-password",
        "peer-out-role": "synthetic-peer-out-password",
        "pbx-one-role": "synthetic-pbx-one-password",
        "pbx-two-role": "synthetic-pbx-two-password",
        "pbx-disabled-role": "synthetic-pbx-disabled-password",
        "local-main-role": "synthetic-local-main-password",
        "local-extra-role": "synthetic-local-extra-password",
    }


def ini_sections(text):
    """Keep duplicate Asterisk section names, rather than overwrite an AOR."""
    result = []
    for match in re.finditer(r"(?m)^\[([^]\r\n]+)\](?:\([^\r\n]*\))?\s*$", text):
        start = match.end()
        next_section = re.search(r"(?m)^\[", text[start:])
        body = text[start:start + next_section.start()] if next_section else text[start:]
        fields = {}
        for line in body.splitlines():
            if "=" in line and not line.lstrip().startswith((";", "#")):
                key, value = line.split("=", 1)
                fields[key.strip()] = value.strip()
        result.append((match.group(1), fields, body))
    return result


def extension_routes(text, pattern):
    """Read exact extension bodies, without treating Asterisk '[' expressions as sections."""
    result = []
    context = ""
    current_pattern = None
    body = []
    for line in text.splitlines():
        section = re.fullmatch(r"\[([A-Za-z0-9_.-]+)\]", line.strip())
        extension = re.match(r"\s*exten\s*=>\s*([^,]+),", line)
        if section or extension:
            if current_pattern == pattern:
                result.append((context, "\n".join(body)))
            body = []
            current_pattern = None
            if section:
                context = section.group(1)
            else:
                current_pattern = extension.group(1).strip()
        if current_pattern is not None:
            body.append(line)
    if current_pattern == pattern:
        result.append((context, "\n".join(body)))
    return result


class RenderDeploymentV2Tests(unittest.TestCase):
    def bundle(self, platform="vowin-windows", value=None, secrets=None):
        return render_deployment_v2(value if value is not None else deployment(platform),
                                    role_secrets() if secrets is None else secrets)

    def assert_invalid(self, value, secrets=None):
        original = deepcopy(value)
        private = role_secrets() if secrets is None else secrets
        before_secrets = deepcopy(private)
        with self.assertRaises(DeploymentV2Error) as caught:
            render_deployment_v2(value, private)
        self.assertEqual(original, value, "rejected model must not be mutated")
        self.assertEqual(before_secrets, private, "rejected secrets must not be mutated")
        self.assertNotIn(SENTINEL, str(caught.exception))
        return caught.exception

    def test_exact_complete_bundle_is_utf8_text_and_public_manifest_is_normalized(self):
        for platform in ("vowin-windows", "vocat-go"):
            with self.subTest(platform=platform):
                value = deployment(platform)
                files = self.bundle(value=value)
                self.assertEqual(OUTPUTS, set(files))
                for name, text in files.items():
                    with self.subTest(platform=platform, artifact=name):
                        self.assertIsInstance(text, str)
                        self.assertNotIn("\x00", text)
                        self.assertFalse(name.startswith(("/", "\\")))
                        self.assertNotIn("..", name.split("/"))
                        text.encode("utf-8", errors="strict")
                self.assertEqual(normalize_deployment_v2(value), json.loads(files["deployment.normalized.json"]))
                self.assertLessEqual(len(files["gateway-client.json"].encode("utf-8")), 65536)
                for name in ("gateway-client.json", "deployment.normalized.json"):
                    self.assertEqual(json.dumps(json.loads(files[name]), ensure_ascii=False, separators=(",", ":")) + "\n",
                                     files[name], "JSON artifacts must use compact UTF-8 plus one newline")

    def test_rendering_is_deterministic_and_never_mutates_or_reuses_input_containers(self):
        for platform in ("vowin-windows", "vocat-go"):
            with self.subTest(platform=platform):
                value, private = deployment(platform), role_secrets()
                original, original_private = deepcopy(value), deepcopy(private)
                first = render_deployment_v2(value, private)
                second = render_deployment_v2(value, private)
                self.assertEqual(first, second)
                self.assertEqual(original, value)
                self.assertEqual(original_private, private)
                first["gateway-client.json"] = "caller-owned-result"
                self.assertEqual(second, render_deployment_v2(value, private))

    def test_pure_rendering_does_not_read_write_generate_secrets_or_contact_any_service(self):
        forbidden = ["builtins.open", "os.open", "socket.socket", "socket.create_connection",
                     "subprocess.run", "subprocess.Popen", "os.system", "secrets.token_hex",
                     "secrets.token_urlsafe"]
        for platform in ("vowin-windows", "vocat-go"):
            with self.subTest(platform=platform), ExitStack() as stack:
                mocks = [stack.enter_context(patch(name, side_effect=AssertionError("pure renderer attempted I/O")))
                         for name in forbidden]
                files = self.bundle(platform)
                self.assertEqual(OUTPUTS, set(files))
                for mock in mocks:
                    mock.assert_not_called()

    def test_secrets_are_role_references_not_usernames_and_all_declared_refs_are_required(self):
        for platform in ("vowin-windows", "vocat-go"):
            for missing in role_secrets():
                with self.subTest(platform=platform, missing=missing):
                    private = role_secrets()
                    del private[missing]
                    self.assert_invalid(deployment(platform), private)
            with self.subTest(platform=platform, substitution="username-is-not-ref"):
                private = role_secrets()
                private["2101"] = private.pop("pbx-one-role")
                self.assert_invalid(deployment(platform), private)

    def test_unknown_casefold_duplicate_or_extra_secret_keys_are_not_accepted(self):
        for private in (dict(role_secrets(), **{SENTINEL: "synthetic-extra-password"}),
                        dict(role_secrets(), **{"PEER-IN-ROLE": "synthetic-casefold-password"}),
                        {key.upper(): value for key, value in role_secrets().items()}):
            with self.subTest(keys=list(private)):
                error = self.assert_invalid(deployment(), private)
                self.assertNotIn(SENTINEL, str(error))

    def test_secret_container_and_values_are_strict_not_coerced_or_generated(self):
        for private in (None, [], "synthetic-not-a-map", {"peer-in-role": "x"}):
            with self.subTest(container=type(private).__name__):
                # None is deliberately sent to the API, not our fixture helper.
                with self.assertRaises(DeploymentV2Error):
                    render_deployment_v2(deployment(), private)
        for case, secret in enumerate(("x" * 11, "x" * 129, "synthetic\npassword", "synthetic password",
                                       "synthetic;password", "synthetic\u4e2dpassword", "synthetic\x00password", 123, True, None)):
            with self.subTest(case=case, secret_type=type(secret).__name__, length=len(secret) if isinstance(secret, str) else None):
                private = role_secrets()
                private["peer-in-role"] = secret
                self.assert_invalid(deployment(), private)

    def test_safe_ascii_secret_boundaries_and_equal_values_keep_independent_roles(self):
        for length in (12, 128):
            with self.subTest(length=length):
                private = {key: "a" * length for key in role_secrets()}
                files = self.bundle(secrets=private)
                profile = json.loads(files["gateway-client.json"])
                self.assertEqual("a" * length, profile["Credentials"]["PeerInboundPassword"])
                self.assertEqual("a" * length, profile["Credentials"]["PeerOutboundPassword"])
                self.assertEqual("a" * length, profile["Credentials"]["MainPassword"])

    def test_private_json_parser_checks_exact_byte_limit_and_never_accepts_duplicate_roles(self):
        raw = json.dumps(role_secrets(), separators=(",", ":")).encode("utf-8")
        for total in (65535, 65536):
            with self.subTest(total_bytes=total):
                self.assertEqual(role_secrets(), parse_deployment_secrets(raw + b" " * (total - len(raw)), deployment()))
        duplicate = b'{"peer-in-role":"synthetic-first-password","\\u0070eer-in-role":"synthetic-second-password"}'
        casefold_duplicate = b'{"peer-in-role":"synthetic-first-password","PEER-IN-ROLE":"synthetic-second-password"}'
        for case, invalid in enumerate((raw + b" " * (65537 - len(raw)), duplicate, casefold_duplicate,
                                        b"\xff", b"{} trailing", b"null", b"[]", b"", raw.decode("utf-8"))):
            with self.subTest(case=case, raw_type=type(invalid).__name__, raw_length=len(invalid)):
                with self.assertRaises(DeploymentV2Error) as caught:
                    parse_deployment_secrets(invalid, deployment())
                self.assertNotIn("synthetic-first-password", str(caught.exception))
                self.assertNotIn("synthetic-second-password", str(caught.exception))

    def test_common_json_serializer_counts_utf8_bytes_including_its_final_newline(self):
        for text in ("x" * (65536 - len(b'{"Text":""}\n')), "\U0001f30d" * 16381):
            with self.subTest(scalar_width=len(text[0].encode("utf-8"))):
                rendered = deployment_json_text({"Text": text})
                self.assertEqual(65536, len(rendered.encode("utf-8")))
                self.assertEqual({"Text": text}, json.loads(rendered))
                self.assertTrue(rendered.endswith("\n"))
                with self.assertRaises(DeploymentV2Error):
                    deployment_json_text({"Text": text + "x"})
        for invalid in ({"Text": "\ud800"}, {"Text": float("nan")}, {"Text": object()}):
            with self.subTest(invalid_type=type(invalid["Text"]).__name__):
                with self.assertRaises(DeploymentV2Error) as caught:
                    deployment_json_text(invalid)
                self.assertIn("Output", str(caught.exception))

    def test_public_outputs_and_unrelated_artifacts_never_contain_resolved_passwords(self):
        for platform in ("vowin-windows", "vocat-go"):
            with self.subTest(platform=platform):
                files = self.bundle(platform)
                for name, text in files.items():
                    if name not in {"config/pjsip.conf", "gateway-client.json"}:
                        for password in role_secrets().values():
                            self.assertNotIn(password, text, name)
                for name in ("config/pjsip.conf", "gateway-client.json"):
                    self.assertNotIn(role_secrets()["pbx-disabled-role"], files[name])
                for key in ("local-main-role", "local-extra-role"):
                    self.assertNotIn(role_secrets()[key], files["config/pjsip.conf"])
                for key in ("pbx-one-role", "pbx-two-role"):
                    self.assertNotIn(role_secrets()[key], files["gateway-client.json"])

    def test_gateway_profiles_preserve_separate_realms_domains_addresses_and_auth_directions(self):
        windows = json.loads(self.bundle()["gateway-client.json"])
        self.assertEqual(2, windows["Version"])
        settings, credentials = windows["Settings"], windows["Credentials"]
        self.assertEqual(2, settings["CredentialVersion"])
        for key, expected in {
            "BindAddress": "127.70.0.3", "Port": 5092, "Realm": "gateway.realm.invalid",
            "ServerAddress": "127.70.0.2", "ServerPort": 5090,
            "IdentityDomain": "messages.example.invalid", "TrustedNetworks": ["127.70.0.2/32"],
            "LocalRegistrarAlgorithm": "SHA-256", "TrunkUsername": "pbx-to-gateway",
            "PeerInboundAlgorithm": "SHA-256", "PeerOutboundUsername": "gateway-to-pbx",
            "PeerExpectedRealm": "pbx.realm.invalid", "PeerAllowedAlgorithms": ["SHA-256"],
        }.items():
            with self.subTest(field=key):
                self.assertEqual(expected, settings[key])
        self.assertEqual(role_secrets()["peer-in-role"], credentials["PeerInboundPassword"])
        self.assertEqual(role_secrets()["peer-out-role"], credentials["PeerOutboundPassword"])
        self.assertEqual(role_secrets()["local-main-role"], credentials["MainPassword"])
        self.assertEqual({"desk-extra": role_secrets()["local-extra-role"]}, credentials["Extensions"])

        go = json.loads(self.bundle("vocat-go")["gateway-client.json"])
        self.assertEqual("127.70.0.3:5092", go["listen"])
        self.assertEqual("gateway.realm.invalid", go["realm"])
        self.assertEqual("SHA-256", go["algorithm"])
        self.assertEqual("messages.example.invalid", go["identity_domain"])
        self.assertEqual(["127.70.0.2/32"], go["allowed_cidrs"])
        self.assertEqual("127.70.0.2:5090", go["peer"]["endpoint"])
        self.assertEqual("pbx-to-gateway", go["peer"]["inbound_auth"]["username"])
        self.assertEqual(role_secrets()["peer-in-role"], go["peer"]["inbound_auth"]["password"])
        self.assertEqual("gateway-to-pbx", go["peer"]["outbound_auth"]["username"])
        self.assertEqual(role_secrets()["peer-out-role"], go["peer"]["outbound_auth"]["password"])
        self.assertEqual("pbx.realm.invalid", go["peer"]["outbound_auth"]["realm"])
        self.assertEqual(["SHA-256"], go["peer"]["outbound_auth"]["algorithms"])

    def test_go_profile_keeps_all_four_features_and_peer_grants_independent_without_autostart(self):
        for flags in ((False, False, False, False), (True, False, False, False),
                      (False, True, False, False), (False, False, True, False),
                      (False, False, False, True), (True, True, True, True)):
            with self.subTest(flags=flags):
                value = deployment("vocat-go")
                for name, enabled in zip(("CallsEnabled", "CarrierEnabled", "MessagesEnabled", "SmsEnabled"), flags):
                    value[name] = enabled
                profile = json.loads(self.bundle(value=value)["gateway-client.json"])
                self.assertIs(False, profile["enabled"])
                for key, expected in zip(("calls_enabled", "carrier_enabled", "messages_enabled", "sms_enabled"), flags):
                    self.assertIs(expected, profile[key])
                peer = profile["peer"]
                for key, expected in {"allow_calls_from_peer": True, "allow_calls_to_peer": False,
                                      "allow_sms_from_peer": False, "allow_sms_to_peer": True}.items():
                    self.assertIs(expected, peer[key])
                self.assertEqual(["synthetic-a"], peer["outgoing_lines"])
                self.assertEqual(["synthetic-b"], peer["sms_send_lines"])

    def test_go_local_accounts_never_copy_pbx_accounts_and_every_line_policy_is_exact(self):
        profile = json.loads(self.bundle("vocat-go")["gateway-client.json"])
        accounts = profile["accounts"]
        self.assertEqual(["3101", "3102"], [account["username"] for account in accounts])
        self.assertEqual([False, True], [account["allow_calls"] for account in accounts])
        self.assertEqual([True, False], [account["allow_messages"] for account in accounts])
        self.assertEqual([["synthetic-a"], ["synthetic-b"]], [account["outgoing_lines"] for account in accounts])
        self.assertEqual([["synthetic-b"], []], [account["sms_send_lines"] for account in accounts])
        self.assertEqual([role_secrets()["local-main-role"], role_secrets()["local-extra-role"]],
                         [account["password"] for account in accounts])
        expected = [
            {"device_id": "synthetic-a", "label": "Synthetic first line", "prefix": "81", "default": True,
             "allow_outgoing": True, "allow_incoming": True, "incoming_extensions": ["3101"],
             "country_code": "86", "national_number_length": 11, "allow_sms_send": False,
             "allow_sms_receive": True, "sms_recipients": ["3102"]},
            {"device_id": "synthetic-b", "label": "Synthetic second line", "prefix": "82", "default": False,
             "allow_outgoing": False, "allow_incoming": False, "incoming_extensions": ["3102"],
             "country_code": "63", "national_number_length": 10, "allow_sms_send": True,
             "allow_sms_receive": False, "sms_recipients": ["3101"]},
        ]
        self.assertEqual(expected, profile["carrier_lines"])
        self.assertEqual([
            {"device_id": "synthetic-a", "call_target": "ring-a", "sms_target": "sms-a"},
            {"device_id": "synthetic-b", "call_target": "ring-b", "sms_target": "sms-b"},
        ], profile["peer"]["incoming_routes"])
        self.assertNotIn("dial_rules", profile)
        self.assertNotIn("sms_dial_rules", profile)
        self.assertNotIn("secrets", profile)
        self.assertNotIn("local_account_epochs", profile)

    def test_go_disabled_local_registrar_does_not_emit_accounts_or_dangle_local_targets(self):
        value = deployment("vocat-go")
        value["GatewayLocalRegistrar"]["Enabled"] = False
        profile = json.loads(self.bundle(value=value)["gateway-client.json"])
        self.assertEqual([], profile["accounts"])
        for line in profile["carrier_lines"]:
            self.assertEqual([], line["incoming_extensions"])
            self.assertEqual([], line["sms_recipients"])
        self.assertNotIn(role_secrets()["local-main-role"], json.dumps(profile))
        self.assertNotIn(role_secrets()["local-extra-role"], json.dumps(profile))

    def test_go_empty_network_acl_is_rejected_instead_of_default_private_network_access(self):
        value = deployment("vocat-go")
        value["Peer"] = {}
        value["GatewayTrustedNetworks"] = []
        self.assertEqual([], normalize_deployment_v2(value)["GatewayTrustedNetworks"])
        private = role_secrets()
        del private["peer-in-role"]
        del private["peer-out-role"]
        error = self.assert_invalid(value, private)
        self.assertIn("GatewayTrustedNetworks", str(error))

    def test_windows_explicit_policy_has_no_null_legacy_fallback_or_primary_account_exception(self):
        settings = json.loads(self.bundle()["gateway-client.json"])["Settings"]
        self.assertIs(True, settings["UseTrunk"])
        self.assertIs(True, settings["LocalRegistrarEnabled"])
        self.assertIs(False, settings.get("WasEnabled", False))
        self.assertIs(False, settings.get("RestoreOnStartup", False))
        self.assertEqual("desk-main", settings["Extension"])
        self.assertEqual("synthetic-a", settings["DefaultOutboundLineId"])
        self.assertEqual([], settings["DialRules"], "PBX rule kinds must not merge into one Windows list")
        for key, expected in {
            "PeerAllowOutboundCalls": True, "PeerAllowIncomingCalls": False,
            "PeerAllowOutboundMessages": False, "PeerAllowIncomingMessages": True,
            "PeerOutgoingLines": ["synthetic-a"], "PeerSmsSendLines": ["synthetic-b"],
        }.items():
            with self.subTest(field=key):
                self.assertEqual(expected, settings[key])
                self.assertIsNotNone(settings[key])
        main = settings["PrimaryPolicy"]
        extra = settings["Extensions"]
        self.assertEqual(["desk-extra"], [account["Username"] for account in extra])
        for policy, expected in (
            (main, {"AllowCalls": False, "AllowSms": True, "AllowInternalCalls": False,
                    "AllowMessages": True, "OutgoingLines": ["synthetic-a"], "SmsSendLines": ["synthetic-b"]}),
            (extra[0]["Policy"], {"AllowCalls": True, "AllowSms": False, "AllowInternalCalls": True,
                                  "AllowMessages": False, "OutgoingLines": ["synthetic-b"], "SmsSendLines": []}),
        ):
            for key, expected_value in expected.items():
                with self.subTest(policy=policy, field=key):
                    self.assertEqual(expected_value, policy[key])
                    self.assertIsNotNone(policy[key])
        routes = settings["LineRoutes"]
        self.assertEqual({"synthetic-a", "synthetic-b"}, set(routes))
        for line_id, expected in {
            "synthetic-a": {"Enabled": True, "AllowIncomingCalls": True,
                            "AllowOutgoingCalls": True, "AllowOutgoingSms": False,
                            "ForwardIncomingSms": True, "IncomingTarget": "ring-a", "SmsTarget": "sms-a",
                            "LocalIncomingExtensions": [], "LocalSmsRecipients": ["desk-extra"],
                            "ForwardCallsToPeer": False, "ForwardSmsToPeer": True,
                            "Prefix": "81", "CountryCode": "86", "NationalNumberLength": 11},
            "synthetic-b": {"Enabled": True, "AllowIncomingCalls": False,
                            "AllowOutgoingCalls": False, "AllowOutgoingSms": True,
                            "ForwardIncomingSms": False, "IncomingTarget": "ring-b", "SmsTarget": "sms-b",
                            "LocalIncomingExtensions": ["desk-extra"], "LocalSmsRecipients": ["desk-main"],
                            "ForwardCallsToPeer": False, "ForwardSmsToPeer": True,
                            "Prefix": "82", "CountryCode": "63", "NationalNumberLength": 10},
        }.items():
            for key, expected_value in expected.items():
                with self.subTest(line=line_id, field=key):
                    self.assertEqual(expected_value, routes[line_id][key])
                    self.assertIsNotNone(routes[line_id][key])

    def test_disabling_windows_roles_never_enables_them_to_preserve_unused_credentials(self):
        for local_enabled, peer_enabled in ((False, False), (True, False), (False, True)):
            with self.subTest(local=local_enabled, peer=peer_enabled):
                value = deployment()
                value["GatewayLocalRegistrar"]["Enabled"] = local_enabled
                value["Peer"]["Enabled"] = peer_enabled
                files = self.bundle(value=value)
                public = json.loads(files["deployment.normalized.json"])
                profile = json.loads(files["gateway-client.json"])
                settings, private = profile["Settings"], profile["Credentials"]
                self.assertIs(local_enabled, settings["LocalRegistrarEnabled"])
                self.assertIs(peer_enabled, settings["UseTrunk"])
                self.assertIs(False, settings.get("WasEnabled", False))
                self.assertEqual(value["Peer"]["InboundAuth"], public["Peer"]["InboundAuth"])
                self.assertEqual(value["Peer"]["OutboundAuth"], public["Peer"]["OutboundAuth"])
                self.assertEqual(value["GatewayLocalRegistrar"]["Accounts"], public["GatewayLocalRegistrar"]["Accounts"])
                self.assertEqual(role_secrets()["peer-in-role"] if peer_enabled else "", private["PeerInboundPassword"])
                self.assertEqual(role_secrets()["peer-out-role"] if peer_enabled else "", private["PeerOutboundPassword"])
                self.assertEqual(role_secrets()["local-main-role"] if local_enabled else "", private["MainPassword"])
                self.assertEqual({"desk-extra": role_secrets()["local-extra-role"]} if local_enabled else {}, private["Extensions"])
                if not local_enabled:
                    self.assertEqual([], settings["Extensions"])
                    for route in settings["LineRoutes"].values():
                        self.assertEqual([], route["LocalIncomingExtensions"])
                        self.assertEqual([], route["LocalSmsRecipients"])
                if not peer_enabled:
                    for password in (role_secrets()["peer-in-role"], role_secrets()["peer-out-role"]):
                        self.assertNotIn(password, files["gateway-client.json"])
                        self.assertNotIn(password, files["config/pjsip.conf"])

    def test_fully_disabled_windows_draft_is_renderable_but_has_no_accounts_or_business_routes(self):
        source = deployment()
        value = {key: source[key] for key in (
            "SchemaVersion", "GatewayPlatform", "ServerIp", "BindAddress", "SipPort",
            "MessageDomain", "PbxRealm", "PbxAlgorithm", "GatewayIp", "GatewayBindAddress",
            "GatewayPort", "GatewayRealm", "GatewayAlgorithm",
        )}
        files = render_deployment_v2(value, {})
        self.assertEqual(OUTPUTS, set(files))
        profile = json.loads(files["gateway-client.json"])
        settings, credentials = profile["Settings"], profile["Credentials"]
        for key in ("UseTrunk", "LocalRegistrarEnabled", "AllowOutboundCalls", "MessagesEnabled",
                    "ForwardIncomingSms", "AllowOutgoingSms"):
            self.assertIs(False, settings[key], key)
        self.assertEqual([], settings["TrustedNetworks"])
        self.assertEqual([], settings["Extensions"])
        self.assertEqual({}, settings["LineRoutes"])
        self.assertEqual([], settings["DialRules"])
        self.assertEqual("", credentials["MainPassword"])
        self.assertEqual({}, credentials["Extensions"])
        self.assertEqual("", credentials["PeerInboundPassword"])
        self.assertEqual("", credentials["PeerOutboundPassword"])
        sections = ini_sections(files["config/pjsip.conf"])
        self.assertFalse(any(fields.get("type") in {"auth", "aor"} for _, fields, _ in sections))
        # An inert endpoint template is not a configured account. No stopped
        # draft may gain an authenticated/registered endpoint via that template.
        self.assertFalse(any(fields.get("type") == "endpoint" and (fields.get("auth") or fields.get("aors"))
                             for _, fields, _ in sections))
        self.assertNotIn("Dial(", files["config/extensions.conf"])
        self.assertNotIn("MessageSend(", files["config/extensions.conf"])

    def test_same_username_in_independent_namespaces_does_not_merge_secret_roles(self):
        for platform in ("vowin-windows", "vocat-go"):
            with self.subTest(platform=platform):
                value = deployment(platform)
                old_main = value["GatewayLocalRegistrar"]["PrimaryUsername"]
                value["GatewayLocalRegistrar"]["PrimaryUsername"] = "2101"
                value["GatewayLocalRegistrar"]["Accounts"][0]["Username"] = "2101"
                value["Peer"]["InboundAuth"]["Username"] = "2101"
                value["Peer"]["OutboundAuth"]["Username"] = "2101"
                for line in value["Lines"]:
                    for key in ("LocalIncomingExtensions", "LocalSmsRecipients"):
                        line[key] = ["2101" if username == old_main else username for username in line[key]]
                files = self.bundle(value=value)
                profile = json.loads(files["gateway-client.json"])
                if platform == "vowin-windows":
                    self.assertEqual(role_secrets()["local-main-role"], profile["Credentials"]["MainPassword"])
                    self.assertEqual(role_secrets()["peer-in-role"], profile["Credentials"]["PeerInboundPassword"])
                    self.assertEqual(role_secrets()["peer-out-role"], profile["Credentials"]["PeerOutboundPassword"])
                else:
                    self.assertEqual(role_secrets()["local-main-role"], profile["accounts"][0]["password"])
                    self.assertEqual(role_secrets()["peer-in-role"], profile["peer"]["inbound_auth"]["password"])
                    self.assertEqual(role_secrets()["peer-out-role"], profile["peer"]["outbound_auth"]["password"])
                auth = [fields for _, fields, _ in ini_sections(files["config/pjsip.conf"])
                        if fields.get("type") == "auth" and fields.get("username") == "2101"]
                self.assertEqual({role_secrets()["pbx-one-role"], role_secrets()["peer-in-role"],
                                  role_secrets()["peer-out-role"]}, {fields["password"] for fields in auth})

    def test_windows_local_display_name_is_explicitly_unsupported_not_silently_discarded(self):
        value = deployment()
        value["GatewayLocalRegistrar"]["Accounts"][0]["DisplayName"] = "Synthetic requested local label"
        self.assertEqual("Synthetic requested local label", normalize_deployment_v2(value)["GatewayLocalRegistrar"]["Accounts"][0]["DisplayName"])
        error = self.assert_invalid(value)
        self.assertIn("DisplayName", str(error))

    def test_dormant_windows_default_line_is_preserved_without_enabling_any_permission(self):
        value = deployment()
        value.update(CallsEnabled=False, CarrierEnabled=False, MessagesEnabled=False, SmsEnabled=False)
        value["Peer"]["Enabled"] = False
        value["GatewayLocalRegistrar"]["Enabled"] = False
        for line in value["Lines"]:
            line.update(AllowIncomingCalls=False, AllowOutgoingCalls=False, AllowSmsSend=False, AllowSmsReceive=False)
        files = self.bundle(value=value)
        settings = json.loads(files["gateway-client.json"])["Settings"]
        self.assertEqual("synthetic-a", settings["DefaultOutboundLineId"])
        self.assertIs(False, settings["UseTrunk"])
        self.assertIs(False, settings["LocalRegistrarEnabled"])
        for line in settings["LineRoutes"].values():
            for key in ("AllowIncomingCalls", "AllowOutgoingCalls", "AllowOutgoingSms", "ForwardIncomingSms",
                        "ForwardCallsToPeer", "ForwardSmsToPeer"):
                self.assertIs(False, line[key], key)
        self.assertNotIn("Dial(", files["config/extensions.conf"])
        self.assertNotIn("MessageSend(", files["config/extensions.conf"])

    def test_windows_sms_transport_does_not_grant_internal_messages_or_calls(self):
        value = deployment()
        value["CallsEnabled"] = False
        value["MessagesEnabled"] = False
        value["SmsEnabled"] = True
        profile = json.loads(self.bundle(value=value)["gateway-client.json"])
        settings = profile["Settings"]
        self.assertIs(True, settings["MessagesEnabled"], "SIP MESSAGE is still required for paid SMS transport")
        self.assertIs(False, settings["AllowOutboundCalls"])
        self.assertIs(True, settings["AllowOutgoingSms"])
        for policy in [settings["PrimaryPolicy"], *(account["Policy"] for account in settings["Extensions"])]:
            self.assertIs(False, policy["AllowInternalCalls"])
            self.assertIs(False, policy["AllowMessages"])
        for line in settings["LineRoutes"].values():
            self.assertIs(False, line["AllowIncomingCalls"])

    def test_go_private_profiles_preserve_disabled_peer_switch_without_using_accounts_as_fallback(self):
        value = deployment("vocat-go")
        value["Peer"]["Enabled"] = False
        profile = json.loads(self.bundle(value=value)["gateway-client.json"])
        self.assertIs(False, profile["enabled"])
        self.assertIs(False, profile["peer"]["enabled"])
        self.assertEqual(role_secrets()["peer-in-role"], profile["peer"]["inbound_auth"]["password"])
        self.assertEqual(role_secrets()["peer-out-role"], profile["peer"]["outbound_auth"]["password"])
        self.assertEqual(["3101", "3102"], [account["username"] for account in profile["accounts"]])
        self.assertNotIn("pbx-to-gateway", [account["username"] for account in profile["accounts"]])

    def test_oversized_json_artifact_is_rejected_even_when_in_memory_model_is_valid(self):
        value = deployment()
        line_ids = ["line-" + str(index).zfill(2) + "x" * 50 for index in range(32)]
        value["Lines"] = [{"Id": line_id, "Prefix": str(100 + index)} for index, line_id in enumerate(line_ids)]
        value["DialRules"] = []
        value["SmsDialRules"] = []
        value["Peer"]["OutgoingLines"] = list(line_ids)
        value["Peer"]["SmsSendLines"] = list(line_ids)
        value["GatewayLocalRegistrar"] = {
            "Enabled": True, "PrimaryUsername": "large-local-00",
            "Accounts": [{"Username": "large-local-" + str(index).zfill(2),
                          "SecretRef": "large-local-role-" + str(index),
                          "OutgoingLines": list(line_ids), "SmsSendLines": list(line_ids)}
                         for index in range(64)],
        }
        private = role_secrets()
        del private["local-main-role"]
        del private["local-extra-role"]
        private.update({"large-local-role-" + str(index): "synthetic-large-local-password" for index in range(64)})
        normalized = normalize_deployment_v2(value)
        self.assertGreater(len(json.dumps(normalized, ensure_ascii=False, separators=(",", ":")).encode("utf-8")), 65536)
        self.assert_invalid(value, private)

    def test_voice_and_sms_dial_rules_stay_in_separate_routes_with_explicit_platform_line_selection(self):
        for platform in ("vowin-windows", "vocat-go"):
            with self.subTest(platform=platform):
                value = deployment(platform)
                value["Peer"]["AllowCallsFromPeer"] = True
                value["Peer"]["AllowSmsFromPeer"] = True
                files = self.bundle(value=value)
                text = files["config/extensions.conf"]
                voice = [body for _, body in extension_routes(text, "_72X.") if "Dial(" in body]
                sms = [body for _, body in extension_routes(text, "_93X.") if "MessageSend(" in body]
                self.assertTrue(voice, "voice-only rule must have an authenticated paid-call route")
                self.assertTrue(sms, "SMS-only rule must have an authenticated paid-SMS route")
                self.assertFalse(any("MessageSend(" in body for _, body in extension_routes(text, "_72X.")))
                self.assertFalse(any("Dial(" in body for _, body in extension_routes(text, "_93X.")))
                for pattern in ("_X.", "_.", "_+X."):
                    self.assertFalse(any("Dial(" in body for _, body in extension_routes(text, pattern)),
                                     "no unrequested paid catch-all")
                if platform == "vocat-go":
                    self.assertTrue(any("81+${EXTEN:2}" in body for body in voice))
                    self.assertTrue(any("82+${EXTEN:2}" in body for body in sms))
                else:
                    self.assertTrue(any("+${EXTEN:2}" in body for body in voice))
                    self.assertTrue(any("+${EXTEN:2}" in body for body in sms))
                    self.assertIn("X-VoWin-Line", text)
                    self.assertTrue(any("synthetic-a" in body for body in voice))
                    self.assertTrue(any("synthetic-b" in body for body in sms))

    def test_same_numeric_pattern_can_select_different_voice_and_sms_lines(self):
        for platform in ("vowin-windows", "vocat-go"):
            with self.subTest(platform=platform):
                value = deployment(platform)
                value["Peer"]["AllowSmsFromPeer"] = True
                value["SmsDialRules"][0]["Pattern"] = value["DialRules"][0]["Pattern"]
                text = self.bundle(value=value)["config/extensions.conf"]
                voice = [body for _, body in extension_routes(text, "_72X.") if "Dial(" in body]
                sms = [body for _, body in extension_routes(text, "_72X.") if "MessageSend(" in body]
                self.assertTrue(voice)
                self.assertTrue(sms)
                self.assertTrue(all("MessageSend(" not in body for body in voice))
                self.assertTrue(all("Dial(" not in body for body in sms))
                if platform == "vocat-go":
                    self.assertTrue(any("81+${EXTEN:2}" in body for body in voice))
                    self.assertTrue(any("82+${EXTEN:2}" in body for body in sms))
                    self.assertFalse(any("82+${EXTEN:2}" in body for body in voice))
                    self.assertFalse(any("81+${EXTEN:2}" in body for body in sms))
                else:
                    self.assertTrue(any("synthetic-a" in body for body in voice))
                    self.assertTrue(any("synthetic-b" in body for body in sms))

    def test_numeric_pattern_classes_and_strip_counts_are_preserved_not_approximated_as_prefix_rules(self):
        for platform in ("vowin-windows", "vocat-go"):
            with self.subTest(platform=platform):
                value = deployment(platform)
                pattern = "_7[2-4]ZNXXXXXXX"
                value["DialRules"] = [{"Pattern": pattern, "LineId": "synthetic-a", "Strip": 3, "Prepend": "+86"}]
                text = self.bundle(value=value)["config/extensions.conf"]
                voice = [body for _, body in extension_routes(text, pattern) if "Dial(" in body]
                self.assertTrue(voice)
                self.assertTrue(any("+86${EXTEN:3}" in body for body in voice))
                self.assertFalse(any("Dial(" in body for _, body in extension_routes(text, "_7X.")))
                if platform == "vowin-windows":
                    self.assertEqual([], json.loads(self.bundle(value=value)["gateway-client.json"])["Settings"]["DialRules"])

    def test_paid_routes_validate_original_input_before_strip_and_validate_transformed_e164_before_sending(self):
        for platform in ("vowin-windows", "vocat-go"):
            with self.subTest(platform=platform):
                value = deployment(platform)
                value["Peer"]["AllowSmsFromPeer"] = True
                text = self.bundle(value=value)["config/extensions.conf"]
                for pattern, application in (("_72X.", "Dial("), ("_93X.", "MessageSend(")):
                    blocks = [body for _, body in extension_routes(text, pattern) if application in body]
                    self.assertTrue(blocks)
                    for body in blocks:
                        original_guard = 'REGEX("^[+]?[0-9]{1,78}$" ${EXTEN})'
                        normalized_guard = 'REGEX("^[+][1-9][0-9]{6,14}$" ${VOWIN_DEST})'
                        self.assertIn(original_guard, body)
                        self.assertIn(normalized_guard, body)
                        self.assertLess(body.index(original_guard), body.index("Set(VOWIN_DEST="))
                        self.assertLess(body.index("Set(VOWIN_DEST="), body.index(normalized_guard))
                        self.assertLess(body.index(normalized_guard), body.index(application))
                        self.assertIn("?reject)", body)
                        self.assertIn("n(reject),Hangup", body)

    def test_go_overlapping_selectors_canonicalize_national_number_before_adding_selected_prefix(self):
        value = deployment("vocat-go")
        value["Lines"][0].update(Prefix="8", AllowSmsSend=True)
        value["Lines"][1].update(Prefix="81", AllowOutgoingCalls=True)
        value["Peer"].update(AllowSmsFromPeer=True, OutgoingLines=["synthetic-a", "synthetic-b"],
                             SmsSendLines=["synthetic-a", "synthetic-b"])
        value["DialRules"][0]["Prepend"] = ""
        value["SmsDialRules"][0].update(LineId="synthetic-a", Prepend="")
        self.assertEqual("8", normalize_deployment_v2(value)["Lines"][0]["Prefix"])
        text = self.bundle(value=value)["config/extensions.conf"]
        for pattern, application in (("_72X.", "Dial("), ("_93X.", "MessageSend(")):
            blocks = [body for _, body in extension_routes(text, pattern) if application in body]
            self.assertTrue(blocks)
            for body in blocks:
                self.assertIn("Set(VOWIN_DEST=${EXTEN:2})", body)
                self.assertIn('REGEX("^[0-9]{11}$" ${VOWIN_DEST})', body)
                self.assertIn("Set(VOWIN_DEST=+86${VOWIN_DEST})", body)
                self.assertIn('REGEX("^[+][1-9][0-9]{6,14}$" ${VOWIN_DEST})', body)
                self.assertIn("8${VOWIN_DEST}@gateway", body)
                self.assertNotIn("8${EXTEN:2}@gateway", body)
                self.assertNotIn("81${VOWIN_DEST}@gateway", body)
                self.assertLess(body.index("Set(VOWIN_DEST=+86${VOWIN_DEST})"), body.index(application))

    def test_opaque_reply_aliases_remain_opaque_and_never_become_default_line_numeric_routes(self):
        for platform in ("vowin-windows", "vocat-go"):
            with self.subTest(platform=platform):
                value = deployment(platform)
                value["Peer"]["AllowSmsFromPeer"] = True
                text = self.bundle(value=value)["config/extensions.conf"]
                for pattern in ("_sms-.", "_sms2-."):
                    blocks = [body for _, body in extension_routes(text, pattern) if "MessageSend(" in body]
                    self.assertTrue(blocks)
                    for body in blocks:
                        self.assertIn("${EXTEN}", body)
                        self.assertNotIn("${EXTEN:", body)
                        self.assertNotIn("X-VoWin-Line", body)
                        self.assertNotIn("81${EXTEN}", body)
                        self.assertNotIn("82${EXTEN}", body)
                        self.assertNotIn("Dial(", body)
                self.assertNotIn("MESSAGE_DATA(Request-URI)", text)
                self.assertIn("PJSIP_PARSE_URI_FROM", text)
                self.assertIn("VOWIN_REQUEST_USER", text)

    def test_disabled_call_sms_or_peer_grants_never_leave_paid_routes_enabled(self):
        for platform in ("vowin-windows", "vocat-go"):
            for disabled in ("CallsEnabled", "CarrierEnabled", "SmsEnabled", "Peer"):
                with self.subTest(platform=platform, disabled=disabled):
                    value = deployment(platform)
                    value["Peer"]["AllowSmsFromPeer"] = True
                    if disabled == "Peer":
                        value["Peer"]["Enabled"] = False
                    else:
                        value[disabled] = False
                    text = self.bundle(value=value)["config/extensions.conf"]
                    if disabled in ("CallsEnabled", "CarrierEnabled", "Peer"):
                        self.assertFalse(any("Dial(" in body for _, body in extension_routes(text, "_72X.")))
                    if disabled in ("SmsEnabled", "Peer"):
                        self.assertFalse(any("MessageSend(" in body for _, body in extension_routes(text, "_93X.")))

    def test_pbx_ring_and_mobile_sms_receive_recipients_use_separate_enabled_account_grants(self):
        for platform in ("vowin-windows", "vocat-go"):
            with self.subTest(platform=platform):
                value = deployment(platform)
                value["Peer"]["AllowCallsToPeer"] = True
                text = self.bundle(value=value)["config/extensions.conf"]
                calls = [body for _, body in extension_routes(text, "ring-a") if "Dial(" in body]
                messages = [body for _, body in extension_routes(text, "sms-a") if "Gosub(" in body]
                self.assertTrue(calls)
                self.assertTrue(messages)
                for body in calls:
                    self.assertIn("PJSIP_DIAL_CONTACTS(2101)", body)
                    self.assertNotIn("PJSIP_DIAL_CONTACTS(2102)", body)
                    self.assertNotIn("2103", body)
                for body in messages:
                    self.assertRegex(body, r"Gosub\([^\n]*\(2102\)\)")
                    self.assertNotRegex(body, r"Gosub\([^\n]*\(2101\)\)")
                    self.assertNotIn("2103", body)
                self.assertFalse(any("Dial(" in body for _, body in extension_routes(text, "ring-b")))
                self.assertFalse(any("Gosub(" in body for _, body in extension_routes(text, "sms-b")))

    def test_paid_sms_permission_does_not_depend_on_internal_message_permission(self):
        for platform in ("vowin-windows", "vocat-go"):
            with self.subTest(platform=platform):
                value = deployment(platform)
                value["Peer"]["AllowSmsFromPeer"] = True
                value["MessagesEnabled"] = False
                value["ExtensionOptions"]["2101"]["AllowMessages"] = False
                text = self.bundle(value=value)["config/extensions.conf"]
                self.assertTrue(any("MessageSend(" in body for _, body in extension_routes(text, "_93X.")))
                for pattern in ("_sms-.", "_sms2-."):
                    self.assertTrue(any("MessageSend(" in body for _, body in extension_routes(text, pattern)))

    def test_ipv6_addresses_are_not_converted_or_rendered_as_ambiguous_unbracketed_uris(self):
        for platform in ("vowin-windows", "vocat-go"):
            with self.subTest(platform=platform):
                value = deployment(platform)
                value.update(ServerIp="fd70::2", BindAddress="fd70::2", GatewayIp="fd70::3",
                             GatewayBindAddress="fd70::3", TrustedNetworks=["fd70::3/128"],
                             GatewayTrustedNetworks=["fd70::2/128"])
                files = self.bundle(value=value)
                self.assertIn("[fd70::2]:5090", files["config/pjsip.conf"])
                self.assertIn("[fd70::3]:5092", files["config/pjsip.conf"])
                self.assertIn("ip6 saddr", files["sip-guard.nft"])
                profile = json.loads(files["gateway-client.json"])
                if platform == "vocat-go":
                    self.assertEqual("[fd70::3]:5092", profile["listen"])
                    self.assertEqual("[fd70::2]:5090", profile["peer"]["endpoint"])
                else:
                    self.assertEqual("fd70::3", profile["Settings"]["BindAddress"])
                    self.assertEqual(["fd70::2/128"], profile["Settings"]["TrustedNetworks"])

    def test_firewall_uses_only_requested_pbx_trust_networks_and_never_claims_to_apply_it(self):
        files = self.bundle()
        text = files["sip-guard.nft"]
        self.assertIn("127.70.0.3/32", text)
        self.assertIn("5090", text)
        self.assertIn("10000-10099", text)
        self.assertIn("drop", text)
        self.assertNotIn("0.0.0.0/0", text)
        self.assertNotIn("10.0.0.0/8", text)
        self.assertNotIn("192.168.0.0/16", text)
        self.assertNotIn("systemctl", text)
        self.assertNotIn("docker", text)

    def test_minimal_pbx_artifacts_do_not_enable_unrequested_admin_or_network_services(self):
        files = self.bundle()
        self.assertIn("autoload=no", files["config/modules.conf"])
        self.assertIn("res_pjsip_messaging.so", files["config/modules.conf"])
        self.assertIn("res_pjsip_nat.so", files["config/modules.conf"])
        self.assertIn("res_pjsip_caller_id.so", files["config/modules.conf"])
        for name in ("config/http.conf", "config/manager.conf"):
            self.assertIn("enabled=no", files[name])
            self.assertNotIn("enabled=yes", files[name])
        for line in ("rtpstart=10000", "rtpend=10099", "icesupport=no", "strictrtp=yes"):
            self.assertIn(line, files["config/rtp.conf"])
        for _, fields, _ in ini_sections(files["config/pjsip.conf"]):
            if fields.get("type") == "endpoint":
                self.assertEqual("no", fields["direct_media"])

    def test_pbx_auth_never_uses_legacy_shared_secret_wildcard_realm_or_unrequested_md5(self):
        sections = ini_sections(self.bundle()["config/pjsip.conf"])
        auth = [fields for _, fields, _ in sections if fields.get("type") == "auth"]
        for username, password, realm in (
            ("gateway-to-pbx", role_secrets()["peer-out-role"], "pbx.realm.invalid"),
            ("pbx-to-gateway", role_secrets()["peer-in-role"], "gateway.realm.invalid"),
        ):
            with self.subTest(username=username):
                matches = [fields for fields in auth if fields.get("username") == username]
                self.assertEqual(1, len(matches))
                self.assertEqual(password, matches[0]["password"])
                self.assertEqual(realm, matches[0]["realm"])
                self.assertIn("SHA-256", " ".join(matches[0].values()))
                self.assertNotIn("MD5", " ".join(matches[0].values()))
        self.assertFalse(any(fields.get("realm") in {"*", "asterisk"} for fields in auth))
        self.assertFalse(any(fields.get("username") == "2103" for fields in auth))
        for username, key in (("2101", "pbx-one-role"), ("2102", "pbx-two-role")):
            matches = [fields for fields in auth if fields.get("username") == username]
            self.assertEqual(1, len(matches))
            self.assertEqual(role_secrets()[key], matches[0]["password"])
            self.assertEqual("pbx.realm.invalid", matches[0]["realm"])

    def test_explicit_md5_remains_an_explicit_role_choice_not_sha256_fallback(self):
        for platform in ("vowin-windows", "vocat-go"):
            with self.subTest(platform=platform):
                value = deployment(platform)
                value["GatewayAlgorithm"] = "MD5"
                files = self.bundle(value=value)
                profile = json.loads(files["gateway-client.json"])
                if platform == "vowin-windows":
                    self.assertEqual("MD5", profile["Settings"]["LocalRegistrarAlgorithm"])
                    self.assertEqual("MD5", profile["Settings"]["PeerInboundAlgorithm"])
                    self.assertEqual(["SHA-256"], profile["Settings"]["PeerAllowedAlgorithms"])
                else:
                    self.assertEqual("MD5", profile["algorithm"])
                    self.assertEqual(["SHA-256"], profile["peer"]["outbound_auth"]["algorithms"])
                auth = [fields for _, fields, _ in ini_sections(files["config/pjsip.conf"])
                        if fields.get("type") == "auth"]
                inbound_to_gateway = next(fields for fields in auth if fields.get("username") == "pbx-to-gateway")
                self.assertIn("MD5", " ".join(inbound_to_gateway.values()))
                gateway_to_pbx = next(fields for fields in auth if fields.get("username") == "gateway-to-pbx")
                self.assertIn("SHA-256", " ".join(gateway_to_pbx.values()))

    def test_bad_public_model_or_unknown_secret_data_is_a_safe_error_not_a_partial_bundle(self):
        for mutate in (
            lambda value: value.update({SENTINEL: True}),
            lambda value: value["Peer"].update({"Password": SENTINEL}),
            lambda value: value.update({"SchemaVersion": 1}),
            lambda value: value.update({"GatewayAlgorithm": "AUTO"}),
            lambda value: value["DialRules"][0].update({"Pattern": "_X."}),
        ):
            with self.subTest(mutation=mutate.__code__.co_firstlineno):
                value = deployment()
                mutate(value)
                self.assert_invalid(value)


if __name__ == "__main__":
    unittest.main()
