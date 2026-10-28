"""Controlled Linux v2 PBX application with durable, private recovery records.

The draft store schema and its immutable generations are not changed.  Every
operation holds its existing fixed flock, then a fixed target flock, across
validation and external actions.  The backend is a trusted, explicitly bound
PBX adapter, never a caller-supplied command.  A complete twenty-file candidate
and all original present/absent states are retained before any PBX mutation.

An operation is never replayed after an uncertain stopping/installing/starting
phase.  Only explicit recovery restores its original snapshot.  Recovery first
classifies every managed target as original or this operation's candidate;
foreign edits are preserved and leave recovery-required.  Writers of the
managed runtime directory must cooperate with the fixed target lock: POSIX
rename cannot provide a content-conditional CAS against arbitrary root writers.

Candidate/original copies remain root-private.  Runtime config entries are
installed for uid/gid 10001 as required by the explicitly supported PBX image;
the bound config directory itself is never renamed or chowned.  Gateway and
firewall artifacts are installed as files only, never imported or activated.
Public status describes a durable journal, not current live service health.

Version-two application heads keep the selected operation separate from the
last installed record. New journals retain OriginalInstalled for rollback.
Legacy version-one history has no installed authority and is never scanned
or guessed into one. A terminal journal/index publication gap can only be
reconciled by the same selected operation and an exact local fingerprint CAS;
it cannot execute a backend action or rewrite runtime files. The nonblocking
overview also distinguishes a pending/unconfirmed record from managed-file
drift, and never treats a prepared operation's revision as installed.
"""

from contextlib import contextmanager
from copy import deepcopy
from functools import wraps
import errno
import hashlib
import json
import os
from pathlib import Path
import re
import stat
import sys

import admin_v2_store as _store
from deployment_bundle_v2 import INSTALLABLE_OUTPUTS
from deployment_v2 import parse_deployment_v2
from render_deployment_v2 import render_installable_deployment_v2


_FILES = tuple(sorted(INSTALLABLE_OUTPUTS))
_TOKEN = re.compile(r"[A-Za-z0-9_-]{43}\Z")
_HASH = re.compile(r"[0-9a-f]{64}\Z")
_META_LIMIT = 65536
_FILE_LIMIT = 1048576
_TARGET_ROOT = ".v2-application"
_GUARD = ".admin-operation-lock"
_MARKER = "v2-managed.json"
_PENDING = frozenset({"stopping", "installing", "starting", "recovery-required"})
_TERMINAL = frozenset({"applied", "rolled-back"})
_STATES = _PENDING | _TERMINAL | {"prepared"}
_TEXT = {
    "invalid": "Invalid version 2 application input or private state.",
    "conflict": "Version 2 application source, target or managed files changed.",
    "busy": "Version 2 application is busy or requires explicit recovery.",
    "uncertain": "Version 2 application outcome is uncertain; inspect and recover the recorded operation.",
}


class ApplicationV2Error(ValueError):
    def __init__(self, code="invalid"):
        self.code = code if type(code) is str and code in _TEXT else "invalid"
        super().__init__(_TEXT[self.code])


def _fail(code="invalid"):
    raise ApplicationV2Error(code) from None


def _safe(function):
    @wraps(function)
    def wrapped(*args, **kwargs):
        try:
            return function(*args, **kwargs)
        except ApplicationV2Error as error:
            _fail(error.code)
        except _store.AdminV2StoreError as error:
            _fail(error.code if error.code in _TEXT else "invalid")
        except Exception:
            _fail()
    return wrapped


def _require_root():
    if sys.platform != "linux" or os.geteuid() != 0 or _store.fcntl is None:
        _fail()


def _token(value):
    if type(value) is not str or _TOKEN.fullmatch(value) is None:
        _fail()
    return value


def _identity(fd):
    info = os.fstat(fd)
    return {"Device": info.st_dev, "Inode": info.st_ino}


def _same_identity(value, fd):
    return value == _identity(fd)


def _json_bytes(value):
    raw = (json.dumps(value, ensure_ascii=False, separators=(",", ":"), allow_nan=False) + "\n").encode("utf-8", "strict")
    if not 0 < len(raw) <= _META_LIMIT:
        _fail()
    return raw


def _json_copy(value):
    raw = _json_bytes(value)
    return json.loads(raw.decode("utf-8"), object_pairs_hook=_store._pairs)


def _capture(backend):
    try:
        value = backend.capture()
        _valid_target(value)
        return _json_copy(value)
    except Exception:
        _fail()


def _valid_target(value):
    if (type(value) is not dict
            or set(value) != {"containerId", "imageId", "configRoot", "user", "restartPolicy"}
            or any(type(item) is not str or not item or len(item.encode("utf-8", "strict")) > 4096
                   or any(ord(character) < 32 for character in item) for item in value.values())):
        _fail()


def _backend(backend, name, *args, code="uncertain"):
    try:
        result = getattr(backend, name)(*(deepcopy(argument) for argument in args))
        if name == "idle":
            if result is False:
                _fail("busy")
            if result is not None and result is not True:
                _fail()
        elif result is not None:
            _fail()
        return result
    except ApplicationV2Error as error:
        _fail(error.code)
    except Exception:
        _fail(code)


def _read_meta(directory, name):
    value = _store._meta(_store._read_file(directory, name, _META_LIMIT))
    return value


def _write_meta(directory, name, value):
    temporary = ".meta-" + _store._token()
    owned = None
    committed = False
    attempted = False
    try:
        owned = _store._write_file(directory, temporary, _json_bytes(value))
        attempted = True
        os.replace(temporary, name, src_dir_fd=directory, dst_dir_fd=directory)
        committed = True
        os.fsync(directory)
    except Exception:
        _fail("uncertain" if attempted else "invalid")
    finally:
        if not committed and owned is not None:
            try:
                current = os.stat(temporary, dir_fd=directory, follow_symlinks=False)
                _store._file_stat(current)
                if _store._same_inode(current, owned):
                    os.unlink(temporary, dir_fd=directory)
            except (OSError, _store.AdminV2StoreError):
                pass


def _ensure_guard(base_fd, create):
    guard_fd = None
    try:
        try:
            guard_fd = _store._open_directory(base_fd, _GUARD, True)
        except FileNotFoundError:
            if not create:
                _fail()
            try:
                guard_fd = _store._mkdir_private(base_fd, _GUARD)
            except FileExistsError:
                _fail("busy")
            _store._write_file(guard_fd, _MARKER, _json_bytes({"ApplicationVersion": 1, "Managed": True}))
            os.fsync(guard_fd)
            os.fsync(base_fd)
        if set(os.listdir(guard_fd)) != {_MARKER}:
            _fail("busy")
        marker = _read_meta(guard_fd, _MARKER)
        if (set(marker) != {"ApplicationVersion", "Managed"}
                or type(marker["ApplicationVersion"]) is not int or marker["ApplicationVersion"] != 1
                or marker["Managed"] is not True):
            _fail("busy")
    except (FileNotFoundError, NotADirectoryError):
        _fail("busy")
    finally:
        _store._close(guard_fd)


def _initialize_target(base_fd):
    stage_fd = operations_fd = None
    stage = ".v2-application-init-" + _store._token()
    published = False
    try:
        stage_fd = _store._mkdir_private(base_fd, stage)
        operations_fd = _store._mkdir_private(stage_fd, "operations")
        os.fsync(operations_fd)
        _store._write_file(stage_fd, "lock", b"")
        _store._write_file(stage_fd, "state.json", _json_bytes(_head_v2(None, None)))
        os.fsync(stage_fd)
        _store._publish_initial(base_fd, stage, _TARGET_ROOT)
        published = True
        os.fsync(base_fd)
    except _store.AdminV2StoreError as error:
        if error.code != "conflict":
            _fail("uncertain" if published else "invalid")
        # Another v2 initializer won. Never overwrite it or remove its lock.
    except Exception:
        _fail("uncertain" if published else "invalid")
    finally:
        _store._close(operations_fd)
        _store._close(stage_fd)


@contextmanager
def _target_lock(base, create=False, exclusive=True):
    parent_fd = base_fd = application_fd = lock_fd = None
    locked = False
    try:
        parent_fd, name = _store._open_parent(base)
        base_fd = _store._open_directory(parent_fd, name, True)
        if create:
            _ensure_guard(base_fd, True)
        try:
            application_fd = _store._open_directory(base_fd, _TARGET_ROOT, True)
        except FileNotFoundError:
            if not create:
                yield None
                return
            _initialize_target(base_fd)
            application_fd = _store._open_directory(base_fd, _TARGET_ROOT, True)
        _ensure_guard(base_fd, False)
        lock_fd = _store._open_file(application_fd, "lock", writable=exclusive)
        if os.fstat(lock_fd).st_size != 0:
            _fail()
        _store.fcntl.flock(lock_fd, _store.fcntl.LOCK_EX if exclusive else _store.fcntl.LOCK_SH)
        locked = True
        context = {"base": base, "parent": parent_fd, "name": name, "base_fd": base_fd,
                   "application_fd": application_fd, "lock_fd": lock_fd}
        _confirm_context(context)
        yield context
    finally:
        if locked:
            try:
                _store.fcntl.flock(lock_fd, _store.fcntl.LOCK_UN)
            except OSError:
                pass
        _store._close(lock_fd)
        _store._close(application_fd)
        _store._close(base_fd)
        _store._close(parent_fd)


def _confirm_context(context):
    _store._confirm_path(context["base"], context["parent"], context["name"], context["base_fd"])
    _store._confirm_link(context["base_fd"], _TARGET_ROOT, context["application_fd"], True)
    _store._confirm_lock(context["application_fd"], context["lock_fd"])
    _ensure_guard(context["base_fd"], False)
    if "store" in context:
        _store._confirm_path(context["store"], context["store_parent"], context["store_name"], context["store_fd"])
        _store._confirm_lock(context["store_fd"], context["store_lock"])


def _bind_store(context, store, parent, name, descriptor, lock):
    context.update({"store": store, "store_parent": parent, "store_name": name,
                    "store_fd": descriptor, "store_lock": lock})
    _confirm_context(context)


def _paths(store, base):
    _require_root()
    store, base = _store._absolute_path(store), _store._absolute_path(base)
    if os.path.commonpath((store, base)) in (store, base):
        _fail()
    return store, base


def _open_config(base_fd):
    before = os.stat("config", dir_fd=base_fd, follow_symlinks=False)
    if (not stat.S_ISDIR(before.st_mode) or before.st_uid != 10001
            or before.st_gid != 10001 or stat.S_IMODE(before.st_mode) not in (0o700, 0o750)
            or before.st_nlink < 1):
        _fail()
    fd = os.open("config", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC, dir_fd=base_fd)
    after = os.fstat(fd)
    if (not _store._same_inode(before, after)
            or (before.st_uid, before.st_gid, before.st_mode) != (after.st_uid, after.st_gid, after.st_mode)):
        _store._close(fd)
        _fail()
    return fd


def _config_identity(fd):
    info = os.fstat(fd)
    return {"Device": info.st_dev, "Inode": info.st_ino, "Mode": stat.S_IMODE(info.st_mode),
            "Uid": info.st_uid, "Gid": info.st_gid}


def _runtime_file(directory, name, config):
    try:
        before = os.stat(name, dir_fd=directory, follow_symlinks=False)
    except FileNotFoundError:
        return {"Exists": False}, None
    if (not stat.S_ISREG(before.st_mode) or before.st_nlink != 1
            or stat.S_IMODE(before.st_mode) not in ((0o600, 0o640) if config else (0o600,))
            or before.st_uid != (10001 if config else 0)
            or before.st_gid != (10001 if config else 0)):
        _fail()
    maximum = _META_LIMIT if name.endswith(".json") else _FILE_LIMIT
    if not 0 <= before.st_size <= maximum:
        _fail()
    fd = os.open(name, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK, dir_fd=directory)
    try:
        opened = os.fstat(fd)
        if not _store._same_inode(before, opened):
            _fail()
        raw = bytearray()
        while len(raw) <= maximum:
            chunk = os.read(fd, min(16384, maximum + 1 - len(raw)))
            if not chunk:
                break
            raw.extend(chunk)
        after = os.fstat(fd)
        if (not _store._same_inode(before, after) or after.st_nlink != 1
                or (before.st_size, before.st_mtime_ns, before.st_ctime_ns, before.st_mode, before.st_uid, before.st_gid)
                != (after.st_size, after.st_mtime_ns, after.st_ctime_ns, after.st_mode, after.st_uid, after.st_gid)
                or len(raw) != before.st_size or len(raw) > maximum):
            _fail()
        raw = bytes(raw)
        return {"Exists": True, "Bytes": len(raw), "SHA256": hashlib.sha256(raw).hexdigest(),
                "Mode": stat.S_IMODE(before.st_mode), "Uid": before.st_uid, "Gid": before.st_gid}, raw
    finally:
        _store._close(fd)


def _inventory(base_fd):
    config_fd = _open_config(base_fd)
    try:
        values, bodies = {}, {}
        for relative in _FILES:
            config = relative.startswith("config/")
            directory, name = (config_fd, relative[7:]) if config else (base_fd, relative)
            values[relative], bodies[relative] = _runtime_file(directory, name, config)
        return values, bodies, _config_identity(config_fd)
    finally:
        _store._close(config_fd)


def _snapshot_directory(operation_fd, name, contents):
    root_fd = config_fd = None
    try:
        root_fd = _store._mkdir_private(operation_fd, name)
        config_fd = _store._mkdir_private(root_fd, "config")
        for relative in _FILES:
            raw = contents.get(relative)
            if raw is None:
                continue
            directory, leaf = (config_fd, relative[7:]) if relative.startswith("config/") else (root_fd, relative)
            _store._write_file(directory, leaf, raw)
        os.fsync(config_fd)
        os.fsync(root_fd)
    finally:
        _store._close(config_fd)
        _store._close(root_fd)


def _valid_metadata(value, relative, candidate=False):
    if type(value) is not dict or type(value.get("Exists")) is not bool:
        _fail()
    if not value["Exists"]:
        if set(value) != {"Exists"}:
            _fail()
        return
    if (set(value) != {"Exists", "Bytes", "SHA256", "Mode", "Uid", "Gid"}
            or type(value["Bytes"]) is not int or not 0 <= value["Bytes"] <= _FILE_LIMIT
            or type(value["SHA256"]) is not str or _HASH.fullmatch(value["SHA256"]) is None
            or type(value["Mode"]) is not int
            or value["Mode"] not in ((0o600, 0o640) if relative.startswith("config/") and not candidate else (0o600,))
            or type(value["Uid"]) is not int or value["Uid"] not in (0, 10001)
            or type(value["Gid"]) is not int or value["Gid"] not in (0, 10001)):
        _fail()
    if relative.endswith(".json") and value["Bytes"] > _META_LIMIT:
        _fail()


def _valid_identity(value, config=False):
    fields = {"Device", "Inode", "Mode", "Uid", "Gid"} if config else {"Device", "Inode"}
    if (type(value) is not dict or set(value) != fields
            or any(type(item) is not int or item < 0 for item in value.values())):
        _fail()
    if config and (value["Mode"] not in (0o700, 0o750) or value["Uid"] != 10001 or value["Gid"] != 10001):
        _fail()


def _valid_installed(value):
    if value is None:
        return
    fields = {"Operation", "Revision", "StoreIdentity", "ConfigIdentity", "Target", "Files"}
    if type(value) is not dict or set(value) != fields:
        _fail()
    _token(value["Operation"])
    _token(value["Revision"])
    _valid_identity(value["StoreIdentity"])
    _valid_identity(value["ConfigIdentity"], config=True)
    _valid_target(value["Target"])
    if type(value["Files"]) is not dict or set(value["Files"]) != set(_FILES):
        _fail()
    for relative in _FILES:
        metadata = value["Files"][relative]
        _valid_metadata(metadata, relative, candidate=True)
        owner = 10001 if relative.startswith("config/") else 0
        if not metadata["Exists"] or metadata["Uid"] != owner or metadata["Gid"] != owner:
            _fail()


def _head_v2(operation, installed):
    if operation is not None:
        _token(operation)
    _valid_installed(installed)
    return {"ApplicationVersion": 2, "Operation": operation, "Installed": deepcopy(installed)}


def _read_snapshot(operation_fd, name, files, side):
    directory_fd = config_fd = None
    try:
        directory_fd = _store._open_directory(operation_fd, name, True)
        config_fd = _store._open_directory(directory_fd, "config", True)
        present = {relative for relative in _FILES if files[relative][side]["Exists"]}
        expected_root = {"config"} | {relative for relative in present if not relative.startswith("config/")}
        expected_config = {relative[7:] for relative in present if relative.startswith("config/")}
        if set(os.listdir(directory_fd)) != expected_root or set(os.listdir(config_fd)) != expected_config:
            _fail()
        bodies = {}
        for relative in _FILES:
            metadata = files[relative][side]
            if not metadata["Exists"]:
                bodies[relative] = None
                continue
            target, leaf = (config_fd, relative[7:]) if relative.startswith("config/") else (directory_fd, relative)
            # Snapshot files are always root-owned, even when runtime metadata
            # intentionally names the daemon owner. Empty originals are valid.
            fd = _store._open_file(target, leaf)
            try:
                info = os.fstat(fd)
                maximum = _META_LIMIT if relative.endswith(".json") else _FILE_LIMIT
                if not 0 <= info.st_size <= maximum:
                    _fail()
                raw = bytearray()
                while len(raw) <= maximum:
                    chunk = os.read(fd, min(16384, maximum + 1 - len(raw)))
                    if not chunk:
                        break
                    raw.extend(chunk)
                raw = bytes(raw)
                after = os.fstat(fd)
                _store._file_stat(after)
                if (not _store._same_inode(info, after) or len(raw) != info.st_size or len(raw) > maximum
                        or (info.st_mtime_ns, info.st_ctime_ns, info.st_size) != (after.st_mtime_ns, after.st_ctime_ns, after.st_size)
                        or len(raw) != metadata["Bytes"] or hashlib.sha256(raw).hexdigest() != metadata["SHA256"]):
                    _fail()
                bodies[relative] = raw
            finally:
                _store._close(fd)
        return bodies
    finally:
        _store._close(config_fd)
        _store._close(directory_fd)


def _read_head(application_fd):
    value = _read_meta(application_fd, "state.json")
    if type(value.get("ApplicationVersion")) is not int or value["ApplicationVersion"] not in (1, 2):
        _fail()
    fields = {"ApplicationVersion", "Operation"}
    if value["ApplicationVersion"] == 2:
        fields.add("Installed")
    if set(value) != fields:
        _fail()
    if value["Operation"] is not None:
        _token(value["Operation"])
    if value["ApplicationVersion"] == 2:
        _valid_installed(value["Installed"])
    return value


def _read_operation(context, token, require_candidate=True):
    _token(token)
    operations_fd = operation_fd = None
    try:
        operations_fd = _store._open_directory(context["application_fd"], "operations", True)
        operation_fd = _store._open_directory(operations_fd, token, True)
        value = _read_meta(operation_fd, "operation.json")
        fields = {"ApplicationVersion", "Operation", "Revision", "State", "StoreIdentity", "BaseIdentity",
                  "ConfigIdentity", "Target", "Files", "ChangedFiles"}
        if type(value.get("ApplicationVersion")) is not int or value["ApplicationVersion"] not in (1, 2):
            _fail()
        if value["ApplicationVersion"] == 2:
            fields.add("OriginalInstalled")
        if (set(value) != fields
                or value["Operation"] != token or value["State"] not in _STATES):
            _fail()
        if value["ApplicationVersion"] == 2:
            _valid_installed(value["OriginalInstalled"])
        _token(value["Revision"])
        for identity in (value["StoreIdentity"], value["BaseIdentity"]):
            if type(identity) is not dict or set(identity) != {"Device", "Inode"} or any(type(item) is not int or item < 0 for item in identity.values()):
                _fail()
        config = value["ConfigIdentity"]
        if (type(config) is not dict or set(config) != {"Device", "Inode", "Mode", "Uid", "Gid"}
                or any(type(item) is not int or item < 0 for item in config.values())
                or config["Mode"] not in (0o700, 0o750) or config["Uid"] != 10001 or config["Gid"] != 10001):
            _fail()
        _valid_target(value["Target"])
        if type(value["Files"]) is not dict or set(value["Files"]) != set(_FILES):
            _fail()
        for relative, pair in value["Files"].items():
            if type(pair) is not dict or set(pair) != {"Original", "Candidate"}:
                _fail()
            _valid_metadata(pair["Original"], relative)
            _valid_metadata(pair["Candidate"], relative, candidate=True)
            if not pair["Candidate"]["Exists"]:
                _fail()
            owner = 10001 if relative.startswith("config/") else 0
            if pair["Candidate"]["Uid"] != owner or pair["Candidate"]["Gid"] != owner:
                _fail()
            if (pair["Original"]["Exists"]
                    and (pair["Original"]["Uid"] != owner or pair["Original"]["Gid"] != owner)):
                _fail()
        expected_changed = [relative for relative in _FILES if value["Files"][relative]["Original"] != value["Files"][relative]["Candidate"]]
        if type(value["ChangedFiles"]) is not list or value["ChangedFiles"] != expected_changed:
            _fail()
        original = _read_snapshot(operation_fd, "original", value["Files"], "Original")
        if require_candidate:
            candidate = _read_snapshot(operation_fd, "candidate", value["Files"], "Candidate")
            model = parse_deployment_v2(candidate["deployment.normalized.json"])
        else:
            # Recovery only needs the validated original bytes and the strict
            # postimage fingerprints above. A lost/corrupt candidate must not
            # hide a pending journal or prevent restoration of known originals.
            # Do not inspect, repair or promote the candidate/draft as a side
            # effect of status or explicit original-only recovery.
            candidate = {relative: None for relative in _FILES}
            model = None
        return value, original, candidate, model
    finally:
        _store._close(operation_fd)
        _store._close(operations_fd)


def _persist(context, operation, state):
    operations_fd = operation_fd = None
    try:
        operations_fd = _store._open_directory(context["application_fd"], "operations", True)
        operation_fd = _store._open_directory(operations_fd, operation["Operation"], True)
        value = deepcopy(operation)
        value["State"] = state
        _write_meta(operation_fd, "operation.json", value)
        operation.clear()
        operation.update(value)
    finally:
        _store._close(operation_fd)
        _store._close(operations_fd)


def _public(operation=None):
    return {
        "operation": operation["Operation"] if operation else None,
        "revision": operation["Revision"] if operation else None,
        "state": operation["State"] if operation else "uninitialized",
        "fileCount": 20 if operation else 0,
        "changedFiles": list(operation["ChangedFiles"]) if operation else [],
        "pbxApplied": bool(operation and operation["State"] == "applied"),
        "gatewayApplied": False, "firewallApplied": False, "statusSource": "journal",
    }


def _installed_from_operation(operation):
    value = {
        "Operation": operation["Operation"], "Revision": operation["Revision"],
        "StoreIdentity": deepcopy(operation["StoreIdentity"]),
        "ConfigIdentity": deepcopy(operation["ConfigIdentity"]),
        "Target": deepcopy(operation["Target"]),
        "Files": {relative: deepcopy(operation["Files"][relative]["Candidate"]) for relative in _FILES},
    }
    _valid_installed(value)
    return value


def _terminal_installed(operation):
    if operation["ApplicationVersion"] != 2 or operation["State"] not in _TERMINAL:
        _fail("conflict")
    return (_installed_from_operation(operation) if operation["State"] == "applied"
            else deepcopy(operation["OriginalInstalled"]))


def _terminal_index_matches(head, operation):
    if operation["ApplicationVersion"] == 1:
        # Version one has no installation index. Never infer or synthesize one
        # by inspecting historical operation directories or a legacy terminal.
        return True
    return head["ApplicationVersion"] == 2 and head["Installed"] == _terminal_installed(operation)


def _terminal_local_cas(context, operation):
    _confirm_context(context)
    if not _same_identity(operation["BaseIdentity"], context["base_fd"]):
        _fail("conflict")
    if "store_fd" in context and not _same_identity(operation["StoreIdentity"], context["store_fd"]):
        _fail("conflict")
    if operation["Target"]["configRoot"] != str(Path(context["base"]) / "config"):
        _fail("conflict")
    inventory, _, config = _inventory(context["base_fd"])
    side = "Candidate" if operation["State"] == "applied" else "Original"
    if (config != operation["ConfigIdentity"]
            or any(inventory[relative] != operation["Files"][relative][side] for relative in _FILES)):
        _fail("conflict")


def _commit_terminal_index(context, operation):
    """Complete the second durable commit, without backend/runtime mutations.

    A terminal journal is already proof that verification completed. If its
    index publication was interrupted, only the same selected operation and
    its exact local file postimage may complete that publication. A different
    selected operation or unrelated valid index is never overwritten.
    """
    if operation["ApplicationVersion"] != 2:
        return
    head = _read_head(context["application_fd"])
    if head["Operation"] != operation["Operation"] or head["ApplicationVersion"] != 2:
        _fail("conflict")
    if _terminal_index_matches(head, operation):
        return
    allowed = (operation["OriginalInstalled"],)
    if operation["State"] == "rolled-back":
        allowed += (_installed_from_operation(operation),)
    if head["Installed"] not in allowed:
        _fail("conflict")
    _terminal_local_cas(context, operation)
    _write_meta(context["application_fd"], "state.json",
                _head_v2(operation["Operation"], _terminal_installed(operation)))


def _operation_path(context, operation, side):
    return str(Path(context["base"]) / _TARGET_ROOT / "operations" / operation["Operation"] / side / "config")


def _assert_bound(context, operation, store_fd):
    _confirm_context(context)
    if not _same_identity(operation["StoreIdentity"], store_fd) or not _same_identity(operation["BaseIdentity"], context["base_fd"]):
        _fail("conflict")
    inventory, bodies, config = _inventory(context["base_fd"])
    if config != operation["ConfigIdentity"]:
        _fail("conflict")
    return inventory, bodies


def _classify(context, operation, store_fd, backend, original_only=False, candidate_only=False):
    inventory, bodies = _assert_bound(context, operation, store_fd)
    for relative in _FILES:
        pair = operation["Files"][relative]
        allowed = ((pair["Original"],) if original_only else
                   (pair["Candidate"],) if candidate_only else (pair["Original"], pair["Candidate"]))
        if inventory[relative] not in allowed:
            _fail("conflict")
    # Known local conflicts must not even access the runtime backend. The
    # stable runtime identity is checked only after the complete local CAS.
    if _capture(backend) != operation["Target"]:
        _fail("conflict")
    return inventory, bodies


def _install_one(directory, name, raw, desired, expected, config):
    current, _ = _runtime_file(directory, name, config)
    if current != expected:
        _fail("conflict")
    temporary = ".v2-file-" + _store._token()
    descriptor = None
    owned = None
    replaced = False
    try:
        descriptor = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW | os.O_CLOEXEC,
                             0o600, dir_fd=directory)
        # Record ownership immediately: later write/chown/fsync failures must
        # not leave our own extra config entry blocking runtime verification.
        owned = os.fstat(descriptor)
        if not stat.S_ISREG(owned.st_mode) or owned.st_nlink != 1:
            _fail()
        os.fchmod(descriptor, desired["Mode"])
        os.fchown(descriptor, desired["Uid"], desired["Gid"])
        view = memoryview(raw)
        while view:
            count = os.write(descriptor, view)
            if count <= 0:
                _fail()
            view = view[count:]
        os.fsync(descriptor)
        complete = os.fstat(descriptor)
        if complete.st_nlink != 1 or complete.st_size != len(raw) or not _store._same_inode(owned, complete):
            _fail()
        os.close(descriptor)
        descriptor = None
        # Check again immediately before the actual mutation, not merely at
        # preflight. Cooperative writers are excluded by the fixed target lock.
        if _runtime_file(directory, name, config)[0] != expected:
            _fail("conflict")
        os.replace(temporary, name, src_dir_fd=directory, dst_dir_fd=directory)
        replaced = True
        os.fsync(directory)
    finally:
        _store._close(descriptor)
        if not replaced and owned is not None:
            try:
                linked = os.stat(temporary, dir_fd=directory, follow_symlinks=False)
                if stat.S_ISREG(linked.st_mode) and linked.st_nlink == 1 and _store._same_inode(linked, owned):
                    os.unlink(temporary, dir_fd=directory)
                    os.fsync(directory)
            except OSError:
                pass


def _install(context, operation, originals, candidate, restore=False):
    config_fd = _open_config(context["base_fd"])
    try:
        if _config_identity(config_fd) != operation["ConfigIdentity"]:
            _fail("conflict")
        for relative in _FILES:
            _confirm_context(context)
            opened = _open_config(context["base_fd"])
            try:
                if _config_identity(opened) != operation["ConfigIdentity"]:
                    _fail("conflict")
            finally:
                _store._close(opened)
            config = relative.startswith("config/")
            directory, name = (config_fd, relative[7:]) if config else (context["base_fd"], relative)
            current, _ = _runtime_file(directory, name, config)
            pair = operation["Files"][relative]
            if current not in (pair["Original"], pair["Candidate"]):
                _fail("conflict")
            desired = pair["Original"] if restore else pair["Candidate"]
            if current == desired:
                continue
            if not desired["Exists"]:
                # An absent original is removed only if this exact managed
                # candidate fingerprint is still present, never a foreign edit.
                if current != pair["Candidate"]:
                    _fail("conflict")
                if _runtime_file(directory, name, config)[0] != current:
                    _fail("conflict")
                os.unlink(name, dir_fd=directory)
                os.fsync(directory)
            else:
                raw = originals[relative] if restore else candidate[relative]
                _install_one(directory, name, raw, desired, current, config)
        os.fsync(config_fd)
        os.fsync(context["base_fd"])
    finally:
        _store._close(config_fd)


def _recovery_required(context, operation):
    try:
        _persist(context, operation, "recovery-required")
    except Exception:
        # The prior durable nonterminal phase also forbids replay. Never mask
        # the uncertain outcome by declaring failure or restoring prepared.
        pass


def _rollback(context, operation, store_fd, backend, originals, candidate):
    _persist(context, operation, "recovery-required")
    _classify(context, operation, store_fd, backend)
    _backend(backend, "idle", operation["Target"], code="busy")
    _backend(backend, "stop", operation["Target"])
    _classify(context, operation, store_fd, backend)
    # Re-persist intent immediately before any restoration mutation.
    _persist(context, operation, "recovery-required")
    _install(context, operation, originals, candidate, restore=True)
    _classify(context, operation, store_fd, backend, original_only=True)
    _backend(backend, "start", operation["Target"])
    _backend(backend, "verify", operation["Target"], _operation_path(context, operation, "original"), None)
    _backend(backend, "restore_policy", operation["Target"])
    _classify(context, operation, store_fd, backend, original_only=True)
    _persist(context, operation, "rolled-back")
    _commit_terminal_index(context, operation)
    return _public(operation)


@_safe
def prepare_application(storeAbs, baseAbs, expectedRevision, backend):
    store, base = _paths(storeAbs, baseAbs)
    _token(expectedRevision)
    with _store._locked_store(store, True) as (store_parent, store_name, store_fd, store_lock):
        snapshot = _store._read_current(store_fd)
        if snapshot["revision"] != expectedRevision:
            _fail("conflict")
        with _target_lock(base, create=True) as context:
            _bind_store(context, store, store_parent, store_name, store_fd, store_lock)
            head = _read_head(context["application_fd"])
            selected = head["Operation"]
            if selected:
                previous = _read_operation(context, selected)[0]
                if previous["State"] in _PENDING:
                    if not _same_identity(previous["StoreIdentity"], store_fd):
                        _fail("conflict")
                    _fail("busy")
                if previous["State"] in _TERMINAL and not _terminal_index_matches(head, previous):
                    # Keep the only selected source for an idempotent index
                    # repair. A new prepare must not hide this interrupted
                    # journal/index publication with another operation head.
                    _fail("busy")
            installed = deepcopy(head.get("Installed"))
            target = _capture(backend)
            if target["configRoot"] != str(Path(base) / "config"):
                _fail("conflict")
            original_files, original_bodies, config = _inventory(context["base_fd"])
            bundle = render_installable_deployment_v2(snapshot["model"], snapshot["secrets"])
            if type(bundle) is not dict or set(bundle) != set(_FILES):
                _fail()
            candidate_bodies = {}
            files = {}
            for relative in _FILES:
                text = bundle[relative]
                if type(text) is not str or "\x00" in text:
                    _fail()
                raw = text.encode("utf-8", "strict")
                if len(raw) > (_META_LIMIT if relative.endswith(".json") else _FILE_LIMIT):
                    _fail()
                candidate_bodies[relative] = raw
                owner = 10001 if relative.startswith("config/") else 0
                files[relative] = {"Original": original_files[relative],
                                   "Candidate": {"Exists": True, "Bytes": len(raw), "SHA256": hashlib.sha256(raw).hexdigest(),
                                                 "Mode": 0o600, "Uid": owner, "Gid": owner}}
            operations_fd = operation_fd = None
            try:
                operations_fd = _store._open_directory(context["application_fd"], "operations", True)
                token = _store._token()
                operation_fd = _store._mkdir_private(operations_fd, token)
                _snapshot_directory(operation_fd, "candidate", candidate_bodies)
                _snapshot_directory(operation_fd, "original", original_bodies)
                operation = {"ApplicationVersion": 2, "Operation": token, "Revision": expectedRevision, "State": "prepared",
                             "StoreIdentity": _identity(store_fd), "BaseIdentity": _identity(context["base_fd"]),
                             "ConfigIdentity": config, "Target": target, "Files": files,
                             "ChangedFiles": [relative for relative in _FILES if files[relative]["Original"] != files[relative]["Candidate"]],
                             "OriginalInstalled": installed}
                _store._write_file(operation_fd, "operation.json", _json_bytes(operation))
                os.fsync(operation_fd)
                os.fsync(operations_fd)
                _backend(backend, "preflight", _operation_path(context, operation, "candidate"), deepcopy(snapshot["model"]), code="invalid")
                _store._confirm_path(store, store_parent, store_name, store_fd)
                _store._confirm_lock(store_fd, store_lock)
                if _store._read_current(store_fd) != snapshot:
                    _fail("conflict")
                current, _ = _classify(context, operation, store_fd, backend, original_only=True)
                if current != original_files:
                    _fail("conflict")
                # Re-read both immutable snapshots before selecting the journal.
                _read_operation(context, token)
                _write_meta(context["application_fd"], "state.json", _head_v2(token, installed))
                return _public(operation)
            finally:
                _store._close(operation_fd)
                _store._close(operations_fd)


@_safe
def apply_application(storeAbs, baseAbs, expectedRevision, operationToken43, backend):
    store, base = _paths(storeAbs, baseAbs)
    _token(expectedRevision)
    token = _token(operationToken43)
    with _store._locked_store(store, True) as (store_parent, store_name, store_fd, store_lock):
        with _target_lock(base) as context:
            if context is None or _read_head(context["application_fd"])["Operation"] != token:
                _fail("conflict")
            _bind_store(context, store, store_parent, store_name, store_fd, store_lock)
            operation, originals, candidate, model = _read_operation(context, token, require_candidate=False)
            if not _same_identity(operation["StoreIdentity"], store_fd) or operation["Revision"] != expectedRevision:
                _fail("conflict")
            if operation["State"] in _TERMINAL:
                if operation["ApplicationVersion"] == 1:
                    # Preserve the legacy strict terminal read, but never
                    # upgrade its absence of an installed index by guessing.
                    _read_operation(context, token)
                else:
                    _commit_terminal_index(context, operation)
                return _public(operation)
            if operation["State"] != "prepared":
                _fail("conflict")
            operation, originals, candidate, model = _read_operation(context, token)
            if _store._read_current(store_fd)["revision"] != expectedRevision:
                _fail("conflict")
            _classify(context, operation, store_fd, backend, original_only=True)
            stopped = verified = False
            try:
                _persist(context, operation, "stopping")
                try:
                    _backend(backend, "idle", operation["Target"], code="busy")
                except ApplicationV2Error as error:
                    if error.code == "busy":
                        _persist(context, operation, "prepared")
                        _fail("busy")
                    raise
                _backend(backend, "stop", operation["Target"])
                stopped = True
                _store._confirm_path(store, store_parent, store_name, store_fd)
                _store._confirm_lock(store_fd, store_lock)
                if _store._read_current(store_fd)["revision"] != expectedRevision:
                    _fail("conflict")
                _classify(context, operation, store_fd, backend, original_only=True)
                _persist(context, operation, "installing")
                _install(context, operation, originals, candidate)
                _classify(context, operation, store_fd, backend, candidate_only=True)
                _persist(context, operation, "starting")
                _backend(backend, "start", operation["Target"])
                _backend(backend, "verify", operation["Target"], _operation_path(context, operation, "candidate"), model)
                _backend(backend, "restore_policy", operation["Target"])
                _classify(context, operation, store_fd, backend, candidate_only=True)
                if _store._read_current(store_fd)["revision"] != expectedRevision:
                    _fail("conflict")
                verified = True
                _persist(context, operation, "applied")
                _commit_terminal_index(context, operation)
                return _public(operation)
            except ApplicationV2Error as error:
                if operation["State"] in _TERMINAL:
                    # The first commit is durable. Do not turn an index-only
                    # acknowledgement failure into a PBX rollback/replay.
                    _fail("uncertain")
                if error.code == "busy" and operation["State"] == "prepared":
                    raise
                _recovery_required(context, operation)
                if stopped and not verified:
                    try:
                        return _rollback(context, operation, store_fd, backend, originals, candidate)
                    except Exception:
                        if operation["State"] in _TERMINAL:
                            _fail("uncertain")
                        _recovery_required(context, operation)
                _fail("uncertain")
            except Exception:
                if operation["State"] in _TERMINAL:
                    _fail("uncertain")
                _recovery_required(context, operation)
                if stopped and not verified:
                    try:
                        return _rollback(context, operation, store_fd, backend, originals, candidate)
                    except Exception:
                        if operation["State"] in _TERMINAL:
                            _fail("uncertain")
                        _recovery_required(context, operation)
                _fail("uncertain")


@_safe
def recover_application(storeAbs, baseAbs, operationToken43, backend):
    store, base = _paths(storeAbs, baseAbs)
    token = _token(operationToken43)
    with _store._locked_store(store, True) as (store_parent, store_name, store_fd, store_lock):
        with _target_lock(base) as context:
            if context is None or _read_head(context["application_fd"])["Operation"] != token:
                _fail("conflict")
            _bind_store(context, store, store_parent, store_name, store_fd, store_lock)
            operation, originals, candidate, _ = _read_operation(context, token, require_candidate=False)
            if not _same_identity(operation["StoreIdentity"], store_fd):
                _fail("conflict")
            if operation["State"] == "rolled-back":
                _commit_terminal_index(context, operation)
                return _public(operation)
            if operation["State"] not in _PENDING:
                _fail("conflict")
            # Reject a known foreign edit before any backend call or journal
            # rewrite. Do not turn a deterministic source conflict into a
            # speculative stop/start of an unrelated runtime.
            _classify(context, operation, store_fd, backend)
            try:
                return _rollback(context, operation, store_fd, backend, originals, candidate)
            except ApplicationV2Error as error:
                if operation["State"] in _TERMINAL:
                    _fail("uncertain")
                _recovery_required(context, operation)
                _fail("conflict" if error.code == "conflict" else "uncertain")
            except Exception:
                if operation["State"] in _TERMINAL:
                    _fail("uncertain")
                _recovery_required(context, operation)
                _fail("uncertain")


@_safe
def application_status(baseAbs):
    _require_root()
    base = _store._absolute_path(baseAbs)
    with _target_lock(base, exclusive=False) as context:
        if context is None:
            return _public()
        selected = _read_head(context["application_fd"])["Operation"]
        if selected is None:
            return _public()
        operation = _read_operation(context, selected, require_candidate=False)[0]
        _confirm_context(context)
        return _public(operation)


@contextmanager
def _overview_lock(base):
    """Try the fixed shared lock before reading any private file contents."""
    parent_fd = base_fd = application_fd = lock_fd = None
    locked = False
    try:
        parent_fd, name = _store._open_parent(base)
        base_fd = _store._open_directory(parent_fd, name, True)
        try:
            application_fd = _store._open_directory(base_fd, _TARGET_ROOT, True)
        except FileNotFoundError:
            yield None, False
            return
        # This only validates regular-file metadata and opens the zero-byte
        # fixed lock. No marker/state/journal/source is read on the busy path.
        lock_fd = _store._open_file(application_fd, "lock")
        if os.fstat(lock_fd).st_size != 0:
            _fail()
        try:
            _store.fcntl.flock(lock_fd, _store.fcntl.LOCK_SH | _store.fcntl.LOCK_NB)
        except OSError as error:
            if error.errno not in (errno.EAGAIN, errno.EWOULDBLOCK):
                raise
            yield None, True
            return
        locked = True
        context = {"base": base, "parent": parent_fd, "name": name, "base_fd": base_fd,
                   "application_fd": application_fd, "lock_fd": lock_fd}
        _confirm_context(context)
        yield context, False
    finally:
        if locked:
            try:
                _store.fcntl.flock(lock_fd, _store.fcntl.LOCK_UN)
            except OSError:
                pass
        _store._close(lock_fd)
        _store._close(application_fd)
        _store._close(base_fd)
        _store._close(parent_fd)


def _overview_public(operation=None, installed=None, state="unknown", busy=False):
    return {
        "schemaVersion": 1, "busy": busy,
        "operation": _public(operation) if operation else None,
        "installed": {"operation": installed["Operation"], "revision": installed["Revision"]} if installed else None,
        "installedState": state, "statusSource": "journal",
        "gatewayApplied": False, "firewallApplied": False,
    }


def _installed_files_match(context, installed):
    if installed["Target"]["configRoot"] != str(Path(context["base"]) / "config"):
        return False
    try:
        inventory, _, config = _inventory(context["base_fd"])
    except (ApplicationV2Error, _store.AdminV2StoreError, OSError):
        # A foreign missing, linked, malformed-permission or replaced runtime
        # entry cannot be declared installed. Never repair it during a read.
        return False
    return config == installed["ConfigIdentity"] and inventory == installed["Files"]


@_safe
def application_overview(baseAbs):
    """Nonblocking journal/index projection; never a live PBX-health probe.

    A busy response is fixed and contains no guessed/cached private state.
    Version-one history never establishes a known installed revision.  A
    known version-two record means its managed-file fingerprints still match,
    not that a container, gateway, SIM or firewall is currently operating.
    """
    _require_root()
    base = _store._absolute_path(baseAbs)
    with _overview_lock(base) as (context, busy):
        if busy:
            return _overview_public(state="unconfirmed", busy=True)
        if context is None:
            return _overview_public()
        head = _read_head(context["application_fd"])
        selected = head["Operation"]
        operation = _read_operation(context, selected, require_candidate=False)[0] if selected else None
        installed = head.get("Installed")
        if operation and operation["State"] in _PENDING:
            state = "unconfirmed"
        elif operation and operation["State"] in _TERMINAL and not _terminal_index_matches(head, operation):
            state = "unconfirmed"
        elif installed is None:
            state = "unknown"
        else:
            state = "known" if _installed_files_match(context, installed) else "drifted"
        _confirm_context(context)
        return _overview_public(operation, installed, state)
