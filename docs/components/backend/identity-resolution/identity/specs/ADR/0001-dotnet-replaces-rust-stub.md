# ADR-0001: .NET 9 Service Replaces the Rust Identity-Resolution Stub

**Status:** Accepted

## Context

The Rust `identity-resolution` stub (originally
`src/backend/services/identity/`, now `services/identity-old/` after
retirement) loaded `bronze_bamboohr.employees` into an in-memory
`HashMap` at startup. With PR #214 introducing the MariaDB `persons`
table, the data source is shifting and we need a service that reads
from the new schema. Two options were on the table: extend the Rust
stub or rewrite on a different stack.

## Decision

Rewrite as a new .NET 9 service. Phase 1 ran both binaries in parallel
under the names `insight-identity-csharp` and
`insight-identity-resolution`. Phase 2 (ADR-0009) cuts over to a
single canonical deployment `insight-identity` (.NET) and retires the
Rust subchart by default.

## Rationale

- The team carries deeper .NET experience for the upcoming Phase 2
  features (reconciliation, merge/split workflows) than for Rust.
- A parallel deployment de-risks the cutover; consumers can swap the
  upstream URL per environment.
- The Rust stub's schema and SQL would need a full rewrite anyway —
  there is no incremental refactor that shrinks the diff meaningfully.

## Consequences

- Two services lived side-by-side during Phase 1; the cut-over PR
  (ADR-0009) collapses them under one canonical name.
- The Rust stub's tests stay green during the migration window so its
  in-cluster behaviour does not regress.
- Phase 1's `insight-identity-csharp` naming is gone; only
  `insight-identity` remains.
