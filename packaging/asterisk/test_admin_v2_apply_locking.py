"""Two actual-thread/fixed-flock supplements, not multi-process acceptance.

All files and commits belong to the real store/application APIs in owned TEMP.
The fake backend only observes lifecycle boundaries. The flock wrapper records
an exact inode/LOCK_EX arrival and unconditionally forwards to native flock;
it never grants a lock or changes an argument/return value. No Docker/PBX/SIM.
Import the fixture module, not its TestCase, to avoid duplicate discovery.
"""
from copy import deepcopy
import json
import os
import queue
import threading
import unittest
from unittest.mock import patch

import admin_v2_store as store_module
from admin_deployment_v2 import read_v2_admin, save_v2_admin
from admin_v2_store import initialize_store
from render_deployment_v2 import render_installable_deployment_v2
import test_admin_v2_apply as fixtures


class _PausedBackend(fixtures.ObservingBackend):
    def __init__(self, case, fixture, entered, release, writer_done=None, source_state=None, boundary=None):
        super().__init__(case, fixture["base"], fixture["old_bytes"], fixture["candidate"], fixture["snapshot"]["model"])
        self.fixture, self.entered, self.release = fixture, entered, release
        self.writer_done, self.source_state, self.boundary = writer_done, source_state, boundary

    def stop(self, target):
        super().stop(target)
        if self.counts["stop"] == 1:
            self.case.assertFalse(self.running)
            self.entered.set()
            self.case.assertTrue(self.release.wait(2), "Main thread must release the stopped application boundary")

    def restore_policy(self, target):
        super().restore_policy(target)
        if self.writer_done is not None:
            self.case.assertFalse(self.writer_done.is_set(), "Draft save must not commit before application's final source check")
            self.case.assertEqual(self.source_state, (self.fixture["store"] / "state.json").read_bytes())
            self.boundary.set()


def _worker(label, action, results, errors, started, done):
    def invoke():
        started.set()
        try:
            results[label] = action()
        except BaseException as error:
            errors.put((label, error))
        finally:
            done.set()
    # A failed lock assertion must not leave a non-daemon thread hanging the
    # entire unittest process. Every started thread is nevertheless joined.
    return threading.Thread(target=invoke, name="vowin-lock-" + label, daemon=True)


def _observe_flock(contender, lock_identity, arrival, acquired):
    native = store_module.fcntl.flock
    def observed(descriptor, operation):
        selected = (threading.current_thread() is contender and operation == store_module.fcntl.LOCK_EX and
                    fixtures.identity(os.fstat(descriptor)) == lock_identity)
        if selected:
            arrival.set()
        result = native(descriptor, operation)
        if selected:
            acquired.set()
        return result
    return observed


class AdminV2ApplicationLockingTests(unittest.TestCase):
    def _finish(self, release, threads, errors):
        # Called after the patch context exits, even on assertion failure.
        release.set()
        for thread in threads:
            if thread.ident is not None:
                thread.join(2)
        self.assertFalse(any(thread.is_alive() for thread in threads), "All real workers must terminate")
        failures = []
        while not errors.empty():
            label, error = errors.get_nowait()
            failures.append((label, type(error).__name__, str(error)))
        self.assertEqual([], failures, "Worker failures must be asserted by the main unittest thread")

    def test_real_draft_save_waits_for_apply_store_lock_and_commits_only_after_final_check(self):
        case = fixtures.AdminV2ApplicationTests()
        prepare, apply, _, status = case._apis()  # Missing engine is an assertion, not import/setup failure.
        with case._fixture() as fixture:
            entered, release, boundary = threading.Event(), threading.Event(), threading.Event()
            apply_started, apply_done = threading.Event(), threading.Event()
            save_started, save_done = threading.Event(), threading.Event()
            arrival, acquired = threading.Event(), threading.Event()
            results, errors = {}, queue.SimpleQueue()
            source_state = (fixture["store"] / "state.json").read_bytes()
            payload = fixtures.save_payload(read_v2_admin(fixture["store"]))
            payload["extensions"][0]["name"] = "Saved after real application lock"
            request = fixtures.encoded(payload)  # Serialize before either worker starts.
            backend = _PausedBackend(self, fixture, entered, release, save_done, source_state, boundary)
            fixture["backend"] = backend
            prepared = prepare(fixture["store"], fixture["base"], fixture["revision"], backend)
            operation_file = (fixture["base"] / ".v2-application" / "operations" /
                              prepared["operation"] / "operation.json")

            def apply_action():
                return apply(fixture["store"], fixture["base"], fixture["revision"], prepared["operation"], backend)
            def save_action():
                value = save_v2_admin(fixture["store"], request)
                results["save_journal_state"] = json.loads(operation_file.read_bytes())["State"]
                results["save_saw_final_boundary"] = boundary.is_set()
                return value

            application = _worker("apply", apply_action, results, errors, apply_started, apply_done)
            saving = _worker("save", save_action, results, errors, save_started, save_done)
            lock_identity = fixtures.identity((fixture["store"] / "lock").stat())
            threads = [application, saving]
            try:
                with patch.object(store_module.fcntl, "flock", side_effect=_observe_flock(saving, lock_identity, arrival, acquired)):
                    application.start()
                    self.assertTrue(apply_started.wait(2))
                    self.assertTrue(entered.wait(2), "Apply must really reach its stopped boundary")
                    self.assertFalse(backend.running)
                    self.assertEqual(fixture["old_bytes"], fixtures.managed_bytes(fixture["base"]))
                    saving.start()
                    self.assertTrue(save_started.wait(2))
                    self.assertTrue(arrival.wait(2), "Save must actually request the real fixed store flock")
                    self.assertFalse(acquired.is_set())
                    self.assertFalse(save_done.wait(0.1), "Save cannot finish while apply owns the store lock")
                    self.assertFalse(apply_done.is_set())
                    self.assertEqual(source_state, (fixture["store"] / "state.json").read_bytes())
                    release.set()
                    self.assertTrue(apply_done.wait(2))
                    self.assertTrue(save_done.wait(2))
            finally:
                self._finish(release, threads, errors)

            self.assertTrue(arrival.is_set() and acquired.is_set() and boundary.is_set())
            self.assertEqual("applied", results["apply"]["state"])
            self.assertTrue(results["apply"]["pbxApplied"])
            self.assertEqual("applied", results["save_journal_state"])
            self.assertIs(True, results["save_saw_final_boundary"])
            self.assertNotEqual(fixture["revision"], results["save"]["revision"])
            self.assertEqual(results["save"], read_v2_admin(fixture["store"]))
            self.assertEqual("Saved after real application lock", results["save"]["extensions"][0]["name"])
            self.assertEqual(fixture["candidate"], fixtures.managed_bytes(fixture["base"]))
            self.assertEqual("applied", status(fixture["base"])["state"])
            self.assertTrue(backend.running)
            self.assertEqual("unless-stopped", backend.policy)

    def test_independent_store_prepare_waits_for_same_target_lock_before_capture_or_preflight(self):
        case = fixtures.AdminV2ApplicationTests()
        prepare, apply, _, status = case._apis()
        with case._fixture() as fixture:
            entered, release = threading.Event(), threading.Event()
            apply_started, apply_done = threading.Event(), threading.Event()
            prepare_started, prepare_done = threading.Event(), threading.Event()
            arrival, acquired, captured, preflighted = (threading.Event() for _ in range(4))
            results, errors = {}, queue.SimpleQueue()
            backend = _PausedBackend(self, fixture, entered, release)
            fixture["backend"] = backend
            prepared = prepare(fixture["store"], fixture["base"], fixture["revision"], backend)
            operation_file = (fixture["base"] / ".v2-application" / "operations" /
                              prepared["operation"] / "operation.json")
            model_b = deepcopy(fixture["snapshot"]["model"])
            model_b["ExtensionOptions"][model_b["Extensions"][0]]["Name"] = "Independent store B draft"
            store_b = fixture["parent"] / "independent-store-b"
            snapshot_b = initialize_store(store_b, model_b, fixture["snapshot"]["secrets"])
            candidate_b = {name: value.encode("utf-8") for name, value in
                           render_installable_deployment_v2(snapshot_b["model"], snapshot_b["secrets"]).items()}

            class AfterApplicationBackend(fixtures.ObservingBackend):
                def capture(inner):
                    captured.set()
                    self.assertTrue(backend.running, "Store B cannot inspect a stopped/mixed target")
                    self.assertEqual("applied", json.loads(operation_file.read_bytes())["State"])
                    self.assertEqual(fixture["candidate"], fixtures.managed_bytes(fixture["base"]))
                    return super().capture()
                def preflight(inner, config_path, model):
                    preflighted.set()
                    return super().preflight(config_path, model)

            backend_b = AfterApplicationBackend(self, fixture["base"], fixture["candidate"], candidate_b, snapshot_b["model"])
            application = _worker("apply", lambda: apply(fixture["store"], fixture["base"], fixture["revision"],
                                  prepared["operation"], backend), results, errors, apply_started, apply_done)
            preparing = _worker("prepare-b", lambda: prepare(store_b, fixture["base"], snapshot_b["revision"], backend_b),
                                results, errors, prepare_started, prepare_done)
            lock_identity = fixtures.identity((fixture["base"] / ".v2-application" / "lock").stat())
            threads = [application, preparing]
            try:
                with patch.object(store_module.fcntl, "flock", side_effect=_observe_flock(preparing, lock_identity, arrival, acquired)):
                    application.start()
                    self.assertTrue(apply_started.wait(2))
                    self.assertTrue(entered.wait(2))
                    self.assertFalse(backend.running)
                    preparing.start()
                    self.assertTrue(prepare_started.wait(2))
                    self.assertTrue(arrival.wait(2), "Store B must actually reach the shared target flock")
                    self.assertFalse(acquired.is_set())
                    self.assertFalse(prepare_done.wait(0.1))
                    self.assertFalse(captured.is_set(), "Capture must run only after the target lock is acquired")
                    self.assertFalse(preflighted.is_set())
                    self.assertEqual(fixture["old_bytes"], fixtures.managed_bytes(fixture["base"]))
                    release.set()
                    self.assertTrue(apply_done.wait(2))
                    self.assertTrue(prepare_done.wait(2))
            finally:
                self._finish(release, threads, errors)

            self.assertTrue(arrival.is_set() and acquired.is_set() and captured.is_set() and preflighted.is_set())
            self.assertEqual("applied", results["apply"]["state"])
            self.assertEqual("prepared", results["prepare-b"]["state"])
            self.assertEqual(snapshot_b["revision"], results["prepare-b"]["revision"])
            self.assertNotEqual(prepared["operation"], results["prepare-b"]["operation"])
            self.assertEqual(20, results["prepare-b"]["fileCount"])
            self.assertEqual(fixture["candidate"], fixtures.managed_bytes(fixture["base"]))
            self.assertEqual(results["prepare-b"], status(fixture["base"]))
            self.assertEqual([fixture["candidate"]], backend.starts)
            self.assertEqual([], backend_b.starts)
            self.assertTrue(backend.running)
