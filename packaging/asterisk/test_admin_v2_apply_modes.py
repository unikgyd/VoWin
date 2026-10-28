"""Supplementary real-application tests for the existing deployment recipe.

configure.py creates daemon-owned config directories/files with 0750/0640.
These tests were added AFTER the original 24-case absent-module RED and do not
claim that earlier run covered this compatibility boundary. The imported
contract module supplies fixtures/assertions only; its TestCase is not imported
as a module-level class, so this module does not rediscover those 24 tests.
The real application engine, not its observing backend, performs all writes.
"""
import os
import stat
import unittest

import test_admin_v2_apply as contract


class AdminV2ApplicationModesTests(unittest.TestCase):
    def _harness(self):
        return contract.AdminV2ApplicationTests(
            "test_apply_really_installs_all_twenty_keeps_config_inode_and_is_terminal_idempotent")

    def _existing_recipe(self, fixture):
        directory = fixture["base"] / "config"
        os.chown(directory, 10001, 10001)
        directory.chmod(0o750)
        for name in contract.CONFIG_OUTPUTS:
            file = fixture["base"] / name
            os.chown(file, 10001, 10001)
            file.chmod(0o640)
        info = directory.stat()
        fixture["old_metadata"] = contract.managed_metadata(fixture["base"])
        fixture["config_metadata"] = (stat.S_IMODE(info.st_mode), info.st_uid, info.st_gid)
        self.assertEqual((0o750, 10001, 10001), fixture["config_metadata"])
        self.assertEqual(fixture["config_inode"], contract.identity(info))
        for name in contract.CONFIG_OUTPUTS:
            self.assertEqual((0o640, 10001, 10001), fixture["old_metadata"][name][1:])
        for name in set(contract.OUTPUTS) - set(contract.CONFIG_OUTPUTS):
            self.assertEqual((0o600, 0, 0), fixture["old_metadata"][name][1:])

    def test_existing_750_640_recipe_prepares_and_installs_all_twenty_preserving_directory(self):
        harness = self._harness()
        prepare, apply, _, status = harness._apis()
        with harness._fixture() as fixture:
            self._existing_recipe(fixture)
            store_before = contract.private_tree(fixture["store"])
            prepared = harness._prepare(prepare, fixture)
            self.assertEqual(fixture["old_metadata"], contract.managed_metadata(fixture["base"]))
            self.assertEqual([], fixture["backend"].starts)
            applied = harness._public(harness._apply(apply, fixture, prepared), fixture, "applied")
            self.assertEqual(fixture["candidate"], contract.managed_bytes(fixture["base"]))
            self.assertEqual([fixture["candidate"]], fixture["backend"].starts)
            self.assertEqual([fixture["candidate"]], fixture["backend"].verifications)
            directory = (fixture["base"] / "config").stat()
            self.assertEqual(fixture["config_inode"], contract.identity(directory))
            self.assertEqual((0o750, 10001, 10001),
                             (stat.S_IMODE(directory.st_mode), directory.st_uid, directory.st_gid))
            for name in contract.CONFIG_OUTPUTS:
                info = (fixture["base"] / name).stat()
                self.assertEqual((0o600, 10001, 10001),
                                 (stat.S_IMODE(info.st_mode), info.st_uid, info.st_gid))
            self.assertEqual(store_before, contract.private_tree(fixture["store"]))
            self.assertEqual(applied, status(fixture["base"]))
            self.assertIs(False, applied["gatewayApplied"])
            self.assertIs(False, applied["firewallApplied"])

    def test_verify_failure_restores_original_twenty_640_files_and_750_directory_metadata(self):
        harness = self._harness()
        prepare, apply, _, status = harness._apis()
        with harness._fixture() as fixture:
            self._existing_recipe(fixture)
            prepared = harness._prepare(prepare, fixture)
            fixture["backend"].failures["verify"] = {1}
            value = harness._failed_apply(apply, status, fixture, prepared)
            harness._public(value, fixture, "rolled-back")
            self.assertEqual([("verify", 1)], fixture["backend"].injected)
            self.assertEqual([fixture["candidate"], fixture["old_bytes"]], fixture["backend"].starts)
            self.assertEqual([fixture["old_bytes"]], fixture["backend"].verifications)
            harness._original_restored(fixture)
            self.assertEqual(fixture["old_metadata"], contract.managed_metadata(fixture["base"]))
            self.assertIs(False, value["pbxApplied"])
            self.assertIs(False, value["gatewayApplied"])
            self.assertIs(False, value["firewallApplied"])

    def test_unsafe_modes_are_rejected_without_mutating_an_otherwise_valid_750_640_runtime(self):
        harness = self._harness()
        prepare, _, _, _ = harness._apis()
        cases = (("config/pjsip.conf", 0o660), ("config/pjsip.conf", 0o644),
                 ("config", 0o770), ("config", 0o755), ("gateway-client.json", 0o640))
        for relative, mode in cases:
            with self.subTest(path=relative, mode=oct(mode)), harness._fixture() as fixture:
                self._existing_recipe(fixture)
                target = fixture["base"] / relative
                target.chmod(mode)
                before = contract.managed_metadata(fixture["base"])
                directory = (fixture["base"] / "config").stat()
                directory_before = (contract.identity(directory), stat.S_IMODE(directory.st_mode),
                                    directory.st_uid, directory.st_gid)
                harness._error(lambda: prepare(fixture["store"], fixture["base"],
                                              fixture["revision"], fixture["backend"]), "invalid")
                self.assertEqual(before, contract.managed_metadata(fixture["base"]))
                directory = (fixture["base"] / "config").stat()
                self.assertEqual(directory_before, (contract.identity(directory), stat.S_IMODE(directory.st_mode),
                                                   directory.st_uid, directory.st_gid))
                self.assertEqual([], fixture["backend"].starts)
                self.assertEqual(0, fixture["backend"].counts.get("stop", 0))


if __name__ == "__main__":
    unittest.main()
