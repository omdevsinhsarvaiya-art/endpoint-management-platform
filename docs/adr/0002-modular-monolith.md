# ADR-0002: Modular monolith with one database and one DbContext

Status: accepted (Phase 0)

## Context

Target scale is 10–100 endpoints initially, with an architecture that must
survive 1,000–10,000 without a rewrite. Microservices at this scale add
distributed-system failure modes (partial writes, eventual consistency,
service discovery) while removing the tools that prevent them (foreign keys,
transactions).

## Decision

A modular monolith: one solution, one PostgreSQL database, one EF Core
`DbContext`, one schema (`endpoint_platform`). Modularity is enforced at the
assembly level (Domain / Infrastructure / two API hosts) by architecture
tests, not at the network level. The two API hosts are separate processes for
trust-boundary reasons (ADR-0001), not for scale.

## Consequences

- Cross-module consistency is a database transaction, not a saga.
- Referential integrity (device → audit, role → permission) is enforced by
  the database.
- Scaling path without rewrite: the API hosts are stateless and horizontally
  scalable behind a load balancer; Redis already backs shared transient state;
  PostgreSQL scales vertically far beyond 10,000 endpoints for this workload.
- If a genuine extraction need appears later, module boundaries already map to
  assemblies with tested dependency directions.
