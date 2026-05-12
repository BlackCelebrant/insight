# ADR-0003: Latest-Per-Source Lookup Semantics

**Status:** Accepted

## Context

`persons` is append-only — the same `(tenant, person, source_type,
source_id, value_type)` may have many rows over time as the source
publishes new values. The service must decide which row "represents"
the current value of an attribute. Two approaches considered:

1. Latest-per-(tenant, person, value_type) — collapse across sources first,
   then pick the most recent row.
2. Latest-per-source per (tenant, person, source_type, source_id,
   value_type) — pick the most recent row in each source partition,
   then collapse across sources at the assembler level.

## Decision

Use option 2 (latest-per-source). For email lookup we additionally
require the latest row per `(value_type='email', value_id=…)` partition
to map to the queried email — otherwise the lookup misses.

## Rationale

- Option 2 surfaces source-level conflicts cleanly: if BambooHR and
  Cursor disagree on `display_name`, both rows survive into the
  assembler, which can then resolve the conflict by `MAX(created_at)`
  (current behaviour) or by source priority (future).
- Option 2 is the same projection the seed and the
  `account_person_map` rebuild use, so SQL stays consistent across the
  identity domain.
- Email rebinding (e.g. an account's email changes upstream) makes the
  old email cease to resolve: the latest row per
  `(source_type, source_id, 'email', value_id=old)` no longer reflects
  the current binding, so the lookup returns 404. This is the agreed
  behaviour — old emails should not silently keep working.

## Consequences

- The lookup query is a CTE with `ROW_NUMBER() OVER PARTITION BY`. The
  index `idx_value_id (insight_tenant_id, value_type, value_id)`
  covers the email lookup; the partition columns sit on
  `idx_tenant_person`.
- Conflict resolution is documented as "max created_at across sources";
  Phase 2 may revisit this with an explicit source-priority table.
