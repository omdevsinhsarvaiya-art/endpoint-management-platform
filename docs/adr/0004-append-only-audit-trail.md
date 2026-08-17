# ADR-0004: Append-only audit trail with three enforcement layers

Status: accepted (Phase 0)

## Context

Every privileged mutation must be auditable, and the audit record is only
worth something if it cannot be quietly edited afterwards. "The application
promises not to" is not a control. An audit trail that can be rewritten is
worse than none: it produces confident, false evidence.

## Decision

`audit_log_entries` is append-only, enforced three times over, weakest layer
first:

1. **In process** — `AuditLogEntry` exposes no public setters or mutating
   methods (reflection-verified by test); `AuditImmutabilityInterceptor`
   throws on any tracked Modified/Deleted audit entity. Catches developer
   error with a clear exception.
2. **Privileges** — the runtime database role holds `SELECT, INSERT` and
   nothing else on the table (ADR-0003). Defeats an attacker holding the
   application's database credential.
3. **Triggers** — `BEFORE UPDATE`, `BEFORE DELETE` (row-level) and
   `BEFORE TRUNCATE` (statement-level — TRUNCATE bypasses row triggers)
   raise exceptions regardless of caller role. Defeats every role short of the
   table owner. All three verified by integration tests against real
   PostgreSQL and manually against the live development database, including
   as the owner role.

Design details:

- Actor/device/target display values are denormalised into the entry so the
  trail stays readable after accounts are renamed or deleted.
- `previous_state`/`new_state` are `jsonb` (queryable), redacted by the caller
  before construction; `source_ip` is `inet` (subnet-containment queries).
- `Denied` is a distinct result from `Failure` so permission denials are
  alertable as a security signal rather than drowned in operational errors.

## Consequences

- Corrections are new entries referencing the old, never edits.
- The table grows monotonically; retention/archival is a Phase 15 concern and
  must archive-then-drop partitions via the owner role, not DELETE.
- Accepted residual risk: the table owner / a superuser can drop the triggers.
  Off-box append-only shipping addresses that in Phase 15 (threat model,
  "Known limitations").
