"""Independent offline contract for the explicit v2 saved-not-applied admin API.

Only synthetic models/secrets and owned temporary Linux directories are used.
The absent product is an assertion-based RED, never an import error or skip.
No CLI, PBX, SIP listener, modem, call, SMS or existing deployment is involved.
"""
from concurrent.futures import ThreadPoolExecutor
from contextlib import ExitStack
from copy import deepcopy
import importlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import socket
import stat
import subprocess
import sys
import tempfile
import threading
import traceback
import unittest
from unittest.mock import patch


SENTINEL = "SyntheticPrivateAdminNeverEchoCredential"
TOKEN = re.compile(r"[A-Za-z0-9_-]{43}\Z")
VIEW_KEYS = {"schemaVersion", "revision", "state", "canApply", "sipServer", "sipPort", "features", "extensions"}
ROW_KEYS = {"number", "sourceNumber", "name", "enabled", "ring", "maxContacts", "allowOutgoing",
            "allowMessages", "allowMobileSmsSend", "allowMobileSmsReceive", "hasPassword"}
FLAGS = ("enabled", "ring", "allowOutgoing", "allowMessages", "allowMobileSmsSend", "allowMobileSmsReceive")
OPTION_FIELDS = {"name": "Name", "enabled": "Enabled", "ring": "Ring", "maxContacts": "MaxContacts",
                 "allowOutgoing": "AllowOutgoing", "allowMessages": "AllowMessages",
                 "allowMobileSmsSend": "AllowMobileSmsSend", "allowMobileSmsReceive": "AllowMobileSmsReceive"}


def encoded(value):
    return (json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")


def synthetic_model():
    return {
        "SchemaVersion": 2, "GatewayPlatform": "vowin-windows", "ServerIp": "127.76.0.2",
        "BindAddress": "127.76.0.2", "SipPort": 5096, "MessageDomain": "identity.example.invalid",
        "PbxRealm": "Independent.Pbx.Realm", "PbxAlgorithm": "SHA-256", "GatewayIp": "127.76.0.3",
        "GatewayBindAddress": "127.76.0.3", "GatewayPort": 5098, "GatewayRealm": "Independent.Gateway.Realm",
        "GatewayAlgorithm": "MD5", "TrustedNetworks": ["127.76.0.3/32"],
        "GatewayTrustedNetworks": ["127.76.0.2/32"], "CallsEnabled": False, "CarrierEnabled": True,
        "MessagesEnabled": True, "SmsEnabled": False,
        "Peer": {"Enabled": False,
                 "InboundAuth": {"Username": "4201", "SecretRef": "peer-in-reference"},
                 "OutboundAuth": {"Username": "4201", "SecretRef": "peer-out-reference"},
                 "AllowCallsFromPeer": True, "AllowCallsToPeer": False,
                 "AllowSmsFromPeer": False, "AllowSmsToPeer": True,
                 "OutgoingLines": ["line-first"], "SmsSendLines": ["line-second"]},
        "GatewayLocalRegistrar": {"Enabled": False, "PrimaryUsername": "4201", "Accounts": [
            {"Username": "4201", "SecretRef": "local-first-reference", "DisplayName": "", "AllowCalls": False,
             "AllowMessages": True, "OutgoingLines": ["line-second"], "SmsSendLines": ["line-first"]},
            {"Username": "desk-extra", "SecretRef": "local-extra-reference", "DisplayName": "", "AllowCalls": True,
             "AllowMessages": False, "OutgoingLines": [], "SmsSendLines": []}]},
        "Extensions": ["4201", "4202"],
        "ExtensionOptions": {
            "4201": {"SecretRef": "pbx-first-reference", "Name": "Synthetic first", "Enabled": True, "Ring": False,
                     "MaxContacts": 2, "AllowOutgoing": False, "AllowMessages": True,
                     "AllowMobileSmsSend": False, "AllowMobileSmsReceive": True},
            "4202": {"SecretRef": "pbx-second-reference", "Name": "Synthetic second", "Enabled": False, "Ring": True,
                     "MaxContacts": 7, "AllowOutgoing": True, "AllowMessages": False,
                     "AllowMobileSmsSend": True, "AllowMobileSmsReceive": False}},
        "Lines": [
            {"Id": "line-first", "Label": "Synthetic first", "Prefix": "71", "Default": True,
             "CountryCode": "86", "NationalNumberLength": 11, "AllowIncomingCalls": True,
             "AllowOutgoingCalls": False, "AllowSmsSend": True, "AllowSmsReceive": False,
             "CallTarget": "ring-first", "SmsTarget": "sms-first", "LocalIncomingExtensions": ["4201"],
             "LocalSmsRecipients": ["desk-extra"]},
            {"Id": "line-second", "Label": "Synthetic second", "Prefix": "72", "Default": False,
             "CountryCode": "63", "NationalNumberLength": 10, "AllowIncomingCalls": False,
             "AllowOutgoingCalls": True, "AllowSmsSend": False, "AllowSmsReceive": True,
             "CallTarget": "ring-second", "SmsTarget": "sms-second", "LocalIncomingExtensions": ["desk-extra"],
             "LocalSmsRecipients": ["4201"]}],
        "DialRules": [{"Pattern": "_73X.", "LineId": "line-first", "Strip": 2, "Prepend": "+"}],
        "SmsDialRules": [{"Pattern": "_84X.", "LineId": "line-second", "Strip": 2, "Prepend": "+"}],
    }


def synthetic_secrets():
    return {"peer-in-reference": "synthetic-peer-in-credential", "peer-out-reference": "synthetic-peer-out-credential",
            "local-first-reference": "synthetic-local-first-credential", "local-extra-reference": "synthetic-local-extra-credential",
            "pbx-first-reference": "synthetic-pbx-first-credential", "pbx-second-reference": "synthetic-pbx-second-credential"}


def tree(directory):
    if not os.path.lexists(directory):
        return {}
    result = {}
    for path in [directory, *directory.rglob("*")]:
        name = "." if path == directory else path.relative_to(directory).as_posix()
        if path.is_symlink():
            result[name] = ("symlink", os.readlink(path))
        elif path.is_dir():
            result[name] = ("directory", stat.S_IMODE(path.stat().st_mode))
        else:
            result[name] = ("file", stat.S_IMODE(path.stat().st_mode), path.read_bytes())
    return result


class AdminDeploymentV2Tests(unittest.TestCase):
    def _api(self, name):
        spec = importlib.util.find_spec("admin_deployment_v2")
        module = importlib.import_module("admin_deployment_v2") if spec is not None else None
        value = getattr(module, name, None)
        self.assertTrue(callable(value), "Explicit v2 admin draft API must exist")
        return value

    def _apis(self):
        return tuple(self._api(name) for name in (
            "initialize_v2_admin", "read_v2_admin", "save_v2_admin", "connection_v2_admin"))

    def _view(self, view, model, private):
        self.assertEqual(VIEW_KEYS, set(view))
        self.assertEqual(2, view["schemaVersion"])
        self.assertIs(type(view["schemaVersion"]), int)
        self.assertRegex(view["revision"], TOKEN)
        self.assertEqual("saved-not-applied", view["state"])
        self.assertIs(False, view["canApply"])
        self.assertEqual(model["ServerIp"], view["sipServer"])
        self.assertEqual(model["SipPort"], view["sipPort"])
        self.assertEqual({"calls": model["CallsEnabled"], "carrier": model["CarrierEnabled"],
                          "messages": model["MessagesEnabled"], "sms": model["SmsEnabled"]}, view["features"])
        self.assertEqual(model["Extensions"], [row["number"] for row in view["extensions"]])
        for row in view["extensions"]:
            self.assertEqual(ROW_KEYS, set(row))
            self.assertEqual(row["number"], row["sourceNumber"])
            option = model["ExtensionOptions"][row["number"]]
            for public, stored in OPTION_FIELDS.items():
                self.assertEqual(option[stored], row[public])
            self.assertIs(True, row["hasPassword"])
            for flag in FLAGS:
                self.assertIs(type(row[flag]), bool)
        output = json.dumps(view, ensure_ascii=False)
        for value in [*private.keys(), *private.values(), SENTINEL]:
            self.assertNotIn(value, output)

    def _request(self, view):
        rows = []
        for saved in view["extensions"]:
            row = {key: deepcopy(value) for key, value in saved.items() if key != "hasPassword"}
            row["passwordAction"] = "keep"
            rows.append(row)
        return {"revision": view["revision"], "extensions": rows}

    def _new(self, number, action="generate", password=None):
        row = {"number": number, "sourceNumber": "", "name": "", "enabled": False, "ring": False,
               "maxContacts": 4, "allowOutgoing": False, "allowMessages": False,
               "allowMobileSmsSend": False, "allowMobileSmsReceive": False, "passwordAction": action}
        if password is not None:
            row["password"] = password
        return row

    def _stored(self, root, view):
        directory = root / "revisions" / view["revision"]
        return (json.loads((directory / "deployment.json").read_bytes()),
                json.loads((directory / "secrets.json").read_bytes()))

    def _error(self, function, code="invalid", extra=()):
        with self.assertRaises(ValueError) as caught:
            function()
        error = caught.exception
        self.assertIn(getattr(error, "code", None), {"invalid", "conflict", "uninitialized", "uncertain"})
        if code is not None:
            self.assertEqual(code, error.code)
        diagnostic = str(error) + repr(error) + "".join(traceback.format_exception(error))
        for value in [SENTINEL, *synthetic_secrets().values(), *extra]:
            self.assertNotIn(value, diagnostic)
        return error

    def test_initialize_projects_exact_redacted_view_and_private_snapshot(self):
        initialize, read, _, _ = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            root = Path(temporary) / "store"
            model, private = synthetic_model(), synthetic_secrets()
            view = initialize(root, encoded(model), encoded(private))
            self._view(view, model, private)
            before = tree(root)
            self.assertEqual(view, read(root))
            self.assertEqual(before, tree(root), "read must not initialize, normalize or rewrite")
            self.assertEqual((model, private), self._stored(root, view))
            self.assertEqual({"state.json", "lock", "revisions"}, {path.name for path in root.iterdir()})
            self.assertEqual({"deployment.json", "secrets.json", "manifest.json"},
                             {path.name for path in (root / "revisions" / view["revision"]).iterdir()})
            for path in [root, *root.rglob("*")]:
                self.assertFalse(path.is_symlink())
                self.assertEqual(0o700 if path.is_dir() else 0o600, stat.S_IMODE(path.stat().st_mode))

    def test_initialize_missing_policy_fields_defaults_to_denial_not_legacy_enable(self):
        initialize, _, _, _ = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            model, private = synthetic_model(), synthetic_secrets()
            for feature in ("CallsEnabled", "CarrierEnabled", "MessagesEnabled", "SmsEnabled"):
                del model[feature]
            model["ExtensionOptions"] = {number: {"SecretRef": model["ExtensionOptions"][number]["SecretRef"]}
                                         for number in model["Extensions"]}
            view = initialize(Path(temporary) / "store", encoded(model), encoded(private))
            self.assertEqual({"calls": False, "carrier": False, "messages": False, "sms": False}, view["features"])
            for row in view["extensions"]:
                self.assertTrue(all(row[flag] is False for flag in FLAGS))
                self.assertEqual(4, row["maxContacts"])
                self.assertEqual("", row["name"])
            self.assertFalse(view["canApply"])

    def test_read_never_initializes_and_initialize_never_replaces_existing_root(self):
        initialize, read, _, _ = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            parent = Path(temporary)
            missing = parent / "missing"
            self._error(lambda: read(missing), "uninitialized")
            self.assertFalse(os.path.lexists(missing))
            for kind in ("empty", "legacy", "partial", "initialized"):
                with self.subTest(kind=kind):
                    root = parent / kind
                    if kind == "initialized":
                        initialize(root, encoded(synthetic_model()), encoded(synthetic_secrets()))
                    else:
                        root.mkdir(mode=0o700)
                        if kind == "legacy":
                            (root / "credentials.json").write_text(json.dumps({"4201": SENTINEL}))
                        if kind == "partial":
                            (root / "revisions").mkdir(mode=0o700)
                    before = tree(root)
                    self._error(lambda: initialize(root, encoded(synthetic_model()), encoded(synthetic_secrets())), "conflict")
                    self.assertEqual(before, tree(root))
                    if kind != "initialized":
                        self._error(lambda: read(root), "invalid")
                        self.assertEqual(before, tree(root))

    def test_initialize_rejects_strict_public_json_and_exact_private_role_map(self):
        initialize, _, _, _ = self._apis()
        public, private = encoded(synthetic_model()), encoded(synthetic_secrets())
        cases = [(None, private), ("{}", private), (b"null", private), (b"[]", private), (b"\xff", private),
                 (public.replace(b'"SchemaVersion":2', b'"SchemaVersion":2,"schemaVersion":2', 1), private),
                 (public.replace(b'"SchemaVersion":2', b'"SchemaVersion":2,"Schema\\u0056ersion":2', 1), private),
                 (public.replace(b'"SchemaVersion":2', b'"SchemaVersion":true', 1), private),
                 (public[:-2] + b',"' + SENTINEL.encode() + b'":1}\n', private),
                 (public, None), (public, bytearray(private)), (public, b"null"), (public, private + b"{}"),
                 (public, private.replace(b'"peer-in-reference":', b'"peer-in-reference":"duplicate-secret","peer-in-reference":', 1)),
                 (public, private.replace(b'"peer-in-reference":', b'"PEER-IN-REFERENCE":"duplicate-secret","peer-in-reference":', 1))]
        for mutate in (lambda value: value.pop("peer-out-reference"),
                       lambda value: value.update({SENTINEL: SENTINEL}),
                       lambda value: value.update({"pbx-first-reference": "short"}),
                       lambda value: value.update({"peer-in-reference": None})):
            changed = synthetic_secrets()
            mutate(changed)
            cases.append((public, encoded(changed)))
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            for index, (public_bytes, secret_bytes) in enumerate(cases):
                with self.subTest(index=index):
                    root = Path(temporary) / str(index)
                    self._error(lambda: initialize(root, public_bytes, secret_bytes))
                    self.assertFalse(os.path.lexists(root))

    def test_save_preserves_entire_other_model_and_independent_permissions(self):
        initialize, read, save, _ = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            root = Path(temporary) / "store"
            model, private = synthetic_model(), synthetic_secrets()
            first = initialize(root, encoded(model), encoded(private))
            request = self._request(first)
            request["extensions"][0].update(name="Changed explicit PBX", enabled=False, ring=True,
                                              allowOutgoing=True, allowMessages=False,
                                              allowMobileSmsSend=True, allowMobileSmsReceive=False)
            after = save(root, encoded(request))
            expected = deepcopy(model)
            for public, stored in OPTION_FIELDS.items():
                expected["ExtensionOptions"]["4201"][stored] = request["extensions"][0][public]
            self.assertNotEqual(first["revision"], after["revision"])
            self._view(after, expected, private)
            self.assertEqual((expected, private), self._stored(root, after))
            self.assertEqual(after, read(root))
            self.assertEqual((model, private), self._stored(root, first), "committed historical revisions are immutable")

    def test_new_generate_replace_keep_and_delete_keep_roles_independent(self):
        initialize, _, save, connection = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            root = Path(temporary) / "store"
            model, private = synthetic_model(), synthetic_secrets()
            # Equal explicitly chosen values do not merge independent roles.
            private["peer-out-reference"] = private["peer-in-reference"]
            first = initialize(root, encoded(model), encoded(private))
            request = self._request(first)
            request["extensions"] = [request["extensions"][0], self._new("4203"),
                                      self._new("4204", "replace", SENTINEL)]
            request["extensions"][0].update(passwordAction="replace", password="A" * 12)
            after = save(root, encoded(request))
            saved, secrets = self._stored(root, after)
            self.assertEqual(["4201", "4203", "4204"], saved["Extensions"])
            self.assertEqual("pbx-first-reference", saved["ExtensionOptions"]["4201"]["SecretRef"])
            self.assertNotIn("pbx-second-reference", secrets)
            for ref, password in private.items():
                if ref not in ("pbx-first-reference", "pbx-second-reference"):
                    self.assertEqual(password, secrets[ref])
            refs = [saved["ExtensionOptions"][number]["SecretRef"] for number in saved["Extensions"]]
            self.assertEqual(len(secrets), len({ref.casefold() for ref in secrets}))
            self.assertNotIn(refs[1], private)
            self.assertNotIn(refs[2], private)
            self.assertNotEqual(refs[1], refs[2])
            self.assertEqual("A" * 12, secrets[refs[0]])
            self.assertEqual(SENTINEL, secrets[refs[2]])
            self.assertRegex(secrets[refs[1]], r"[A-Za-z0-9_-]{12,128}\Z")
            self.assertNotIn(secrets[refs[1]], [*private.values(), SENTINEL, "A" * 12])
            for number in ("4203", "4204"):
                row = next(row for row in after["extensions"] if row["number"] == number)
                self.assertTrue(all(row[flag] is False for flag in FLAGS))
                result = connection(root, encoded({"revision": after["revision"], "number": number}))
                self.assertEqual(secrets[saved["ExtensionOptions"][number]["SecretRef"]], result["password"])
            again = save(root, encoded(self._request(after)))
            self.assertEqual((saved, secrets), self._stored(root, again))

    def test_empty_extension_list_is_valid_and_does_not_remove_other_roles(self):
        initialize, _, save, _ = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            root = Path(temporary) / "store"
            model, private = synthetic_model(), synthetic_secrets()
            before = initialize(root, encoded(model), encoded(private))
            after = save(root, encoded({"revision": before["revision"], "extensions": []}))
            expected = deepcopy(model)
            expected.update(Extensions=[], ExtensionOptions={})
            kept = {ref: password for ref, password in private.items() if not ref.startswith("pbx-")}
            self._view(after, expected, kept)
            self.assertEqual((expected, kept), self._stored(root, after))

    def test_save_strict_unknown_duplicate_escaped_casefold_and_missing_fields(self):
        initialize, _, save, _ = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            root = Path(temporary) / "store"
            view = initialize(root, encoded(synthetic_model()), encoded(synthetic_secrets()))
            base = self._request(view)
            raw = encoded(base)
            cases = [raw + b"{}", b"[]", b"null", b"{", b"\xff",
                     raw.replace(b'"revision":', b'"revision":"duplicate","revision":', 1),
                     raw.replace(b'"revision":', b'"Revision":"duplicate","revision":', 1),
                     raw.replace(b'"revision":', b'"revis\\u0069on":"duplicate","revision":', 1),
                     raw.replace(b'"name":', b'"name":"duplicate","name":', 1),
                     raw.replace(b'"name":', b'"Name":"duplicate","name":', 1),
                     raw.replace(b'"name":', b'"na\\u006de":"duplicate","name":', 1)]
            for key in base:
                value = deepcopy(base)
                del value[key]
                cases.append(encoded(value))
            for key in base["extensions"][0]:
                value = deepcopy(base)
                del value["extensions"][0][key]
                cases.append(encoded(value))
            for key in (SENTINEL, "SecretRef", "hasPassword", "apply", "Peer"):
                value = deepcopy(base)
                value["extensions"][0][key] = SENTINEL
                cases.append(encoded(value))
            value = deepcopy(base)
            value[SENTINEL] = {"nested": SENTINEL}
            cases.append(encoded(value))
            cases.append(b'{"revision":"' + view["revision"].encode() + b'","extensions":' + b"[" * 2000 + b"0" + b"]" * 2000 + b"}")
            before = tree(root)
            for index, request in enumerate(cases):
                with self.subTest(index=index):
                    self._error(lambda: save(root, request))
                    self.assertEqual(before, tree(root))

    def test_save_rejects_null_wrong_types_and_malformed_unicode_without_mutation(self):
        initialize, _, save, _ = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            root = Path(temporary) / "store"
            view = initialize(root, encoded(synthetic_model()), encoded(synthetic_secrets()))
            base = self._request(view)
            cases = []
            for key, invalid in [("revision", None), ("revision", 123), ("extensions", None), ("extensions", {})]:
                value = deepcopy(base)
                value[key] = invalid
                cases.append(encoded(value))
            for key in base["extensions"][0]:
                for invalid in (None, []):
                    value = deepcopy(base)
                    value["extensions"][0][key] = invalid
                    cases.append(encoded(value))
            for key in FLAGS:
                value = deepcopy(base)
                value["extensions"][0][key] = 1
                cases.append(encoded(value))
            for invalid in (True, 1.5, "4"):
                value = deepcopy(base)
                value["extensions"][0]["maxContacts"] = invalid
                cases.append(encoded(value))
            for text in ("bad\n[endpoint]", "bad\x00name", SENTINEL + "\ud800"):
                value = deepcopy(base)
                value["extensions"][0]["name"] = text
                cases.append((json.dumps(value, ensure_ascii=True) + "\n").encode())
            before = tree(root)
            for index, request in enumerate([None, "{}", bytearray(b"{}"), *cases]):
                with self.subTest(index=index):
                    self._error(lambda: save(root, request))
                    self.assertEqual(before, tree(root))

    def test_exact_65536_utf8_byte_limits_have_valid_positive_controls(self):
        initialize, _, save, connection = self._apis()
        public, private = encoded(synthetic_model()), encoded(synthetic_secrets())
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            parent = Path(temporary)
            for index, (model_bytes, secret_bytes) in enumerate(((public.ljust(65537), private), (public, private.ljust(65537)))):
                root = parent / ("over-" + str(index))
                self._error(lambda: initialize(root, model_bytes, secret_bytes))
                self.assertFalse(os.path.lexists(root))
            root = parent / "store"
            first = initialize(root, public.ljust(65536), private.ljust(65536))
            request = self._request(first)
            request["extensions"][0]["name"] = "中文测试"
            raw = encoded(request)
            self.assertGreater(len(raw), len(raw.decode("utf-8")))
            before = tree(root)
            self._error(lambda: save(root, raw.ljust(65537)))
            self.assertEqual(before, tree(root))
            after = save(root, raw.ljust(65536))
            query = encoded({"revision": after["revision"], "number": "4201"})
            self.assertEqual("4201", connection(root, query.ljust(65536))["username"])
            before = tree(root)
            self._error(lambda: connection(root, query.ljust(65537)))
            self.assertEqual(before, tree(root))

    def test_account_number_contact_and_collection_boundaries_have_positive_controls(self):
        initialize, _, save, _ = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            root = Path(temporary) / "store"
            view = initialize(root, encoded(synthetic_model()), encoded(synthetic_secrets()))
            base = self._request(view)
            for number in ("1", "123456789", "+4201", "42a1", 4201):
                value = deepcopy(base)
                value["extensions"] = [self._new(number)]
                self._error(lambda: save(root, encoded(value)))
            for maximum in (0, 9):
                value = deepcopy(base)
                value["extensions"][0]["maxContacts"] = maximum
                self._error(lambda: save(root, encoded(value)))
            duplicate = deepcopy(base)
            duplicate["extensions"].append(deepcopy(duplicate["extensions"][0]))
            self._error(lambda: save(root, encoded(duplicate)))
            rows = [self._new("00"), self._new("99999999")]
            rows += [self._new(str(5000 + index)) for index in range(62)]
            rows[0]["maxContacts"], rows[1]["maxContacts"] = 1, 8
            over = {"revision": view["revision"], "extensions": rows + [self._new("6000")]}
            before = tree(root)
            self._error(lambda: save(root, encoded(over)))
            self.assertEqual(before, tree(root))
            after = save(root, encoded({"revision": view["revision"], "extensions": rows}))
            self.assertEqual(64, len(after["extensions"]))
            self.assertEqual([row["number"] for row in rows], [row["number"] for row in after["extensions"]])

    def test_connection_uses_actual_endpoint_and_reveals_only_chosen_pbx_role(self):
        initialize, _, _, connection = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            root = Path(temporary) / "store"
            model, private = synthetic_model(), synthetic_secrets()
            view = initialize(root, encoded(model), encoded(private))
            before = tree(root)
            result = connection(root, encoded({"revision": view["revision"], "number": "4201"}))
            self.assertEqual({"revision": view["revision"], "state": "saved-not-applied", "server": model["ServerIp"],
                              "port": model["SipPort"], "username": "4201", "password": private["pbx-first-reference"],
                              "transport": "UDP"}, result)
            self.assertNotEqual(model["MessageDomain"], result["server"])
            for ref, password in private.items():
                self.assertNotIn(ref, json.dumps(result))
                if ref != "pbx-first-reference":
                    self.assertNotIn(password, json.dumps(result))
            self.assertEqual(before, tree(root))
            for value in ({"revision": view["revision"], "number": "desk-extra"},
                          {"revision": view["revision"], "number": "peer-in-reference"},
                          {"revision": view["revision"], "number": "4201", "SecretRef": "peer-in-reference"},
                          {"number": "4201"}, {"revision": view["revision"], "number": None}):
                self._error(lambda: connection(root, encoded(value)))
                self.assertEqual(before, tree(root))
            query = encoded({"revision": view["revision"], "number": "4201"})
            for raw in (query.replace(b'"number":', b'"number":"4202","number":', 1),
                        query.replace(b'"number":', b'"Number":"4202","number":', 1),
                        query.replace(b'"number":', b'"num\\u0062er":"4202","number":', 1),
                        query + b"{}", b"\xff", b"null"):
                self._error(lambda: connection(root, raw))
                self.assertEqual(before, tree(root))

    def test_stale_save_and_connection_compare_and_swap_before_mutation(self):
        initialize, read, save, connection = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            root = Path(temporary) / "store"
            first = initialize(root, encoded(synthetic_model()), encoded(synthetic_secrets()))
            request = self._request(first)
            request["extensions"][0].update(passwordAction="replace", password="Z" * 128)
            second = save(root, encoded(request))
            self.assertNotEqual(first["revision"], second["revision"])
            before = tree(root)
            self._error(lambda: save(root, encoded(self._request(first))), "conflict")
            self._error(lambda: connection(root, encoded({"revision": first["revision"], "number": "4201"})), "conflict")
            self.assertEqual(before, tree(root))
            self.assertEqual(second, read(root))

    def test_two_threads_with_one_revision_have_exactly_one_commit_winner(self):
        initialize, read, save, _ = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            root = Path(temporary) / "store"
            initial = initialize(root, encoded(synthetic_model()), encoded(synthetic_secrets()))
            barrier = threading.Barrier(2)
            def worker(name):
                request = self._request(initial)
                request["extensions"][0]["name"] = name
                barrier.wait(timeout=5)
                try:
                    return ("saved", save(root, encoded(request)))
                except ValueError as error:
                    return ("error", getattr(error, "code", None))
            with ThreadPoolExecutor(max_workers=2) as pool:
                results = list(pool.map(worker, ["First contender", "Second contender"]))
            winners = [value for kind, value in results if kind == "saved"]
            self.assertEqual(1, len(winners))
            self.assertEqual(["conflict"], [value for kind, value in results if kind == "error"])
            self.assertEqual(winners[0], read(root))
            self.assertNotEqual(initial["revision"], winners[0]["revision"])
            self.assertEqual((synthetic_model(), synthetic_secrets()), self._stored(root, initial))

    def test_replace_failure_keeps_old_head_and_original_revision_bytes(self):
        initialize, read, save, _ = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            root = Path(temporary) / "store"
            first = initialize(root, encoded(synthetic_model()), encoded(synthetic_secrets()))
            head = (root / "state.json").read_bytes()
            old_revision = tree(root / "revisions" / first["revision"])
            request = self._request(first)
            request["extensions"][0]["name"] = "Replacement must fail"
            with patch("os.replace", side_effect=OSError(SENTINEL)) as replace:
                self._error(lambda: save(root, encoded(request)), code=None)
                self.assertTrue(replace.called)
            self.assertEqual(head, (root / "state.json").read_bytes())
            self.assertEqual(old_revision, tree(root / "revisions" / first["revision"]))
            self.assertEqual(first, read(root))

    def test_symlinks_with_otherwise_valid_contents_are_never_followed(self):
        initialize, read, save, _ = self._apis()
        names = ("root", "state.json", "lock", "revisions", "revision", "deployment.json", "secrets.json", "manifest.json")
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            parent = Path(temporary)
            for index, name in enumerate(names):
                with self.subTest(target=name):
                    root = parent / ("store-" + str(index))
                    view = initialize(root, encoded(synthetic_model()), encoded(synthetic_secrets()))
                    revision = root / "revisions" / view["revision"]
                    target = root if name == "root" else root / name if name in ("state.json", "lock", "revisions") else revision if name == "revision" else revision / name
                    outside = parent / ("outside-" + str(index))
                    if target.is_dir():
                        shutil.copytree(target, outside)
                    else:
                        outside.write_bytes(target.read_bytes())
                        outside.chmod(0o600)
                    target.rename(parent / ("original-" + str(index)))
                    target.symlink_to(outside, target_is_directory=outside.is_dir())
                    before = tree(outside)
                    self._error(lambda: read(root))
                    self._error(lambda: save(root, encoded(self._request(view))))
                    self.assertEqual(before, tree(outside))
            real_parent = parent / "real-parent"
            real_parent.mkdir(mode=0o700)
            alias = parent / "parent-alias"
            alias.symlink_to(real_parent, target_is_directory=True)
            self._error(lambda: initialize(alias / "store", encoded(synthetic_model()), encoded(synthetic_secrets())))
            self.assertEqual([], list(real_parent.iterdir()))

    def test_corrupt_or_nonprivate_store_is_not_reinitialized_or_repaired_by_read(self):
        initialize, read, save, _ = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            for index, name in enumerate(("state.json", "deployment.json", "secrets.json", "manifest.json", "public-mode")):
                with self.subTest(target=name):
                    root = Path(temporary) / str(index)
                    view = initialize(root, encoded(synthetic_model()), encoded(synthetic_secrets()))
                    target = root / "state.json" if name in ("state.json", "public-mode") else root / "revisions" / view["revision"] / name
                    if name == "public-mode":
                        target.chmod(0o644)
                    else:
                        target.write_bytes(b"{}\n")
                    before = tree(root)
                    self._error(lambda: read(root))
                    self._error(lambda: save(root, encoded(self._request(view))))
                    self._error(lambda: initialize(root, encoded(synthetic_model()), encoded(synthetic_secrets())), "conflict")
                    self.assertEqual(before, tree(root))

    def test_no_process_network_actions_and_only_linux_absolute_store_paths(self):
        initialize, read, save, connection = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            parent = Path(temporary)
            with ExitStack() as stack:
                guards = [stack.enter_context(patch.object(owner, name, side_effect=AssertionError("forbidden external action")))
                          for owner, name in ((subprocess, "Popen"), (subprocess, "run"), (os, "system"),
                                              (socket, "socket"), (socket, "create_connection"))]
                root = parent / "store"
                view = initialize(root, encoded(synthetic_model()), encoded(synthetic_secrets()))
                self.assertEqual(view, read(root))
                view = save(root, encoded(self._request(view)))
                connection(root, encoded({"revision": view["revision"], "number": "4201"}))
                for guard in guards:
                    guard.assert_not_called()
            before = tree(root)
            for platform in ("win32", "darwin"):
                with self.subTest(platform=platform), patch.object(sys, "platform", platform):
                    self._error(lambda: read(root))
                    self._error(lambda: save(root, encoded(self._request(view))))
                    self._error(lambda: connection(root, encoded({"revision": view["revision"], "number": "4201"})))
                    self._error(lambda: initialize(parent / platform, encoded(synthetic_model()), encoded(synthetic_secrets())))
            self.assertEqual(before, tree(root))
            self._error(lambda: read(Path("synthetic-relative-store")))

    def test_returned_views_are_detached_and_no_secret_refs_or_hashes_are_public(self):
        initialize, read, save, _ = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            root = Path(temporary) / "store"
            model, private = synthetic_model(), synthetic_secrets()
            view = initialize(root, encoded(model), encoded(private))
            original = deepcopy(view)
            view["features"]["sms"] = not view["features"]["sms"]
            view["extensions"][0]["name"] = SENTINEL
            view["extensions"].clear()
            self.assertEqual(original, read(root))
            after = save(root, encoded(self._request(original)))
            self._view(after, model, private)
            self.assertEqual(VIEW_KEYS, set(after))
            self.assertFalse(any("hash" in key.casefold() or "secret" in key.casefold() for key in after))

    def test_password_actions_sources_and_secret_boundaries_reject_before_any_commit(self):
        initialize, _, save, _ = self._apis()
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-unit-") as temporary:
            root = Path(temporary) / "store"
            view = initialize(root, encoded(synthetic_model()), encoded(synthetic_secrets()))
            base = self._request(view)
            invalid_rows = []
            for change in ({"passwordAction": "unknown"}, {"passwordAction": "KEEP"}, {"password": SENTINEL},
                           {"passwordAction": "generate", "password": SENTINEL}, {"passwordAction": "replace"},
                           {"passwordAction": "replace", "password": ""}, {"passwordAction": "replace", "password": "a" * 11},
                           {"passwordAction": "replace", "password": "a" * 129},
                           {"passwordAction": "replace", "password": SENTINEL + "\n"},
                           {"sourceNumber": "4202"}, {"sourceNumber": "4299"}, {"sourceNumber": ""},
                           {"number": "4299"}, {"SecretRef": "peer-in-reference"}):
                row = deepcopy(base["extensions"][0])
                row.update(change)
                invalid_rows.append(row)
            invalid_rows.append(self._new("4299", "keep"))
            before = tree(root)
            for index, row in enumerate(invalid_rows):
                with self.subTest(index=index):
                    request = {"revision": view["revision"], "extensions": [row, base["extensions"][1]]}
                    self._error(lambda: save(root, encoded(request)))
                    self.assertEqual(before, tree(root))


if __name__ == "__main__":
    unittest.main()
