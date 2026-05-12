# ADR-0006: Display-Name Split Fallback for First/Last Name

**Status:** Accepted

## Context

BambooHR observations carry `displayName`, `firstName`, and `lastName`
as separate fields. Other connectors (Cursor, Claude Admin) only emit
`display_name`. The response schema needs `first_name` / `last_name`
populated for downstream callers regardless of which connector is the
source of truth.

## Decision

When the assembler finds no `first_name` or `last_name` observation, it
falls back to `DisplayNameSplitter.Split(displayName)`:

1. `"Last, First"` (comma-separated) → `(First, Last)`.
2. `"First Rest"` (space-separated) → `(First, Rest)` where `Rest`
   keeps any middle names.
3. Single token → `(token, "")`.
4. Empty / whitespace → `("", "")`.

## Rationale

- The seed cannot back-fill these fields universally — Cursor and
  Claude Admin do not provide them.
- The split is best-effort and explicitly documented; downstream
  callers that need authoritative first/last must read BambooHR
  directly.
- BambooHR's `displayName` formats as `"Last, First"`; Cursor uses
  `"First Last"`. The two-step heuristic covers both common shapes.

## Consequences

- Names with multiple commas or unusual punctuation may split
  incorrectly. The split is unit-tested for the canonical formats but
  not for every edge case.
- A future PR may wire connector-specific splitters; the current shape
  is good enough for Phase 1.
