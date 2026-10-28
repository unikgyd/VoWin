"""Independent, offline, actual-filesystem v2 application contract.

The absent application API is an assertion RED, never an import error/skip.
The backend only observes lifecycle calls and injects faults: it NEVER installs
or restores a file. All twenty-file changes, journals, CAS and recovery must be
performed by the real application engine, in this test's owned Linux TEMP.
No Docker, PBX, listener, modem, call, SMS or existing deployment is used.
"""
from contextlib import contextmanager
from copy import deepcopy
import importlib
import importlib.util
import json
import os
from pathlib import Path
import re
import socket
import stat
import subprocess
import sys
import tempfile
import traceback
import unittest
from unittest.mock import patch

from admin_deployment_v2 import read_v2_admin, save_v2_admin
from admin_v2_store import initialize_store, read_store
from deployment_bundle_v2 import INSTALLABLE_OUTPUTS, write_installable_deployment_bundle
from render_deployment_v2 import render_installable_deployment_v2
from test_admin_deployment_v2 import SENTINEL, encoded, synthetic_model, synthetic_secrets


TOKEN = re.compile(r"[A-Za-z0-9_-]{43}\Z")
PUBLIC_KEYS = {"operation", "revision", "state", "fileCount", "changedFiles",
               "pbxApplied", "gatewayApplied", "firewallApplied", "statusSource"}
OUTPUTS = tuple(sorted(INSTALLABLE_OUTPUTS))
CONFIG_OUTPUTS = tuple(name for name in OUTPUTS if name.startswith("config/"))


def identity(info):
    return info.st_dev, info.st_ino


def managed_bytes(base):
    return {name: (base / name).read_bytes() for name in OUTPUTS}


def managed_metadata(base):
    result = {}
    for name in OUTPUTS:
        file = base / name
        info = file.lstat()
        result[name] = (file.read_bytes(), stat.S_IMODE(info.st_mode), info.st_uid, info.st_gid)
    return result


def private_tree(base):
    result = {}
    if not base.exists():
        return result
    for file in [base, *sorted(base.rglob("*"))]:
        info = file.lstat()
        key = "." if file == base else file.relative_to(base).as_posix()
        if stat.S_ISDIR(info.st_mode):
            result[key] = ("directory", stat.S_IMODE(info.st_mode), info.st_uid, info.st_gid)
        elif stat.S_ISLNK(info.st_mode):
            result[key] = ("symlink", os.readlink(file))
        else:
            result[key] = ("file", stat.S_IMODE(info.st_mode), info.st_uid, info.st_gid,
                           info.st_nlink, file.read_bytes())
    return result


def different_token(value):
    return ("B" if value[0] == "A" else "A") + value[1:]


def save_payload(view):
    return {"revision": view["revision"], "extensions": [
        {**{key: deepcopy(value) for key, value in row.items() if key != "hasPassword"},
         "passwordAction": "keep"} for row in view["extensions"]]}


class ObservingBackend:
    """No file writes; checks installation is between real stop/start calls."""

    def __init__(self, case, base, original, candidate, model):
        self.case, self.base = case, base
        self.original, self.candidate, self.model = original, candidate, deepcopy(model)
        self.target = {"containerId": "synthetic-container-id", "imageId": "synthetic-image-id",
                       "configRoot": str(base / "config"), "user": "10001:10001",
                       "restartPolicy": "unless-stopped"}
        self.running = True
        self.policy = "unless-stopped"
        self.idle_result = None
        self.calls, self.failures, self.injected, self.starts, self.verifications = [], {}, [], [], []
        self.counts = {}

    def _record(self, name, value=None):
        self.calls.append((name, deepcopy(value)))
        ordinal = self.counts.get(name, 0) + 1
        self.counts[name] = ordinal
        if ordinal in self.failures.get(name, set()):
            self.injected.append((name, ordinal))
            raise RuntimeError(SENTINEL)

    def _target(self, target):
        self.case.assertEqual(self.target, target)

    def capture(self):
        self._record("capture")
        return deepcopy(self.target)

    def preflight(self, config_path, model):
        directory = Path(config_path)
        self.case.assertEqual(self.model, model)
        self.case.assertEqual({name.removeprefix("config/") for name in CONFIG_OUTPUTS},
                              {file.name for file in directory.iterdir()})
        self.case.assertEqual({name.removeprefix("config/"): self.candidate[name] for name in CONFIG_OUTPUTS},
                              {file.name: file.read_bytes() for file in directory.iterdir()})
        self._record("preflight", str(directory))

    def idle(self, target):
        self._target(target)
        self._record("idle")
        return self.idle_result

    def stop(self, target):
        self._target(target)
        current = managed_bytes(self.base)
        self.case.assertIn(current, (self.original, self.candidate), "Stop must not observe a mixed installation")
        self._record("stop", current)
        self.running = False
        self.policy = "no"

    def start(self, target):
        self._target(target)
        self.case.assertFalse(self.running, "Installation/recovery must start from a stopped backend")
        current = managed_bytes(self.base)
        self.case.assertIn(current, (self.original, self.candidate), "All twenty files must be complete before start")
        self._record("start", current)
        self.starts.append(deepcopy(current))
        self.running = True

    def verify(self, target, expected_config_path, model):
        self._target(target)
        self.case.assertTrue(self.running)
        current = managed_bytes(self.base)
        directory = Path(expected_config_path)
        expected = {name.removeprefix("config/"): current[name] for name in CONFIG_OUTPUTS}
        self.case.assertEqual(expected, {file.name: file.read_bytes() for file in directory.iterdir()})
        if model is None:
            self.case.assertEqual(self.original, current)
        else:
            self.case.assertEqual(self.model, model)
            self.case.assertEqual(self.candidate, current)
        self._record("verify", {"config": str(directory), "original": model is None})
        self.verifications.append(deepcopy(current))

    def restore_policy(self, target):
        self._target(target)
        self._record("restore_policy")
        self.policy = target["restartPolicy"]


class AdminV2ApplicationTests(unittest.TestCase):
    def _api(self, name):
        spec = importlib.util.find_spec("admin_v2_apply")
        module = importlib.import_module("admin_v2_apply") if spec is not None else None
        value = getattr(module, name, None)
        self.assertTrue(callable(value), "Explicit v2 application API must exist")
        return value

    def _apis(self):
        return tuple(self._api(name) for name in (
            "prepare_application", "apply_application", "recover_application", "application_status"))

    @contextmanager
    def _fixture(self):
        self.assertEqual("linux", sys.platform)
        self.assertEqual(0, os.geteuid(), "The owned native-Linux application fixture requires root")
        with tempfile.TemporaryDirectory(prefix="vowin-apply-v2-unit-") as temporary:
            parent = Path(temporary)
            parent.chmod(0o700)
            store, base = parent / "store", parent / "installed"
            model, private = synthetic_model(), synthetic_secrets()
            initialize_store(store, model, private)
            old_bundle = render_installable_deployment_v2(model, private)
            write_installable_deployment_bundle(old_bundle, base)
            # Legal extra whitespace makes every one of the twenty source
            # files observably different from the candidate's canonical text.
            for name in OUTPUTS:
                file = base / name
                file.write_bytes(file.read_bytes() + b"\n")
                if name.startswith("config/"):
                    os.chown(file, 10001, 10001)
            # This is an already configured runtime, not enrollment of a
            # root-only export directory. Preserve its daemon-owned inode.
            os.chown(base / "config", 10001, 10001)
            old_bytes, old_metadata = managed_bytes(base), managed_metadata(base)
            old_config = base.joinpath("config").stat()
            old_view = read_v2_admin(store)
            payload = save_payload(old_view)
            payload["extensions"] = payload["extensions"][:1]
            row = payload["extensions"][0]
            row.update({"name": "Prepared independently", "enabled": False, "ring": False,
                        "maxContacts": 4, "allowOutgoing": False, "allowMessages": False,
                        "allowMobileSmsSend": False, "allowMobileSmsReceive": False,
                        "passwordAction": "replace", "password": "synthetic-replacement-pbx-credential"})
            view = save_v2_admin(store, encoded(payload))
            snapshot = read_store(store)
            candidate = {name: value.encode("utf-8") for name, value in
                         render_installable_deployment_v2(snapshot["model"], snapshot["secrets"]).items()}
            self.assertEqual(set(OUTPUTS), set(candidate))
            self.assertTrue(all(candidate[name] != old_bytes[name] for name in OUTPUTS))
            backend = ObservingBackend(self, base, old_bytes, candidate, snapshot["model"])
            yield {"parent": parent, "store": store, "base": base, "view": view,
                   "revision": view["revision"], "snapshot": snapshot, "candidate": candidate,
                   "old_bytes": old_bytes, "old_metadata": old_metadata,
                   "config_inode": identity(old_config), "config_metadata": (
                       stat.S_IMODE(old_config.st_mode), old_config.st_uid, old_config.st_gid),
                   "backend": backend}

    def _public(self, value, fixture=None, state=None):
        self.assertIs(type(value), dict)
        self.assertEqual(PUBLIC_KEYS, set(value))
        self.assertEqual("journal", value["statusSource"])
        self.assertIs(type(value["fileCount"]), int)
        self.assertIn(value["state"], {"uninitialized", "prepared", "stopping", "installing", "starting",
                                       "applied", "rolled-back", "recovery-required"})
        if state is not None:
            self.assertEqual(state, value["state"])
        if value["state"] == "uninitialized":
            self.assertIsNone(value["operation"])
            self.assertIsNone(value["revision"])
            self.assertEqual(0, value["fileCount"])
            self.assertEqual([], value["changedFiles"])
        else:
            self.assertRegex(value["operation"], TOKEN)
            self.assertRegex(value["revision"], TOKEN)
            self.assertIs(type(value["fileCount"]), int)
            self.assertEqual(20, value["fileCount"])
            self.assertEqual(list(OUTPUTS), value["changedFiles"])
            if fixture is not None:
                self.assertEqual(fixture["revision"], value["revision"])
        self.assertIs(value["state"] == "applied", value["pbxApplied"])
        self.assertIs(False, value["gatewayApplied"])
        self.assertIs(False, value["firewallApplied"])
        diagnostic = json.dumps(value, ensure_ascii=False)
        for secret in [SENTINEL, *synthetic_secrets().keys(), *synthetic_secrets().values(),
                       "synthetic-replacement-pbx-credential"]:
            self.assertNotIn(secret, diagnostic)
        if fixture is not None:
            self.assertNotIn(str(fixture["parent"]), diagnostic)
        return value

    def _error(self, action, code=None):
        error_type = self._api("ApplicationV2Error")
        try:
            action()
        except ValueError as error:
            self.assertIsInstance(error, error_type)
            self.assertIn(getattr(error, "code", None), {"invalid", "conflict", "busy", "uncertain"})
            if code is not None:
                self.assertEqual(code, error.code)
            diagnostic = str(error) + repr(error) + "".join(traceback.format_exception(error))
            for secret in [SENTINEL, *synthetic_secrets().values(), "synthetic-replacement-pbx-credential"]:
                self.assertNotIn(secret, diagnostic)
            return error
        self.fail("The actual application engine must reject this operation")

    def _prepare(self, prepare, fixture):
        return self._public(prepare(fixture["store"], fixture["base"], fixture["revision"], fixture["backend"]),
                            fixture, "prepared")

    def _apply(self, apply, fixture, prepared):
        return apply(fixture["store"], fixture["base"], fixture["revision"], prepared["operation"], fixture["backend"])

    def _failed_apply(self, apply, status, fixture, prepared):
        try:
            value = self._apply(apply, fixture, prepared)
        except ValueError as error:
            self._error(lambda: self._raise(error))
        else:
            self._public(value, fixture)
            self.assertNotEqual("applied", value["state"])
        return self._public(status(fixture["base"]), fixture)

    @staticmethod
    def _raise(error):
        raise error

    def _original_restored(self, fixture):
        self.assertEqual(fixture["old_metadata"], managed_metadata(fixture["base"]))
        info = (fixture["base"] / "config").stat()
        self.assertEqual(fixture["config_inode"], identity(info))
        self.assertEqual(fixture["config_metadata"], (stat.S_IMODE(info.st_mode), info.st_uid, info.st_gid))
        self.assertTrue(fixture["backend"].running)
        self.assertEqual("unless-stopped", fixture["backend"].policy)

    def _journal(self, fixture, prepared):
        return fixture["base"] / ".v2-application" / "operations" / prepared["operation"]

    def _new_revision(self, fixture, name="Newer draft must not be restored"):
        payload = save_payload(read_v2_admin(fixture["store"]))
        payload["extensions"][0]["name"] = name
        return save_v2_admin(fixture["store"], encoded(payload))["revision"]

    def test_status_is_readonly_uninitialized_then_detached_and_explicitly_journal_only(self):
        prepare, _, _, status = self._apis()
        with self._fixture() as f:
            before = private_tree(f["base"])
            self._public(status(f["base"]), state="uninitialized")
            self.assertEqual(before, private_tree(f["base"]))
            prepared = self._prepare(prepare, f)
            before = private_tree(f["base"])
            value = self._public(status(f["base"]), f, "prepared")
            self.assertEqual(prepared, value)
            value["changedFiles"].clear()
            self.assertEqual(prepared, status(f["base"]))
            self.assertEqual(before, private_tree(f["base"]))

    def test_prepare_materializes_exact_candidate_and_original_twenty_without_installing(self):
        prepare, _, _, _ = self._apis()
        with self._fixture() as f:
            before_store = private_tree(f["store"])
            prepared = self._prepare(prepare, f)
            operation = self._journal(f, prepared)
            self.assertEqual(f["candidate"], managed_bytes(operation / "candidate"))
            self.assertEqual(f["old_bytes"], managed_bytes(operation / "original"))
            self.assertEqual(set(OUTPUTS), {p.relative_to(operation / "candidate").as_posix()
                                          for p in (operation / "candidate").rglob("*") if p.is_file()})
            self.assertEqual(f["old_metadata"], managed_metadata(f["base"]))
            self.assertEqual(f["config_inode"], identity((f["base"] / "config").stat()))
            self.assertEqual(before_store, private_tree(f["store"]))
            self.assertTrue(f["backend"].running)
            self.assertFalse(any(name in {"stop", "start", "verify", "restore_policy"} for name, _ in f["backend"].calls))
            self.assertTrue((f["base"] / ".admin-operation-lock").is_dir(), "Legacy Go apply must remain blocked")
            for p in [f["base"] / ".v2-application", *(f["base"] / ".v2-application").rglob("*")]:
                self.assertEqual(0o700 if p.is_dir() else 0o600, stat.S_IMODE(p.lstat().st_mode))

    def test_apply_really_installs_all_twenty_keeps_config_inode_and_is_terminal_idempotent(self):
        prepare, apply, _, status = self._apis()
        with self._fixture() as f:
            prepared = self._prepare(prepare, f)
            original_store = private_tree(f["store"])
            applied = self._public(self._apply(apply, f, prepared), f, "applied")
            self.assertEqual(f["candidate"], managed_bytes(f["base"]))
            self.assertEqual(f["config_inode"], identity((f["base"] / "config").stat()))
            self.assertEqual([f["candidate"]], f["backend"].starts)
            self.assertEqual([f["candidate"]], f["backend"].verifications)
            self.assertEqual("unless-stopped", f["backend"].policy)
            self.assertEqual(original_store, private_tree(f["store"]))
            self.assertEqual(applied, status(f["base"]))
            for name in CONFIG_OUTPUTS:
                info = (f["base"] / name).stat()
                permissions = stat.S_IMODE(info.st_mode)
                readable = permissions & (0o400 if info.st_uid == 10001 else 0o040 if info.st_gid == 10001 else 0o004)
                self.assertTrue(readable, "The real runtime uid/gid must be able to read its config")
                self.assertEqual(0, permissions & 0o027)
            directory = (f["base"] / "config").stat()
            traverse = stat.S_IMODE(directory.st_mode) & (0o100 if directory.st_uid == 10001 else 0o010 if directory.st_gid == 10001 else 0o001)
            self.assertTrue(traverse)
            calls, installed = deepcopy(f["backend"].calls), private_tree(f["base"])
            self.assertEqual(applied, self._apply(apply, f, prepared))
            self.assertEqual(calls, f["backend"].calls)
            self.assertEqual(installed, private_tree(f["base"]))

    def test_reprepare_can_replace_only_prepared_operation_and_old_token_cannot_apply(self):
        prepare, apply, _, _ = self._apis()
        with self._fixture() as f:
            old = self._prepare(prepare, f)
            current = self._prepare(prepare, f)
            self.assertNotEqual(old["operation"], current["operation"])
            self._error(lambda: self._apply(apply, f, old), "conflict")
            self.assertEqual(f["old_metadata"], managed_metadata(f["base"]))
            self.assertEqual([], f["backend"].starts)
            self._public(self._apply(apply, f, current), f, "applied")

    def test_revision_and_operation_tokens_are_strict_and_unknown_valid_tokens_do_not_scan(self):
        prepare, apply, recover, _ = self._apis()
        for invalid in (None, True, 17, "", "A" * 42, "A" * 44, "../" + "A" * 40, SENTINEL + "\n"):
            with self.subTest(token=repr(invalid)), self._fixture() as f:
                self._error(lambda: prepare(f["store"], f["base"], invalid, f["backend"]), "invalid")
                prepared = self._prepare(prepare, f)
                self._error(lambda: apply(f["store"], f["base"], f["revision"], invalid, f["backend"]), "invalid")
                self._error(lambda: recover(f["store"], f["base"], invalid, f["backend"]), "invalid")
                self._error(lambda: apply(f["store"], f["base"], f["revision"], different_token(prepared["operation"]), f["backend"]), "conflict")
                self.assertEqual(f["old_metadata"], managed_metadata(f["base"]))
                self.assertEqual([], f["backend"].starts)

    def test_newer_draft_revision_conflicts_before_any_runtime_stop_or_file_installation(self):
        prepare, apply, _, _ = self._apis()
        with self._fixture() as f:
            prepared = self._prepare(prepare, f)
            newer = self._new_revision(f)
            self.assertNotEqual(f["revision"], newer)
            self._error(lambda: self._apply(apply, f, prepared), "conflict")
            self.assertEqual(f["old_metadata"], managed_metadata(f["base"]))
            self.assertEqual(0, f["backend"].counts.get("stop", 0))

    def test_pending_recovery_blocks_other_store_and_reapply_until_explicit_recovery(self):
        prepare, apply, _, status = self._apis()
        with self._fixture() as f:
            prepared = self._prepare(prepare, f)
            f["backend"].failures["start"] = {1, 2}
            failed = self._failed_apply(apply, status, f, prepared)
            self.assertEqual("recovery-required", failed["state"])
            self.assertEqual([("start", 1), ("start", 2)], f["backend"].injected)
            second_store = f["parent"] / "other-store"
            second = initialize_store(second_store, synthetic_model(), synthetic_secrets())
            calls, files = deepcopy(f["backend"].calls), managed_metadata(f["base"])
            self._error(lambda: prepare(second_store, f["base"], second["revision"], f["backend"]), "conflict")
            self._error(lambda: apply(second_store, f["base"], second["revision"], prepared["operation"], f["backend"]), "conflict")
            self._error(lambda: self._apply(apply, f, prepared), "conflict")
            self.assertEqual(failed, status(f["base"]))
            self.assertEqual(files, managed_metadata(f["base"]))
            self.assertEqual(calls, f["backend"].calls, "No implicit retry or recovery is allowed")

    def test_active_calls_refuse_apply_without_stopping_or_installing(self):
        prepare, apply, _, status = self._apis()
        with self._fixture() as f:
            prepared = self._prepare(prepare, f)
            f["backend"].idle_result = False
            self._error(lambda: self._apply(apply, f, prepared), "busy")
            self.assertGreater(f["backend"].counts.get("idle", 0), 0)
            self.assertEqual(0, f["backend"].counts.get("stop", 0))
            self.assertEqual(f["old_metadata"], managed_metadata(f["base"]))
            self.assertTrue(f["backend"].running)
            self._public(status(f["base"]), f, "prepared")

    def test_preflight_failure_is_safe_and_never_mutates_installed_twenty(self):
        prepare, _, _, _ = self._apis()
        with self._fixture() as f:
            f["backend"].failures["preflight"] = {1}
            self._error(lambda: self._prepare(prepare, f))
            self.assertEqual([("preflight", 1)], f["backend"].injected)
            self.assertEqual(f["old_metadata"], managed_metadata(f["base"]))
            self.assertEqual(0, f["backend"].counts.get("stop", 0))

    def test_stop_failure_never_installs_candidate_or_reports_applied(self):
        prepare, apply, _, status = self._apis()
        with self._fixture() as f:
            prepared = self._prepare(prepare, f)
            f["backend"].failures["stop"] = {1}
            value = self._failed_apply(apply, status, f, prepared)
            self.assertEqual([("stop", 1)], f["backend"].injected)
            self.assertNotEqual("applied", value["state"])
            self.assertEqual(f["old_metadata"], managed_metadata(f["base"]))
            self.assertTrue(f["backend"].running)

    def test_start_failure_really_restores_original_twenty_and_runtime_policy(self):
        prepare, apply, _, status = self._apis()
        with self._fixture() as f:
            prepared = self._prepare(prepare, f)
            f["backend"].failures["start"] = {1}
            value = self._failed_apply(apply, status, f, prepared)
            self.assertEqual("rolled-back", value["state"])
            self.assertEqual([("start", 1)], f["backend"].injected)
            self.assertEqual([f["old_bytes"]], f["backend"].starts)
            self.assertEqual([f["old_bytes"]], f["backend"].verifications)
            self._original_restored(f)

    def test_verify_failure_really_restores_original_twenty_after_candidate_started(self):
        prepare, apply, _, status = self._apis()
        with self._fixture() as f:
            prepared = self._prepare(prepare, f)
            f["backend"].failures["verify"] = {1}
            value = self._failed_apply(apply, status, f, prepared)
            self.assertEqual("rolled-back", value["state"])
            self.assertEqual([("verify", 1)], f["backend"].injected)
            self.assertEqual([f["candidate"], f["old_bytes"]], f["backend"].starts)
            self.assertEqual([f["old_bytes"]], f["backend"].verifications)
            self._original_restored(f)

    def test_explicit_recovery_uses_original_not_current_draft_and_is_idempotent(self):
        prepare, apply, recover, status = self._apis()
        with self._fixture() as f:
            prepared = self._prepare(prepare, f)
            f["backend"].failures["start"] = {1, 2}
            self.assertEqual("recovery-required", self._failed_apply(apply, status, f, prepared)["state"])
            self._new_revision(f)
            # Recovery must not parse or adopt a different/new corrupt draft.
            (f["store"] / "state.json").write_bytes(b"corrupt newer draft\n")
            f["backend"].failures.clear()
            value = self._public(recover(f["store"], f["base"], prepared["operation"], f["backend"]), f, "rolled-back")
            self._original_restored(f)
            calls, tree = deepcopy(f["backend"].calls), private_tree(f["base"])
            self.assertEqual(value, recover(f["store"], f["base"], prepared["operation"], f["backend"]))
            self.assertEqual(calls, f["backend"].calls)
            self.assertEqual(tree, private_tree(f["base"]))

    def test_recovery_refuses_third_party_edit_instead_of_overwriting_unknown_state(self):
        prepare, apply, recover, status = self._apis()
        with self._fixture() as f:
            prepared = self._prepare(prepare, f)
            f["backend"].failures["start"] = {1, 2}
            self.assertEqual("recovery-required", self._failed_apply(apply, status, f, prepared)["state"])
            changed = f["base"] / "config/rtp.conf"
            changed.write_bytes(changed.read_bytes() + b"; independent third-party edit\n")
            before, calls = managed_metadata(f["base"]), deepcopy(f["backend"].calls)
            self._error(lambda: recover(f["store"], f["base"], prepared["operation"], f["backend"]), "conflict")
            self.assertEqual(before, managed_metadata(f["base"]))
            self.assertEqual(calls, f["backend"].calls)
            self._public(status(f["base"]), f, "recovery-required")

    def test_manual_original_change_after_prepare_conflicts_before_stop(self):
        prepare, apply, _, _ = self._apis()
        with self._fixture() as f:
            prepared = self._prepare(prepare, f)
            changed = f["base"] / "config/extensions.conf"
            changed.write_bytes(changed.read_bytes() + b"; manual original edit\n")
            before = managed_metadata(f["base"])
            self._error(lambda: self._apply(apply, f, prepared), "conflict")
            self.assertEqual(before, managed_metadata(f["base"]))
            self.assertEqual(0, f["backend"].counts.get("stop", 0))

    def test_runtime_target_change_after_prepare_cannot_activate_a_different_container(self):
        prepare, apply, _, _ = self._apis()
        with self._fixture() as f:
            prepared = self._prepare(prepare, f)
            f["backend"].target["imageId"] = "different-synthetic-image-id"
            self._error(lambda: self._apply(apply, f, prepared), "conflict")
            self.assertEqual(f["old_metadata"], managed_metadata(f["base"]))
            self.assertEqual(0, f["backend"].counts.get("stop", 0))

    def test_corrupt_prepared_candidate_is_rejected_before_any_runtime_mutation(self):
        prepare, apply, _, _ = self._apis()
        with self._fixture() as f:
            prepared = self._prepare(prepare, f)
            candidate = self._journal(f, prepared) / "candidate/config/pjsip.conf"
            candidate.write_bytes(candidate.read_bytes() + SENTINEL.encode("ascii"))
            self._error(lambda: self._apply(apply, f, prepared), "invalid")
            self.assertEqual(f["old_metadata"], managed_metadata(f["base"]))
            self.assertEqual(0, f["backend"].counts.get("stop", 0))

    def test_actual_admin_save_deletion_and_four_independent_grants_are_the_installed_model(self):
        prepare, apply, _, _ = self._apis()
        with self._fixture() as f:
            request = save_payload(read_v2_admin(f["store"]))
            request["extensions"][0].update({"enabled": True, "allowOutgoing": True, "allowMessages": False,
                                              "allowMobileSmsSend": True, "allowMobileSmsReceive": False})
            view = save_v2_admin(f["store"], encoded(request))
            f["view"], f["revision"], f["snapshot"] = view, view["revision"], read_store(f["store"])
            f["candidate"] = {name: value.encode("utf-8") for name, value in
                              render_installable_deployment_v2(f["snapshot"]["model"], f["snapshot"]["secrets"]).items()}
            f["backend"].candidate = f["candidate"]
            f["backend"].model = deepcopy(f["snapshot"]["model"])
            source_before = private_tree(f["store"])
            prepared = self._prepare(prepare, f)
            self._public(self._apply(apply, f, prepared), f, "applied")
            installed = json.loads((f["base"] / "deployment.normalized.json").read_bytes())
            self.assertEqual(f["snapshot"]["model"], installed)
            self.assertEqual(["4201"], installed["Extensions"])
            row = installed["ExtensionOptions"]["4201"]
            self.assertEqual([True, False, True, False], [row[k] for k in (
                "AllowOutgoing", "AllowMessages", "AllowMobileSmsSend", "AllowMobileSmsReceive")])
            self.assertEqual(source_before, private_tree(f["store"]))
            self.assertEqual(f["candidate"], managed_bytes(f["base"]))

    def test_symlink_store_base_and_ancestors_are_rejected_without_following(self):
        prepare, _, _, _ = self._apis()
        for part in ("store", "base", "store-ancestor", "base-ancestor"):
            with self.subTest(path=part), self._fixture() as f:
                alias = f["parent"] / "alias"
                if part.endswith("ancestor"):
                    os.symlink(f["parent"], alias, target_is_directory=True)
                    store = alias / "store" if part.startswith("store") else f["store"]
                    base = alias / "installed" if part.startswith("base") else f["base"]
                else:
                    os.symlink(f[part], alias, target_is_directory=True)
                    store, base = (alias, f["base"]) if part == "store" else (f["store"], alias)
                before = managed_metadata(f["base"])
                self._error(lambda: prepare(store, base, f["revision"], f["backend"]), "invalid")
                self.assertEqual(before, managed_metadata(f["base"]))
                self.assertEqual([], f["backend"].starts)
                self.assertTrue(alias.is_symlink())

    def test_symlink_managed_original_or_candidate_cannot_write_through_owned_link(self):
        prepare, apply, _, _ = self._apis()
        for location in ("original-config", "original-json", "candidate"):
            with self.subTest(location=location), self._fixture() as f:
                prepared = self._prepare(prepare, f) if location == "candidate" else None
                target = (self._journal(f, prepared) / "candidate/config/pjsip.conf" if prepared else
                          f["base"] / ("config/pjsip.conf" if location == "original-config" else "gateway-client.json"))
                outside = f["parent"] / "owned-external-original"
                outside.write_bytes(target.read_bytes())
                outside.chmod(0o600)
                outside_before = outside.read_bytes()
                target.unlink()
                os.symlink(outside, target)
                action = (lambda: self._apply(apply, f, prepared)) if prepared else (
                    lambda: prepare(f["store"], f["base"], f["revision"], f["backend"]))
                self._error(action, "invalid")
                self.assertTrue(target.is_symlink())
                self.assertEqual(outside_before, outside.read_bytes())
                self.assertEqual(0, f["backend"].counts.get("stop", 0))

    def test_hardlinked_managed_sources_or_draft_state_are_not_accepted_as_private_input(self):
        prepare, apply, _, _ = self._apis()
        for location in ("config", "metadata", "draft-state", "candidate"):
            with self.subTest(location=location), self._fixture() as f:
                prepared = self._prepare(prepare, f) if location == "candidate" else None
                target = {"config": f["base"] / "config/pjsip.conf", "metadata": f["base"] / "gateway-client.json",
                          "draft-state": f["store"] / "state.json"}.get(location)
                if prepared:
                    target = self._journal(f, prepared) / "candidate/config/pjsip.conf"
                link = f["parent"] / "owned-hardlink"
                os.link(target, link)
                before = link.read_bytes()
                self.assertEqual(2, target.stat().st_nlink)
                action = (lambda: self._apply(apply, f, prepared)) if prepared else (
                    lambda: prepare(f["store"], f["base"], f["revision"], f["backend"]))
                self._error(action, "invalid")
                self.assertEqual(before, link.read_bytes())
                self.assertEqual(identity(target.stat()), identity(link.stat()))
                self.assertEqual(0, f["backend"].counts.get("stop", 0))

    def test_nonprivate_base_draft_managed_and_prepared_permissions_fail_closed(self):
        prepare, apply, _, _ = self._apis()
        for location in ("base", "config", "root-owned-config", "draft", "original", "journal", "candidate"):
            with self.subTest(location=location), self._fixture() as f:
                prepared = self._prepare(prepare, f) if location in {"journal", "candidate"} else None
                target = {"base": f["base"], "config": f["base"] / "config", "draft": f["store"],
                          "original": f["base"] / "gateway-client.json"}.get(location)
                if location == "journal":
                    target = f["base"] / ".v2-application"
                elif location == "candidate":
                    target = self._journal(f, prepared) / "candidate/gateway-client.json"
                elif location == "root-owned-config":
                    target = f["base"] / "config"
                    os.chown(target, 0, 0)
                target.chmod(0o700 if location == "root-owned-config" else 0o777 if target.is_dir() else 0o666)
                before = managed_bytes(f["base"])
                action = (lambda: self._apply(apply, f, prepared)) if prepared else (
                    lambda: prepare(f["store"], f["base"], f["revision"], f["backend"]))
                self._error(action, "invalid")
                self.assertEqual(before, managed_bytes(f["base"]))
                self.assertEqual(0, f["backend"].counts.get("stop", 0))

    def test_applied_journal_directory_fsync_failure_is_uncertain_and_requires_explicit_recovery(self):
        prepare, apply, recover, status = self._apis()
        with self._fixture() as f:
            prepared = self._prepare(prepare, f)
            directory = self._journal(f, prepared)
            journal_inode, real_fsync, observations = identity(directory.stat()), os.fsync, []

            def fail_applied_barrier(fd):
                if identity(os.fstat(fd)) == journal_inode and not observations:
                    value = json.loads((directory / "operation.json").read_bytes())
                    if value["State"] == "applied":
                        observations.append(value["State"])
                        raise OSError(SENTINEL)
                return real_fsync(fd)

            with patch.object(os, "fsync", side_effect=fail_applied_barrier):
                self._error(lambda: self._apply(apply, f, prepared), "uncertain")
            self.assertEqual(["applied"], observations, "Fault the real post-state directory durability barrier")
            self._public(status(f["base"]), f, "recovery-required")
            self.assertEqual(f["candidate"], managed_bytes(f["base"]))
            self.assertEqual([f["candidate"]], f["backend"].starts, "Uncertain acknowledgement must not replay or auto-rollback")
            self._public(recover(f["store"], f["base"], prepared["operation"], f["backend"]), f, "rolled-back")
            self._original_restored(f)

    def test_engine_lifecycle_uses_only_explicit_backend_not_process_or_network_shortcuts(self):
        prepare, apply, _, _ = self._apis()
        with self._fixture() as f:
            with patch.object(subprocess, "Popen", side_effect=AssertionError(SENTINEL)), \
                    patch.object(socket, "socket", side_effect=AssertionError(SENTINEL)), \
                    patch.object(os, "system", side_effect=AssertionError(SENTINEL)):
                prepared = self._prepare(prepare, f)
                self._public(self._apply(apply, f, prepared), f, "applied")
            self.assertEqual(f["candidate"], managed_bytes(f["base"]))
            names = [name for name, _ in f["backend"].calls]
            self.assertIn("capture", names)
            self.assertIn("preflight", names)
            self.assertIn("idle", names)
            self.assertLess(names.index("idle"), names.index("stop"))
            self.assertLess(names.index("stop"), names.index("start"))
            self.assertLess(names.index("start"), names.index("verify"))
            self.assertIn("restore_policy", names)


if __name__ == "__main__":
    unittest.main()
