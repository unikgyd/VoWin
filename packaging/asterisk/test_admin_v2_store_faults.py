"""Actual saved-not-applied store durability/path faults, using owned TEMP only.

These supplement the already implemented API, not an absent-feature RED.
Every fsync fault is selected by the fstat device/inode of its exact target;
other real fsync calls proceed.  No PBX, renderer, process or network is used.
"""

from copy import deepcopy
import hashlib
import json
import os
from pathlib import Path
import stat
import tempfile
import traceback
import unittest
from unittest.mock import patch

from admin_v2_store import AdminV2StoreError, initialize_store, read_store, update_store
from test_admin_deployment_v2 import SENTINEL, synthetic_model, synthetic_secrets


def _identity(info):
    return info.st_dev, info.st_ino


def _tree(directory):
    """Compare contents/modes/links, not read-induced access timestamps."""
    result = {}
    for path in [directory, *sorted(directory.rglob("*"))]:
        relative = "." if path == directory else path.relative_to(directory).as_posix()
        info = path.lstat()
        if stat.S_ISDIR(info.st_mode):
            result[relative] = ("directory", stat.S_IMODE(info.st_mode))
        elif stat.S_ISREG(info.st_mode):
            result[relative] = ("file", stat.S_IMODE(info.st_mode), info.st_nlink, path.read_bytes())
        elif stat.S_ISLNK(info.st_mode):
            result[relative] = ("symlink", os.readlink(path))
        else:
            result[relative] = ("other", info.st_mode)
    return result


def _change_name(name):
    def transform(model, private):
        model["ExtensionOptions"]["4201"]["Name"] = name
        return model, private
    return transform


class AdminV2StoreFaultTests(unittest.TestCase):
    def _initial(self, root):
        return initialize_store(root, synthetic_model(), synthetic_secrets())

    def _error(self, action, code="invalid"):
        try:
            action()
        except ValueError as error:
            self.assertIsInstance(error, AdminV2StoreError)
            self.assertEqual(code, error.code)
            # Capture the actual active exception, including its traceback and
            # any chain.  A sanitized final str alone is not sufficient.
            diagnostic = str(error) + repr(error) + "".join(traceback.format_exception(error))
            for private in (SENTINEL, *synthetic_secrets().values()):
                self.assertNotIn(private, diagnostic)
            return error
        self.fail("The actual private store operation must fail closed")

    def test_update_head_committed_then_root_fsync_failure_is_uncertain_and_readable(self):
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-fault-") as temporary:
            root = Path(temporary) / "store"
            initial = self._initial(root)
            old_head = (root / "state.json").read_bytes()
            old_revision = _tree(root / "revisions" / initial["revision"])
            root_inode = _identity(root.stat())
            real_fsync = os.fsync
            observations = []

            def fail_root_barrier(fd):
                if _identity(os.fstat(fd)) == root_inode:
                    observations.append((root / "state.json").read_bytes())
                    raise OSError(SENTINEL)
                return real_fsync(fd)

            with patch.object(os, "fsync", side_effect=fail_root_barrier):
                self._error(lambda: update_store(root, initial["revision"], _change_name("Committed despite uncertain barrier")),
                            "uncertain")

            self.assertEqual(1, len(observations), "Only the post-head root durability barrier must be faulted")
            self.assertNotEqual(old_head, observations[0], "The failure must occur after the actual head replacement")
            current = read_store(root)
            self.assertNotEqual(initial["revision"], current["revision"])
            expected_model = deepcopy(initial["model"])
            expected_model["ExtensionOptions"]["4201"]["Name"] = "Committed despite uncertain barrier"
            self.assertEqual(expected_model, current["model"])
            self.assertEqual(initial["secrets"], current["secrets"])
            self.assertEqual(observations[0], (root / "state.json").read_bytes())
            self.assertEqual(old_revision, _tree(root / "revisions" / initial["revision"]))

    def test_initialize_published_then_parent_fsync_failure_is_uncertain_not_absent(self):
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-fault-") as temporary:
            parent = Path(temporary)
            root = parent / "store"
            parent_inode = _identity(parent.stat())
            real_fsync = os.fsync
            observations = []

            def fail_parent_barrier(fd):
                if _identity(os.fstat(fd)) == parent_inode:
                    observations.append((root / "state.json").read_bytes() if root.is_dir() else None)
                    raise OSError(SENTINEL)
                return real_fsync(fd)

            with patch.object(os, "fsync", side_effect=fail_parent_barrier):
                self._error(lambda: self._initial(root), "uncertain")

            self.assertEqual(1, len(observations))
            self.assertIsNotNone(observations[0], "The real no-replace publication must already have happened")
            self.assertTrue(root.is_dir(), "An uncertain published initialization must not pretend the root was never created")
            current = read_store(root)
            self.assertEqual(synthetic_model(), current["model"])
            self.assertEqual(synthetic_secrets(), current["secrets"])
            self.assertEqual(observations[0], (root / "state.json").read_bytes())
            self.assertEqual({"state.json", "lock", "revisions"}, {entry.name for entry in root.iterdir()})
            before = _tree(root)
            self._error(lambda: self._initial(root), "conflict")
            self.assertEqual(before, _tree(root), "Retrying initialization must not overwrite its committed uncertain result")

    def test_new_revision_source_fsync_failures_preserve_old_head_and_do_not_promote(self):
        # First source: before a complete revision exists.  Second source:
        # public data was written, but the private pair is still incomplete.
        for source in ("deployment.json", "secrets.json"):
            with self.subTest(barrier=source), tempfile.TemporaryDirectory(prefix="vowin-admin-v2-fault-") as temporary:
                root = Path(temporary) / "store"
                initial = self._initial(root)
                old_head = (root / "state.json").read_bytes()
                old_revision = _tree(root / "revisions" / initial["revision"])
                real_fsync = os.fsync
                observations = []

                def fail_new_source_barrier(fd):
                    opened = _identity(os.fstat(fd))
                    for revision in (root / "revisions").iterdir():
                        if revision.name == initial["revision"]:
                            continue
                        target = revision / source
                        if target.is_file() and _identity(target.stat()) == opened:
                            observations.append((revision.name, opened))
                            raise OSError(SENTINEL)
                    return real_fsync(fd)

                with patch.object(os, "fsync", side_effect=fail_new_source_barrier):
                    self._error(lambda: update_store(root, initial["revision"], _change_name("Uncommitted source barrier")))

                self.assertEqual(1, len(observations), "A real new source inode must reach the selected fsync")
                self.assertNotEqual(initial["revision"], observations[0][0])
                self.assertEqual(old_head, (root / "state.json").read_bytes())
                self.assertEqual(old_revision, _tree(root / "revisions" / initial["revision"]))
                before_read = _tree(root)
                self.assertEqual(initial, read_store(root))
                self.assertEqual(before_read, _tree(root), "Reading may not promote or clean failed private revisions")

    def test_hardlinked_head_lock_and_active_revision_sources_fail_closed(self):
        for target_name in ("state.json", "lock", "deployment.json", "secrets.json", "manifest.json"):
            with self.subTest(target=target_name), tempfile.TemporaryDirectory(prefix="vowin-admin-v2-fault-") as temporary:
                parent = Path(temporary)
                root = parent / "store"
                initial = self._initial(root)
                directory = root if target_name in ("state.json", "lock") else root / "revisions" / initial["revision"]
                target = directory / target_name
                outside_link = parent / "owned-hardlink"
                unrelated = parent / "owned-unrelated"
                unrelated.write_bytes(b"Unrelated owned target must not change\n")
                unrelated.chmod(0o600)
                os.link(target, outside_link)
                self.assertEqual(2, target.stat().st_nlink)
                self.assertEqual(_identity(target.stat()), _identity(outside_link.stat()))
                before_root = _tree(root)
                before_outside = (outside_link.read_bytes(), outside_link.stat().st_nlink,
                                  stat.S_IMODE(outside_link.stat().st_mode), unrelated.read_bytes())
                callbacks = []

                def never_transform(model, private):
                    callbacks.append(True)
                    return model, private

                self._error(lambda: read_store(root))
                self._error(lambda: update_store(root, initial["revision"], never_transform))
                self.assertEqual([], callbacks, "Invalid linked input must be rejected before running the transform")
                self.assertEqual(before_root, _tree(root))
                self.assertEqual(before_outside, (outside_link.read_bytes(), outside_link.stat().st_nlink,
                                                 stat.S_IMODE(outside_link.stat().st_mode), unrelated.read_bytes()))

    def test_callback_moving_parent_or_root_cannot_commit_through_detached_descriptors(self):
        for moved_part in ("root", "parent"):
            with self.subTest(moved=moved_part), tempfile.TemporaryDirectory(prefix="vowin-admin-v2-fault-") as temporary:
                base = Path(temporary)
                parent = base / "parent"
                parent.mkdir(mode=0o700)
                root = parent / "store"
                initial = self._initial(root)
                old_head = (root / "state.json").read_bytes()
                old_revision = _tree(root / "revisions" / initial["revision"])
                moved = base / ("moved-" + moved_part)
                moved_root = moved if moved_part == "root" else moved / "store"
                callbacks = []

                def move_while_locked(model, private):
                    callbacks.append(True)
                    if moved_part == "root":
                        root.rename(moved)
                    else:
                        parent.rename(moved)
                        parent.mkdir(mode=0o700)
                    root.mkdir(mode=0o700)
                    model["ExtensionOptions"]["4201"]["Name"] = "Must not commit through moved descriptors"
                    return model, private

                with patch.object(os, "replace", wraps=os.replace) as replace:
                    self._error(lambda: update_store(root, initial["revision"], move_while_locked))
                self.assertEqual([True], callbacks, "The actual transform must run under the real store lock")
                replace.assert_not_called()
                self.assertEqual(old_head, (moved_root / "state.json").read_bytes())
                self.assertEqual(old_revision, _tree(moved_root / "revisions" / initial["revision"]))
                self.assertEqual(initial, read_store(moved_root), "The moved store must still select its unchanged old head")
                self.assertEqual({".": ("directory", 0o700)}, _tree(root), "The replacement absolute root must not be written")

    def test_read_does_not_promote_or_collect_valid_or_partial_unreferenced_revisions(self):
        with tempfile.TemporaryDirectory(prefix="vowin-admin-v2-fault-") as temporary:
            root = Path(temporary) / "store"
            initial = self._initial(root)
            old_head = (root / "state.json").read_bytes()
            # An actual complete revision is rendered by the store, but its
            # sole commit-point replacement fails.  Do not fabricate a valid
            # manifest or bypass the real writer for this positive orphan.
            with patch.object(os, "replace", side_effect=OSError(SENTINEL)) as replace:
                self._error(lambda: update_store(root, initial["revision"], _change_name("Valid but unreferenced revision")))
                self.assertTrue(replace.called)
            self.assertEqual(old_head, (root / "state.json").read_bytes())
            unreferenced = [directory for directory in (root / "revisions").iterdir()
                            if directory.name != initial["revision"]]
            self.assertEqual(1, len(unreferenced), "The actual writer must have completed one uncommitted private revision")
            complete = unreferenced[0]
            self.assertEqual({"deployment.json", "secrets.json", "manifest.json"}, {path.name for path in complete.iterdir()})
            manifest = json.loads((complete / "manifest.json").read_bytes())
            self.assertEqual(complete.name, manifest["Revision"])
            for source in ("deployment.json", "secrets.json"):
                raw = (complete / source).read_bytes()
                self.assertEqual({"Bytes": len(raw), "SHA256": hashlib.sha256(raw).hexdigest()}, manifest["Files"][source])
            self.assertEqual("Valid but unreferenced revision",
                             json.loads((complete / "deployment.json").read_bytes())["ExtensionOptions"]["4201"]["Name"])
            partial_name = next("P" * 42 + suffix for suffix in "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
                                if not (root / "revisions" / ("P" * 42 + suffix)).exists())
            partial = root / "revisions" / partial_name
            partial.mkdir(mode=0o700)
            (partial / "deployment.json").write_bytes(b'{"syntheticPartial":true}\n')
            (partial / "deployment.json").chmod(0o600)
            # Timestamps cannot confer a commit; both unreferenced directories
            # deliberately look newer than the actual selected revision.
            os.utime(complete, (2000000000, 2000000000))
            os.utime(partial, (2000000001, 2000000001))
            before_read = _tree(root)
            self.assertEqual(initial, read_store(root))
            self.assertEqual(initial, read_store(root))
            self.assertEqual(old_head, (root / "state.json").read_bytes())
            self.assertEqual(before_read, _tree(root), "Read must neither promote complete orphans nor delete partial ones")


if __name__ == "__main__":
    unittest.main()
