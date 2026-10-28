"""Additional post-review boundaries; prior component tests remain unchanged."""
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

import deployment_bundle_v2 as writer
from deployment_v2 import DeploymentV2Error
from render_deployment_v2 import render_deployment_v2
from test_deployment_bundle_v2 import private_bundle
from test_render_deployment_v2 import deployment, role_secrets


class DeploymentBundlePlatformV2Tests(unittest.TestCase):
    def test_posix_modes_are_not_misrepresented_as_windows_private_acls(self):
        for platform in ("win32", "darwin"):
            with self.subTest(platform=platform), tempfile.TemporaryDirectory() as temporary:
                output = Path(temporary) / "synthetic-output"
                with patch.object(writer.sys, "platform", platform), patch.object(writer.tempfile, "mkdtemp") as create:
                    with self.assertRaises(DeploymentV2Error):
                        writer.write_deployment_bundle(private_bundle(), output)
                    create.assert_not_called()
                self.assertFalse(output.exists())

    def test_pbx_endpoint_acls_have_their_explicit_enforcement_module_loaded(self):
        for platform in ("vowin-windows", "vocat-go"):
            with self.subTest(platform=platform):
                files = render_deployment_v2(deployment(platform), role_secrets())
                self.assertIn("autoload=no", files["config/modules.conf"])
                self.assertIn("load=res_pjsip_acl.so\n", files["config/modules.conf"])
                self.assertIn("permit=127.70.0.3/32", files["config/pjsip.conf"])
                self.assertIn("deny=0.0.0.0/0", files["config/pjsip.conf"])
