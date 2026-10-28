"""Explicit native-Linux v2 administration of saved, never-applied drafts.

The public API edits only PBX account policy. Peer, gateway-local, line, realm,
network and feature policy remains part of the independently validated model.
No legacy username/password lookup, rendering, reload, process or network use.
"""
from copy import deepcopy
from functools import wraps
import json
import os
from pathlib import Path
import re
import secrets
import stat
import sys

from deployment_v2 import (
    MAX_DEPLOYMENT_BYTES, normalize_deployment_v2, parse_deployment_secrets,
    parse_deployment_v2, resolve_deployment_secrets,
)
from admin_v2_store import (
    AdminV2StoreError, initialize_store, read_store, update_store,
)


_CODES = frozenset({"invalid", "conflict", "uninitialized", "uncertain"})
_TOKEN = re.compile(r"[A-Za-z0-9_-]{43}\Z")
_NUMBER = re.compile(r"[0-9]{2,8}\Z")
_PASSWORD = re.compile(r"[A-Za-z0-9_-]{12,128}\Z")
_FLAGS = ("enabled", "ring", "allowOutgoing", "allowMessages", "allowMobileSmsSend", "allowMobileSmsReceive")
_OPTIONS = {
    "name": "Name", "enabled": "Enabled", "ring": "Ring", "maxContacts": "MaxContacts",
    "allowOutgoing": "AllowOutgoing", "allowMessages": "AllowMessages",
    "allowMobileSmsSend": "AllowMobileSmsSend", "allowMobileSmsReceive": "AllowMobileSmsReceive",
}
_ROW_FIELDS = frozenset({"number", "sourceNumber", "name", "enabled", "ring", "maxContacts",
                         "allowOutgoing", "allowMessages", "allowMobileSmsSend", "allowMobileSmsReceive",
                         "passwordAction"})
_ERROR_TEXT = {
    "invalid": "Invalid version 2 admin draft request or store",
    "conflict": "Version 2 admin draft changed or already exists",
    "uninitialized": "Version 2 admin draft store is not initialized",
    "uncertain": "Version 2 admin draft commit outcome is uncertain; read before retrying",
}


class AdminDeploymentV2Error(ValueError):
    """Only a fixed safe message and one public outcome code are exposed."""
    def __init__(self, code="invalid"):
        self.code = code if code in _CODES else "invalid"
        super().__init__(_ERROR_TEXT[self.code])


def _fail(code="invalid"):
    raise AdminDeploymentV2Error(code) from None


def _safe_api(function):
    @wraps(function)
    def safe(*args, **kwargs):
        try:
            return function(*args, **kwargs)
        except AdminDeploymentV2Error:
            raise
        except AdminV2StoreError as error:
            _fail(error.code if error.code in _CODES else "invalid")
        except Exception:
            # Underlying JSON, Unicode and filesystem exceptions may retain
            # caller bytes, file paths or credential values. Never chain them.
            _fail()
    return safe


def _root(value):
    if sys.platform != "linux":
        _fail()
    try:
        text = os.fspath(value)
    except (TypeError, ValueError):
        _fail()
    if type(text) is not str or not text or "\x00" in text or not Path(text).is_absolute():
        _fail()
    # Do not resolve away symlinks; the private store validates each ancestor.
    return Path(text)


def _object(value, fields, required=None):
    if type(value) is not dict or set(value) - set(fields) or not set(required if required is not None else fields) <= set(value):
        _fail()
    return value


def _pairs(pairs):
    result, seen = {}, set()
    for key, value in pairs:
        if type(key) is not str or key.casefold() in seen:
            _fail()
        seen.add(key.casefold())
        result[key] = value
    return result


def _number_literal(text):
    if len(text) > 20:
        _fail()
    return int(text)


def _reject_number(_value):
    _fail()


def _unicode_json(value, depth=0):
    if depth > 64:
        _fail()
    if type(value) is str:
        value.encode("utf-8", "strict")
    elif type(value) is dict:
        for key, child in value.items():
            _unicode_json(key, depth + 1)
            _unicode_json(child, depth + 1)
    elif type(value) is list:
        for child in value:
            _unicode_json(child, depth + 1)


def _request(raw):
    if type(raw) is not bytes or not 0 < len(raw) <= MAX_DEPLOYMENT_BYTES:
        _fail()
    value = json.loads(raw.decode("utf-8", "strict"), object_pairs_hook=_pairs,
                       parse_int=_number_literal, parse_float=_reject_number, parse_constant=_reject_number)
    _unicode_json(value)
    return value


def _revision(value):
    if type(value) is not str or not _TOKEN.fullmatch(value):
        _fail()
    return value


def _number(value):
    if type(value) is not str or not _NUMBER.fullmatch(value):
        _fail()
    return value


def _snapshot(value):
    _object(value, {"revision", "model", "secrets"})
    revision = _revision(value["revision"])
    model = normalize_deployment_v2(value["model"])
    private = resolve_deployment_secrets(model, value["secrets"])
    return revision, model, private


def _view(snapshot):
    revision, model, private = _snapshot(snapshot)
    rows = []
    for number in model["Extensions"]:
        option = model["ExtensionOptions"][number]
        row = {"number": number, "sourceNumber": number}
        row.update({public: option[stored] for public, stored in _OPTIONS.items()})
        row["hasPassword"] = bool(private[option["SecretRef"]])
        rows.append(row)
    return {
        "schemaVersion": 2, "revision": revision, "state": "saved-not-applied", "canApply": False,
        "sipServer": model["ServerIp"], "sipPort": model["SipPort"],
        "features": {"calls": model["CallsEnabled"], "carrier": model["CarrierEnabled"],
                     "messages": model["MessagesEnabled"], "sms": model["SmsEnabled"]},
        "extensions": rows,
    }


def _rows(value):
    if type(value) is not list or len(value) > 64:
        _fail()
    result, seen = [], set()
    for incoming in value:
        row = _object(incoming, _ROW_FIELDS | {"password"}, _ROW_FIELDS)
        number = _number(row["number"])
        if number.casefold() in seen:
            _fail()
        seen.add(number.casefold())
        source = row["sourceNumber"]
        if type(source) is not str or source and not _NUMBER.fullmatch(source):
            _fail()
        if type(row["name"]) is not str or type(row["maxContacts"]) is not int or not 1 <= row["maxContacts"] <= 8:
            _fail()
        if any(type(row[key]) is not bool for key in _FLAGS):
            _fail()
        action = row["passwordAction"]
        if type(action) is not str or action not in {"keep", "replace", "generate"}:
            _fail()
        if ("password" in row) != (action == "replace"):
            _fail()
        if action == "replace" and (type(row["password"]) is not str or not _PASSWORD.fullmatch(row["password"])):
            _fail()
        result.append(deepcopy(row))
    return result


def _fresh_ref(used):
    for _ in range(64):
        value = "pbx-" + secrets.token_urlsafe(24)
        if value.casefold() not in used:
            used.add(value.casefold())
            return value
    _fail()


def _fresh_password(used_values):
    for _ in range(64):
        value = secrets.token_urlsafe(32)
        if _PASSWORD.fullmatch(value) and value not in used_values:
            used_values.add(value)
            return value
    _fail()


@_safe_api
def initialize_v2_admin(rootAbsolutePath, publicBytes, secretMapBytes):
    """Initialize only an explicitly requested new private store, never apply."""
    root = _root(rootAbsolutePath)
    if os.path.lexists(root):
        _fail("conflict")
    model = parse_deployment_v2(publicBytes)
    private = parse_deployment_secrets(secretMapBytes, model)
    return _view(initialize_store(root, model, private))


@_safe_api
def read_v2_admin(rootAbsolutePath):
    """Read an existing committed draft without initialization or rewriting."""
    return _view(read_store(_root(rootAbsolutePath)))


@_safe_api
def save_v2_admin(rootAbsolutePath, rawRequestBytes):
    """CAS replacement of PBX account rows, retaining every unrelated role."""
    root = _root(rootAbsolutePath)
    body = _object(_request(rawRequestBytes), {"revision", "extensions"})
    expected = _revision(body["revision"])
    rows = _rows(body["extensions"])

    def transform(old_model, old_private):
        old_model = normalize_deployment_v2(old_model)
        old_private = resolve_deployment_secrets(old_model, old_private)
        members = set(old_model["Extensions"])
        for row in rows:
            source, number = row["sourceNumber"], row["number"]
            if source:
                if source != number or source not in members:
                    _fail()
            elif number in members:
                _fail()
            if row["passwordAction"] == "keep" and not source:
                _fail()
        model, private = deepcopy(old_model), dict(old_private)
        used_refs = {ref.casefold() for ref in old_private}
        used_values = set(old_private.values())
        retained = {row["sourceNumber"] for row in rows if row["sourceNumber"]}
        for number in members - retained:
            del private[old_model["ExtensionOptions"][number]["SecretRef"]]
        options = {}
        for row in rows:
            source, number, action = row["sourceNumber"], row["number"], row["passwordAction"]
            ref = old_model["ExtensionOptions"][source]["SecretRef"] if source else _fresh_ref(used_refs)
            if action == "replace":
                private[ref] = row["password"]
                used_values.add(row["password"])
            elif action == "generate":
                private[ref] = _fresh_password(used_values)
            # keep is allowed only for the same existing account and exact ref.
            option = {"SecretRef": ref}
            option.update({stored: row[public] for public, stored in _OPTIONS.items()})
            options[number] = option
        model["Extensions"] = [row["number"] for row in rows]
        model["ExtensionOptions"] = options
        model = normalize_deployment_v2(model)
        return model, resolve_deployment_secrets(model, private)

    return _view(update_store(root, expected, transform))


@_safe_api
def connection_v2_admin(rootAbsolutePath, rawRequestBytes):
    """Explicit CAS-gated disclosure of exactly one PBX draft credential."""
    root = _root(rootAbsolutePath)
    body = _object(_request(rawRequestBytes), {"revision", "number"})
    expected, number = _revision(body["revision"]), _number(body["number"])
    revision, model, private = _snapshot(read_store(root))
    if expected != revision:
        _fail("conflict")
    if number not in model["Extensions"]:
        _fail()
    ref = model["ExtensionOptions"][number]["SecretRef"]
    return {"revision": revision, "state": "saved-not-applied", "server": model["ServerIp"],
            "port": model["SipPort"], "username": number, "password": private[ref], "transport": "UDP"}


def _input_file(value, private=False):
    """Bounded descriptor-relative reads; never follow a path component link."""
    path = _root(value)
    parts = path.parts
    descriptor = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC)
    try:
        for component in parts[1:-1]:
            if component in {"", ".", ".."}:
                _fail()
            next_descriptor = os.open(component, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW | os.O_CLOEXEC,
                                      dir_fd=descriptor)
            os.close(descriptor)
            descriptor = next_descriptor
        if not parts or parts[-1] in {"", ".", "..", "/"}:
            _fail()
        # A caller-supplied FIFO/device must not block before the regular-file
        # check. O_NONBLOCK does not alter ordinary regular-file reads.
        file_descriptor = os.open(parts[-1], os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK,
                                  dir_fd=descriptor)
        try:
            before = os.fstat(file_descriptor)
            if not stat.S_ISREG(before.st_mode) or not 0 < before.st_size <= MAX_DEPLOYMENT_BYTES:
                _fail()
            if private and (before.st_uid != os.geteuid() or stat.S_IMODE(before.st_mode) != 0o600):
                _fail()
            chunks, size = [], 0
            while size <= MAX_DEPLOYMENT_BYTES:
                chunk = os.read(file_descriptor, min(8192, MAX_DEPLOYMENT_BYTES + 1 - size))
                if not chunk:
                    break
                chunks.append(chunk)
                size += len(chunk)
            after = os.fstat(file_descriptor)
            if size > MAX_DEPLOYMENT_BYTES or (before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns, before.st_ctime_ns) != (
                    after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns):
                _fail()
            return b"".join(chunks)
        finally:
            os.close(file_descriptor)
    finally:
        os.close(descriptor)


def _cli(argv):
    # Do not let argparse echo unknown arguments, paths or private inputs.
    if len(argv) % 2:
        _fail()
    options = {}
    for index in range(0, len(argv), 2):
        key = argv[index]
        if key not in {"--action", "--store", "--public", "--secrets"} or key in options:
            _fail()
        options[key] = argv[index + 1]
    if not {"--action", "--store"} <= set(options):
        _fail()
    action = options["--action"]
    if action not in {"init", "read", "save", "connection"}:
        _fail()
    if set(options) != ({"--action", "--store", "--public", "--secrets"} if action == "init" else {"--action", "--store"}):
        _fail()
    body = sys.stdin.buffer.read(MAX_DEPLOYMENT_BYTES + 1)
    if len(body) > MAX_DEPLOYMENT_BYTES or action in {"init", "read"} and body:
        _fail()
    if action == "init":
        return initialize_v2_admin(options["--store"], _input_file(options["--public"]),
                                   _input_file(options["--secrets"], private=True))
    if action == "read":
        return read_v2_admin(options["--store"])
    if action == "save":
        return save_v2_admin(options["--store"], body)
    return connection_v2_admin(options["--store"], body)


def main(argv=None):
    try:
        value = _cli(sys.argv[1:] if argv is None else argv)
        response, code = {"ok": True, "value": value}, 0
    except BaseException as error:
        public_code = getattr(error, "code", "invalid")
        if type(public_code) is not str or public_code not in _CODES:
            public_code = "invalid"
        response, code = {"ok": False, "code": public_code, "error": _ERROR_TEXT[public_code]}, 1
    try:
        sys.stdout.write(json.dumps(response, ensure_ascii=False, separators=(",", ":"), allow_nan=False) + "\n")
        sys.stdout.flush()
    except BaseException:
        return 1
    return code


if __name__ == "__main__":
    raise SystemExit(main())
