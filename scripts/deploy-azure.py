#!/usr/bin/env python3
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
import time
import urllib.error
import urllib.request


def azure(*arguments):
    result = subprocess.run(
        ["az", *arguments, "--only-show-errors", "--output", "json"],
        check=True, capture_output=True, text=True,
    )
    return json.loads(result.stdout) if result.stdout.strip() else None


def required(name):
    value = os.environ.get(name, "")
    if not value:
        raise SystemExit(f"Missing {name}")
    return value


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("environment", choices=["development", "production"])
    args = parser.parse_args()
    production = args.environment == "production"
    group = "rg-sporkast-prod" if production else "rg-sporkast-dev"
    name = "prod-sporkast-api" if production else "dev-sporkast-api"
    environment = "env-sporkast-prod" if production else "env-sporkast-dev"
    image = required("DEPLOY_IMAGE")
    if not image.startswith("ghcr.io/tom-knighton/recipeparserapi@sha256:"):
        raise SystemExit("DEPLOY_IMAGE must be an immutable RecipeParserAPI digest")

    secret_values = {
        "ghcr-read-token": required("GHCR_READ_TOKEN"),
        "discovery-database": required("DISCOVERY_CONNECTION_STRING"),
    }
    if os.environ.get("OPENAI_API_KEY"):
        secret_values["openai-api-key"] = os.environ["OPENAI_API_KEY"]

    apps = azure("containerapp", "list", "--resource-group", group)
    existing = next((app for app in apps if app["name"] == name), None)
    if existing:
        current = azure("containerapp", "show", "--name", name, "--resource-group", group)
        definition = {key: current[key] for key in ("name", "location", "tags", "identity") if key in current}
        definition["properties"] = {
            key: current["properties"][key]
            for key in ("environmentId", "configuration", "template")
        }
    else:
        managed_environment = azure("containerapp", "env", "show", "--name", environment, "--resource-group", group)
        definition = {
            "name": name,
            "location": managed_environment["location"],
            "properties": {
                "environmentId": managed_environment["id"],
                "configuration": {},
                "template": {"containers": [{"name": name}], "scale": {}},
            },
        }

    properties = definition["properties"]
    configuration = properties["configuration"]
    configuration["activeRevisionsMode"] = "Single"
    secrets = {secret["name"]: secret for secret in configuration.get("secrets", [])}
    secrets.update({key: {"name": key, "value": value} for key, value in secret_values.items()})
    configuration["secrets"] = list(secrets.values())
    registries = [registry for registry in configuration.get("registries", []) if registry["server"] != "ghcr.io"]
    registries.append({"server": "ghcr.io", "username": required("GHCR_USERNAME"), "passwordSecretRef": "ghcr-read-token"})
    configuration["registries"] = registries
    ingress = configuration.setdefault("ingress", {})
    ingress.update({"external": True, "allowInsecure": False, "targetPort": 8080, "transport": "auto"})
    ingress["traffic"] = [{"latestRevision": True, "weight": 100}]

    template = properties["template"]
    template["revisionSuffix"] = f"deploy-{int(time.time())}"
    template.setdefault("scale", {}).update({"minReplicas": 1 if production else 0, "maxReplicas": 2})
    container = template["containers"][0]
    container["image"] = image
    container["resources"] = {"cpu": 1, "memory": "2Gi"}
    variables = {variable["name"]: variable for variable in container.get("env", [])}
    variables["ASPNETCORE_ENVIRONMENT"] = {"name": "ASPNETCORE_ENVIRONMENT", "value": "Production" if production else "Development"}
    variables["ConnectionStrings__Discovery"] = {"name": "ConnectionStrings__Discovery", "secretRef": "discovery-database"}
    if "openai-api-key" in secrets:
        variables["OpenAI__ApiKey"] = {"name": "OpenAI__ApiKey", "secretRef": "openai-api-key"}
    container["env"] = list(variables.values())
    container["probes"] = [
        {"type": kind, "httpGet": {"path": path, "port": 8080, "scheme": "HTTP"},
         "periodSeconds": 10, "timeoutSeconds": 5, "failureThreshold": threshold}
        for kind, path, threshold in [
            ("Startup", "/health/live", 60),
            ("Liveness", "/health/live", 3),
            ("Readiness", "/health/ready", 3),
        ]
    ]

    with tempfile.TemporaryDirectory() as directory:
        path = Path(directory) / "containerapp.json"
        path.touch(mode=0o600)
        path.write_text(json.dumps(definition))
        try:
            azure("containerapp", "update" if existing else "create", "--name", name,
                  "--resource-group", group, "--yaml", str(path))
        except subprocess.CalledProcessError:
            raise SystemExit("Azure deployment failed. Inspect Azure deployment diagnostics; secret output was suppressed.")

    current = azure("containerapp", "show", "--name", name, "--resource-group", group)
    revision = current["properties"]["latestRevisionName"]
    fqdn = current["properties"]["configuration"]["ingress"]["fqdn"]
    for _ in range(60):
        state = azure("containerapp", "revision", "show", "--name", name,
                      "--resource-group", group, "--revision", revision)["properties"]
        if state.get("healthState") == "Healthy":
            try:
                with urllib.request.urlopen(f"https://{fqdn}/health/ready", timeout=30) as response:
                    if response.status == 200:
                        print(f"Verified {args.environment}: {revision}, https://{fqdn}")
                        summary = os.environ.get("GITHUB_STEP_SUMMARY")
                        if summary:
                            with open(summary, "a") as output:
                                output.write(f"Deployed **{args.environment}**: `{revision}`\n\nImage: `{image}`\n\n[API](https://{fqdn})\n")
                        return
            except (urllib.error.URLError, TimeoutError):
                pass
        time.sleep(10)
    raise SystemExit(f"Revision {revision} did not become healthy; inspect Container Apps logs")


if __name__ == "__main__":
    main()
