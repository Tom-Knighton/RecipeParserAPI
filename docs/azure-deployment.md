# Azure API deployment

The API runs on Azure Container Apps in UK West. Images remain private in GHCR.

| Environment | Branch | Resource group | Container App | Azure environment | Custom hostname |
| --- | --- | --- | --- | --- | --- |
| Development (Azure) | `develop` | `rg-sporkast-dev` | `dev-sporkast-api` | `env-sporkast-dev` | `api.dev.recipe.tomk.online` |
| Production | `main` | `rg-sporkast-prod` | `prod-sporkast-api` | `env-sporkast-prod` | `api.sporkast.tomk.online` |

`api.dev.sporkast.tomk.online` is also bound to the Azure development app for compatibility with installed builds that used Railway. New development builds use `api.dev.recipe.tomk.online` directly. Both hostnames serve the same Azure app; Railway is retired.

The **Deploy API to Azure** workflow runs on pushes to these branches. It retains the registered legacy dev workflow filename so it can be dispatched before this change reaches the default branch. A manual run deploys the selected branch to the explicitly selected environment; use a reviewed release commit for production. The CLI can dispatch the registered workflow on the deployment branch; GitHub’s manual-run button follows the default branch configuration.

Manual runs can supply `image_digest` to promote an existing image from this GHCR package. Use the digest recorded by a verified development deployment to release the exact same image to production. Leaving it blank builds the selected source ref. Promotion still runs deployment checks and database migrations from the selected source ref, which must match the release being promoted.

GitHub environment policies restrict development to `develop` and production to `main`. The bootstrap branch `codex/azure-production-pipeline` is temporarily allowed in both environments while the deployment changes await review; remove these two bootstrap exceptions after merging and promoting the release.

Development scales to zero with a maximum of two replicas. Production keeps one replica warm and scales to two. Each replica has one CPU and 2 GiB of memory. Production has a separate Azure environment and Log Analytics workspace.

## Authentication and secrets

GitHub Actions publishes using its automatically issued `GITHUB_TOKEN` with `packages: write`. The GHCR package must grant this repository Actions write access under its package settings. The image carries the repository source label to associate newly published packages with the repository.

Azure pulls images using a separate classic GitHub PAT with only `read:packages`. Fine-grained PATs are not supported by GHCR. Choose an expiry and rotate before that date. The credential verified on 19 October 2026 expires at 23:00 UTC on 17 October 2027 (18 October in the UK); update this record when rotating it. The PAT owner must have read access to `tom-knighton/recipeparserapi`. If the package requires organization SSO, authorize the token for that organization too.

Set the shared pull token through a secure terminal prompt:

```sh
gh secret set GHCR_READ_TOKEN --repo Tom-Knighton/RecipeParserAPI
```

Set separate Discovery database connection strings in GitHub's `development` and `production` environments:

```sh
gh secret set DISCOVERY_CONNECTION_STRING --repo Tom-Knighton/RecipeParserAPI --env development
gh secret set DISCOVERY_CONNECTION_STRING --repo Tom-Knighton/RecipeParserAPI --env production
```

These self-hosted endpoints use Supavisor session pooling, so their usernames include the configured tenant identifier:

| Environment | Host | Port | Username |
| --- | --- | --- | --- |
| Development | `db.sporkast.dev.tomk.online` | `15432` | `postgres.sporkast-bc-dev` |
| Production | `db.sporkast.tomk.online` | `15433` | `postgres.sporkast-prd` |

Keep passwords in the secret store. A missing tenant identifier or a stopped upstream Postgres container can surface as a closed authentication stream. Check the relevant database and pooler container health before rotating a valid database password.

For OpenAI-assisted parsing, set `OPENAI_API_KEY` separately in each environment. Never place database strings or tokens in a workflow, checked-in settings file, shell history, or chat.

The workflow copies these secrets into Azure Container Apps secrets and references them from the registry and environment variables. Temporary deployment files have owner-only permissions and are deleted after use. Existing custom domains, other environment variables, and existing secrets are preserved on updates. Existing Azure-only OpenAI configuration is preserved when no replacement key is supplied.

Azure login uses OIDC through environment-scoped user-assigned identities. It does not use the legacy `*_AZURE_CREDENTIALS` secrets. Each identity has Contributor access only to its own resource group. The non-secret GitHub environment variables are:

- `AZURE_CLIENT_ID`
- `AZURE_TENANT_ID`
- `AZURE_SUBSCRIPTION_ID`
- `GHCR_USERNAME`
- `OPENAI_MODEL` (the existing Railway API uses `gpt-5.4-mini`)

## Release verification

Each deployment runs focused offline tests, builds a Linux AMD64 image, pushes it to GHCR, applies pending EF Core Discovery migrations to the selected environment's database, and deploys the resulting immutable image digest. Review migrations before releasing; keep them backward compatible with the previous revision. The workflow does not migrate the database on application startup or revert database changes on rollback.

The startup and liveness probes use `/health/live`. Readiness uses `/health/ready`, which queries the Discovery candidate table so missing migrations and database access failures prevent a revision becoming ready. Azure's single-revision mode retains the previous serving revision until the replacement is ready. The deployment script checks the new revision's health and then verifies readiness over its Azure HTTPS endpoint. Failed verification fails the workflow.

The initial Discovery synchronization can take several minutes. The startup probe allows ten minutes. Azure terminates public TLS at ingress and forwards HTTP to port 8080; `allowInsecure` is disabled.

Inspect status without exposing secrets:

```sh
az containerapp revision list -n dev-sporkast-api -g rg-sporkast-dev -o table
az containerapp logs show -n dev-sporkast-api -g rg-sporkast-dev --type system --tail 30
az containerapp logs show -n prod-sporkast-api -g rg-sporkast-prod --type console --tail 30
```

To restore a previous release, rerun the workflow against its reviewed source ref, or deploy its recorded digest with `scripts/deploy-azure.py` and the selected environment's secrets. Restoring the image does not undo migrations.

## Custom domains

After creating the app, obtain its Azure FQDN and ownership verification ID:

```sh
az containerapp show -n prod-sporkast-api -g rg-sporkast-prod \
  --query '{fqdn:properties.configuration.ingress.fqdn,verificationId:properties.customDomainVerificationId}'
```

Production uses a DNS-only A record for `api.sporkast.tomk.online` pointing to the environment’s static IP (`20.90.40.159`), a CAA record at that same hostname with `0 issue "digicert.com"`, and a TXT record at `asuid.api.sporkast.tomk.online` containing the verification ID. The parent `sporkast.tomk.online` CNAME resolves to Vercel CAA records that exclude DigiCert; the API-specific CAA record overrides that inherited policy. A CNAME cannot coexist with a CAA record at the same hostname, so production uses an A record and HTTP validation. Keep Cloudflare proxying disabled for Azure validation and renewal. If the Azure environment is recreated, update the A record to its new static IP. Bind the hostname:

```sh
az containerapp hostname add -n prod-sporkast-api -g rg-sporkast-prod --hostname api.sporkast.tomk.online
az containerapp hostname bind -n prod-sporkast-api -g rg-sporkast-prod \
  --hostname api.sporkast.tomk.online --environment env-sporkast-prod --validation-method HTTP
```

Development keeps its existing `api.dev.recipe.tomk.online` Azure binding. Its legacy `api.dev.sporkast.tomk.online` hostname uses a DNS-only A record pointing to `20.254.210.169`, an API-specific DigiCert CAA record, the matching `asuid` TXT record, and an HTTP-validated managed certificate. Keep dev running while issuing a certificate; temporarily set its minimum replicas to one if it has scaled to zero, then restore zero after HTTPS verification. Verify `/health/ready` over each Azure custom hostname after binding. Set the iOS production build variable `SPORKCAST_CI_API_URL` to `https://api.sporkast.tomk.online/` after production verification. The iOS development API is `https://api.dev.recipe.tomk.online/`. The legacy `https://api.dev.sporkast.tomk.online/` hostname also serves Azure for existing installations.

## Diagnosed legacy failure

On 18 October 2026, the existing `develop` GitHub workflow failed GHCR login with `denied: denied`. Azure revisions failed with `ImagePullBackOff` and a 403 while requesting a GHCR pull token. The stored registry credential was rejected; the service's old image tag itself was not sufficient to keep it running because Azure must authenticate on future pulls. The provider does not disclose whether the token expired, was revoked, or lost permissions.

The old `main` workflow referenced a removed ACR and a removed development app. It was not a production deployment. The replacement workflow explicitly maps `main` to production and `develop` to development.

On 19 October, the existing development Postgres container was also found stopped. It was started again, and the verified Supavisor tenant names above were used to configure both GitHub environments. Both Azure deployments passed database readiness checks and a real recipe import using the same image digest. The Railway hostname and service configuration were preserved during the initial deployment. A later cutover moved the legacy dev hostname to Azure and retired Railway.

## Database TLS and certificate renewal

Both Supavisor poolers serve the existing, publicly trusted Let’s Encrypt certificates for their database hostnames. Discovery connection strings in both GitHub environments and Azure require `SSL Mode=VerifyFull`, validating the hostname and certificate chain. Keep this setting when rotating database credentials. TLS terminates at Supavisor; its connection to Postgres remains on the server’s Docker network. Other existing pooler clients have not been forced to enable TLS.

On the database server (`188.165.252.22`), `/home/tomk/sporkast-db-tls/` contains a copy of `scripts/sync-pooler-certificates.py`, a hostname/container configuration, and certificate directories with owner-only permissions. The script extracts only the two database certificates from the existing Traefik ACME store through Docker, checks their hostnames, expiry and matching keys, and restarts only the relevant pooler when its certificate changes. Existing Traefik renewal continues to manage issuance. The `tomk` user’s cron runs synchronization every six hours; logs are in the same directory. It does not request a new certificate or expose private keys in output.

The dev and production Compose environment overrides mount their respective certificate directory read-only at `/etc/pooler/tls` and configure `GLOBAL_DOWNSTREAM_CERT_PATH` / `GLOBAL_DOWNSTREAM_KEY_PATH`. Backups ending `.before-pooler-tls` preserve the previous overrides. Do not remove the certificate directory while pooler TLS is enabled. Restore the matching override backup and recreate only `supavisor` if reverting TLS, and coordinate client connection strings before doing so.

Check renewal without changing files or restarting services:

```sh
python3 /home/tomk/sporkast-db-tls/sync-pooler-certificates.py \
  --config /home/tomk/sporkast-db-tls/config.json --check
```

AngleSharp is upgraded to 1.5.0 in both projects to address [GHSA-pgww-w46g-26qg](https://github.com/advisories/GHSA-pgww-w46g-26qg). Twenty-two focused and extraction tests passed. Full recipe output from saved BBC and Beat the Budget HTML snapshots was identical on 1.3.0 and 1.5.0. Live-site assertions can vary with publisher review content; the existing Beat the Budget review-text assertion failed identically on both versions.
