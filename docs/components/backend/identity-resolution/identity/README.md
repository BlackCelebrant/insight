# Identity Resolution (.NET 9)

Person-lookup API over MariaDB `persons`. Read-only consumer of the
observation log written by
[seed-persons-from-identity-input.py](../../../../src/backend/services/identity/seed/seed-persons-from-identity-input.py)
and the (forthcoming) reconciliation service. Replaces the legacy Rust
stub (`services/identity-old/`, retired) that previously bound bronze
BambooHR rows into an in-memory map at startup.

| Spec | Path |
|---|---|
| PRD | [specs/PRD.md](specs/PRD.md) |
| DESIGN | [specs/DESIGN.md](specs/DESIGN.md) |
| ADRs | [specs/ADR/](specs/ADR/) |

## Deployment

| Path | Command |
|---|---|
| Dev (local kind) | `./dev-up.sh --env local backend` — builds `insight-identity:local`, loads into kind, installs umbrella with `identity.deploy=true`. |
| Production / staging | Standard umbrella install. Override `identity.deploy=true` and `identity.image.tag=<release>` in your values overlay. |
| Standalone (no umbrella) | `helm install identity ./src/backend/services/identity/helm` with a pre-created `insight-identity-config` Secret. |

The umbrella emits Secret `insight-identity-config` automatically when
`identity.deploy=true`. It carries `IDENTITY__mariadb__url` (derived
from auto-generated MariaDB credentials in `insight-db-creds`) and
optionally `IDENTITY__identity__tenant_default_id` (from
`identity.tenantDefaultId`).

## API surface

| Endpoint | Description |
|---|---|
| `GET /v1/persons/{email}` | Resolve person by email (lowercased). Returns 404 when no current observation matches. |
| `GET /health` | DB ping. 200 / 503. |
| `GET /healthz` | Process liveness. 200 `text/plain "ok"`. |

Tenant resolution: header `X-Insight-Tenant-Id` → JWT claim (Phase 1.5
stub) → config default. First non-null wins. Empty config default
forces every request to carry the header.

## Local run (VS F5 / `dotnet run`)

```sh
cp src/backend/services/identity/.env.local.example \
   src/backend/services/identity/.env.local
# Edit .env.local with real credentials. VS F5 reads
# Properties/launchSettings.json (gitignored) which mirrors the same
# vars; create your own from the example block in `.env.local`.
```

## Tests

```sh
dotnet test src/backend/services/identity/Insight.Identity.sln
```

Integration tests pull a MariaDB image via Testcontainers; Docker must
be running on the host.

## Migration from the Rust stub

The Rust `identityResolution` subchart (alias `identityResolution`) is
retired — `deploy: false` by default in the umbrella. It survives only
for clusters mid-migration that still carry an override. The follow-up
cleanup PR removes `src/backend/services/identity-old/` and the
umbrella dependency entirely.

**URL compatibility.** api-gateway proxies both `/identity/*`
(preferred) and `/identity-resolution/*` (legacy) to the same
`insight-identity` Service, so existing callers keep working through
the cutover. The env var `ANALYTICS__identity_resolution_url` retains
its legacy name in analytics-api source — it now points at
`http://insight-identity:8082`.

**Configuration variable rename.** `IDENTITY_CSHARP_TENANT_DEFAULT_ID`
→ `IDENTITY_TENANT_DEFAULT_ID` (set in `.env.local`). The old name is
still honoured by `dev-up.sh` as a fallback during the migration.

**user-secrets path.** `dotnet user-secrets` storage moved from
`%APPDATA%\Microsoft\UserSecrets\insight-identity-csharp\` to
`%APPDATA%\Microsoft\UserSecrets\insight-identity\`. Re-run
`dotnet user-secrets set "mariadb:url" "..."` once after pulling
this PR.
