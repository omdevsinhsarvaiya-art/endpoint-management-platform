# Threat model

This platform ships privileged software to every managed endpoint and
concentrates control over those endpoints in one place. That makes the platform
itself the highest-value target in the estate: whoever controls it controls
every enrolled machine. Design decisions below are driven by that fact.

Status: written at Phase 0. Sections marked *(future)* describe controls whose
implementation phase has not arrived yet; they are recorded now so the design
does not paint itself into a corner.

## Assets

| Asset | Why it matters |
|---|---|
| Agent fleet | Runs as LocalSystem on every managed computer |
| Device credentials | Each proves an endpoint's identity to the platform |
| Admin credentials/sessions | Grant control over devices via the platform |
| Audit trail | The evidence record for every privileged action |
| Platform database | Devices, identities, policies, audit |
| Enrollment tokens *(future)* | Convert an unknown machine into a trusted device |
| Task/script pipeline *(future)* | Executes privileged actions on endpoints |

## Adversaries considered

1. **External attacker** with network access to the APIs but no credentials.
2. **Malicious or compromised endpoint** holding a valid device credential.
3. **Compromised administrator workstation / phished admin** (browser-level).
4. **Limited insider**: a low-privileged platform user (Helpdesk, Auditor)
   attempting to exceed their role.
5. **Application-level compromise**: an attacker who has obtained the API
   process's database credential (e.g. via SSRF/RCE in the app).

A malicious DBA / full database-host compromise is **out of scope** for
database-level controls (see Known limitations).

## Key threats and mitigations

### T1. Cross-boundary credential replay
*An agent credential used against admin endpoints, or vice versa.*

- Admin API and Agent API are separate processes on separate ports with
  separate authentication schemes (ADR-0001).
- Architecture tests fail the build if either host references the other, and
  API tests assert agent routes 404 on the admin host.

### T2. Audit trail tampering
*An attacker (or embarrassed insider) rewrites history.*

Three independent layers, all verified by automated tests against real
PostgreSQL and manually against the live database:

1. `AuditImmutabilityInterceptor` — fails fast on accidental mutation in code.
2. The runtime database role holds only `SELECT, INSERT` on
   `audit_log_entries` (verified: `UPDATE`/`DELETE`/`TRUNCATE`/DDL all denied).
3. `BEFORE UPDATE/DELETE/TRUNCATE` triggers raise exceptions **regardless of
   role** (verified against the owner role, including the TRUNCATE path that
   row-level triggers do not cover).

The domain type itself exposes no mutators (reflection-verified by test).

### T3. Secret leakage through logs and telemetry
- Serilog request logging never records query strings or bodies.
- Health endpoints return check names/statuses only — the default writer's
  exception text (which can include connection strings) is replaced.
- Problem-details responses carry a correlation id, never stack traces.
- EF sensitive-data logging is refused outside Development by code, not
  convention.
- The domain's `PlatformUser` only ever holds an encoded hash; there is no
  code path that accepts a plaintext password into an entity.

### T4. Header/log injection via client-controlled values
- `X-Correlation-Id` is the only client value echoed into headers/logs. It is
  length-capped (128) and restricted to `[A-Za-z0-9._-]`; anything else is
  discarded in favour of the server-generated id. Unit tests cover CRLF,
  control characters and over-length payloads.

### T5. Cross-origin abuse of the credentialed Admin API
- CORS is an explicit origin allow-list; the API refuses to start without one.
  Wildcard + credentials is impossible by construction. Tests assert that
  non-listed origins receive no CORS headers.
- The Agent API has no CORS at all — browsers have no business there.

### T6. Privilege escalation within the platform
- Authorization is permission-based; role names are never checked in code.
- Built-in role grants are reconciled against the code catalogue on every
  deployment — a grant added directly in the database is **reverted** (covered
  by an integration test).
- Auditor is provably read-only and Helpdesk provably cannot perform
  high-impact operations (unit tests pin both).
- Separation of duties: IT Administrator cannot manage platform users/roles.

### T7. Compromised application database credential (adversary 5)
- The runtime role cannot alter audit history (T2 layers 2–3).
- The runtime role has no DDL: it cannot drop the audit triggers, create
  tables, or modify the schema.
- The migration job runs under the owner role in a separate, short-lived
  process — the owner credential is never present in an API process.

### T8. Supply-chain / dependency risks
- Central package management pins every dependency version in one reviewed
  file (`Directory.Packages.props`) with transitive pinning enabled.
- The shared `Contracts` assembly (shipped to every endpoint) is
  dependency-free, enforced by test.

### T9. Agent as an injection vector *(largely future)*
The agent will run as LocalSystem, so its request-handling surface is a
privileged boundary:

- The agent codebase cannot launch processes or embed PowerShell — the
  assemblies do not reference process creation at all, enforced by tests
  (ADR-0005). Windows work is done through APIs with no command line.
- **(Implemented, Phase 1)** Enrollment: scoped, expiring, limited-use tokens
  stored only as hashes; per-device credentials; no fleet-wide shared secret;
  refusals indistinguishable on the wire and audited with reasons; optimistic
  concurrency on the last remaining use. See ADR-0008.
- **(Implemented, Phase 1)** Device credential stored DPAPI-protected at
  machine scope with extra entropy; directory ACL'd to SYSTEM/Administrators
  when elevated; corrupt blobs degrade to re-enrollment, never crash.
- **(Implemented, Phase 1)** Re-enrollment revokes prior credentials; retired
  devices fail closed on both heartbeat and re-enrollment.
- *(Phase 10)* Only typed tasks; scripts require hash + signature + recorded
  approval. No arbitrary command endpoint will exist.
- The agent refuses to disable TLS certificate validation outside Debug builds
  (enforced in options validation, not convention).

## Known limitations (accepted at this phase)

| Limitation | Risk | Planned remedy |
|---|---|---|
| Audit records stay on the same PostgreSQL host | Table owner/superuser can drop triggers and rewrite history | Phase 15: ship audit stream to append-only external storage |
| Enrollment token is a bearer bootstrap secret | Holder of an unused token can enroll a hostile machine into management | Expiry ≤30 days, use caps, revocation, one-time display; operational handling guidance in deployment docs |
| Development runs plain HTTP on localhost | Local traffic unencrypted | TLS termination is a deployment concern; HSTS+redirect already active outside Development |
| Login rate limiting is in-memory, per instance | Resets on restart; not shared across replicas | Phase 15: Redis-backed limiter (account lockout already covers the account dimension) |
| Single-node PostgreSQL/Redis | Availability, not security | Phase 15 |

## Standing rules (all phases)

1. Never store plaintext passwords; never log secrets, tokens or key material.
2. Every privileged mutation produces an audit event before it is reported
   successful.
3. Authorization decisions happen server-side, against permissions.
4. All input — including from enrolled agents — is untrusted and validated.
5. The agent initiates all connections; no listening sockets on endpoints.
6. No arbitrary command execution surface, on server or agent, ever.

## Local administrator posture (Milestone 11b)

**What it answers.** Whether an endpoint's interactive accounts are standard
users, derived from the accounts the agent reports rather than stored as a
cached verdict — the facts it needs already live on the account rows, and a
second copy could disagree with them.

**Three verdicts.** `Unknown` is distinct from `Compliant` and never collapses
into it. A machine that has reported nothing is not evidence of good posture,
and rendering it as clean would overstate how much of the estate has been
checked. The API returns a null `lastReportedAt` alongside it, so the absence of
evidence is visible rather than implied.

**Two exclusions**, both answering whether a person could actually sign in and
act as an administrator:

- *Disabled* accounts confer nothing interactively. Still reported: a disabled
  administrator is one setting away from being live.
- *Built-in* accounts are excluded because the platform refuses to delete or
  disable RID 500 precisely so an organization cannot lock itself out. Counting
  an account we protect by policy would mark every Windows machine permanently
  non-compliant with no available remedy, and a finding nobody can act on is not
  a finding.

Excluded is not hidden — every discounted account is returned with its reason.

**Built-ins are matched by RID, never by name.** Renaming the built-in
Administrator is standard hardening and the names are localized, so a name-based
rule would fail on a German install or after a rename — silently, in the
direction of excusing an administrator. A malformed SID resolves to *not*
built-in, which counts the account towards the verdict rather than excusing it:
the SID arrives from an endpoint, so it is input, and a garbled report must not
be able to produce a clean bill of health.

**It evaluates; it does not remediate.** No path in 11b removes an account from
Administrators, and the console offers no control to do so. Remediation is
Milestone 12, behind its own explicit authorization.

**Audit.** `localuser.posture.changed` is raised on a *transition*, detected at
inventory ingest where the previous and new account sets are both in hand. There
is deliberately no per-evaluation event: the verdict is derived on read, so
evaluating is not a mutation, and an endpoint reporting every few minutes would
otherwise bury real transitions under thousands of identical rows in an
append-only store. The first report after enrollment is not treated as a
transition — that is evidence arriving, not the machine changing.

**Limitation, stated in the payload itself.** Administrator rights held only
through a nested group are not detected; membership is read from direct
membership of the local Administrators group. The limitation travels with the
verdict so a caller acting on it can see its scope without reading this
document.
