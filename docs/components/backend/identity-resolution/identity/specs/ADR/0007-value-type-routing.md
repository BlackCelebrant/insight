# ADR-0007: `value_type` Routing for Identity Reads

**Status:** Accepted

## Context

The `persons` schema splits the value across three columns by
`value_type`:

- `value_id VARCHAR(320) COLLATE utf8mb4_bin` — strict byte comparison,
  hot-path index target.
- `value_full_text VARCHAR(512) COLLATE utf8mb4_unicode_ci` —
  case-insensitive search, room for FULLTEXT.
- `value TEXT` — catch-all, indexed only via `value_hash`.

The C# service must agree with the seed pipeline on which `value_type`
lands in which column, otherwise lookups will miss rows the seed wrote.

## Decision

The shared routing table:

| Column | `value_type`s |
|---|---|
| `value_id` | `id`, `email`, `username`, `employee_id`, `parent_email`, `parent_id`, `parent_person_id` |
| `value_full_text` | `display_name`, `first_name`, `last_name`, `department`, `division`, `job_title`, `status` |
| `value` (catch-all) | anything else (custom attributes, future types) |

The service reads `value_effective` (the generated coalesce of the
three columns), so it does not need to know the routing for read; the
routing matters only for writes (the seed) and for lookups that filter
by `value_id` (the email resolution path).

## Rationale

- `parent_email`, `parent_id`, and `parent_person_id` are identifier
  shapes — exact byte equality, hot-path index. Hence `value_id`.
- BambooHR free-form attributes (`first_name`, `department`, …) want
  case-insensitive search and the existing `idx_value_full_text`
  covers them.
- `employee_id` migrated from the catch-all into `value_id` because
  it's an identifier; the seed must lowercase nothing for it (it's
  numeric or alphanumeric ID).

## Consequences

- The seed pipeline's `VALUE_TYPES_FOR_VALUE_ID` and
  `VALUE_TYPES_FOR_VALUE_FULL_TEXT` constants are kept in lockstep
  with this table. A future change to the table requires touching both
  the Python seeder and this ADR.
- `parent_person_id` is stored as the canonical 36-char string form so
  the email-lookup-by-`value_id` SQL works for it without special
  casing. `BINARY(16)` would have required a different lookup path.
