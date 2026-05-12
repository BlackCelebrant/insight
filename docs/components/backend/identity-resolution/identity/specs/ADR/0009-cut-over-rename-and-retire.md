# ADR-0009: Cut-Over Rename and Retire the Rust Stub

**Status:** Accepted

## Context

Phase 1 shipped `insight-identity-csharp` side-by-side with the legacy
Rust `insight-identity-resolution`. The two services share the MariaDB
`persons` table; the Rust one is the original (in-memory BambooHR
cache + ClickHouse loader) and the .NET one is the persons-table
reader designed to replace it.

After dev-cluster smoke validation (curl returns expected JSON,
latency 10–50 ms warm-pool, identical to the Rust stub's range), the
team is ready to cut over to a single canonical deployment named
`insight-identity`.

## Decision

Cut-over is split across **two PRs**:

1. **This PR — service-level rename + source-folder rename + Rust-retire-by-default:**
   - Helm subchart name `insight-identity-csharp` → `insight-identity`.
   - Service / Deployment / Secret renamed accordingly
     (`insight-identity-config`, etc.).
   - Image `insight-identity-csharp` → `insight-identity`.
   - Umbrella alias `identityCsharp` → `identity`, values block
     `identityCsharp:` → `identity:`.
   - **Source folders renamed**:
     - `src/backend/services/identity/` → `src/backend/services/identity-old/`
       (Rust source — retired but kept buildable for the migration window)
     - `src/backend/services/identity-csharp/` → `src/backend/services/identity/`
       (canonical .NET location)
   - **Spec folder renamed**:
     `docs/components/backend/identity-resolution/identity-csharp/` →
     `docs/components/backend/identity-resolution/identity/`
   - Seed pipeline scripts (`seed-persons.sh`,
     `seed-persons-from-identity-input.py`) move with the new identity
     folder — they populate the `persons` table the .NET service reads.
   - Legacy Rust subchart `identityResolution` defaults to
     `deploy: false`. Dependency retained in
     `charts/insight/Chart.yaml` so clusters mid-migration with
     `identityResolution.deploy=true` overrides still render.
   - api-gateway proxies both `/identity/*` (preferred) and
     `/identity-resolution/*` (legacy) to the same `insight-identity`
     Service; analytics-api's `ANALYTICS__identity_resolution_url`
     points at the new Service.
   - dev-up.sh stops building the Rust `identity` image and instead
     builds the .NET Dockerfile under that image name.
   - Serilog `service` enricher value: `identity-csharp` → `identity`.

2. **Follow-up cleanup PR — remove the retired Rust stub:**
   - Delete `src/backend/services/identity-old/` entirely.
   - Drop `identityResolution` dependency from umbrella `Chart.yaml`.
   - Drop the legacy `/identity-resolution/*` proxy prefix from
     `apiGateway.proxy.routes`.
   - Drop the legacy alias `INSIGHT_IDENTITY_RESOLUTION_HOST` from
     `insight-platform` ConfigMap.
   - Drop the legacy alias `insight.identityResolution.host` helper
     from `_helpers.tpl`.
   - Trim the `identity-old` Cargo workspace member from
     `src/backend/Cargo.toml` and update analytics-api / api-gateway
     Dockerfiles to stop copying `services/identity-old/Cargo.toml`.

## Rationale

- **Splitting the cut-over.** A single PR that simultaneously renames
  the deployment AND deletes the Rust source is a huge diff with
  intermixed concerns — reviewers cannot judge the rename without
  also re-validating that the Rust service is truly unused. Splitting
  lets PR #1 land as a clean rename (revertable in one `helm
  rollback`) and gives operators a transition window to remove any
  lingering `identityResolution.deploy=true` overrides before PR #2
  hard-removes the dependency and source.

- **Folder rename included in PR #1 (not deferred).** The deployed
  service is the canonical `identity`; keeping the source folder
  named `identity-csharp` would be a permanent visual mismatch
  ("which folder owns the deployment?"). Reviewers can audit the
  rename in a single PR rather than chasing a follow-up cosmetic
  patch. The Rust source moves to a self-documenting `identity-old/`
  name so its retired status is obvious from `ls`.

- **Legacy URL prefix kept (until PR #2).** api-gateway's
  `/identity-resolution/*` prefix may be referenced by internal
  callers we have not audited yet. Removing it in PR #1 would
  cascade breakage; instead we proxy both prefixes to the same
  Service. PR #2 removes the legacy prefix after grep'ing for
  callers.

## Consequences

- A single `helm upgrade` from Phase 1 to this state recreates
  resources under the new names; helm's diff handles the rename
  cleanly (old `insight-identity-csharp-*` resources deleted, new
  `insight-identity-*` resources created).
- The legacy env var `IDENTITY_CSHARP_TENANT_DEFAULT_ID` in
  `.env.local` is honoured as a fallback alias for
  `IDENTITY_TENANT_DEFAULT_ID` in `dev-up.sh` — developers do not
  need to update their `.env.local` immediately.
- `ANALYTICS__identity_resolution_url` retains its legacy env-var
  name in the analytics-api binary; only its value changes.
  Renaming the env var requires a coordinated change in
  analytics-api source and is scheduled for PR #2.
- Serilog log dashboards filtering by `service: identity-csharp`
  need a one-line update to `service: identity` after this PR
  deploys.
- `dotnet user-secrets` storage location changes:
  `%APPDATA%\Microsoft\UserSecrets\insight-identity-csharp\` →
  `%APPDATA%\Microsoft\UserSecrets\insight-identity\`. Operators
  using user-secrets re-run their `dotnet user-secrets set` commands
  once after pulling this PR.
