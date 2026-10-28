"""Independent installed-revision and nonblocking-overview application contract.

Missing overview support is an explicit assertion RED, not an import failure.
All installation, rollback, journals and locking belong to the real product.
Only its runtime adapter is the existing observing synthetic backend; no real
PBX, Docker, socket or subprocess is used. Imported fixture TestCases are not
module-level classes, so their already frozen tests are not rediscovered.
"""
from copy import deepcopy
import hashlib
import importlib
import importlib.util
import json
import os
import queue
import stat
import threading
import unittest
from unittest.mock import patch

from admin_deployment_v2 import read_v2_admin, save_v2_admin
from admin_v2_store import read_store
from render_deployment_v2 import render_installable_deployment_v2
import test_admin_v2_apply as contract


OVERVIEW_KEYS = {"schemaVersion", "busy", "operation", "installed", "installedState",
                 "statusSource", "gatewayApplied", "firewallApplied"}
OPERATION_KEYS = {"operation", "revision", "state", "fileCount", "changedFiles", "pbxApplied",
                  "gatewayApplied", "firewallApplied", "statusSource"}
RECORD_KEYS = {"Operation", "Revision", "StoreIdentity", "ConfigIdentity", "Target", "Files"}


def encoded(value):
    return (json.dumps(value, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8")


def live_fingerprints(base):
    # Reading may change atime. Content, ownership, inode, mtime and ctime must
    # not change during a metadata-only publication retry or overview read.
    result = {}
    for name in contract.OUTPUTS:
        file = base / name
        info = file.stat()
        result[name] = (file.read_bytes(), contract.identity(info), stat.S_IMODE(info.st_mode),
                        info.st_uid, info.st_gid, info.st_mtime_ns, info.st_ctime_ns)
    return result


class AdminV2ApplicationOverviewTests(unittest.TestCase):
    def _apis(self):
        spec = importlib.util.find_spec("admin_v2_apply")
        module = importlib.import_module("admin_v2_apply") if spec is not None else None
        overview = getattr(module, "application_overview", None)
        self.assertTrue(callable(overview), "Explicit v2 application overview API must exist")
        harness = contract.AdminV2ApplicationTests(
            "test_apply_really_installs_all_twenty_keeps_config_inode_and_is_terminal_idempotent")
        return overview, harness, *harness._apis()

    def _operation(self, value, fixture=None, state=None):
        self.assertIs(type(value), dict)
        self.assertEqual(OPERATION_KEYS, set(value))
        self.assertEqual("journal", value["statusSource"])
        self.assertIn(value["state"], {"prepared", "stopping", "installing", "starting",
                                       "applied", "rolled-back", "recovery-required"})
        self.assertRegex(value["operation"], contract.TOKEN)
        self.assertRegex(value["revision"], contract.TOKEN)
        self.assertIs(type(value["fileCount"]), int)
        self.assertEqual(20, value["fileCount"])
        self.assertIs(type(value["changedFiles"]), list)
        self.assertEqual(sorted(set(value["changedFiles"])), value["changedFiles"])
        self.assertTrue(set(value["changedFiles"]) <= set(contract.OUTPUTS))
        self.assertIs(value["state"] == "applied", value["pbxApplied"])
        self.assertIs(False, value["gatewayApplied"])
        self.assertIs(False, value["firewallApplied"])
        if fixture is not None:
            self.assertEqual(fixture["revision"], value["revision"])
        if state is not None:
            self.assertEqual(state, value["state"])
        return value

    def _view(self, value, installed=None, installed_state="unknown", busy=False):
        self.assertIs(type(value), dict)
        self.assertEqual(OVERVIEW_KEYS, set(value))
        self.assertIs(type(value["schemaVersion"]), int)
        self.assertEqual(1, value["schemaVersion"])
        self.assertIs(busy, value["busy"])
        self.assertEqual(installed_state, value["installedState"])
        self.assertEqual("journal", value["statusSource"])
        self.assertIs(False, value["gatewayApplied"])
        self.assertIs(False, value["firewallApplied"])
        self.assertEqual(installed, value["installed"])
        if installed is not None:
            self.assertEqual({"operation", "revision"}, set(installed))
            self.assertRegex(installed["operation"], contract.TOKEN)
            self.assertRegex(installed["revision"], contract.TOKEN)
        if value["operation"] is not None:
            self._operation(value["operation"])
        if busy:
            self.assertIsNone(value["operation"])
            self.assertIsNone(value["installed"])
            self.assertEqual("unconfirmed", value["installedState"])
        public = json.dumps(value, ensure_ascii=False)
        for secret in [contract.SENTINEL, *contract.synthetic_secrets().keys(),
                       *contract.synthetic_secrets().values(), "synthetic-replacement-pbx-credential"]:
            self.assertNotIn(secret, public)
        return value

    @staticmethod
    def _installed(operation):
        return {"operation": operation["operation"], "revision": operation["revision"]}

    def _head(self, fixture):
        path = fixture["base"] / ".v2-application/state.json"
        value = json.loads(path.read_bytes())
        self.assertEqual({"ApplicationVersion", "Operation", "Installed"}, set(value))
        self.assertIs(type(value["ApplicationVersion"]), int)
        self.assertEqual(2, value["ApplicationVersion"])
        return path, value

    def _record(self, fixture, operation):
        _, head = self._head(fixture)
        record = head["Installed"]
        self.assertIs(type(record), dict)
        self.assertEqual(RECORD_KEYS, set(record))
        self.assertEqual(operation["operation"], record["Operation"])
        self.assertEqual(operation["revision"], record["Revision"])
        store, config = fixture["store"].stat(), (fixture["base"] / "config").stat()
        self.assertEqual({"Device": store.st_dev, "Inode": store.st_ino}, record["StoreIdentity"])
        self.assertEqual({"Device": config.st_dev, "Inode": config.st_ino,
                          "Mode": stat.S_IMODE(config.st_mode), "Uid": config.st_uid, "Gid": config.st_gid},
                         record["ConfigIdentity"])
        self.assertEqual(fixture["backend"].target, record["Target"])
        self.assertEqual(set(contract.OUTPUTS), set(record["Files"]))
        for name in contract.OUTPUTS:
            file = fixture["base"] / name
            info, raw = file.stat(), file.read_bytes()
            self.assertEqual({"Exists": True, "Bytes": len(raw), "SHA256": hashlib.sha256(raw).hexdigest(),
                              "Mode": 0o600, "Uid": info.st_uid, "Gid": info.st_gid}, record["Files"][name])
        return deepcopy(record)

    def _apply_a(self, prepare, apply, fixture):
        prepared = self._operation(prepare(fixture["store"], fixture["base"], fixture["revision"], fixture["backend"]),
                                   fixture, "prepared")
        result = self._operation(apply(fixture["store"], fixture["base"], fixture["revision"],
                                       prepared["operation"], fixture["backend"]), fixture, "applied")
        self.assertEqual(fixture["candidate"], contract.managed_bytes(fixture["base"]))
        return result

    def _next(self, harness, fixture, name="Independent candidate B"):
        request = contract.save_payload(read_v2_admin(fixture["store"]))
        request["extensions"][0].update({"name": name, "enabled": True, "ring": True, "allowMessages": True})
        view = save_v2_admin(fixture["store"], encoded(request))
        source = read_store(fixture["store"])
        candidate = {key: value.encode("utf-8") for key, value in
                     render_installable_deployment_v2(source["model"], source["secrets"]).items()}
        updated = {**fixture, "view": view, "revision": view["revision"], "snapshot": source,
                   "candidate": candidate, "old_bytes": contract.managed_bytes(fixture["base"]),
                   "old_metadata": contract.managed_metadata(fixture["base"])}
        updated["backend"] = contract.ObservingBackend(harness, fixture["base"], updated["old_bytes"], candidate, source["model"])
        return updated

    def _prepare_b(self, prepare, fixture):
        return self._operation(prepare(fixture["store"], fixture["base"], fixture["revision"], fixture["backend"]),
                               fixture, "prepared")

    def _apply(self, apply, fixture, operation):
        return apply(fixture["store"], fixture["base"], fixture["revision"], operation["operation"], fixture["backend"])

    def _failed(self, harness, apply, status, fixture, operation):
        try:
            value = self._apply(apply, fixture, operation)
        except ValueError as error:
            harness._error(lambda: harness._raise(error))
        else:
            self._operation(value, fixture)
            self.assertNotEqual("applied", value["state"])
        return self._operation(status(fixture["base"]), fixture)

    def _fail_applied_head_once(self, fixture, operation):
        directory = fixture["base"] / ".v2-application"
        target_inode = contract.identity(directory.stat())
        journal = directory / "operations" / operation["operation"] / "operation.json"
        native, observations = os.replace, []

        def replace(source, destination, *args, **kwargs):
            descriptor = kwargs.get("dst_dir_fd")
            if (os.fspath(destination) == "state.json" and descriptor is not None
                    and contract.identity(os.fstat(descriptor)) == target_inode and not observations
                    and json.loads(journal.read_bytes())["State"] == "applied"):
                observations.append(json.loads((directory / "state.json").read_bytes()))
                raise OSError(contract.SENTINEL)
            return native(source, destination, *args, **kwargs)

        return replace, observations

    def test_uninitialized_overview_is_exact_readonly_and_does_not_create_management_state(self):
        overview, harness, _, _, _, _ = self._apis()
        with harness._fixture() as fixture:
            before = contract.private_tree(fixture["base"])
            view = self._view(overview(fixture["base"]))
            self.assertIsNone(view["operation"])
            self.assertEqual(before, contract.private_tree(fixture["base"]))
            self.assertEqual([], fixture["backend"].calls)

    def test_installed_a_survives_b_prepare_and_rollback_then_changes_only_after_b_success(self):
        overview, harness, prepare, apply, _, status = self._apis()
        with harness._fixture() as fixture:
            a = self._apply_a(prepare, apply, fixture)
            a_identity = self._installed(a)
            self._view(overview(fixture["base"]), a_identity, "known")
            a_record = self._record(fixture, a)
            b = self._next(harness, fixture)
            prepared = self._prepare_b(prepare, b)
            value = self._view(overview(b["base"]), a_identity, "known")
            self.assertEqual(prepared, value["operation"])
            _, head = self._head(b)
            self.assertEqual(a_record, head["Installed"])
            journal = json.loads((harness._journal(b, prepared) / "operation.json").read_bytes())
            self.assertEqual(2, journal["ApplicationVersion"])
            self.assertEqual(a_record, journal["OriginalInstalled"])
            b["backend"].failures["verify"] = {1}
            rolled_back = self._failed(harness, apply, status, b, prepared)
            self.assertEqual("rolled-back", rolled_back["state"])
            self.assertEqual([("verify", 1)], b["backend"].injected)
            self.assertEqual(b["old_metadata"], contract.managed_metadata(b["base"]))
            self._view(overview(b["base"]), a_identity, "known")
            self.assertEqual(a_record, self._head(b)[1]["Installed"])
            retry = self._prepare_b(prepare, b)
            self.assertNotEqual(prepared["operation"], retry["operation"])
            applied = self._operation(self._apply(apply, b, retry), b, "applied")
            self._view(overview(b["base"]), self._installed(applied), "known")
            self.assertEqual(b["candidate"], contract.managed_bytes(b["base"]))
            self._record(b, applied)

    def test_pending_is_unconfirmed_and_explicit_recovery_restores_a_without_candidate_or_current_draft(self):
        overview, harness, prepare, apply, recover, status = self._apis()
        with harness._fixture() as fixture:
            a = self._apply_a(prepare, apply, fixture)
            b = self._next(harness, fixture)
            prepared = self._prepare_b(prepare, b)
            b["backend"].failures["start"] = {1, 2}
            self.assertEqual("recovery-required", self._failed(harness, apply, status, b, prepared)["state"])
            self.assertEqual([("start", 1), ("start", 2)], b["backend"].injected)
            self._view(overview(b["base"]), self._installed(a), "unconfirmed")
            (harness._journal(b, prepared) / "candidate/config/pjsip.conf").unlink()
            (b["store"] / "state.json").write_bytes(b"corrupt unrelated current draft\n")
            b["backend"].failures.clear()
            result = self._operation(recover(b["store"], b["base"], prepared["operation"], b["backend"]), b, "rolled-back")
            self.assertEqual(b["old_metadata"], contract.managed_metadata(b["base"]))
            self._view(overview(b["base"]), self._installed(a), "known")
            calls, live = deepcopy(b["backend"].calls), live_fingerprints(b["base"])
            self.assertEqual(result, recover(b["store"], b["base"], prepared["operation"], b["backend"]))
            self.assertEqual(calls, b["backend"].calls)
            self.assertEqual(live, live_fingerprints(b["base"]))

    def test_overview_returns_busy_before_releasing_real_apply_flock_without_mocking_locking(self):
        overview, harness, prepare, apply, _, _ = self._apis()
        with harness._fixture() as fixture:
            self._apply_a(prepare, apply, fixture)
            b = self._next(harness, fixture)
            entered, release, read_done = threading.Event(), threading.Event(), threading.Event()
            errors, results = queue.SimpleQueue(), {}

            class PausedBackend(contract.ObservingBackend):
                def stop(inner, target):
                    super().stop(target)
                    if inner.counts["stop"] == 1:
                        entered.set()
                        self.assertTrue(release.wait(5), "Test must release the actual stopped boundary")

            b["backend"] = PausedBackend(harness, b["base"], b["old_bytes"], b["candidate"], b["snapshot"]["model"])
            prepared = self._prepare_b(prepare, b)

            def writer():
                try:
                    results["apply"] = self._apply(apply, b, prepared)
                except BaseException as error:
                    errors.put(error)

            def reader():
                try:
                    results["overview"] = overview(b["base"])
                except BaseException as error:
                    errors.put(error)
                finally:
                    read_done.set()

            application = threading.Thread(target=writer, daemon=True)
            reading = threading.Thread(target=reader, daemon=True)
            try:
                application.start()
                self.assertTrue(entered.wait(2), "Real apply must hold its native lock at stop")
                before, calls = contract.private_tree(b["base"]), deepcopy(b["backend"].calls)
                reading.start()
                self.assertTrue(read_done.wait(1), "Overview must not wait for the active application's flock")
                self.assertFalse(release.is_set())
                self.assertTrue(application.is_alive())
                self.assertIn("overview", results, "The independent reader must return a public overview, not fail")
                self._view(results["overview"], installed_state="unconfirmed", busy=True)
                self.assertEqual(before, contract.private_tree(b["base"]))
                self.assertEqual(calls, b["backend"].calls)
            finally:
                release.set()
                application.join(5)
                if reading.ident is not None:
                    reading.join(5)
            self.assertFalse(application.is_alive() or reading.is_alive())
            failures = []
            while not errors.empty():
                failures.append(type(errors.get_nowait()).__name__)
            self.assertEqual([], failures)
            self._operation(results["apply"], b, "applied")

    def test_legacy_v1_applied_journal_remains_unknown_without_scanning_or_guessing_installed(self):
        overview, harness, prepare, apply, _, status = self._apis()
        with harness._fixture() as fixture:
            a = self._apply_a(prepare, apply, fixture)
            b = self._next(harness, fixture)
            self._prepare_b(prepare, b)  # Leave a real unselected v2 record containing A's prior index.
            journal = harness._journal(fixture, a) / "operation.json"
            old = json.loads(journal.read_bytes())
            old["ApplicationVersion"] = 1
            del old["OriginalInstalled"]
            journal.write_bytes(encoded(old))
            head = fixture["base"] / ".v2-application/state.json"
            head.write_bytes(encoded({"ApplicationVersion": 1, "Operation": a["operation"]}))
            before = contract.private_tree(fixture["base"])
            result = self._view(overview(fixture["base"]), installed_state="unknown")
            self.assertEqual(a, result["operation"])
            self.assertEqual(a, status(fixture["base"]))
            self.assertEqual(before, contract.private_tree(fixture["base"]))

    def test_known_installed_record_rejects_malformed_shape_identity_and_twenty_file_fingerprints(self):
        overview, harness, prepare, apply, _, _ = self._apis()
        cases = ("unknown-field", "missing-revision", "boolean-revision", "missing-file",
                 "bad-sha256", "noncanonical-mode", "identity-type", "record-list")
        for corruption in cases:
            with self.subTest(index=corruption), harness._fixture() as fixture:
                a = self._apply_a(prepare, apply, fixture)
                self._record(fixture, a)
                head, value = self._head(fixture)
                record = value["Installed"]
                if corruption == "unknown-field":
                    record["Unexpected"] = contract.SENTINEL
                elif corruption == "missing-revision":
                    del record["Revision"]
                elif corruption == "boolean-revision":
                    record["Revision"] = True
                elif corruption == "missing-file":
                    del record["Files"]["config/pjsip.conf"]
                elif corruption == "bad-sha256":
                    record["Files"]["config/pjsip.conf"]["SHA256"] = "f" * 63
                elif corruption == "noncanonical-mode":
                    record["Files"]["config/pjsip.conf"]["Mode"] = 0o640
                elif corruption == "identity-type":
                    record["ConfigIdentity"]["Inode"] = "not-an-inode"
                else:
                    value["Installed"] = []
                head.write_bytes(encoded(value))
                before, live = contract.private_tree(fixture["base"]), live_fingerprints(fixture["base"])
                harness._error(lambda: overview(fixture["base"]), "invalid")
                self.assertEqual(before, contract.private_tree(fixture["base"]))
                self.assertEqual(live, live_fingerprints(fixture["base"]))

    def test_known_installed_head_symlink_or_hardlink_is_rejected_without_repair(self):
        overview, harness, prepare, apply, _, _ = self._apis()
        for kind in ("symlink", "hardlink"):
            with self.subTest(link=kind), harness._fixture() as fixture:
                a = self._apply_a(prepare, apply, fixture)
                self._record(fixture, a)
                head, _ = self._head(fixture)
                outside = fixture["parent"] / "owned-installed-index-copy"
                if kind == "symlink":
                    outside.write_bytes(head.read_bytes())
                    outside.chmod(0o600)
                    head.unlink()
                    os.symlink(outside, head)
                else:
                    os.link(head, outside)
                before, live = contract.private_tree(fixture["base"]), live_fingerprints(fixture["base"])
                saved = outside.read_bytes()
                harness._error(lambda: overview(fixture["base"]), "invalid")
                self.assertEqual(before, contract.private_tree(fixture["base"]))
                self.assertEqual(live, live_fingerprints(fixture["base"]))
                self.assertEqual(saved, outside.read_bytes())

    def test_current_content_or_mode_drift_is_reported_without_overwriting_or_reclassifying_as_known(self):
        overview, harness, prepare, apply, _, _ = self._apis()
        for drift in ("content", "mode"):
            with self.subTest(drift=drift), harness._fixture() as fixture:
                a = self._apply_a(prepare, apply, fixture)
                file = fixture["base"] / "config/pjsip.conf"
                if drift == "content":
                    file.write_bytes(file.read_bytes() + b"; independent runtime edit\n")
                else:
                    file.chmod(0o640)
                before, live = contract.private_tree(fixture["base"]), live_fingerprints(fixture["base"])
                result = self._view(overview(fixture["base"]), self._installed(a), "drifted")
                self.assertEqual(a, result["operation"])
                self.assertEqual(before, contract.private_tree(fixture["base"]))
                self.assertEqual(live, live_fingerprints(fixture["base"]))

    def test_terminal_applied_before_installed_head_publication_is_repaired_without_runtime_replay(self):
        overview, harness, prepare, apply, _, _ = self._apis()
        with harness._fixture() as fixture:
            a = self._apply_a(prepare, apply, fixture)
            b = self._next(harness, fixture)
            prepared = self._prepare_b(prepare, b)
            replacement, observations = self._fail_applied_head_once(b, prepared)
            with patch.object(os, "replace", side_effect=replacement):
                harness._error(lambda: self._apply(apply, b, prepared), "uncertain")
            self.assertEqual(1, len(observations), "Reach the actual post-terminal index publication")
            self.assertEqual(a["operation"], observations[0]["Installed"]["Operation"])
            terminal = json.loads((harness._journal(b, prepared) / "operation.json").read_bytes())
            self.assertEqual("applied", terminal["State"])
            self.assertEqual(b["candidate"], contract.managed_bytes(b["base"]))
            self._view(overview(b["base"]), self._installed(a), "unconfirmed")
            calls, live = deepcopy(b["backend"].calls), live_fingerprints(b["base"])
            result = self._operation(self._apply(apply, b, prepared), b, "applied")
            self.assertEqual(calls, b["backend"].calls)
            self.assertEqual(live, live_fingerprints(b["base"]))
            self._view(overview(b["base"]), self._installed(result), "known")
            self._record(b, result)

    def test_unreconciled_terminal_head_cannot_be_hidden_by_preparing_another_operation(self):
        overview, harness, prepare, apply, _, _ = self._apis()
        with harness._fixture() as fixture:
            a = self._apply_a(prepare, apply, fixture)
            b = self._next(harness, fixture)
            prepared = self._prepare_b(prepare, b)
            replacement, observations = self._fail_applied_head_once(b, prepared)
            with patch.object(os, "replace", side_effect=replacement):
                harness._error(lambda: self._apply(apply, b, prepared), "uncertain")
            self.assertEqual(1, len(observations))
            before, calls = contract.private_tree(b["base"]), deepcopy(b["backend"].calls)
            error = harness._error(lambda: prepare(b["store"], b["base"], b["revision"], b["backend"]))
            self.assertIn(error.code, {"busy", "conflict"})
            self.assertEqual(before, contract.private_tree(b["base"]))
            self.assertEqual(calls, b["backend"].calls)
            value = self._view(overview(b["base"]), self._installed(a), "unconfirmed")
            self.assertEqual(prepared["operation"], value["operation"]["operation"])


if __name__ == "__main__":
    unittest.main()
