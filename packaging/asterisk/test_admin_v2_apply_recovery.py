"""Supplementary recovery-material controls, added after the original RED.

Candidate bytes are not needed to restore complete originals: the committed
journal already holds candidate fingerprints for classifying live files.
Original bytes remain mandatory and strictly verified before runtime access.
No production service, subprocess, modem, call or message is used here.
"""
from copy import deepcopy
import unittest

import test_admin_v2_apply as contract


class AdminV2ApplicationRecoveryMaterialTests(unittest.TestCase):
    def _harness(self):
        return contract.AdminV2ApplicationTests(
            "test_explicit_recovery_uses_original_not_current_draft_and_is_idempotent")

    def _pending(self, harness, prepare, apply, status, fixture):
        prepared = harness._prepare(prepare, fixture)
        fixture["backend"].failures["start"] = {1, 2}
        value = harness._failed_apply(apply, status, fixture, prepared)
        harness._public(value, fixture, "recovery-required")
        self.assertEqual([("start", 1), ("start", 2)], fixture["backend"].injected)
        self.assertFalse(fixture["backend"].running)
        self.assertEqual(fixture["old_metadata"], contract.managed_metadata(fixture["base"]))
        return prepared, value

    def test_pending_originals_can_restore_when_candidate_file_is_corrupt_or_missing(self):
        harness = self._harness()
        prepare, apply, recover, status = harness._apis()
        for damage in ("corrupt", "missing"):
            with self.subTest(candidate=damage), harness._fixture() as fixture:
                prepared, pending = self._pending(harness, prepare, apply, status, fixture)
                candidate = harness._journal(fixture, prepared) / "candidate/config/pjsip.conf"
                if damage == "corrupt":
                    candidate.write_bytes(candidate.read_bytes() + contract.SENTINEL.encode("ascii"))
                    damaged_bytes = candidate.read_bytes()
                else:
                    candidate.unlink()
                    damaged_bytes = None
                # A different/corrupt current draft must not become recovery's
                # source of truth; only this operation's recorded original is.
                (fixture["store"] / "state.json").write_bytes(b"corrupt newer draft must not be parsed\n")
                self.assertEqual(pending, status(fixture["base"]))
                fixture["backend"].failures.clear()
                restored = harness._public(recover(fixture["store"], fixture["base"],
                                                  prepared["operation"], fixture["backend"]),
                                           fixture, "rolled-back")
                harness._original_restored(fixture)
                self.assertEqual([fixture["old_bytes"]], fixture["backend"].starts)
                self.assertEqual([fixture["old_bytes"]], fixture["backend"].verifications)
                self.assertEqual(restored, status(fixture["base"]))
                self.assertIs(False, restored["gatewayApplied"])
                self.assertIs(False, restored["firewallApplied"])
                if damage == "corrupt":
                    self.assertEqual(damaged_bytes, candidate.read_bytes(), "Recovery must not repair or replay its candidate")
                else:
                    self.assertFalse(candidate.exists(), "Recovery must not reconstruct or replay a missing candidate")

    def test_corrupt_original_material_rejects_recovery_before_backend_or_managed_writes(self):
        harness = self._harness()
        prepare, apply, recover, status = harness._apis()
        with harness._fixture() as fixture:
            prepared, _ = self._pending(harness, prepare, apply, status, fixture)
            original = harness._journal(fixture, prepared) / "original/config/pjsip.conf"
            original.write_bytes(original.read_bytes() + contract.SENTINEL.encode("ascii"))
            corrupt_bytes = original.read_bytes()
            before = contract.managed_metadata(fixture["base"])
            calls = deepcopy(fixture["backend"].calls)
            fixture["backend"].failures.clear()
            harness._error(lambda: recover(fixture["store"], fixture["base"],
                                          prepared["operation"], fixture["backend"]), "invalid")
            self.assertEqual(calls, fixture["backend"].calls,
                             "Invalid originals must fail before even reading runtime identity")
            self.assertEqual(before, contract.managed_metadata(fixture["base"]))
            self.assertEqual(corrupt_bytes, original.read_bytes())
            self.assertFalse(fixture["backend"].running)


if __name__ == "__main__":
    unittest.main()
