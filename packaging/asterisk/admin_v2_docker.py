"""Candidate native-Linux Docker adapter. Not an installed/application entry point.

Only an explicitly pinned local Docker container may be stopped/started. The
preflight container is separately owned, network-none, disposable and never
shares the target's writable volumes. No command output/configuration is logged.
The engine owns durable journals and stable-directory, per-file publication.
"""
from functools import wraps
import hashlib
import ipaddress
import json
import os
from pathlib import Path
import re
import secrets
import selectors
import stat
import subprocess
import sys
import tempfile
import time


CONFIG_FILES = frozenset({
    "asterisk.conf", "modules.conf", "logger.conf", "rtp.conf", "http.conf",
    "manager.conf", "pjsip.conf", "extensions.conf", "stasis.conf", "udptl.conf",
    "acl.conf", "indications.conf", "features.conf", "cel.conf", "cdr.conf",
    "ccss.conf", "pjproject.conf",
})
_ID = re.compile(r"[0-9a-f]{64}\Z")
_IMAGE = re.compile(r"sha256:[0-9a-f]{64}\Z")
_NAME = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\Z")
_OBJECT = re.compile(r"[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\Z")
_MODULE = re.compile(r"[a-z][a-z0-9_]*\.so\Z")
_LABEL = "org.vowin.admin.preflight"
_SAFE = "Docker PBX operation could not be safely confirmed; reconciliation required"
_BUSY = "PBX is busy; end active calls before applying"
_LIMIT = 1024 * 1024
_MODULE_ROOT = "/usr/lib/x86_64-linux-gnu/asterisk/modules"
_RUNTIME_PATHS = ("/run/asterisk", "/var/lib/asterisk", "/var/spool/asterisk", "/var/log/asterisk", "/tmp")
_INSPECT_FIELDS = frozenset({
    "Id", "Name", "Image", "User", "Running", "StartedAt", "Restart", "RetryCount",
    "Network", "Privileged", "Devices", "DeviceRequests", "DeviceRules", "CapAdd", "CapDrop",
    "SecurityOpt", "ReadOnly", "PidsLimit", "Memory", "PidMode", "IpcMode", "UTSMode",
    "PortBindings", "PublishAll", "Ports", "Tmpfs", "Sysctls", "OwnedLabel", "Entrypoint", "Cmd", "Mounts",
})

# Deliberately do not inspect Config.Env, the full Config, or all labels.
_INSPECT = (
    '{"Id":{{json .Id}},"Name":{{json .Name}},"Image":{{json .Image}},'
    '"User":{{json .Config.User}},"Running":{{json .State.Running}},'
    '"StartedAt":{{json .State.StartedAt}},"Restart":{{json .HostConfig.RestartPolicy.Name}},'
    '"RetryCount":{{json .HostConfig.RestartPolicy.MaximumRetryCount}},'
    '"Network":{{json .HostConfig.NetworkMode}},"Privileged":{{json .HostConfig.Privileged}},'
    '"Devices":{{json .HostConfig.Devices}},"DeviceRequests":{{json .HostConfig.DeviceRequests}},'
    '"DeviceRules":{{json .HostConfig.DeviceCgroupRules}},'
    '"CapAdd":{{json .HostConfig.CapAdd}},"CapDrop":{{json .HostConfig.CapDrop}},'
    '"SecurityOpt":{{json .HostConfig.SecurityOpt}},"ReadOnly":{{json .HostConfig.ReadonlyRootfs}},'
    '"PidsLimit":{{json .HostConfig.PidsLimit}},"Memory":{{json .HostConfig.Memory}},'
    '"PidMode":{{json .HostConfig.PidMode}},"IpcMode":{{json .HostConfig.IpcMode}},'
    '"UTSMode":{{json .HostConfig.UTSMode}},'
    '"PortBindings":{{json .HostConfig.PortBindings}},"PublishAll":{{json .HostConfig.PublishAllPorts}},'
    '"Ports":{{json .NetworkSettings.Ports}},"Tmpfs":{{json .HostConfig.Tmpfs}},'
    '"Sysctls":{{json (index .HostConfig "Sysctls")}},'
    '"OwnedLabel":{{json (index .Config.Labels "' + _LABEL + '")}},'
    '"Entrypoint":{{json .Config.Entrypoint}},"Cmd":{{json .Config.Cmd}},'
    '"Mounts":[{{range $index,$mount := .Mounts}}{{if $index}},{{end}}'
    '{"Type":{{json $mount.Type}},"Source":{{json $mount.Source}},'
    '"Destination":{{json $mount.Destination}},"RW":{{json $mount.RW}}}{{end}}]}'
)


def _fail(message=_SAFE):
    raise RuntimeError(message) from None


def _public(function):
    @wraps(function)
    def guarded(*args, **kwargs):
        try:
            return function(*args, **kwargs)
        except Exception as error:
            message = _BUSY if type(error) is RuntimeError and str(error) == _BUSY else _SAFE
            raise RuntimeError(message) from None
    return guarded


def _require(condition):
    if not condition:
        _fail()


def _absolute(value):
    value = os.fspath(value)
    _require(type(value) is str and value.startswith("/") and not value.startswith("//") and value != "/")
    _require(not any(ord(character) < 32 or ord(character) == 127 for character in value))
    _require("," not in value and all(part not in (".", "..") for part in value.split("/")))
    _require(os.path.normpath(value) == value)
    return Path(value)


def _directory(path, permitted=(0,), exact_private=False):
    path = _absolute(path)
    for current in (*reversed(path.parents), path):
        info = current.lstat()
        _require(stat.S_ISDIR(info.st_mode) and not stat.S_ISLNK(info.st_mode))
        _require(info.st_uid in permitted)
        mode = stat.S_IMODE(info.st_mode)
        sticky_root = info.st_uid == 0 and bool(info.st_mode & stat.S_ISVTX)
        _require(not mode & 0o022 or sticky_root)
    info = path.lstat()
    if exact_private:
        _require(stat.S_IMODE(info.st_mode) == 0o700)
    return info.st_dev, info.st_ino


def _read_file(path):
    before = path.lstat()
    _require(stat.S_ISREG(before.st_mode) and before.st_nlink == 1 and before.st_uid in (0, 10001))
    _require(not stat.S_IMODE(before.st_mode) & 0o007 and not stat.S_IMODE(before.st_mode) & 0o022)
    _require(0 <= before.st_size <= _LIMIT)
    descriptor = os.open(path, os.O_RDONLY | os.O_NOFOLLOW | os.O_CLOEXEC | os.O_NONBLOCK)
    try:
        opened = os.fstat(descriptor)
        _require((before.st_dev, before.st_ino) == (opened.st_dev, opened.st_ino))
        chunks, length = [], 0
        while length <= _LIMIT:
            chunk = os.read(descriptor, min(16384, _LIMIT + 1 - length))
            if not chunk:
                break
            chunks.append(chunk)
            length += len(chunk)
        after = os.fstat(descriptor)
        _require(length == before.st_size and length <= _LIMIT)
        _require((before.st_dev, before.st_ino, before.st_size, before.st_mtime_ns, before.st_ctime_ns) ==
                 (after.st_dev, after.st_ino, after.st_size, after.st_mtime_ns, after.st_ctime_ns))
        return b"".join(chunks)
    finally:
        os.close(descriptor)


def _configuration(path, complete):
    path = _absolute(path)
    identity = _directory(path, (0, 10001))
    names = set(os.listdir(path))
    _require(names <= CONFIG_FILES and (not complete or names == CONFIG_FILES))
    _require("pjsip.conf" in names and "modules.conf" in names)
    value = {name: _read_file(path / name) for name in sorted(names)}
    _require(sum(map(len, value.values())) <= 4 * _LIMIT)
    _require(_directory(path, (0, 10001)) == identity and set(os.listdir(path)) == names)
    return value


def _property(text, name):
    values = re.findall(r"(?m)^\s*" + re.escape(name) + r"\s*:\s*(.*?)\s*$", text)
    _require(len(values) == 1)
    return values[0]


def _sections(raw):
    text = raw.decode("utf-8", "strict")
    sections, current = [], None
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith(";"):
            continue
        if line.startswith("["):
            match = re.fullmatch(r"\[([A-Za-z0-9][A-Za-z0-9_.-]{0,127})\]", line)
            _require(match is not None)
            current = (match.group(1), {})
            sections.append(current)
        elif current is not None and "=" in line:
            key, value = (part.strip() for part in line.split("=", 1))
            current[1].setdefault(key, []).append(value)
        else:
            _fail()
    return sections


def _one(properties, key):
    values = properties.get(key, [])
    _require(len(values) == 1)
    return values[0]


def _bind(value):
    if value.startswith("["):
        match = re.fullmatch(r"\[([^\]]+)\]:([0-9]{1,5})", value)
    else:
        match = re.fullmatch(r"([^:]+):([0-9]{1,5})", value)
    _require(match is not None)
    address = ipaddress.ip_address(match.group(1))
    _require(address.scope_id is None if isinstance(address, ipaddress.IPv6Address) else True)
    port = int(match.group(2))
    _require(1 <= port <= 65535 and not getattr(address, "ipv4_mapped", None))
    return address.version, str(address), port


class DockerApplicationBackend:
    @_public
    def __init__(self, containerName, baseAbs, imageId, restartPolicy="unless-stopped"):
        _require(sys.platform == "linux" and os.geteuid() == 0)
        _require(type(containerName) is str and _NAME.fullmatch(containerName) is not None)
        _require(type(imageId) is str and _IMAGE.fullmatch(imageId) is not None)
        _require(restartPolicy in ("no", "unless-stopped"))
        self.container = containerName
        self.base = _absolute(baseAbs)
        self.config = self.base / "config"
        self.image = imageId
        self.policy = restartPolicy
        self._base_identity = _directory(self.base)
        self._config_identity = _directory(self.config, (0, 10001))

    def _command(self, args, timeout=10, check=True, include_stderr=False):
        _require(type(args) is list and all(type(part) is str and "\x00" not in part for part in args))
        _require(0 < timeout <= 60)
        # Fixed local daemon: never honor a remote DOCKER_HOST/context or config.
        environment = {key: value for key, value in os.environ.items()
                       if not key.startswith(("DOCKER_", "VOCAT", "VOWIN", "PBX_ADMIN"))}
        environment["LC_ALL"] = "C"
        process = subprocess.Popen(["docker", "--host", "unix:///var/run/docker.sock", *args],
            stdin=subprocess.DEVNULL, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            env=environment, close_fds=True, shell=False)
        chunks, total = {"stdout": [], "stderr": []}, 0
        deadline = time.monotonic() + timeout
        selector = selectors.DefaultSelector()
        try:
            for name in chunks:
                pipe = getattr(process, name)
                os.set_blocking(pipe.fileno(), False)
                selector.register(pipe, selectors.EVENT_READ, name)
            while selector.get_map():
                remaining = deadline - time.monotonic()
                _require(remaining > 0)
                for key, _ in selector.select(min(remaining, 0.2)):
                    chunk = os.read(key.fileobj.fileno(), 16384)
                    if not chunk:
                        selector.unregister(key.fileobj)
                        continue
                    total += len(chunk)
                    _require(total <= _LIMIT)
                    chunks[key.data].append(chunk)
            code = process.wait(timeout=max(0.01, deadline - time.monotonic()))
            if check:
                _require(code == 0)
            output = b"".join(chunks["stdout"])
            if include_stderr:
                output += b"\n" + b"".join(chunks["stderr"])
            _require(len(output) <= _LIMIT)
            return code, output
        finally:
            selector.close()
            if process.poll() is None:
                process.kill()
                try:
                    process.wait(timeout=2)
                except Exception:
                    pass
            process.stdout.close()
            process.stderr.close()

    def _inspect(self, identifier):
        _require(type(identifier) is str and (_ID.fullmatch(identifier) or identifier == self.container or identifier.startswith("vowin-v2-preflight-")))
        _, output = self._command(["inspect", "--type", "container", "--format", _INSPECT, identifier])
        def pairs(items):
            result = {}
            for key, item in items:
                _require(key not in result)
                result[key] = item
            return result
        value = json.loads(output.decode("utf-8", "strict"), object_pairs_hook=pairs,
                           parse_constant=lambda value: _fail())
        _require(type(value) is dict and set(value) == _INSPECT_FIELDS)
        _require(type(value["Id"]) is str and _ID.fullmatch(value["Id"]) is not None)
        _require(type(value["Image"]) is str and _IMAGE.fullmatch(value["Image"]) is not None)
        for key in ("Running", "Privileged", "ReadOnly", "PublishAll"):
            _require(type(value[key]) is bool)
        for key in ("Name", "User", "StartedAt", "Restart", "Network", "PidMode", "IpcMode", "UTSMode"):
            _require(type(value[key]) is str and "\x00" not in value[key])
        for key in ("RetryCount", "Memory"):
            _require(type(value[key]) is int and value[key] >= 0)
        _require(value["PidsLimit"] is None or type(value["PidsLimit"]) is int)
        for key in ("Devices", "DeviceRequests", "DeviceRules", "CapAdd", "CapDrop", "SecurityOpt", "Entrypoint", "Cmd"):
            _require(value[key] is None or type(value[key]) is list)
        for key in ("PortBindings", "Ports", "Tmpfs", "Sysctls"):
            _require(value[key] is None or type(value[key]) is dict)
        _require(value["OwnedLabel"] is None or type(value["OwnedLabel"]) is str)
        return value

    def _mounts(self, value, config):
        mounts = value.get("Mounts")
        _require(type(mounts) is list)
        matches = []
        destinations = set()
        for mount in mounts:
            _require(type(mount) is dict and set(mount) == {"Type", "Source", "Destination", "RW"})
            _require(type(mount["Type"]) is str and type(mount["Destination"]) is str and
                     type(mount["Source"]) is str and type(mount["RW"]) is bool)
            destination, source = mount["Destination"], mount["Source"]
            _require(destination.startswith("/") and os.path.normpath(destination) == destination)
            _require(destination not in destinations)
            destinations.add(destination)
            _require("docker.sock" not in source and "docker.sock" not in destination)
            if destination == "/etc/asterisk":
                matches.append(mount)
            else:
                _require(not destination.startswith("/etc/asterisk/") and not "/etc/asterisk".startswith(destination.rstrip("/") + "/"))
                _require(mount["Type"] in ("volume", "tmpfs") and destination in _RUNTIME_PATHS)
        _require(len(matches) == 1 and matches[0].get("Type") == "bind" and
                 matches[0]["Source"] == str(config) and matches[0].get("RW") is False)

    def _target(self, target=None):
        _require(_directory(self.base) == self._base_identity and
                 _directory(self.config, (0, 10001)) == self._config_identity)
        if target is not None:
            _require(type(target) is dict and set(target) == {"containerId", "imageId", "configRoot", "user", "restartPolicy"})
            _require(type(target["containerId"]) is str and _ID.fullmatch(target["containerId"]) is not None)
            _require(target["imageId"] == self.image and target["configRoot"] == str(self.config) and
                     target["user"] == "10001:10001" and target["restartPolicy"] == self.policy)
        value = self._inspect(self.container if target is None else target["containerId"])
        _require(value.get("Name") == "/" + self.container and value.get("Image") == self.image and value.get("User") == "10001:10001")
        if target is not None:
            _require(value["Id"] == target["containerId"])
        _require(value.get("Restart") in ("no", self.policy) and value.get("RetryCount") == 0)
        _require(value.get("Network") in ("host", "none") and value.get("Privileged") is False)
        _require(value["ReadOnly"] is True and value["CapDrop"] == ["ALL"] and
                 value["SecurityOpt"] in (["no-new-privileges"], ["no-new-privileges:true"]))
        for key in ("Devices", "DeviceRequests", "DeviceRules", "CapAdd"):
            _require(value.get(key) in (None, []))
        _require(value["PidMode"] == "" and value["IpcMode"] == "private" and value["UTSMode"] == "")
        self._mounts(value, self.config)
        return value

    @_public
    def capture(self):
        value = self._target()
        return {"containerId": value["Id"], "imageId": self.image, "configRoot": str(self.config),
                "user": "10001:10001", "restartPolicy": self.policy}

    def _cli(self, identifier, query, timeout=10):
        _, output = self._command(["exec", identifier, "/usr/sbin/asterisk", "-rx", query], timeout)
        text = output.decode("utf-8", "strict")
        _require(re.search(r"(?i)no such command|unable to connect to remote asterisk|command not found", text) is None)
        return text

    def _wait_ready(self, identifier):
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            try:
                text = self._cli(identifier, "core waitfullybooted", min(2, max(0.1, deadline - time.monotonic())))
                if "fully booted" in text.lower():
                    return
            except Exception:
                pass
            time.sleep(min(0.25, max(0, deadline - time.monotonic())))
        _fail()

    @_public
    def idle(self, target):
        value = self._target(target)
        if not value["Running"]:
            return
        text = self._cli(value["Id"], "core show channels count")
        channels = re.findall(r"(?m)^([0-9]+) active channels\r?$", text)
        calls = re.findall(r"(?m)^([0-9]+) active calls\r?$", text)
        _require(len(channels) == len(calls) == 1)
        if channels[0] != "0" or calls[0] != "0":
            _fail(_BUSY)

    @_public
    def stop(self, target):
        value = self._target(target)
        if value["Running"]:
            self.idle(target)
        self._command(["update", "--restart=no", value["Id"]])
        value = self._target(target)
        _require(value["Restart"] == "no")
        if not value["Running"]:
            return
        deadline = time.monotonic() + 30
        try:
            self._cli(value["Id"], "core stop gracefully", 5)
        except Exception:
            if not self._target(target)["Running"]:
                return
            raise
        while time.monotonic() < deadline:
            if not self._target(target)["Running"]:
                return
            time.sleep(min(0.25, max(0, deadline - time.monotonic())))
        # Never docker-stop/kill the target, even during rollback. Cancel the
        # graceful shutdown if still alive and require journal reconciliation.
        if self._target(target)["Running"]:
            try:
                self._cli(value["Id"], "core abort shutdown", 3)
            except Exception:
                pass
            _fail()

    @_public
    def start(self, target):
        value = self._target(target)
        _require(not value["Running"] and value["Restart"] == "no")
        self._command(["start", value["Id"]])
        self._wait_ready(value["Id"])
        _require(self._target(target)["Running"])

    @_public
    def restore_policy(self, target):
        value = self._target(target)
        self._command(["update", "--restart=" + self.policy, value["Id"]])
        _require(self._target(target)["Restart"] == self.policy)

    def _hashes(self, identifier, expected):
        names = sorted(expected)
        _, output = self._command(["exec", identifier, "/usr/bin/sha256sum", "--", *["/etc/asterisk/" + name for name in names]])
        actual = {}
        for line in output.decode("ascii", "strict").splitlines():
            match = re.fullmatch(r"([0-9a-f]{64})  /etc/asterisk/([a-z][a-z0-9_.-]*)", line)
            _require(match is not None and match.group(2) not in actual)
            actual[match.group(2)] = match.group(1)
        _require(actual == {name: hashlib.sha256(raw).hexdigest() for name, raw in expected.items()})
        # Missing originals must remain missing after restore, not be filled
        # with candidate defaults to fabricate a successful rollback.
        for name in sorted(CONFIG_FILES - set(expected)):
            code, _ = self._command(["exec", identifier, "/usr/bin/test", "-e", "/etc/asterisk/" + name], check=False)
            _require(code == 1)

    def _module(self, identifier, name):
        _require(_MODULE.fullmatch(name) is not None)
        text = self._cli(identifier, "module show like " + name)
        matches = re.findall(r"(?im)^\s*" + re.escape(name) + r"\s+.*?\s+[0-9]+\s+Running\s+\S+\s*$", text)
        _require(len(matches) == 1)

    def _transports(self, identifier, expected):
        transports = {}
        for name, props in _sections(expected["pjsip.conf"]):
            if props.get("type") != ["transport"]:
                continue
            _require(name not in transports and _one(props, "protocol") == "udp")
            transports[name] = _bind(_one(props, "bind"))
        _require(bool(transports))
        text = self._cli(identifier, "pjsip show transports")
        actual = [item for item in re.findall(r"(?m)^\s*Transport:\s+(\S+)", text) if not item.startswith("<")]
        _require(len(actual) == len(transports) and set(actual) == set(transports))
        for name, binding in transports.items():
            _require(_OBJECT.fullmatch(name) is not None)
            loaded = self._cli(identifier, "pjsip show transport " + name)
            _require(_property(loaded, "protocol") == "udp" and _bind(_property(loaded, "bind")) == binding)
        return transports

    def _verify(self, identifier, configuration, model):
        expected = _configuration(configuration, model is not None)
        self._wait_ready(identifier)
        self._hashes(identifier, expected)
        for name in ("chan_pjsip.so", "pbx_config.so"):
            self._module(identifier, name)
        transports = self._transports(identifier, expected)
        if model is None:
            return
        from deployment_v2 import normalize_deployment_v2
        model = normalize_deployment_v2(model)
        binding = ipaddress.ip_address(model["BindAddress"])
        _require(transports == {"transport-udp": (binding.version, str(binding), model["SipPort"])})
        sections = _sections(expected["modules.conf"])
        _require(len(sections) == 1 and sections[0][0] == "modules")
        properties = sections[0][1]
        _require(_one(properties, "autoload") == "no" and set(properties) == {"autoload", "load"})
        modules = properties.get("load", [])
        _require(modules and len(modules) == len(set(modules)) and all(_MODULE.fullmatch(name) for name in modules))
        _require({"chan_pjsip.so", "pbx_config.so", "res_pjsip_acl.so"} <= set(modules))
        _, paths = self._command(["exec", identifier, "/usr/bin/stat", "-L", "--format=%F", _MODULE_ROOT,
                                 *[_MODULE_ROOT + "/" + name for name in modules]])
        _require(paths.decode("ascii", "strict").splitlines() == ["directory"] + ["regular file"] * len(modules))
        for name in modules:
            self._module(identifier, name)
        enabled = [user for user in model["Extensions"] if model["ExtensionOptions"][user]["Enabled"]]
        endpoint_text = self._cli(identifier, "pjsip show endpoints")
        endpoints = [token.split("/", 1)[0] for token in re.findall(r"(?m)^\s*Endpoint:\s+(\S+)", endpoint_text) if not token.startswith("<")]
        wanted = set(enabled) | ({"gateway"} if model["Peer"]["Enabled"] else set())
        _require(len(endpoints) == len(wanted) and set(endpoints) == wanted)
        auths = []
        for user in enabled:
            endpoint = self._cli(identifier, "pjsip show endpoint " + user)
            for key, desired in (("context", "phone-calls-" + user), ("message_context", "phone-messages-" + user),
                                 ("auth", user + "-auth"), ("aors", user)):
                _require(_property(endpoint, key) == desired)
            auths.append((user + "-auth", user, model["PbxRealm"], model["PbxAlgorithm"]))
        if model["Peer"]["Enabled"]:
            endpoint = self._cli(identifier, "pjsip show endpoint gateway")
            for key, desired in (("context", "from-gateway"), ("message_context", "from-gateway-messages"),
                                 ("auth", "gateway-in-auth"), ("outbound_auth", "gateway-out-auth"), ("aors", "gateway-aor")):
                _require(_property(endpoint, key) == desired)
            auths += [("gateway-in-auth", model["Peer"]["OutboundAuth"]["Username"], model["PbxRealm"], model["PbxAlgorithm"]),
                      ("gateway-out-auth", model["Peer"]["InboundAuth"]["Username"], model["GatewayRealm"], model["GatewayAlgorithm"])]
        for name, username, realm, algorithm in auths:
            auth = self._cli(identifier, "pjsip show auth " + name)
            _require(_property(auth, "username") == username and _property(auth, "realm") == realm)
            for key in ("supported_algorithms_uas", "supported_algorithms_uac"):
                _require(re.split(r"[,\s]+", _property(auth, key)) == [algorithm])
        contexts = [name for name, _ in _sections(expected["extensions.conf"]) if name != "general"]
        _require(len(contexts) == len(set(contexts)))
        for name in contexts:
            loaded = self._cli(identifier, "dialplan show " + name)
            _require(re.search(r"(?m)^\[\s*Context\s+'" + re.escape(name) + r"'\s+created by\s+'[^']+'\s*\]", loaded) is not None)
        version = self._cli(identifier, "core show version")
        match = re.search(r"\bAsterisk\s+([0-9]+)\.([0-9]+)(?:\.([0-9]+))?", version)
        _require(match is not None)
        major, minor = int(match[1]), int(match[2])
        _require(major > 22 or major == 22 and minor >= 2 or major == 21 and minor >= 7 or major == 20 and minor >= 12)
        build = self._cli(identifier, "pjproject show buildopts")
        match = re.search(r"\bPJ_VERSION\b[^0-9\r\n]{0,40}([0-9]+(?:\.[0-9]+){1,2})", build)
        _require(match is not None)
        version = tuple(map(int, match[1].split(".")))
        _require(version + (0,) * (3 - len(version)) >= (2, 15, 1))
        for kind in ("cdr", "cel"):
            text = self._cli(identifier, kind + " show status")
            _require(len(re.findall(r"(?im)^\s*(?:(?:CDR|CEL)\s+)?Logging\s*:\s*Disabled\s*$", text)) == 1)
            _require(re.search(r"(?im)^\s*(?:(?:CDR|CEL)\s+)?Logging\s*:\s*Enabled\s*$", text) is None)

    @_public
    def verify(self, target, expectedConfigAbs, modelOrNone):
        value = self._target(target)
        _require(value["Running"])
        expected = _configuration(expectedConfigAbs, modelOrNone is not None)
        # Also verify the host-side live directory. Per-file publication must
        # never replace its inode or leave candidate-only files after restore.
        _require(_configuration(self.config, modelOrNone is not None) == expected)
        self._verify(value["Id"], expectedConfigAbs, modelOrNone)
        if modelOrNone is not None:
            self._logs(value["Id"], value.get("StartedAt"))
        _require(self._target(target)["Running"] and _configuration(expectedConfigAbs, modelOrNone is not None) == expected)

    def _logs(self, identifier, since):
        _require(type(since) is str and re.fullmatch(r"[0-9TZ:.-]+", since) is not None)
        _, output = self._command(["logs", "--since", since, "--tail", "2000", identifier], include_stderr=True)
        text = output.decode("utf-8", "strict")
        _require(re.search(r"(?im)\b(?:ERROR|FATAL)\b|\b(?:unknown|unrecognized)\s+(?:configuration\s+)?(?:option|setting)|"
                           r"\b(?:unable|failed)\s+to\s+(?:load|parse|apply|register).*(?:conf|module|endpoint|auth|transport)", text) is None)

    def _owned(self, identifier, name, label, configuration, family):
        value = self._inspect(identifier)
        _require(value["Name"] == "/" + name and value.get("OwnedLabel") == label and value["Image"] == self.image)
        _require(value["User"] == "10001:10001" and value.get("Entrypoint") == ["/usr/sbin/asterisk"] and value.get("Cmd") == ["-f", "-g"])
        _require(value["Network"] == "none" and value["Restart"] == "no" and value["RetryCount"] == 0)
        _require(value["Privileged"] is False and value["ReadOnly"] is True and value.get("CapDrop") == ["ALL"])
        for key in ("Devices", "DeviceRequests", "DeviceRules", "CapAdd"):
            _require(value.get(key) in (None, []))
        for key in ("Ports", "PortBindings"):
            _require(value.get(key) in (None, {}))
        _require(value["PublishAll"] is False and value["Memory"] == 268435456 and value["PidsLimit"] == 128)
        _require(value.get("SecurityOpt") in (["no-new-privileges"], ["no-new-privileges:true"]))
        _require(value["PidMode"] == "" and value["UTSMode"] == "" and value["IpcMode"] == "private")
        expected_sysctls = {"net.ipv4.ip_nonlocal_bind" if family == 4 else "net.ipv6.ip_nonlocal_bind": "1"}
        _require(value.get("Sysctls") == expected_sysctls)
        tmpfs = value.get("Tmpfs")
        _require(type(tmpfs) is dict and set(tmpfs) == set(_RUNTIME_PATHS))
        for options in tmpfs.values():
            _require(type(options) is str and set(options.split(",")) ==
                     {"rw", "noexec", "nosuid", "nodev", "size=16m", "uid=10001", "gid=10001", "mode=0700"})
        self._mounts(value, configuration)
        for mount in value["Mounts"]:
            if mount["Destination"] != "/etc/asterisk":
                _require(mount.get("Type") == "tmpfs" and mount["Destination"] in _RUNTIME_PATHS)
        return value

    @_public
    def preflight(self, candidateConfigAbs, model):
        target = self._target()
        _require(target["Restart"] == self.policy and target["Running"])
        # Refuse an unsupported original before stopping anything. Rollback
        # must be able to verify the exact existing allowlist (including its
        # genuinely missing files), not discover extra configuration afterward.
        _configuration(self.config, False)
        from deployment_v2 import normalize_deployment_v2
        model = normalize_deployment_v2(model)
        family = ipaddress.ip_address(model["BindAddress"]).version
        source = _configuration(candidateConfigAbs, True)
        _, image = self._command(["image", "inspect", "--format", "{{.Id}}", self.image])
        _require(image.decode("ascii", "strict").strip() == self.image)
        runtime = None
        configuration = None
        identifier = None
        label = secrets.token_hex(16)
        name = "vowin-v2-preflight-" + label
        runtime_identity = configuration_identity = None
        failure = None
        try:
            runtime = Path(tempfile.mkdtemp(prefix="vowin-v2-preflight-"))
            os.chmod(runtime, 0o700)
            runtime_identity = _directory(runtime, (0,), True)
            configuration = runtime / "config"
            configuration.mkdir(mode=0o700)
            for filename, raw in source.items():
                descriptor = os.open(configuration / filename, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
                try:
                    offset = 0
                    while offset < len(raw):
                        count = os.write(descriptor, raw[offset:])
                        _require(count > 0)
                        offset += count
                    os.fchown(descriptor, 10001, 10001)
                    os.fchmod(descriptor, 0o600)
                    os.fsync(descriptor)
                finally:
                    os.close(descriptor)
            os.chown(configuration, 10001, 10001)
            configuration_identity = _directory(configuration, (0, 10001), True)
            _require(_configuration(configuration, True) == source)
            args = ["create", "--pull=never", "--name", name, "--label", _LABEL + "=" + label,
                "--restart=no", "--network=none", "--user=10001:10001", "--read-only", "--cap-drop=ALL",
                "--security-opt=no-new-privileges", "--pids-limit=128", "--memory=256m",
                "--sysctl", ("net.ipv4.ip_nonlocal_bind=1" if family == 4 else "net.ipv6.ip_nonlocal_bind=1"),
                "--mount", "type=bind,src=" + str(configuration) + ",dst=/etc/asterisk,readonly"]
            for destination in _RUNTIME_PATHS:
                args += ["--tmpfs", destination + ":rw,noexec,nosuid,nodev,size=16m,uid=10001,gid=10001,mode=0700"]
            args += ["--entrypoint=/usr/sbin/asterisk", self.image, "-f", "-g"]
            _, created = self._command(args, 30)
            candidate = created.decode("ascii", "strict").strip()
            _require(_ID.fullmatch(candidate) is not None)
            value = self._owned(candidate, name, label, configuration, family)
            identifier = value["Id"]
            self._command(["start", identifier])
            self._verify(identifier, configuration, model)
            value = self._owned(identifier, name, label, configuration, family)
            _require(value["Running"])
            self._logs(identifier, value.get("StartedAt"))
            _require(_configuration(candidateConfigAbs, True) == source and _configuration(configuration, True) == source)
            final_target = self._target()
            _require(final_target["Id"] == target["Id"] and final_target["Restart"] == self.policy and final_target["Running"])
        except Exception:
            failure = True
        finally:
            # Creation may succeed even if the CLI response is lost. A readonly
            # exact-name/unguessable-label inspection may capture its full ID;
            # destructive cleanup is NEVER addressed by name or a broad filter.
            if configuration is not None:
                try:
                    owned = self._owned(identifier or name, name, label, configuration, family)
                    identifier = owned["Id"]
                    try:
                        if owned["Running"]:
                            self._cli(identifier, "core stop now", 3)
                    except Exception:
                        pass
                    self._owned(identifier, name, label, configuration, family)
                    self._command(["rm", "--force", identifier], 20)
                    code, absent = self._command(["inspect", "--type", "container", "--format", "{{.Id}}", identifier],
                                                 check=False, include_stderr=True)
                    _require(code == 1 and re.search(rb"(?i)no such (?:object|container)", absent) is not None)
                except Exception:
                    # If no owned container can be proved absent, retain its
                    # private host configuration and fail rather than remove a
                    # mount still potentially used by an unknown process.
                    failure = True
                    configuration = None
            if runtime is not None and configuration is not None:
                try:
                    _require(_directory(runtime, (0,), True) == runtime_identity)
                    _require(_directory(configuration, (0, 10001), True) == configuration_identity)
                    _require(_configuration(configuration, True) == source and set(os.listdir(runtime)) == {"config"})
                    for filename in sorted(CONFIG_FILES):
                        os.unlink(configuration / filename)
                    os.rmdir(configuration)
                    os.rmdir(runtime)
                except Exception:
                    failure = True
        if failure:
            _fail()
