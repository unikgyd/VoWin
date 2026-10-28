"""Publish a reviewed v2 bundle into a NEW owner-only directory, never apply it.

No credential discovery, merging, generation, reload, network or process use.
Publication is native Linux only: POSIX mode bits are not a Windows DACL.
Pure rendering supports both targets, and Windows imports use its own protected
settings lifecycle. Linux uses RENAME_NOREPLACE so even an existing empty
destination cannot be overwritten in a publication race. Files/staging dirs
are fsynced; this is not a claim of parent-directory crash durability.
"""
import ctypes
import os
from pathlib import Path
import shutil
import stat
import sys
import tempfile

from deployment_v2 import DeploymentV2Error


OUTPUTS = frozenset({
    "config/asterisk.conf", "config/modules.conf", "config/logger.conf",
    "config/rtp.conf", "config/http.conf", "config/manager.conf",
    "config/pjsip.conf", "config/extensions.conf", "gateway-client.json",
    "deployment.normalized.json", "sip-guard.nft",
})
INSTALLABLE_OUTPUTS = OUTPUTS | frozenset({
    "config/stasis.conf", "config/udptl.conf", "config/acl.conf",
    "config/indications.conf", "config/features.conf", "config/cel.conf",
    "config/cdr.conf", "config/ccss.conf", "config/pjproject.conf",
})


def _fail():
    raise DeploymentV2Error("Output: unable to publish a new private deployment directory") from None


def _publish_no_replace(stage, destination):
    if sys.platform == "linux":
        library = ctypes.CDLL(None, use_errno=True)
        function = library.renameat2
        function.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
        function.restype = ctypes.c_int
        if function(-100, os.fsencode(stage), -100, os.fsencode(destination), 1) != 0:
            raise OSError(ctypes.get_errno(), "private deployment publication failed")
    else:
        raise OSError("no atomic non-overwriting publication available")


def write_deployment_bundle(bundle, output: Path) -> None:
    """Atomically publish eleven validated files; refuse any existing target.

    output's existing parent must be non-symlink, as must every ancestor.
    Publication uses no-replace semantics. Only a private staging directory
    created by this call may be removed on failure; existing output is untouched.
    """
    _write_bundle(bundle, output, OUTPUTS)


def write_installable_deployment_bundle(bundle, output: Path) -> None:
    """Publish exactly twenty installable files with the same private boundary."""
    _write_bundle(bundle, output, INSTALLABLE_OUTPUTS)


def _write_bundle(bundle, output, expected_outputs):
    staging = None
    try:
        if sys.platform != "linux":
            # chmod(0600) on Windows does not make plaintext secrets private.
            # Never publish first and pretend to have enforced a DACL.
            _fail()
        if type(bundle) is not dict or set(bundle) != expected_outputs:
            _fail()
        encoded = {}
        for name in expected_outputs:
            value = bundle[name]
            if type(value) is not str or "\x00" in value:
                _fail()
            encoded[name] = value.encode("utf-8", "strict")
            if name.endswith(".json") and len(encoded[name]) > 65536:
                _fail()
        destination = Path(os.path.abspath(os.fspath(output)))
        parent = destination.parent
        if destination == parent or not parent.is_dir():
            _fail()
        for ancestor in (parent, *parent.parents):
            if ancestor.is_symlink():
                _fail()
        # lexists also refuses a dangling symlink. Do not resolve it away.
        if os.path.lexists(destination):
            _fail()
        staging = Path(tempfile.mkdtemp(prefix=".vowin-deployment-", dir=parent))
        staging.chmod(0o700)
        (staging / "config").mkdir(mode=0o700)
        for name in sorted(encoded):
            flags = os.O_WRONLY | os.O_CREAT | os.O_EXCL | getattr(os, "O_NOFOLLOW", 0)
            fd = os.open(staging / name, flags, 0o600)
            with os.fdopen(fd, "wb") as handle:
                handle.write(encoded[name])
                handle.flush()
                os.fsync(handle.fileno())
        if os.name != "nt":
            for directory in (staging / "config", staging):
                fd = os.open(directory, os.O_RDONLY | getattr(os, "O_DIRECTORY", 0))
                try:
                    os.fsync(fd)
                finally:
                    os.close(fd)
        _publish_no_replace(staging, destination)
        staging = None
    except (OSError, ValueError, TypeError, AttributeError, UnicodeError, DeploymentV2Error):
        _fail()
    finally:
        if staging is not None:
            # Verify the resolved absolute target before recursive cleanup.
            try:
                if (staging.parent == parent and staging.name.startswith(".vowin-deployment-")
                        and staging.resolve() == staging and parent.resolve() == parent
                        and stat.S_ISDIR(staging.lstat().st_mode) and not staging.is_symlink()):
                    shutil.rmtree(staging)
            except OSError:
                # A private failed stage may remain recoverable, never broaden
                # cleanup to the requested output, parent or an unknown path.
                pass
