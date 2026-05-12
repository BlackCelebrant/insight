# ADR-0005: Composite Tenant Context With JWT Stub

**Status:** Accepted

## Context

The service is per-tenant by every query (`insight_tenant_id` is part of
every index). The Rust stub never had a tenant concept because it loaded
all of BambooHR in-memory. The C# service needs a tenant per request.

Two flows must coexist:

- Internal callers (api-gateway, dbt-runner) send a header
  `X-Insight-Tenant-Id`.
- A future direct-call flow (cookie/JWT issued by api-gateway) will
  carry tenants in claims.

For local development, a single tenant is wired in by configuration.

## Decision

Implement three resolvers and a composite that walks them in declaration
order:

1. `HeaderTenantContext` — reads `X-Insight-Tenant-Id`.
2. `JwtTenantContext` — reads the `insight_tenant_id` claim. Stub for
   Phase 1.5; relies on api-gateway forwarding the principal.
3. `ConfigTenantContext` — returns
   `IDENTITY__identity__tenant_default_id` when set.

If all return `null`, the endpoint returns 400 with an RFC 7807 body.

## Rationale

- Header-first is what every current internal caller sends.
- The JWT stub is wired in DI now so when api-gateway flips on the new
  flow nothing else needs to change.
- A configured default keeps single-tenant local clusters trivial — no
  header gymnastics in `helmfile -e local`.

## Consequences

- A misconfigured default in a multi-tenant environment is a data-leak
  risk. Operators must leave the default unset in shared production.
- The composite is the only `ITenantContext` registered in DI; the
  individual resolvers are still classes for tests to instantiate
  directly.
