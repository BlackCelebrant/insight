# DESIGN — Identity Resolution

## 1. Overview

`insight-identity` (.NET 9 / ASP.NET Core minimal API) is the canonical
successor to the legacy Rust `insight-identity-resolution` stub. The
Rust stub is retired (`identityResolution.deploy: false` by default in
the umbrella) and lives in `src/backend/services/identity-old/` only
until the cleanup PR removes it entirely. The Phase 1 surface is
identical to what the Rust stub exposed plus the new `parent_*` and
`person_id` fields the underlying `persons` schema makes available.

## 2. Components

```
┌──────────────────────────────────────────────────────────────────┐
│                        Insight.Identity.Api                       │
│  Program.cs                                                       │
│   ├── Configuration (yaml + IDENTITY__ env)                       │
│   ├── Auth/CompositeTenantContext (header → JWT stub → config)    │
│   ├── Endpoints/PersonsEndpoints (3 routes)                       │
│   └── Logging/Serilog (compact JSON)                              │
└──────────────────────────────────────────────────────────────────┘
                  │ depends on
                  ▼
┌──────────────────────────────────────────────────────────────────┐
│                  Insight.Identity.Domain                          │
│   PersonLookupService (orchestrates lookup + assembly)            │
│   PersonAssembler (latest-per-type collapse + display-name split) │
│   IPersonsReader (port to infrastructure)                         │
│   Person, PersonObservation, ValueTypes                           │
└──────────────────────────────────────────────────────────────────┘
                  │ depends on
                  ▼
┌──────────────────────────────────────────────────────────────────┐
│              Insight.Identity.Infrastructure                      │
│   MariaDbConnectionFactory (parses mysql:// URL, pools)           │
│   PersonsRepository (SQL + parameter binding, BINARY(16))         │
│   Sql (centralised CTE queries; ROW_NUMBER OVER PARTITION)        │
└──────────────────────────────────────────────────────────────────┘
```

## 3. Lookup flow

```
HTTP GET /v1/persons/alice@example.com
   │
   ▼
ITenantContext.Resolve  ─── header → JWT stub → config
   │  (null → 400)
   ▼
PersonLookupService.GetByEmailAsync
   │ lowercase email, trim
   ▼
IPersonsReader.ResolvePersonIdByEmailAsync
   │ SQL: latest-per-source row WHERE value_type='email' AND value_id=@email
   │  (null → 404)
   ▼
IPersonsReader.GetLatestObservationsAsync
   │ SQL: ROW_NUMBER() OVER (PARTITION BY source_type,source_id,value_type)
   ▼
PersonAssembler.Assemble
   │ pick latest value per value_type across all sources
   │ fall back to DisplayNameSplitter when first/last absent
   ▼
PersonResponse (snake_case JSON, parity with Rust)
```

## 4. Database access

### 4.1 Schema dependency

The service reads exclusively from `persons` (PR #214). Generated columns
`value_effective` (display) and `value_hash` (uniqueness) are produced by
the DB; the service reads `value_effective` and never writes.

### 4.2 Latest-per-source projection

```sql
WITH ranked AS (
  SELECT
    person_id, insight_source_type, insight_source_id,
    value_type, value_effective, created_at,
    ROW_NUMBER() OVER (
      PARTITION BY insight_source_type, insight_source_id, value_type
      ORDER BY created_at DESC, id DESC
    ) AS rn
  FROM persons
  WHERE insight_tenant_id = @tenant_id
    AND person_id        = @person_id
)
SELECT person_id, insight_source_type, insight_source_id,
       value_type, value_effective, created_at
FROM ranked
WHERE rn = 1
```

This is the canonical "latest observation per source per attribute"
projection; the assembler picks the per-attribute winner across sources by
`MAX(created_at)` after this CTE returns.

### 4.3 BINARY(16) UUID handling

`tenant_id`, `source_id`, `person_id`, `author_person_id` are all stored
as `BINARY(16)`. The repository binds `Guid.ToByteArray()` and reconstructs
`new Guid(bytes)` on read. We never let MySqlConnector fall back to
`ToString()` — the 36-char form would be silently truncated to 16 ASCII
bytes by the column.

## 5. Tenant context

```
HeaderTenantContext  → reads X-Insight-Tenant-Id
JwtTenantContext     → reads `insight_tenant_id` claim (Phase 1.5 stub)
ConfigTenantContext  → IDENTITY__identity__tenant_default_id
```

`CompositeTenantContext` walks them in declaration order and returns the
first non-null. The composite is the only `ITenantContext` registered in
DI; individual resolvers stay testable in isolation.

## 6. Configuration

| Env var | Meaning |
|---|---|
| `IDENTITY__mariadb__url` | `mysql://user:pass@host:port/db`, percent-encoding allowed. |
| `IDENTITY__mariadb__min_pool_size` | Default 0. |
| `IDENTITY__mariadb__max_pool_size` | Default 16; tuned smaller than analytics-api per P4 in the design review. |
| `IDENTITY__identity__bind_addr` | Default `0.0.0.0:8082`. |
| `IDENTITY__identity__tenant_default_id` | Optional; used only when no header arrives. |
| `IDENTITY__identity__expand_subordinates` | Phase 2 toggle (`false` until reconciliation lands). |

## 7. Logging

Serilog with `CompactJsonFormatter` writes one structured JSON line
per log event. Request logging is enabled via
`UseSerilogRequestLogging`. The Serilog `service` enricher value is
`identity` (matches the deployment name) so log dashboards can filter
by a stable tag regardless of source-folder names.

## 8. Deployment

Helm chart `insight-identity` (folder
`src/backend/services/identity/helm/`) emits the canonical
`insight-identity` Service. The umbrella generates Secret
`insight-identity-config` (env vars `IDENTITY__*`) on install when
`identity.deploy=true`, derived from `insight-db-creds`. Liveness
probe hits `/health` (DB ping), readiness probe hits `/healthz`
(process liveness without DB).

The legacy Rust subchart `identityResolution` (folder
`src/backend/services/identity-old/`) is retired (`deploy: false` by
default) but remains as a dependency in the umbrella for clusters
mid-migration. The follow-up cleanup PR removes it entirely.

## 9. Tests

- **Unit** — `DisplayNameSplitter`, `PersonAssembler` (latest-per-type,
  display-name fallback, parent attributes).
- **Integration** — Testcontainers MariaDB applies the same DDL as the
  SeaORM migration, seeds an Alice row across multiple `value_type`s, and
  asserts the endpoint returns the assembled response.

## 10. Migration path

**Phase 1 (already shipped):** Rust `insight-identity-resolution` and
the .NET `insight-identity-csharp` both deployed side by side.
Operators verified behavioural parity on at least one dev cluster.

**Phase 2 (this PR — cut-over):** chart and folder both renamed to
`insight-identity` (`src/backend/services/identity/`). The Rust
subchart `identityResolution` defaults to `deploy: false` and its
source moved to `src/backend/services/identity-old/`. The
`identityResolution` dependency stays in `charts/insight/Chart.yaml`
purely as a compatibility shim so clusters with overrides setting
`identityResolution.deploy: true` continue to render through the
transition. analytics-api's `ANALYTICS__identity_resolution_url` now
points at `http://insight-identity:8082`; api-gateway proxies both
`/identity/*` (preferred) and `/identity-resolution/*` (legacy) to the
same Service. Serilog `service` enricher value is now `identity`.

**Phase 3 (follow-up cleanup PR):**

- Delete `src/backend/services/identity-old/` (Rust source).
- Remove `identityResolution` dependency from
  `charts/insight/Chart.yaml`.
- Remove the legacy `helmfile/values/identity-resolution.yaml`
  overlay if any.
- Drop the legacy `/identity-resolution/*` proxy prefix from
  `apiGateway.proxy.routes`.
- Drop `INSIGHT_IDENTITY_RESOLUTION_HOST` from `insight-platform`
  ConfigMap; drop `insight.identityResolution.host` helper.
- Trim the `identity-old` Cargo workspace member; stop copying
  `services/identity-old/Cargo.toml` in the api-gateway /
  analytics-api Dockerfiles.

The schema (`persons`, `account_person_map`) does not change at any
phase — both the legacy Rust seed pipeline (kept under
`services/identity/seed/`) and the .NET service operate on the same
MariaDB tables.
