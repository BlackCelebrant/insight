# PRD — Identity Resolution

## 1. Overview

### 1.1 Purpose

Provide a drop-in replacement for the existing Rust identity-resolution stub
that serves person lookups by email. The new service reads from the
service-owned MariaDB `persons` table introduced in PR #214 instead of the
ClickHouse `bronze_bamboohr.employees` snapshot, runs on .NET 9 / ASP.NET
Core, and ships in parallel with the Rust deployment so the migration can
be cut over per-environment without downtime.

### 1.2 Background

The Rust stub (`src/backend/services/identity/`) loads BambooHR employees
into an in-memory `HashMap` at startup, which has three problems: (1) it
relies on a bronze table that may not exist on a fresh cluster, (2) it
ignores all non-BambooHR sources, and (3) it does not see updates between
pod restarts. PR #214 lands an append-only observation log that already
unifies all sources behind the same schema; this service is the first
consumer that queries it directly.

### 1.3 Goals

| Goal | Success criterion |
|---|---|
| Behavioural parity with Rust stub | API contract `GET /v1/persons/{email}` returns the same field shape; `/health` and `/healthz` keep their semantics. |
| Multi-source coverage | Lookup answers correctly for any source whose connector emits identity observations, not only BambooHR. |
| Live data | Updates land in `persons` are visible without a pod restart. |
| Tenant safety | Every query is scoped by `insight_tenant_id`. |

## 2. Actors

- **api-gateway** — calls `GET /v1/persons/{email}` to enrich responses.
- **dbt-runner / Argo workflows** — internal callers that may need person
  metadata when materialising Gold tables.
- **Operators** — read `/health` for liveness, `/healthz` for readiness.

## 3. Scope

### 3.1 In scope (Phase 1)

- `GET /v1/persons/{email}` returning a single Person with parent
  attributes (`parent_email`, `parent_id`, `parent_person_id`) but no
  recursive subordinate expansion.
- `GET /health` (DB ping) and `GET /healthz` (process liveness).
- Tenant resolution by `X-Insight-Tenant-Id` header with fallback to
  `IDENTITY__identity__tenant_default_id` config.
- Lowercase email lookup against `value_type = 'email'`.
- Fallback first/last from `display_name` split when explicit
  observations are absent.

### 3.2 Phase 2 (out of scope, but designed-for)

- Recursive subordinate expansion via `parent_person_id`.
- JWT-claim tenant resolution.
- Multi-result return shape.

### 3.3 Cut-over (this PR)

- The deployment ships under the canonical name `insight-identity`
  (Service, Secret `insight-identity-config`, image `insight-identity`).
- The Rust `identityResolution` subchart defaults to `deploy: false`.
- The api-gateway proxies both `/identity/*` (preferred) and
  `/identity-resolution/*` (legacy) to the same `insight-identity`
  Service for caller compatibility through the migration window.
- See ADR-0009 for the cut-over decision record.

### 3.4 Out of scope (follow-up cleanup PR)

- Identity reconciliation (separate service writes `parent_person_id`).
- Merge/split workflows.
- Removing the retired Rust source folder
  `src/backend/services/identity-old/` and the `identityResolution`
  umbrella dependency entirely (this PR moved it aside but kept it
  buildable for clusters mid-migration; the cleanup PR deletes it).

## 4. Functional Requirements

| # | Requirement |
|---|---|
| FR-1 | Resolve email → `person_id` using the latest observation per `(insight_source_type, insight_source_id, value_type, value_id)` partition. |
| FR-2 | Hydrate every other field with the latest observation per `(insight_source_type, insight_source_id, value_type)` partition for that `person_id`. |
| FR-3 | Return 404 with RFC 7807 problem-details body when no current observation matches. |
| FR-4 | Return 400 problem-details body when the request carries no header and no default tenant is configured. |
| FR-5 | Lowercase the email before lookup; storage and lookup share `utf8mb4_bin` collation. |
| FR-6 | Surface `parent_email`, `parent_id`, `parent_person_id` on the response when present. |

## 5. Non-Functional Requirements

| # | Requirement |
|---|---|
| NFR-1 | P95 lookup latency under 50 ms for tenants with under 50k persons (single-row cardinality on a covered index). |
| NFR-2 | Process memory under 384 Mi at steady state; no in-memory full-table cache. |
| NFR-3 | Logs are structured JSON via Serilog `CompactJsonFormatter` with the enricher `service=identity`. Request-logging middleware records an allow-listed property set per request (`RequestMethod`, `RequestPath` template, `StatusCode`, `Elapsed`, `RequestId`, `ConnectionId`, `@tr`/`@sp` trace+span IDs) — never the raw email path segment or any other PII. Unhandled-exception payloads include exception type + message + the sanitised `db_target` (`host:port/db`, no credentials) and never the connection string. |
| NFR-4 | All UUIDs round-trip as `BINARY(16)` to MariaDB to avoid the 36-char-string truncation bug captured in the Python seeder. |

## 6. Use Cases

### 6.1 Resolve email to person

api-gateway forwards the user-facing email to `GET /v1/persons/{email}`
with the header. The service returns the assembled person object; the
gateway merges it into the analytics response.

### 6.2 Liveness and readiness

Kubernetes hits `/healthz` for liveness (process up) and `/health` for
readiness (DB reachable). A failing DB ping flips the pod out of the
service endpoints until the pool recovers.

## 7. Acceptance Criteria

- Integration test against a Testcontainers MariaDB returns the seeded
  Alice record with email/display/job_title fields populated.
- Same integration test returns 404 for an unknown email.
- Helm chart installs cleanly into the local `helmfile -e local sync`
  alongside the existing `identity-resolution` release.
- Unit tests cover display-name splitting and assembler latest-per-source
  selection.

## 8. Dependencies

- PR #214 (`persons` table migration).
- Reconciliation service (separate PR) writes `parent_person_id`
  observations; until it lands, Phase 2 subordinates always return empty.
- BambooHR `identity_inputs` SQL extended with the seven new value_types.
- Python seeder `seed-persons-from-identity-input.py` extended with the
  routing rules that match the SQL.

## 9. Risks

- The `persons` schema may evolve; the SQL is centralised in `Sql.cs` so
  one place absorbs the change.
- A misconfigured tenant default returning the wrong person to the wrong
  caller is a data-leak risk — the composite resolver always lets the
  header win, and the default is only used in single-tenant clusters.
