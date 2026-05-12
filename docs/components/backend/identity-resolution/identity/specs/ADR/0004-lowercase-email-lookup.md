# ADR-0004: Lowercase Emails on Storage and Lookup

**Status:** Accepted

## Context

`persons.value_id` is `VARCHAR(320) COLLATE utf8mb4_bin`. The `_bin`
collation makes byte equality the only equality, so `Alice@Example.COM`
and `alice@example.com` would not match. Three options considered:

1. Switch the collation to `utf8mb4_general_ci` for case-insensitive
   matching.
2. Wrap the lookup in `LOWER(value_id) = LOWER(@email)` — defeats the
   index.
3. Lowercase on write and on lookup; preserve the original case in
   `display_name` (or in a future column) when needed.

## Decision

Option 3. The seed already lowercases via `LOWER(TRIM())` when checking
the existing-email set, and the service applies `ToLowerInvariant()` to
the lookup parameter before binding.

## Rationale

- Keeps the hot-path index (`idx_value_id`) intact; case-insensitive
  collations are slower for byte-level equality and disable some
  optimizations.
- Standard practice for email storage — RFC 5321 mandates the local
  part is case-sensitive, but operationally everyone treats them as
  case-insensitive, and we want lookups to be deterministic.

## Consequences

- Original casing is lost from `value_id` (it is preserved on
  `display_name` rows, which use `utf8mb4_unicode_ci`).
- The seed must lowercase before insert; that contract is enforced by
  ADR documentation, not by a CHECK constraint, so a future writer
  must follow the convention. A lint or test on the seed is a possible
  follow-up.
