# ADR-0002: Read From the MariaDB `persons` Table

**Status:** Accepted

## Context

The Rust stub queries `bronze_bamboohr.employees` in ClickHouse. PR #214
introduces a service-owned MariaDB `persons` table (append-only
observation log) populated from `identity_inputs` for every connector,
not just BambooHR. Reading from the bronze snapshot would limit the
service to one source and re-introduce the first-install crash-loop the
Rust code defends against.

## Decision

`insight-identity` reads exclusively from MariaDB `persons` via
`MySqlConnector`. ClickHouse access is removed from the service; bronze
tables remain the upstream input to the dbt pipeline that feeds
`identity_inputs`, but the service does not see them.

## Rationale

- Multi-source coverage is a requirement and `persons` already unifies
  every connector behind one schema.
- The seed pipeline (`seed-persons-from-identity-input.py`) keeps
  `persons` warm; the service does not need to know how rows arrive.
- Removing the ClickHouse client cuts the dependency surface and the
  config block.

## Consequences

- The C# service depends on MariaDB being reachable at startup; the
  Helm readiness probe wires this through `/health`.
- A first-install cluster needs the seed to run before lookups succeed;
  the readiness probe still passes (DB is reachable, table is empty,
  every lookup just returns 404). This matches the Rust stub's
  "empty store" behaviour.
- Future multi-database deployments are out of scope; one MariaDB URL
  per service instance.
