import contextlib
import importlib.util
import io
import json
import os
from pathlib import Path
import unittest
from unittest.mock import MagicMock, patch

spec = importlib.util.spec_from_file_location("deploy_azure", Path(__file__).with_name("deploy-azure.py"))
deployment = importlib.util.module_from_spec(spec)
spec.loader.exec_module(deployment)


class DeploymentTests(unittest.TestCase):
    def deploy(self, environment, existing):
        observed = []
        app = {
            "name": "dev-sporkast-api", "location": "ukwest",
            "properties": {
                "environmentId": "environment-id",
                "configuration": {
                    "ingress": {"customDomains": [{"name": "existing.example", "certificateId": "certificate"}]},
                    "secrets": [{"name": "existing-secret"}],
                },
                "template": {"containers": [{"name": "api", "env": [{"name": "EXISTING", "secretRef": "existing-secret"}]}]},
                "latestRevisionName": "new-revision",
            },
        }

        def azure(*arguments):
            if arguments[:2] == ("containerapp", "list"):
                return [app] if existing else []
            if arguments[:3] == ("containerapp", "env", "show"):
                return {"id": "environment-id", "location": "ukwest"}
            if arguments[:2] == ("containerapp", "show"):
                app["properties"]["configuration"]["ingress"]["fqdn"] = "api.example"
                return app
            if arguments[1] in ("create", "update"):
                path = Path(arguments[arguments.index("--yaml") + 1])
                self.assertEqual(path.stat().st_mode & 0o777, 0o600)
                observed.append((arguments[1], json.loads(path.read_text())))
                return None
            if arguments[:3] == ("containerapp", "revision", "show"):
                return {"properties": {"healthState": "Healthy"}}
            self.fail(f"Unexpected Azure call: {arguments}")

        variables = {
            "DEPLOY_IMAGE": "ghcr.io/tom-knighton/recipeparserapi@sha256:" + "a" * 64,
            "GHCR_READ_TOKEN": "test-token", "DISCOVERY_CONNECTION_STRING": "test-database",
            "GHCR_USERNAME": "test-user",
        }
        response = MagicMock()
        response.__enter__.return_value.status = 200
        with patch.dict(os.environ, variables, clear=True), \
                patch("sys.argv", ["deploy-azure.py", environment]), \
                patch.object(deployment, "azure", side_effect=azure), \
                patch.object(deployment.urllib.request, "urlopen", return_value=response), \
                contextlib.redirect_stdout(io.StringIO()):
            deployment.main()
        return observed[0]

    def test_dev_update_preserves_domain_and_secret_references(self):
        operation, definition = self.deploy("development", existing=True)
        self.assertEqual(operation, "update")
        properties = definition["properties"]
        self.assertEqual(properties["configuration"]["ingress"]["customDomains"][0]["certificateId"], "certificate")
        self.assertEqual(properties["template"]["scale"]["minReplicas"], 0)
        container = properties["template"]["containers"][0]
        self.assertIn({"name": "EXISTING", "secretRef": "existing-secret"}, container["env"])
        self.assertEqual(next(probe for probe in container["probes"] if probe["type"] == "Readiness")["httpGet"]["path"], "/health/ready")

    def test_prod_is_isolated_and_keeps_replica_warm(self):
        operation, definition = self.deploy("production", existing=False)
        self.assertEqual(operation, "create")
        self.assertEqual(definition["name"], "prod-sporkast-api")
        properties = definition["properties"]
        self.assertEqual(properties["template"]["scale"]["minReplicas"], 1)
        variables = properties["template"]["containers"][0]["env"]
        self.assertIn({"name": "ASPNETCORE_ENVIRONMENT", "value": "Production"}, variables)
        self.assertIn({"name": "ConnectionStrings__Discovery", "secretRef": "discovery-database"}, variables)

    def test_missing_config_prevents_azure_mutations(self):
        with patch.dict(os.environ, {}, clear=True), \
                patch("sys.argv", ["deploy-azure.py", "production"]), \
                patch.object(deployment, "azure") as azure, \
                self.assertRaisesRegex(SystemExit, "Missing DEPLOY_IMAGE"):
            deployment.main()
        azure.assert_not_called()


if __name__ == "__main__":
    unittest.main()
