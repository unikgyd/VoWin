"""Native-Linux, owner-only saved-not-applied storage for deployment v2.

The public model and the complete, role-keyed private map are one immutable
revision.  A fixed flock serializes readers/writers across threads and
processes; state.json is the sole commit point.  No renderer, subprocess,
network, installed configuration, gateway or PBX is consulted or changed.

An initialized root contains lock, state.json and revisions/.  Each revision
has exactly deployment.json, secrets.json and manifest.json.  State and
manifest have the exact StoreVersion=1 layouts checked below.  Revisions use
fresh opaque 32-byte URL-safe identifiers, never a public credential digest.
Old and unreferenced revisions are not promoted, rewritten or garbage-
collected.  An interrupted initialization may retain its private sibling
stage, but can never leave a partially initialized published root.

All directory traversal uses pinned directory descriptors and O_NOFOLLOW.
Store directories/files must be owned by the effective uid with exact
0700/0600 modes; files must have one link.  Ancestors must be owned by root
or that uid and not group/other-writable, except root-owned sticky temporary
directories.  This is a POSIX private-file boundary, not encryption against
the owner/root, nor a Windows ACL implementation.

Publication and head changes are fsynced, including the containing directory.
If a final rename succeeded but its directory fsync failed, callers receive
"uncertain" and must re-read: the module never falsely promises rollback.
"""

from contextlib import contextmanager
from copy import deepcopy
import ctypes
import errno
import hashlib
import json
import os
import re
import secrets as _tokens
import stat
import sys

try:
    import fcntl
except ImportError:
    fcntl = None

from deployment_v2 import (
    deployment_json_text,
    normalize_deployment_v2,
    parse_deployment_secrets,
    parse_deployment_v2,
    resolve_deployment_secrets,
)


_TOKEN = re.compile(r"[A-Za-z0-9_-]{43}\Z")
_SHA256 = re.compile(r"[0-9a-f]{64}\Z")
_SOURCE_LIMIT = 65536
_META_LIMIT = 4096
_SOURCE_FILES = ("deployment.json", "secrets.json")
_REVISION_FILES = frozenset((*_SOURCE_FILES, "manifest.json"))
_MESSAGES = {
    "invalid": "Invalid private version 2 admin store or input.",
    "conflict": "Version 2 admin store revision conflict.",
    "uninitialized": "Version 2 admin store is not initialized.",
    "uncertain": "Version 2 admin store commit result is uncertain; re-read required.",
}


class AdminV2StoreError(ValueError):
    """A stable safe code; neither the message nor repr includes source input."""

    def __init__(self, code="invalid"):
        self.code = code if type(code) is str and code in _MESSAGES else "invalid"
        super().__init__(_MESSAGES[self.code])


def _fail(code="invalid"):
    raise AdminV2StoreError(code) from None


def _close(fd):
    if fd is not None:
        try:
            os.close(fd)
        except OSError:
            pass


def _absolute_path(root):
    if sys.platform != "linux" or fcntl is None:
        _fail()
    value = os.fspath(root)
    if type(value) is not str or not value or "\x00" in value:
        _fail()
    value.encode("utf-8", "strict")
    if not value.startswith("/") or value.startswith("//"):
        _fail()
    if any(part in (".", "..") for part in value.split("/")):
        _fail()
    value = os.path.normpath(value)
    if value == "/":
        _fail()
    return value


def _same_inode(left, right):
    return left.st_dev == right.st_dev and left.st_ino == right.st_ino


def _directory_stat(info, private):
    if not stat.S_ISDIR(info.st_mode) or info.st_nlink < 1:
        _fail()
    mode = stat.S_IMODE(info.st_mode)
    if private:
        if info.st_uid != os.geteuid() or mode != 0o700:
            _fail()
    else:
        if info.st_uid not in (0, os.geteuid()):
            _fail()
        writable = mode & 0o022
        sticky_root = info.st_uid == 0 and bool(info.st_mode & stat.S_ISVTX)
        if writable and not sticky_root:
            _fail()


def _file_stat(info):
    if (not stat.S_ISREG(info.st_mode) or info.st_nlink != 1
            or info.st_uid != os.geteuid() or stat.S_IMODE(info.st_mode) != 0o600):
        _fail()


def _open_directory(parent, name, private):
    before = os.stat(name, dir_fd=parent, follow_symlinks=False)
    _directory_stat(before, private)
    fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                 dir_fd=parent)
    try:
        after = os.fstat(fd)
        _directory_stat(after, private)
        if not _same_inode(before, after):
            _fail()
        return fd
    except BaseException:
        _close(fd)
        raise


def _open_parent(root):
    path = _absolute_path(root)
    parts = path.split("/")[1:]
    fd = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        _directory_stat(os.fstat(fd), False)
        for part in parts[:-1]:
            next_fd = _open_directory(fd, part, False)
            _close(fd)
            fd = next_fd
        return fd, parts[-1]
    except BaseException:
        _close(fd)
        raise


def _open_file(directory, name, writable=False):
    before = os.stat(name, dir_fd=directory, follow_symlinks=False)
    _file_stat(before)
    flags = (os.O_RDWR if writable else os.O_RDONLY)
    fd = os.open(name, flags | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK,
                 dir_fd=directory)
    try:
        after = os.fstat(fd)
        _file_stat(after)
        if not _same_inode(before, after):
            _fail()
        return fd
    except BaseException:
        _close(fd)
        raise


def _read_file(directory, name, maximum):
    fd = _open_file(directory, name)
    try:
        before = os.fstat(fd)
        if not 0 < before.st_size <= maximum:
            _fail()
        chunks, remaining = [], maximum + 1
        while remaining:
            chunk = os.read(fd, min(remaining, 16384))
            if not chunk:
                break
            chunks.append(chunk)
            remaining -= len(chunk)
        data = b"".join(chunks)
        after = os.fstat(fd)
        _file_stat(after)
        if (not _same_inode(before, after) or len(data) != before.st_size
                or after.st_size != before.st_size or len(data) > maximum
                or after.st_mtime_ns != before.st_mtime_ns
                or after.st_ctime_ns != before.st_ctime_ns):
            _fail()
        return data
    finally:
        _close(fd)


def _write_file(directory, name, data):
    fd = os.open(name, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                 0o600, dir_fd=directory)
    try:
        os.fchmod(fd, 0o600)
        _file_stat(os.fstat(fd))
        view = memoryview(data)
        while view:
            written = os.write(fd, view)
            if written <= 0:
                _fail()
            view = view[written:]
        os.fsync(fd)
        info = os.fstat(fd)
        _file_stat(info)
        if info.st_size != len(data):
            _fail()
    finally:
        os.close(fd)
    return info


def _mkdir_private(parent, name):
    os.mkdir(name, 0o700, dir_fd=parent)
    # A restrictive umask cannot broaden access.  On the normal supported
    # owner-private path, fchmod also makes the stored mode deterministic.
    fd = os.open(name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                 dir_fd=parent)
    try:
        os.fchmod(fd, 0o700)
        _directory_stat(os.fstat(fd), True)
        return fd
    except BaseException:
        _close(fd)
        raise


def _token():
    value = _tokens.token_urlsafe(32)
    if type(value) is not str or _TOKEN.fullmatch(value) is None:
        _fail()
    return value


def _json_bytes(value):
    return deployment_json_text(value).encode("utf-8", "strict")


def _pairs(pairs):
    value, seen = {}, set()
    for key, item in pairs:
        folded = key.casefold()
        if folded in seen:
            _fail()
        seen.add(folded)
        value[key] = item
    return value


def _meta(data):
    value = json.loads(data.decode("utf-8", "strict"), object_pairs_hook=_pairs)
    if type(value) is not dict:
        _fail()
    return value


def _normalize_pair(model, private):
    model = normalize_deployment_v2(deepcopy(model))
    private = resolve_deployment_secrets(model, deepcopy(private))
    # Enforce the same actual-byte serialization boundary as future re-import.
    public_bytes, private_bytes = _json_bytes(model), _json_bytes(private)
    return model, private, public_bytes, private_bytes


def _snapshot(revision, model, private):
    return {"revision": revision, "model": deepcopy(model), "secrets": deepcopy(private)}


def _state(revision):
    return {"StoreVersion": 1, "Revision": revision}


def _read_state(root_fd):
    raw = _read_file(root_fd, "state.json", _META_LIMIT)
    state = _meta(raw)
    if (set(state) != {"StoreVersion", "Revision"}
            or type(state["StoreVersion"]) is not int or state["StoreVersion"] != 1
            or type(state["Revision"]) is not str
            or _TOKEN.fullmatch(state["Revision"]) is None):
        _fail()
    return state["Revision"], raw


def _read_current(root_fd):
    _directory_stat(os.fstat(root_fd), True)
    revision, raw_state = _read_state(root_fd)
    revisions_fd = revision_fd = None
    try:
        revisions_fd = _open_directory(root_fd, "revisions", True)
        revision_fd = _open_directory(revisions_fd, revision, True)
        if set(os.listdir(revision_fd)) != _REVISION_FILES:
            _fail()
        manifest = _meta(_read_file(revision_fd, "manifest.json", _META_LIMIT))
        if (set(manifest) != {"StoreVersion", "Revision", "Files"}
                or type(manifest["StoreVersion"]) is not int or manifest["StoreVersion"] != 1
                or manifest["Revision"] != revision
                or type(manifest["Files"]) is not dict
                or set(manifest["Files"]) != set(_SOURCE_FILES)):
            _fail()
        sources = {}
        for name in _SOURCE_FILES:
            expected = manifest["Files"][name]
            if (type(expected) is not dict or set(expected) != {"Bytes", "SHA256"}
                    or type(expected["Bytes"]) is not int
                    or not 0 < expected["Bytes"] <= _SOURCE_LIMIT
                    or type(expected["SHA256"]) is not str
                    or _SHA256.fullmatch(expected["SHA256"]) is None):
                _fail()
            raw = _read_file(revision_fd, name, _SOURCE_LIMIT)
            if len(raw) != expected["Bytes"] or hashlib.sha256(raw).hexdigest() != expected["SHA256"]:
                _fail()
            sources[name] = raw
        model = parse_deployment_v2(sources["deployment.json"])
        private = parse_deployment_secrets(sources["secrets.json"], model)
        # Do not accept a head which moved during a snapshot, even if a
        # non-cooperating writer bypassed the fixed lock.
        if _read_file(root_fd, "state.json", _META_LIMIT) != raw_state:
            _fail()
        return _snapshot(revision, model, private)
    finally:
        _close(revision_fd)
        _close(revisions_fd)


def _create_revision(root_fd, model, private, public_bytes, private_bytes):
    revisions_fd = revision_fd = None
    try:
        revisions_fd = _open_directory(root_fd, "revisions", True)
        revision = None
        for _ in range(8):
            candidate = _token()
            try:
                revision_fd = _mkdir_private(revisions_fd, candidate)
            except FileExistsError:
                continue
            revision = candidate
            break
        if revision is None:
            _fail()
        manifest = {"StoreVersion": 1, "Revision": revision, "Files": {}}
        for name, raw in (("deployment.json", public_bytes), ("secrets.json", private_bytes)):
            _write_file(revision_fd, name, raw)
            manifest["Files"][name] = {"Bytes": len(raw), "SHA256": hashlib.sha256(raw).hexdigest()}
        _write_file(revision_fd, "manifest.json", _json_bytes(manifest))
        os.fsync(revision_fd)
        os.fsync(revisions_fd)
        return _snapshot(revision, model, private)
    finally:
        # Partial/new unreferenced revisions remain owner-private.  In
        # particular, never delete another committed revision during failure.
        _close(revision_fd)
        _close(revisions_fd)


def _confirm_link(parent, name, fd, private):
    current = os.stat(name, dir_fd=parent, follow_symlinks=False)
    opened = os.fstat(fd)
    _directory_stat(current, private)
    _directory_stat(opened, private)
    if not _same_inode(current, opened):
        _fail()


def _confirm_path(root, parent_fd, name, root_fd=None):
    """Do not silently commit into a parent moved out of the supplied path."""
    checked_parent = None
    try:
        checked_parent, checked_name = _open_parent(root)
        if checked_name != name or not _same_inode(os.fstat(checked_parent), os.fstat(parent_fd)):
            _fail()
        if root_fd is not None:
            _confirm_link(checked_parent, checked_name, root_fd, True)
    finally:
        _close(checked_parent)


@contextmanager
def _locked_store(root, exclusive):
    parent_fd = root_fd = lock_fd = None
    locked = False
    try:
        try:
            parent_fd, name = _open_parent(root)
            root_fd = _open_directory(parent_fd, name, True)
        except FileNotFoundError:
            _fail("uninitialized")
        # Existing roots are never repaired.  A missing lock/state/revisions
        # after publication is corrupt, not a request to initialize.
        lock_fd = _open_file(root_fd, "lock", writable=exclusive)
        if os.fstat(lock_fd).st_size != 0:
            _fail()
        fcntl.flock(lock_fd, fcntl.LOCK_EX if exclusive else fcntl.LOCK_SH)
        locked = True
        _confirm_path(root, parent_fd, name, root_fd)
        _confirm_lock(root_fd, lock_fd)
        yield parent_fd, name, root_fd, lock_fd
    finally:
        if locked:
            try:
                fcntl.flock(lock_fd, fcntl.LOCK_UN)
            except OSError:
                pass
        _close(lock_fd)
        _close(root_fd)
        _close(parent_fd)


def _confirm_lock(root_fd, lock_fd):
    current = os.stat("lock", dir_fd=root_fd, follow_symlinks=False)
    opened = os.fstat(lock_fd)
    _file_stat(current)
    _file_stat(opened)
    if current.st_size != 0 or opened.st_size != 0 or not _same_inode(current, opened):
        _fail()


def _publish_initial(parent_fd, stage, destination):
    library = ctypes.CDLL(None, use_errno=True)
    rename = library.renameat2
    rename.argtypes = [ctypes.c_int, ctypes.c_char_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_uint]
    rename.restype = ctypes.c_int
    if rename(parent_fd, os.fsencode(stage), parent_fd, os.fsencode(destination), 1) != 0:
        number = ctypes.get_errno()
        if number in (errno.EEXIST, errno.ENOTEMPTY):
            _fail("conflict")
        raise OSError(number, "private store publication failed")


def initialize_store(rootAbs, modelDict, secretsDict):
    """Create a NEW private root atomically; refuse every existing target.

    The entire normalized model and all declared role secrets are retained,
    including inactive accounts/peer roles.  No legacy source is discovered.
    """
    parent_fd = stage_fd = revisions_fd = None
    published = False
    try:
        parent_fd, destination = _open_parent(rootAbs)
        try:
            os.stat(destination, dir_fd=parent_fd, follow_symlinks=False)
        except FileNotFoundError:
            pass
        else:
            _fail("conflict")
        model, private, public_bytes, private_bytes = _normalize_pair(modelDict, secretsDict)
        stage = None
        for _ in range(8):
            candidate = ".admin-v2-init-" + _token()
            try:
                stage_fd = _mkdir_private(parent_fd, candidate)
            except FileExistsError:
                continue
            stage = candidate
            break
        if stage is None:
            _fail()
        revisions_fd = _mkdir_private(stage_fd, "revisions")
        os.fsync(revisions_fd)
        _close(revisions_fd)
        revisions_fd = None
        _write_file(stage_fd, "lock", b"")
        snapshot = _create_revision(stage_fd, model, private, public_bytes, private_bytes)
        _write_file(stage_fd, "state.json", _json_bytes(_state(snapshot["revision"])))
        os.fsync(stage_fd)
        _confirm_path(rootAbs, parent_fd, destination)
        _confirm_link(parent_fd, stage, stage_fd, True)
        _publish_initial(parent_fd, stage, destination)
        published = True
        os.fsync(parent_fd)
        _confirm_path(rootAbs, parent_fd, destination, stage_fd)
        return snapshot
    except AdminV2StoreError as error:
        _fail("uncertain" if published else error.code)
    except Exception:
        _fail("uncertain" if published else "invalid")
    finally:
        # A failed unpublished sibling stage is deliberately retained private
        # for inspection.  It is never adopted by read_store, and neither the
        # requested root nor any existing bundle is removed during cleanup.
        _close(revisions_fd)
        _close(stage_fd)
        _close(parent_fd)


def read_store(root):
    """Read one committed snapshot without creating, repairing or promoting it."""
    try:
        with _locked_store(root, False) as (parent_fd, name, root_fd, lock_fd):
            snapshot = _read_current(root_fd)
            _confirm_path(root, parent_fd, name, root_fd)
            _confirm_lock(root_fd, lock_fd)
            return snapshot
    except AdminV2StoreError as error:
        _fail(error.code)
    except Exception:
        _fail()


def update_store(root, expectedRevision, transform):
    """CAS a complete new revision; call transform on detached data under lock.

    transform(model, secrets) must return a tuple (newmodel, newsecrets).
    It is not called for a stale revision.  A failure before the head rename
    preserves the old head; an unreferenced private revision may remain.
    """
    replaced = False
    head_name = None
    owned_head = None
    try:
        if (type(expectedRevision) is not str or _TOKEN.fullmatch(expectedRevision) is None
                or not callable(transform)):
            _fail()
        with _locked_store(root, True) as (parent_fd, name, root_fd, lock_fd):
            current = _read_current(root_fd)
            if current["revision"] != expectedRevision:
                _fail("conflict")
            result = transform(deepcopy(current["model"]), deepcopy(current["secrets"]))
            if type(result) is not tuple or len(result) != 2:
                _fail()
            model, private, public_bytes, private_bytes = _normalize_pair(*result)
            snapshot = _create_revision(root_fd, model, private, public_bytes, private_bytes)
            # Recheck the actual committed snapshot, root and fixed lock after
            # the callback and all revision writes; no stale snapshot is
            # knowingly installed over a non-cooperating manual edit.
            checked = _read_current(root_fd)
            if checked != current:
                _fail("conflict")
            _confirm_path(root, parent_fd, name, root_fd)
            _confirm_lock(root_fd, lock_fd)
            head_name = ".state-" + _token()
            try:
                owned_head = _write_file(root_fd, head_name, _json_bytes(_state(snapshot["revision"])))
                os.replace(head_name, "state.json", src_dir_fd=root_fd, dst_dir_fd=root_fd)
                replaced = True
                head_name = None
                os.fsync(root_fd)
                _confirm_path(root, parent_fd, name, root_fd)
                _confirm_lock(root_fd, lock_fd)
                return snapshot
            finally:
                if head_name is not None and owned_head is not None:
                    try:
                        # This exact exclusive-created temporary head belongs
                        # to this operation, not an existing revision or root.
                        candidate = os.stat(head_name, dir_fd=root_fd, follow_symlinks=False)
                        _file_stat(candidate)
                        if _same_inode(candidate, owned_head):
                            os.unlink(head_name, dir_fd=root_fd)
                    except (OSError, AdminV2StoreError):
                        pass
    except AdminV2StoreError as error:
        _fail("uncertain" if replaced else error.code)
    except Exception:
        _fail("uncertain" if replaced else "invalid")
