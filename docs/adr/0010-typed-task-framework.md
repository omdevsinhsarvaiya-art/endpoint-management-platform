# ADR-0010: Typed task framework and device actions (Phases 5 & 10 core)

Status: accepted

## Context

Several phases need the server to make an endpoint *do* something: restart/lock
(Phase 5), start a service or kill a process (Phase 9), install a package
(Phase 11), run an approved script (Phase 10 full). The spec is emphatic: no
`POST /execute-command` that takes an arbitrary string, and no unauthenticated
server-to-agent execution.

## Decision

A single **pull-based, typed** task pipeline underpins all of them.

- **Closed task-type enum** (`DeviceTaskType`). There is no "arbitrary command"
  member. Each type maps to exactly one reviewed agent-side executor with a
  typed, server-validated payload. Adding capability = adding an enum member +
  executor + tests; a member with no `DeviceTaskCatalog` entry cannot be queued
  (`Require` throws — fail closed).
- **Pull delivery.** The agent's authenticated heartbeat returns `TasksPending`;
  the agent then `GET /agent/v1/tasks` to claim queued tasks and
  `POST .../{id}/result` to report. The server never connects to an agent; no
  inbound port on the endpoint.
- **Per-permission authorization.** Each type carries a required permission
  (restart→`device.restart`, etc.), checked at the Admin endpoint before queueing.
- **One-way, guarded lifecycle.** Queued→Delivered→(Succeeded|Failed), or
  Queued→(Cancelled|Expired). A result is accepted only in Delivered state, and
  the task id is scoped to the authenticated device — a stolen credential cannot
  forge or overwrite another device's outcome, nor rewrite a terminal one.
  Concurrent claims are resolved with `xmin` optimistic concurrency.
- **Audited end to end.** Queue (PlatformUser actor) and result (Agent actor)
  both write audit entries; high-risk types are flagged for UI confirmation.
- **Expiry.** Each type has a TTL so a restart requested an hour ago does not
  fire when a laptop finally checks in.

Phase 5 device actions (restart/shutdown/lock/sign-out) are the first executors,
delivered through this pipeline via `IDeviceControl` (Win32: InitiateSystem-
ShutdownEx / LockWorkStation / ExitWindowsEx — never a shell string, ADR-0005).

## Testing & safety note

The pipeline is verified live end-to-end with the benign `Ping` type
(claim → execute → result → audit, all green on the running stack). The
destructive executors (restart/shutdown/sign-out) are built, isolated in
`EndpointAgent.Windows`, and unit-tested against a fake `IDeviceControl`; they
are deliberately **not** live-fired against the development laptop. They require
LocalSystem elevation in production and an elevated Windows test host for live
verification — the same gate the spec sets for high-risk operations.

`RunApprovedScript` remains deferred to Phase 10-full: it needs the
hash+signature+approval package model (Phase 11) as its trust anchor.

## Consequences

- Every current and future remote capability flows through one auditable,
  permission-checked, replay-resistant channel with no arbitrary-command surface.
- The agent gains a general executor-registry; new task types are additive.
