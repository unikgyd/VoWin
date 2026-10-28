"""Docker metadata parsing/ownership regression without Docker or Go execution.

Only the command transport is substituted with fixed synthetic inspect JSON.
The actual product _inspect, _owned and mount/security validators still run.
This does not claim to execute Docker's Go template engine or a live daemon;
the optional-field template spelling is an additional, not sole, control.
"""
from copy import deepcopy
import importlib
import importlib.util
import json
import unittest


IDENTIFIER = "a" * 64
IMAGE = "sha256:" + "b" * 64
LABEL = "c" * 32
NAME = "vowin-v2-preflight-" + LABEL
CONFIGURATION = "/synthetic-metadata/config"
SAFE_ERROR = "Docker PBX operation could not be safely confirmed; reconciliation required"
SENTINEL = "SyntheticMetadataValueMustNotLeak"
RUNTIME_PATHS = ("/run/asterisk", "/var/lib/asterisk", "/var/spool/asterisk", "/var/log/asterisk", "/tmp")


def metadata(sysctls):
    """Independent complete Docker projection, not derived from product fields."""
    return {
        "Id": IDENTIFIER, "Name": "/" + NAME, "Image": IMAGE, "User": "10001:10001",
        "Running": True, "StartedAt": "2026-10-24T00:00:00.000000000Z",
        "Restart": "no", "RetryCount": 0, "Network": "none", "Privileged": False,
        "Devices": None, "DeviceRequests": None, "DeviceRules": None,
        "CapAdd": None, "CapDrop": ["ALL"], "SecurityOpt": ["no-new-privileges"],
        "ReadOnly": True, "PidsLimit": 128, "Memory": 268435456,
        "PidMode": "", "IpcMode": "private", "UTSMode": "",
        "PortBindings": None, "PublishAll": False, "Ports": {},
        "Tmpfs": {path: "rw,noexec,nosuid,nodev,size=16m,uid=10001,gid=10001,mode=0700"
                  for path in RUNTIME_PATHS},
        "Sysctls": deepcopy(sysctls), "OwnedLabel": LABEL,
        "Entrypoint": ["/usr/sbin/asterisk"], "Cmd": ["-f", "-g"],
        "Mounts": [{"Type": "bind", "Source": CONFIGURATION,
                    "Destination": "/etc/asterisk", "RW": False},
                   *[{"Type": "tmpfs", "Source": "", "Destination": path, "RW": True}
                     for path in RUNTIME_PATHS]],
    }


class AdminV2DockerMetadataTests(unittest.TestCase):
    def _product(self):
        spec = importlib.util.find_spec("admin_v2_docker")
        module = importlib.import_module("admin_v2_docker") if spec is not None else None
        self.assertIsNotNone(module, "The actual Docker application backend must exist")
        self.assertTrue(callable(getattr(module, "DockerApplicationBackend", None)))
        return module

    def _backend(self, value):
        module = self._product()
        # Constructor path/daemon enrollment is outside this pure metadata
        # boundary. Do not replace _inspect/_owned or launch any process.
        backend = object.__new__(module.DockerApplicationBackend)
        backend.container, backend.image = NAME, IMAGE
        transport_calls = []
        raw = (json.dumps(value, separators=(",", ":")) + "\n").encode("utf-8")

        def command(arguments, *args, **kwargs):
            transport_calls.append((deepcopy(arguments), deepcopy(args), deepcopy(kwargs)))
            return 0, raw

        backend._command = command
        return backend, transport_calls

    def _inspect_command(self, calls):
        self.assertEqual(1, len(calls))
        arguments, positional, options = calls[0]
        self.assertEqual(["inspect", "--type", "container", "--format"], arguments[:4])
        self.assertEqual(6, len(arguments))
        self.assertEqual(IDENTIFIER, arguments[-1])
        self.assertIs(type(arguments[4]), str)
        self.assertEqual((), positional)
        self.assertEqual({}, options)
        return arguments[4]

    def _reject(self, action):
        with self.assertRaises(RuntimeError) as caught:
            action()
        self.assertEqual(SAFE_ERROR, str(caught.exception))
        self.assertNotIn(SENTINEL, str(caught.exception) + repr(caught.exception))

    def test_real_inspect_accepts_present_null_sysctls_as_optional_target_metadata(self):
        expected = metadata(None)
        backend, calls = self._backend(expected)
        actual = backend._inspect(IDENTIFIER)
        self.assertEqual(expected, actual)
        self.assertIn("Sysctls", actual)
        self.assertIsNone(actual["Sysctls"])
        self._inspect_command(calls)
        self.assertEqual(metadata(None), expected, "The parser must not rewrite its input fixture")

    def test_real_inspect_still_requires_projected_field_and_rejects_wrong_container_types(self):
        cases = [("missing", None), ("list", []), ("string", SENTINEL), ("boolean", True), ("integer", 1)]
        for label, value in cases:
            with self.subTest(shape=label):
                projected = metadata(value)
                if label == "missing":
                    del projected["Sysctls"]
                backend, calls = self._backend(projected)
                self._reject(lambda: backend._inspect(IDENTIFIER))
                self._inspect_command(calls)

    def test_real_owned_candidate_accepts_exact_isolated_ipv4_or_ipv6_nonlocal_bind(self):
        for family, key in ((4, "net.ipv4.ip_nonlocal_bind"), (6, "net.ipv6.ip_nonlocal_bind")):
            with self.subTest(family=family):
                expected = metadata({key: "1"})
                backend, calls = self._backend(expected)
                actual = backend._owned(IDENTIFIER, NAME, LABEL, CONFIGURATION, family)
                self.assertEqual(expected, actual)
                self.assertEqual({key: "1"}, actual["Sysctls"])
                self.assertEqual("none", actual["Network"])
                self.assertIs(False, actual["Privileged"])
                self._inspect_command(calls)

    def test_null_target_metadata_does_not_relax_required_exact_candidate_namespace_sysctls(self):
        for family, required, other in ((4, "net.ipv4.ip_nonlocal_bind", "net.ipv6.ip_nonlocal_bind"),
                                        (6, "net.ipv6.ip_nonlocal_bind", "net.ipv4.ip_nonlocal_bind")):
            cases = [("null", None), ("empty", {}), ("wrong-family", {other: "1"}),
                     ("disabled", {required: "0"}), ("integer-value", {required: 1}),
                     ("extra-sysctl", {required: "1", other: "1"})]
            for label, sysctls in cases:
                with self.subTest(family=family, policy=label):
                    backend, calls = self._backend(metadata(sysctls))
                    self._reject(lambda: backend._owned(IDENTIFIER, NAME, LABEL, CONFIGURATION, family))
                    self._inspect_command(calls)

    def test_inspect_template_indexes_optional_hostconfig_sysctls_without_broad_secret_projection(self):
        backend, calls = self._backend(metadata(None))
        self.assertIsNone(backend._inspect(IDENTIFIER)["Sysctls"])
        template = self._inspect_command(calls)
        self.assertIn('"Sysctls":{{json (index .HostConfig "Sysctls")}}', template)
        self.assertNotIn("{{json .HostConfig.Sysctls}}", template)
        self.assertNotIn("{{json .Config.Env}}", template)
        self.assertNotIn("{{json .Config}}", template)
        self.assertNotIn("{{json .Config.Labels}}", template)


if __name__ == "__main__":
    unittest.main()
