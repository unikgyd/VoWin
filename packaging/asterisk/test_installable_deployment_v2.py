"""Offline installable-bundle RED; old eleven-file APIs remain independent.

All identities and credentials are fixed synthetic fixtures. These tests never
invoke a CLI, container, process, SIP peer, modem, call or SMS. Publication is
tested only in each test's own temporary directory on its supported Linux host.
"""
from contextlib import ExitStack
from copy import deepcopy
import builtins
import os
from pathlib import Path
import re
import secrets
import socket
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import deployment_bundle_v2 as publishers
import render_deployment_v2 as renderers
from deployment_v2 import DeploymentV2Error
from test_render_deployment_v2 import deployment, role_secrets


OLD_FILES = frozenset({
    "config/asterisk.conf", "config/modules.conf", "config/logger.conf",
    "config/rtp.conf", "config/http.conf", "config/manager.conf",
    "config/pjsip.conf", "config/extensions.conf", "gateway-client.json",
    "deployment.normalized.json", "sip-guard.nft",
})
CORE_FILES = frozenset({
    "config/stasis.conf", "config/udptl.conf", "config/acl.conf",
    "config/indications.conf", "config/features.conf", "config/cel.conf",
    "config/cdr.conf", "config/ccss.conf", "config/pjproject.conf",
})
INSTALLABLE_FILES = OLD_FILES | CORE_FILES
PRIVATE_SENTINEL = "synthetic-never-echo-installable-secret"


def owned_tree(directory):
    result = {}
    for path in directory.rglob("*"):
        key = path.relative_to(directory).as_posix()
        if path.is_symlink():
            result[key] = ("symlink", os.readlink(path))
        elif path.is_dir():
            result[key] = ("directory",)
        else:
            result[key] = ("file", path.read_bytes())
    return result


class InstallableDeploymentV2Tests(unittest.TestCase):
    def renderer(self):
        value = getattr(renderers, "render_installable_deployment_v2", None)
        self.assertTrue(callable(value), "The explicit installable rendering API must exist")
        return value

    def publisher(self):
        value = getattr(publishers, "write_installable_deployment_bundle", None)
        self.assertTrue(callable(value), "The strict installable publication API must exist")
        return value

    def bundle(self, platform="vowin-windows"):
        return self.renderer()(deployment(platform), role_secrets())

    def test_installable_renderer_keeps_old_eleven_and_adds_exactly_nine_core_configs(self):
        render = self.renderer()
        for platform in ("vowin-windows", "vocat-go"):
            with self.subTest(platform=platform):
                model, private = deployment(platform), role_secrets()
                old = renderers.render_deployment_v2(model, private)
                actual = render(model, private)
                self.assertEqual(OLD_FILES, set(old))
                self.assertEqual(INSTALLABLE_FILES, set(actual))
                self.assertEqual(20, len(actual))
                self.assertEqual(old, {name: actual[name] for name in OLD_FILES})
                self.assertEqual(old, renderers.render_deployment_v2(model, private))
                self.assertEqual(old["deployment.normalized.json"], actual["deployment.normalized.json"])
                self.assertEqual(old["gateway-client.json"], actual["gateway-client.json"])

    def test_core_configs_disable_unrequested_event_outputs_and_management_protocols(self):
        actual = self.bundle()
        for name in CORE_FILES:
            with self.subTest(core=name):
                self.assertIs(type(actual[name]), str)
                self.assertTrue(actual[name].strip())
                self.assertNotIn("\x00", actual[name])
                for password in role_secrets().values():
                    self.assertNotIn(password, actual[name])
        for name in ("config/cdr.conf", "config/cel.conf"):
            with self.subTest(disabled=name):
                self.assertRegex(actual[name], r"(?mi)^\s*\[general\]\s*$")
                self.assertRegex(actual[name], r"(?mi)^\s*enable\s*=\s*no\s*$")
                self.assertNotRegex(actual[name], r"(?mi)^\s*enable\s*=\s*yes\s*$")
        self.assertRegex(actual["config/http.conf"], r"(?mi)^\s*enabled\s*=\s*no\s*$")
        self.assertRegex(actual["config/manager.conf"], r"(?mi)^\s*enabled\s*=\s*no\s*$")
        self.assertRegex(actual["config/manager.conf"], r"(?mi)^\s*webenabled\s*=\s*no\s*$")

    def test_installable_publication_is_private_fresh_and_exactly_twenty_files(self):
        publish = self.publisher()
        bundle = self.bundle()
        original = deepcopy(bundle)
        self.assertEqual("linux", sys.platform, "The private publisher is intentionally native-Linux-only")
        with tempfile.TemporaryDirectory(prefix="vowin-installable-unit-") as temporary:
            parent = Path(temporary)
            output = parent / "fresh-installable"
            (parent / "unrelated.txt").write_bytes(b"owner data must remain")
            self.assertIsNone(publish(bundle, output))
            self.assertEqual(INSTALLABLE_FILES,
                             {path.relative_to(output).as_posix() for path in output.rglob("*") if path.is_file()})
            self.assertEqual({"config"}, {path.relative_to(output).as_posix() for path in output.rglob("*") if path.is_dir()})
            self.assertEqual(0o700, output.stat().st_mode & 0o777)
            self.assertEqual(0o700, (output / "config").stat().st_mode & 0o777)
            for name, text in bundle.items():
                self.assertFalse((output / name).is_symlink())
                self.assertEqual(text.encode("utf-8"), (output / name).read_bytes())
                self.assertEqual(0o600, (output / name).stat().st_mode & 0o777)
            self.assertEqual(b"owner data must remain", (parent / "unrelated.txt").read_bytes())
        self.assertEqual(original, bundle)

    def test_old_and_installable_publishers_keep_separate_strict_key_sets(self):
        publish = self.publisher()
        complete = self.bundle()
        legacy = renderers.render_deployment_v2(deployment(), role_secrets())
        invalid = [legacy, None, [], {**complete, "config/extra.conf": "[general]\n"},
                   {**complete, "../outside.txt": "not owned"}]
        for missing in CORE_FILES:
            invalid.append({name: text for name, text in complete.items() if name != missing})
        with tempfile.TemporaryDirectory(prefix="vowin-installable-unit-") as temporary:
            parent = Path(temporary)
            publishers.write_deployment_bundle(legacy, parent / "legacy-eleven")
            self.assertEqual(OLD_FILES, {path.relative_to(parent / "legacy-eleven").as_posix()
                                        for path in (parent / "legacy-eleven").rglob("*") if path.is_file()})
            with self.assertRaises(DeploymentV2Error):
                publishers.write_deployment_bundle(complete, parent / "legacy-rejects-twenty")
            for index, value in enumerate(invalid):
                with self.subTest(invalid=index):
                    output = parent / ("invalid-" + str(index))
                    before = owned_tree(parent)
                    with self.assertRaises(DeploymentV2Error):
                        publish(value, output)
                    self.assertEqual(before, owned_tree(parent))
                    self.assertFalse(output.exists())

    def test_installable_publisher_never_replaces_existing_files_directories_or_symlinks(self):
        publish = self.publisher()
        complete = self.bundle()
        for kind in ("file", "empty-directory", "populated-directory", "symlink", "dangling-symlink"):
            with self.subTest(kind=kind), tempfile.TemporaryDirectory(prefix="vowin-installable-unit-") as temporary:
                parent = Path(temporary)
                output = parent / "existing"
                if kind == "file":
                    output.write_bytes(b"original owner file")
                elif kind.endswith("symlink"):
                    target = parent / "owner-target"
                    if kind == "symlink":
                        target.mkdir()
                        (target / "owner.txt").write_bytes(b"original owner directory")
                    os.symlink(target, output, target_is_directory=True)
                else:
                    output.mkdir()
                    if kind == "populated-directory":
                        (output / "owner.txt").write_bytes(b"original private deployment")
                before = owned_tree(parent)
                with self.assertRaises(DeploymentV2Error):
                    publish(complete, output)
                self.assertEqual(before, owned_tree(parent))

    def test_installable_publisher_rejects_symlink_ancestors_and_cleans_only_owned_failed_stage(self):
        publish = self.publisher()
        complete = self.bundle()
        with tempfile.TemporaryDirectory(prefix="vowin-installable-unit-") as temporary:
            parent = Path(temporary)
            actual = parent / "owner-directory"
            actual.mkdir()
            (actual / "nested").mkdir()
            alias = parent / "ancestor-alias"
            os.symlink(actual, alias, target_is_directory=True)
            before = owned_tree(parent)
            with self.assertRaises(DeploymentV2Error):
                publish(complete, alias / "nested" / "blocked")
            self.assertEqual(before, owned_tree(parent))
            unrelated = parent / ".vowin-deployment-not-this-operation"
            unrelated.mkdir()
            (unrelated / "owner.txt").write_bytes(b"preserve unrelated owner stage")
            before = owned_tree(parent)
            original_open = os.open
            failures = []

            def fail_only_private_copy(path, flags, *args, **kwargs):
                if os.fspath(path).endswith("gateway-client.json"):
                    failures.append(os.fspath(path))
                    raise OSError(PRIVATE_SENTINEL)
                return original_open(path, flags, *args, **kwargs)

            with patch.object(os, "open", side_effect=fail_only_private_copy):
                with self.assertRaises(DeploymentV2Error) as caught:
                    publish(complete, parent / "failed-installable")
            self.assertEqual(1, len(failures), "The failure must occur after owned staging actually began")
            self.assertNotIn(PRIVATE_SENTINEL, str(caught.exception))
            self.assertEqual(before, owned_tree(parent))

    def test_installable_render_is_pure_and_does_not_mutate_model_or_secrets(self):
        render = self.renderer()
        model, private = deployment(), role_secrets()
        original_model, original_private = deepcopy(model), deepcopy(private)
        with ExitStack() as stack:
            denied = []
            for owner, name in ((builtins, "open"), (os, "open"), (socket, "socket"), (socket, "create_connection"),
                                (subprocess, "Popen"), (subprocess, "run"), (secrets, "token_hex"), (secrets, "token_urlsafe")):
                denied.append(stack.enter_context(patch.object(owner, name, side_effect=AssertionError("Pure rendering attempted I/O or secret generation"))))
            result = render(model, private)
        self.assertEqual(INSTALLABLE_FILES, set(result))
        self.assertEqual(original_model, model)
        self.assertEqual(original_private, private)
        for mocked in denied:
            mocked.assert_not_called()


if __name__ == "__main__":
    unittest.main()
