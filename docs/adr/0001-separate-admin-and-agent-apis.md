# ADR-0001: Separate Admin API and Agent API hosts

Status: accepted (Phase 0)

## Context

The platform serves two completely different kinds of caller: human
administrators (browser, session credentials, RBAC) and enrolled machines
(service process, device credentials, no human). A single API surface serving
both is the classic shape of MDM vulnerabilities: one authentication-bypass or
confused-deputy bug lets a device credential reach administrative operations.

## Decision

Two separate ASP.NET Core processes: `EndpointPlatform.Api` (admin, port 5080)
and `EndpointPlatform.AgentApi` (agent, port 5081). They share the Domain,
Infrastructure and Contracts assemblies but never reference each other. Each
configures its own authentication scheme and endpoints. CORS exists only on
the Admin API (explicit allow-list); the Agent API has none.

## Consequences

- A flaw in agent request handling is physically unable to expose an
  administrative endpoint, and vice versa.
- Deployment can firewall them differently: the Agent API is the only surface
  exposed to the endpoint network.
- Two processes to run and monitor instead of one; shared plumbing lives in
  Infrastructure to avoid duplication.
- Enforced continuously by `EndpointPlatform.Architecture.Tests` (no
  cross-references) and API tests (agent routes 404 on the admin host).
