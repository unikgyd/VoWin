"""Offline, owned-directory publication tests; never runs a CLI or a service.

Synthetic secret-bearing strings and real symlinks stay below each test's own
temporary directory. A failed operation may remove only its verified staging
directory, never the output requested by the caller or a competing publisher.
"""
import contextlib
from copy import deepcopy
import io
import json
import os
from pathlib import Path
import secrets
import socket
import stat
import subprocess
import tempfile
import unittest
from unittest.mock import patch

import deployment_bundle_v2 as writer
from deployment_v2 import DeploymentV2Error


EXPECTED_FILES = {
    "config/asterisk.conf", "config/modules.conf", "config/logger.conf",
    "config/rtp.conf", "config/http.conf", "config/manager.conf",
    "config/pjsip.conf", "config/extensions.conf", "gateway-client.json",
    "deployment.normalized.json", "sip-guard.nft",
}
PRIVATE_SENTINEL = "synthetic-private-bundle-secret"
PATH_SENTINEL = "synthetic-private-path-never-log"


def private_bundle():
    result = {name: "; synthetic artifact " + name + "\n" for name in EXPECTED_FILES}
    result["config/pjsip.conf"] = "[synthetic-auth]\ntype=auth\npassword=" + PRIVATE_SENTINEL + "\n"
    result["gateway-client.json"] = json.dumps({"Version": 2, "Credentials": {"MainPassword": PRIVATE_SENTINEL}},
                                              ensure_ascii=False, separators=(",", ":")) + "\n"
    result["deployment.normalized.json"] = '{"SchemaVersion":2}\n'
    result["config/logger.conf"] = "; synthetic UTF-8 \u4e2d\u6587 \U0001f30d\n"
    return result


def tree_contents(directory):
    """Only inspect the task-owned fixture; never follow links recursively."""
    entries = {}
    for path in directory.rglob("*"):
        relative = path.relative_to(directory).as_posix()
        if path.is_symlink():
            entries[relative] = ("symlink", os.readlink(path))
        elif path.is_dir():
            entries[relative] = ("directory",)
        else:
            entries[relative] = ("file", path.read_bytes())
    return entries


class DeploymentBundleV2Tests(unittest.TestCase):
    def assert_safe_failure(self, bundle, output):
        original = deepcopy(bundle)
        stdout, stderr = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
            with self.assertRaises(DeploymentV2Error) as caught:
                writer.write_deployment_bundle(bundle, output)
        self.assertEqual(original, bundle)
        for text in (str(caught.exception), stdout.getvalue(), stderr.getvalue()):
            self.assertNotIn(PRIVATE_SENTINEL, text)
            self.assertNotIn(PATH_SENTINEL, text)
            self.assertNotIn(str(output), text)
        self.assertEqual("", stdout.getvalue())
        self.assertEqual("", stderr.getvalue())
        return caught.exception

    def assert_complete_private_tree(self, output, bundle):
        files = {path.relative_to(output).as_posix() for path in output.rglob("*") if path.is_file()}
        self.assertEqual(EXPECTED_FILES, files)
        self.assertEqual({"config"}, {path.relative_to(output).as_posix() for path in output.rglob("*") if path.is_dir()})
        self.assertFalse(any(path.is_symlink() for path in output.rglob("*")))
        for name, value in bundle.items():
            self.assertEqual(value.encode("utf-8"), (output / name).read_bytes(), name)
        if os.name == "posix":
            self.assertEqual(0o700, stat.S_IMODE(output.stat().st_mode))
            self.assertEqual(0o700, stat.S_IMODE((output / "config").stat().st_mode))
            for name in EXPECTED_FILES:
                self.assertEqual(0o600, stat.S_IMODE((output / name).stat().st_mode), name)

    def test_new_directory_contains_exact_text_and_private_modes_without_logging_secrets(self):
        bundle, stdout, stderr = private_bundle(), io.StringIO(), io.StringIO()
        original = deepcopy(bundle)
        with tempfile.TemporaryDirectory(prefix="vowin-bundle-unit-") as temporary:
            parent = Path(temporary)
            output = parent / PATH_SENTINEL
            (parent / "unrelated.txt").write_bytes(b"unrelated owner data")
            with contextlib.redirect_stdout(stdout), contextlib.redirect_stderr(stderr):
                self.assertIsNone(writer.write_deployment_bundle(bundle, output))
            self.assert_complete_private_tree(output, bundle)
            self.assertEqual({PATH_SENTINEL, "unrelated.txt"}, {path.name for path in parent.iterdir()})
            self.assertEqual(b"unrelated owner data", (parent / "unrelated.txt").read_bytes())
        self.assertEqual(original, bundle)
        self.assertEqual("", stdout.getvalue())
        self.assertEqual("", stderr.getvalue())

    def test_publication_observes_complete_same_parent_stage_before_output_becomes_visible(self):
        bundle = private_bundle()
        original_publish = writer._publish_no_replace
        observed = []
        with tempfile.TemporaryDirectory(prefix="vowin-bundle-unit-") as temporary:
            parent = Path(temporary)
            output = parent / PATH_SENTINEL

            def inspect_then_publish(stage, destination):
                stage, destination = Path(stage), Path(destination)
                observed.append(stage)
                self.assertEqual(parent, stage.parent)
                self.assertNotEqual(output, stage)
                self.assertEqual(output, destination)
                self.assertFalse(output.exists())
                self.assert_complete_private_tree(stage, bundle)
                return original_publish(stage, destination)

            with patch.object(writer, "_publish_no_replace", side_effect=inspect_then_publish) as publish:
                writer.write_deployment_bundle(bundle, output)
            self.assertEqual(1, publish.call_count)
            self.assertEqual(1, len(observed))
            self.assertFalse(observed[0].exists())
            self.assert_complete_private_tree(output, bundle)

    def test_all_files_and_both_staging_directories_are_fsynced_before_publication(self):
        original_fsync, original_publish = os.fsync, writer._publish_no_replace
        synchronized = []
        with tempfile.TemporaryDirectory(prefix="vowin-bundle-unit-") as temporary:
            output = Path(temporary) / PATH_SENTINEL

            def record_fsync(descriptor):
                info = os.fstat(descriptor)
                synchronized.append((stat.S_ISREG(info.st_mode), stat.S_ISDIR(info.st_mode), info.st_dev, info.st_ino))
                return original_fsync(descriptor)

            def inspect_then_publish(stage, destination):
                self.assertEqual(11, len([item for item in synchronized if item[0]]))
                self.assertEqual(11, len({(item[2], item[3]) for item in synchronized if item[0]}))
                if os.name != "nt":
                    self.assertEqual(2, len([item for item in synchronized if item[1]]))
                return original_publish(stage, destination)

            with patch.object(os, "fsync", side_effect=record_fsync), patch.object(writer, "_publish_no_replace", side_effect=inspect_then_publish):
                writer.write_deployment_bundle(private_bundle(), output)
            self.assert_complete_private_tree(output, private_bundle())

    def test_existing_populated_or_empty_directory_and_regular_file_are_never_overwritten(self):
        for target_kind in ("populated-directory", "empty-directory", "regular-file"):
            with self.subTest(target_kind=target_kind), tempfile.TemporaryDirectory(prefix="vowin-bundle-unit-") as temporary:
                parent = Path(temporary)
                output = parent / PATH_SENTINEL
                if target_kind == "regular-file":
                    output.write_bytes(b"existing private owner data")
                else:
                    output.mkdir()
                    if target_kind == "populated-directory":
                        (output / "credentials.json").write_bytes(b"existing private owner credentials")
                before = tree_contents(parent)
                with patch.object(writer, "_publish_no_replace") as publish:
                    self.assert_safe_failure(private_bundle(), output)
                publish.assert_not_called()
                self.assertEqual(before, tree_contents(parent))

    def test_missing_parent_is_not_created_as_a_side_effect(self):
        with tempfile.TemporaryDirectory(prefix="vowin-bundle-unit-") as temporary:
            parent = Path(temporary)
            output = parent / "absent-parent" / PATH_SENTINEL
            self.assert_safe_failure(private_bundle(), output)
            self.assertEqual([], list(parent.iterdir()))

    def test_invalid_bundle_is_rejected_before_any_staging_directory_is_created(self):
        variants = []
        for removed in EXPECTED_FILES:
            value = private_bundle()
            del value[removed]
            variants.append(("missing-" + removed, value))
        for extra in ("../outside.txt", "/absolute.txt", "C:/absolute.txt", "config\\pjsip.conf", "extra.txt"):
            value = private_bundle()
            value[extra] = PRIVATE_SENTINEL
            variants.append(("extra-path", value))
        for invalid in (None, b"binary", 123, True, "invalid\x00text", "invalid\ud800text"):
            value = private_bundle()
            value["config/pjsip.conf"] = invalid
            variants.append(("invalid-text", value))
        for invalid in (None, [], "not-a-bundle"):
            variants.append(("invalid-container", invalid))
        for index, (label, value) in enumerate(variants):
            with self.subTest(case=label, variant=index), tempfile.TemporaryDirectory(prefix="vowin-bundle-unit-") as temporary:
                parent = Path(temporary)
                output = parent / PATH_SENTINEL
                (parent / "unrelated.txt").write_bytes(b"preserve")
                before = tree_contents(parent)
                with patch.object(writer.tempfile, "mkdtemp", side_effect=AssertionError("validation must precede staging")) as mkdtemp:
                    self.assert_safe_failure(value, output)
                mkdtemp.assert_not_called()
                self.assertEqual(before, tree_contents(parent))

    def test_json_output_limits_count_bytes_and_cannot_publish_an_oversized_secret_import(self):
        for name in ("gateway-client.json", "deployment.normalized.json"):
            with self.subTest(artifact=name), tempfile.TemporaryDirectory(prefix="vowin-bundle-unit-") as temporary:
                parent = Path(temporary)
                bundle = private_bundle()
                bundle[name] = "\U0001f30d" * 16384 + "x"
                self.assertGreater(len(bundle[name].encode("utf-8")), 65536)
                self.assertLess(len(bundle[name]), 65536)
                self.assert_safe_failure(bundle, parent / PATH_SENTINEL)
                self.assertEqual([], list(parent.iterdir()))

    def test_real_output_symlinks_including_dangling_links_are_rejected_without_touching_targets(self):
        for target_exists in (True, False):
            with self.subTest(target_exists=target_exists), tempfile.TemporaryDirectory(prefix="vowin-bundle-unit-") as temporary:
                parent = Path(temporary)
                target, output = parent / "link-target", parent / PATH_SENTINEL
                if target_exists:
                    target.mkdir()
                    (target / "private-owner.txt").write_bytes(b"preserve target")
                os.symlink(target, output, target_is_directory=True)
                before = tree_contents(parent)
                self.assert_safe_failure(private_bundle(), output)
                self.assertEqual(before, tree_contents(parent))
                self.assertTrue(output.is_symlink())

    def test_real_symlink_parent_or_more_distant_ancestor_is_rejected_before_writing(self):
        for deep_parent in (False, True):
            with self.subTest(deep_parent=deep_parent), tempfile.TemporaryDirectory(prefix="vowin-bundle-unit-") as temporary:
                parent = Path(temporary)
                actual, alias = parent / "actual-parent", parent / "linked-parent"
                actual.mkdir()
                (actual / "nested").mkdir()
                (actual / "private-owner.txt").write_bytes(b"preserve owner data")
                os.symlink(actual, alias, target_is_directory=True)
                output = alias / "nested" / PATH_SENTINEL if deep_parent else alias / PATH_SENTINEL
                before = tree_contents(parent)
                self.assert_safe_failure(private_bundle(), output)
                self.assertEqual(before, tree_contents(parent))
                self.assertFalse((actual / "nested" / PATH_SENTINEL).exists())
                self.assertFalse((actual / PATH_SENTINEL).exists())

    def test_file_open_failure_removes_only_its_own_stage_without_partial_output(self):
        original_open = os.open
        injected = []
        with tempfile.TemporaryDirectory(prefix="vowin-bundle-unit-") as temporary:
            parent = Path(temporary)
            output = parent / PATH_SENTINEL
            unrelated = parent / ".vowin-deployment-other-owner"
            unrelated.mkdir()
            (unrelated / "owner.txt").write_bytes(b"not this operation")
            before = tree_contents(parent)

            def fail_private_file(path, flags, *args, **kwargs):
                if os.fspath(path).endswith("gateway-client.json"):
                    injected.append(os.fspath(path))
                    raise OSError(PRIVATE_SENTINEL + " " + PATH_SENTINEL)
                return original_open(path, flags, *args, **kwargs)

            with patch.object(os, "open", side_effect=fail_private_file):
                self.assert_safe_failure(private_bundle(), output)
            self.assertEqual(1, len(injected), "fixture must reach the intended write failure")
            self.assertEqual(before, tree_contents(parent))
            self.assertFalse(output.exists())

    def test_fsync_or_publish_failure_has_no_partial_output_and_never_cleans_another_stage(self):
        for failing_operation in ("fsync", "publish"):
            with self.subTest(operation=failing_operation), tempfile.TemporaryDirectory(prefix="vowin-bundle-unit-") as temporary:
                parent = Path(temporary)
                output = parent / PATH_SENTINEL
                unrelated = parent / ".vowin-deployment-other-owner"
                unrelated.mkdir()
                (unrelated / "owner.txt").write_bytes(b"not this operation")
                before = tree_contents(parent)
                target = patch.object(os, "fsync", side_effect=OSError(PRIVATE_SENTINEL)) if failing_operation == "fsync" else \
                    patch.object(writer, "_publish_no_replace", side_effect=OSError(PRIVATE_SENTINEL))
                with target as injected:
                    self.assert_safe_failure(private_bundle(), output)
                self.assertEqual(1, injected.call_count)
                self.assertEqual(before, tree_contents(parent))
                self.assertFalse(output.exists())

    def test_competing_empty_or_populated_output_created_during_publish_is_never_replaced(self):
        original_publish = writer._publish_no_replace
        for populate in (False, True):
            with self.subTest(populate=populate), tempfile.TemporaryDirectory(prefix="vowin-bundle-unit-") as temporary:
                parent = Path(temporary)
                output = parent / PATH_SENTINEL
                observed_stage = []

                def competing_publisher(stage, destination):
                    observed_stage.append(Path(stage))
                    Path(destination).mkdir()
                    if populate:
                        (Path(destination) / "competitor-secret.txt").write_bytes(b"competing owner data")
                    return original_publish(stage, destination)

                with patch.object(writer, "_publish_no_replace", side_effect=competing_publisher) as publish:
                    self.assert_safe_failure(private_bundle(), output)
                self.assertEqual(1, publish.call_count)
                self.assertEqual(1, len(observed_stage))
                self.assertFalse(observed_stage[0].exists())
                self.assertEqual({PATH_SENTINEL}, {path.name for path in parent.iterdir()})
                self.assertEqual({"competitor-secret.txt"} if populate else set(), {path.name for path in output.iterdir()})
                if populate:
                    self.assertEqual(b"competing owner data", (output / "competitor-secret.txt").read_bytes())

    def test_writer_never_executes_a_process_contacts_a_socket_or_creates_credentials(self):
        with tempfile.TemporaryDirectory(prefix="vowin-bundle-unit-") as temporary:
            output = Path(temporary) / PATH_SENTINEL
            with patch.object(socket, "socket", side_effect=AssertionError("no socket")) as socket_mock, \
                    patch.object(socket, "create_connection", side_effect=AssertionError("no connection")) as connect_mock, \
                    patch.object(subprocess, "run", side_effect=AssertionError("no process")) as run_mock, \
                    patch.object(subprocess, "Popen", side_effect=AssertionError("no process")) as popen_mock, \
                    patch.object(secrets, "token_hex", side_effect=AssertionError("no credentials")) as token_mock, \
                    patch.object(secrets, "token_urlsafe", side_effect=AssertionError("no credentials")) as urlsafe_mock:
                writer.write_deployment_bundle(private_bundle(), output)
            for mocked in (socket_mock, connect_mock, run_mock, popen_mock, token_mock, urlsafe_mock):
                mocked.assert_not_called()
            self.assert_complete_private_tree(output, private_bundle())


if __name__ == "__main__":
    unittest.main()
