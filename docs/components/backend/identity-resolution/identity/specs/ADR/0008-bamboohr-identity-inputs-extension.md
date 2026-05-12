# ADR-0008: Extend BambooHR `identity_inputs` With Person Attributes

**Status:** Accepted

## Context

`bamboohr__identity_inputs.sql` initially emits three fields:
`workEmail` (email), `employeeNumber` (employee_id), and `displayName`
(display_name). The C# service projects every BambooHR person attribute
onto the `Person` response (first/last name, department, division,
job title, status, parent email, parent id), so the dbt model must emit
them too.

## Decision

Extend the model to emit eleven fields (see source for the full list):

- Profile attributes: `firstName`, `lastName`, `department`, `division`,
  `jobTitle`, `status` → `value_full_text`.
- Org-chart pointers: `supervisorEmail` → `parent_email`,
  `supervisorEId` → `parent_id` → `value_id`.

`parent_person_id` is intentionally **not** emitted by the dbt model —
it is written by the reconciliation service (separate PR) once it
resolves `parent_email` / `parent_id` to a stable Insight `person_id`.

## Rationale

- The attributes already exist in `bronze_bamboohr.employees`; the
  identity-inputs CDC macro picks them up for free.
- Splitting `parent_email` / `parent_id` / `parent_person_id` into three
  distinct `value_type`s lets the reconciliation service work
  asynchronously without colliding with raw observations.
- BambooHR is the only connector with a strong supervisor signal;
  emitting the three parent fields here means the seed populates them
  for every active employee from day one.

## Consequences

- The seed pipeline's column-routing constants must include the new
  `value_type`s (handled in
  `seed-persons-from-identity-input.py`).
- BambooHR sync time grows slightly (more rows per employee), but the
  observation log is append-only and steady-state increases are
  bounded by attribute change frequency, not by employee count.
