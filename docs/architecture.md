# Architecture

Endpoint Management Platform — internal enterprise endpoint management for
organization-owned Windows computers.

## System overview

```
                     ┌─────────────────────────┐
                     │   Admin Browser (SPA)    │
                     │  React + TypeScript      │
                     │  dashboard/  (port 5173) │
                     └────────────┬────────────┘
                                  │ HTTPS
                     ┌────────────▼─────────────┐
                     │        ADMIN API          │
                     │  EndpointPlatform.Api     │
                     │  (port 5080)              │
                     │  authn/authz/RBAC/audit   │
                     └───────┬─────────┬────────┘
                             │         │
                 ┌───────────▼──┐   ┌──▼───────────┐
                 │ PostgreSQL   │   │    Redis     │
                 │ (55432)      │   │  (56379)     │
                 └───────▲──────┘   └──▲───────────┘
                         │             │
                     ┌───┴─────────────┴────────┐
                     │        AGENT API          │
                     │ EndpointPlatform.AgentApi │
                     │  (port 5081)              │
                     │ enrollment/heartbeat/     │
                     │ inventory/task delivery   │
                     └────────────▲─────────────┘
                                  │ HTTPS (agent-initiated only)
                 ┌────────────────┼────────────────┐
          ┌──────▼──────┐  ┌──────▼──────┐  ┌──────▼──────┐
          │ Windows     │  │ Windows     │  │ Windows     │
          │ Agent       │  │ Agent       │  │ Agent       │
          │ (service)   │  │ (service)   │  │ (service)   │
          └─────────────┘  └─────────────┘  └─────────────┘
```

## Trust boundaries

The platform has **two separate API hosts** and they are never merged
(ADR-0001):

| | Admin API | Agent API |
|---|---|---|
| Principal | Human administrator | Enrolled machine identity |
| Authentication | Session/token (Phase 3) | Device credential (Phase 1) |
| CORS | Explicit dashboard allow-list | **None** (machine-to-machine) |
| Port | 5080 | 5081 |
| Process | `EndpointPlatform.Api` | `EndpointPlatform.AgentApi` |

A stolen device credential must never reach administrative surface, and an
administrator session must never be replayable against agent endpoints. The
separation is enforced by architecture tests
(`EndpointPlatform.Architecture.Tests`), which fail the build if either host
references the other.

Agents only make **outbound** connections. No inbound port is ever opened on an
endpoint, and there is no unauthenticated server-to-agent command path.

## Solution layout

```
endpoint-platform/
├── server/
│   ├── Api/              Admin API host (trust boundary: administrators)
│   ├── AgentApi/         Agent API host (trust boundary: machine identities)
│   ├── Domain/           Pure domain model. No EF, no ASP.NET, no I/O.
│   ├── Infrastructure/   EF Core, Redis, health checks, shared host plumbing
│   ├── Migrations/       EF migrations + standalone migration/seed runner
│   └── tests/            Domain, Infrastructure, Api, AgentApi, Architecture
├── agent/
│   ├── EndpointAgent.Core/     Platform-neutral agent logic + abstractions
│   ├── EndpointAgent.Windows/  ALL Windows API usage (WMI, Win32, DPAPI)
│   ├── EndpointAgent.Service/  Windows Service composition root
│   └── tests/                  Core (any OS) + Windows integration tests
├── shared/Contracts/     Wire contracts, dependency-free
├── dashboard/            React + TypeScript + Vite SPA
├── infra/                docker-compose.yml (PostgreSQL + Redis), init scripts
└── docs/                 This documentation + ADRs
```

## Layering rules (enforced by tests)

1. `Domain` references nothing: no EF Core, no ASP.NET, no other project.
2. `Contracts` is dependency-free — every byte ships to every endpoint.
3. `Infrastructure` never references either API host.
4. The API hosts never reference each other.
5. Agent Windows-API usage lives only in `EndpointAgent.Windows`, behind
   interfaces declared in `EndpointAgent.Core`.
6. Agent assemblies do not reference process creation or the PowerShell SDK at
   all (ADR-0005).

## Data architecture

Single PostgreSQL database, schema `endpoint_platform`, snake_case naming.
One EF Core `DbContext` (modular monolith — ADR-0002). Two database roles
(ADR-0003):

- **owner** (`endpoint_owner` locally): DDL; used only by the migration job.
- **runtime** (`endpoint_app` locally): DML only; used by both APIs. Holds
  `SELECT, INSERT` — and nothing else — on `audit_log_entries`, and cannot
  perform DDL.

The audit trail is append-only through three independent layers (ADR-0004):
an EF save interceptor (developer error), revoked UPDATE/DELETE privileges
(compromised app credential), and BEFORE UPDATE/DELETE/TRUNCATE triggers
(any role short of the table owner).

Redis is cache/queue/transient state only. Nothing that must survive a restart
lives in Redis.

## Identity model

`PlatformUser` (human administrator) is deliberately distinct from the Windows
local users the platform manages on endpoints. Authorization is
**permission-based**: roles are bundles of permissions, and no authorization
decision ever inspects a role name. The permission catalogue is code
(`Permissions.cs`), seeded idempotently; built-in role grants are reconciled to
the code definition on every deployment, so out-of-band grants in the database
are reverted automatically.

## Configuration and secrets

- Strongly-typed options (`DatabaseOptions`, `RedisOptions`, `AgentOptions`)
  with data-annotation validation, validated at startup.
- Committed configuration files contain **no secrets**; connection strings are
  empty in `appsettings.json` and supplied via `ENDPOINTPLATFORM_`-prefixed
  environment variables or user-secrets. Startup fails loudly if missing.
- Local infrastructure credentials are generated per-machine into `infra/.env`
  (git-ignored); `infra/.env.example` documents the shape with placeholders.
- EF sensitive-data logging is refused outside the Development environment even
  if configured.

## Observability

- Serilog structured logging in both hosts and the agent.
- Every request gets a correlation id (`X-Correlation-Id`), validated as
  untrusted input, echoed in responses, attached to all log lines, and included
  in problem-details bodies.
- `/health/live` (process only) and `/health/ready` (PostgreSQL required,
  Redis degrades). Health responses expose check names and statuses only —
  never exception text or connection details.

## Current phase status

- **Phase 0 (foundation): complete.**
- **Phase 1 (secure enrollment + heartbeat): complete.** Enrollment tokens,
  per-device credentials, authenticated heartbeat, device list/counts in the
  dashboard. See ADR-0008.
- **Phase 2 (device inventory): complete.** Hardware/network/logged-on-user
  collection via WMI and managed APIs, pull-based refresh handshake through the
  heartbeat response, device detail page (Overview/Hardware/Network tabs).
- **Phase 3 (authentication + RBAC + audit enforcement): complete.** PBKDF2
  passwords, opaque revocable sessions (HttpOnly cookie + Bearer), permission
  policies on every admin endpoint, denial auditing, lockout and login rate
  limiting, operator bootstrap command, dashboard sign-in. See ADR-0009.
- **Phase 4 (read side, superseded by the entry above): complete.** Local
  users/groups/membership collected via System.DirectoryServices.AccountManagement
  (SID-keyed, admin flag via well-known S-1-5-32-544 membership), ingested with
  the inventory snapshot, Users/Groups tabs on the device page.
  **Mutations (create/disable/reset password/change type/membership) are NOT
  implemented**: they are gated on an elevated Windows test environment and the
  typed-task delivery channel, per the phase's own safety requirements. The
  read/write split is structural — `ILocalAccountsCollector` is read-only and a
  separate mutation abstraction will be introduced with its own tests.
- **Phase 5 (device actions) + Phase 10 task-framework core: complete.**
  Typed, pull-based, per-permission, audited task pipeline (ADR-0010);
  restart/shutdown/lock/sign-out as typed tasks via Win32 IDeviceControl;
  dashboard Actions + Tasks tabs. Pipeline verified live with the benign
  Ping type; destructive executors unit-tested, not live-fired (need an
  elevated Windows host).
- **Phase 7 (software inventory): complete.** Installed apps read from the
  Windows uninstall registry (read-only), ingested with the inventory snapshot,
  device Software tab plus a fleet-wide Software page (search, publisher filter,
  per-title install counts, and a drill-down naming the devices a title is on).
- **Milestone 1.5.0 (complete software discovery): complete.** Three sources are
  read, all read-only and all registry: the 64-bit and 32-bit (WOW6432Node)
  machine uninstall keys, and the uninstall key of every **loaded user profile
  hive** under HKEY_USERS.

  The third is the substance. The agent runs as LocalSystem, so the previous
  `RegistryHive.CurrentUser` read resolved to SYSTEM's own profile — which holds
  no installed software at all. It contributed **zero rows across the entire
  fleet** while appearing to cover per-user installs, so every application that
  installs into a user profile by default was invisible: Zoom, Teams, Discord,
  OneDrive, Docker Desktop, VS Code's user installer, and most Electron apps.
  Reading HKEY_USERS instead recovered 7 such applications on the first machine
  tested, Zoom Workplace among them.

  **What it still does not see:** users who are fully signed out have no loaded
  hive. Reading those would mean `RegLoadKey` on a profile the agent does not
  own, which can fail on a locked or roaming profile and, if a hive were left
  mounted, block that user's next logon. Under-reporting a signed-out user is the
  safer failure and is a deliberate choice, not an oversight.

  Each entry now carries its **installation scope** (`Machine`/`User`), the
  **account** a per-user install belongs to, and the **MSI product code** where
  one exists — the last being the join between an installed application and an
  approved managed package. Normalization and de-duplication live in
  `SoftwareInventoryNormalizer`, a pure class in `EndpointAgent.Core` so the
  rules are tested with fixtures rather than against whatever a CI machine
  happens to have installed. An installation's identity is
  (name, version, publisher, scope, user): the same product installed for two
  people is two installations, because uninstalling one leaves the other running.

  Consequently **fleet install counts are over DISTINCT devices**, not rows — a
  machine where three people have the same application is one device, and
  counting rows would overstate the coverage an administrator decides on.

  `Architecture` is **not** the binary's architecture and is not presented as
  such: it records which uninstall registry view the entry was found in, and
  64-bit products routinely register under WOW6432Node (Chrome, Edge and Brave
  all report `x86`). The console labels it "Found in / registry view".

- **Application execution control (enable/disable): NOT implemented, by
  decision.** The fleet runs Windows 11 Pro. AppLocker enforcement requires
  Enterprise/Education, so it is unavailable by edition. WDAC/App Control is
  available on Pro but cannot be applied under ADR-0005: the supported refresh is
  `CiTool.exe` (a process launch), policy compilation is a PowerShell cmdlet, and
  a malformed CI policy can prevent a machine booting. SRP is deprecated and
  trivially bypassed; IFEO debugger hijacking is a hostile technique. Real
  execution blocking therefore requires a dedicated application-control
  subsystem, and **catalogue withdrawal (`IsWithdrawn`) is not it** — withdrawing
  a package stops new deployments and does nothing to software already installed.
  The console must never imply otherwise.
- **Phase 4 (local user/group management) — COMPLETE (read + write).** Windows
  local accounts are now fully manageable, not just observable. Nine typed tasks
  (create/delete/enable/disable/reset-password/force-password-change/change-type/
  add-to-group/remove-from-group) run through the existing pipeline to
  `WindowsLocalAccountControl`, which calls netapi32 account-management APIs
  (`NetUserAdd`, `NetUserDel`, `NetUserSetInfo`, `NetLocalGroupAddMembers`,
  `NetLocalGroupDelMembers`) — no shell, no process launch, ADR-0005 intact and
  its scan unchanged. **Administrator status is real Windows state**: promotion
  adds the account to `BUILTIN\Administrators` (SID S-1-5-32-544) and demotion
  removes it; the dashboard reconciles against reported inventory rather than
  assuming success. Targets are addressed by SID (names are renameable).
  Passwords never persist: they go to an AES-GCM-sealed, 15-minute, one-time
  Redis entry and the task carries only a device-bound reference the agent
  redeems once (atomic GETDEL). Safety rules (built-in Administrator protected;
  last enabled administrator cannot be deleted/disabled/demoted) are enforced
  server-side against inventory AND re-checked by the agent against live Windows
  state. Authorization is permission AND device scope — new administrators are
  deny-by-default; pre-existing ones were migrated to organization-wide scope.
  Device Users/Groups tabs gained full management UI with explicit confirmation.
  See [ADR-0011](adr/0011-local-account-management.md).
- **Phase 15 (hardening / reporting / scale): complete.** Background task-expiry
  sweeper (Admin host, batched, backed by the `device_tasks(expires_at)` index)
  so tasks for offline devices still expire rather than firing late. Consolidated
  fleet report endpoint `/admin/v1/reports/summary` (device health, security and
  patch rollups, policy compliance, task throughput, active packages) surfaced on
  the dashboard. Hot query paths were indexed from the initial schema (verified:
  device lookup, task claim/expiry, audit composites). Operations runbook
  ([operations.md](operations.md)) covers backup/restore, credential and token
  rotation, offboarding, background jobs, scale and observability.
- **Phase 14 (offboarding): complete.** Offboarding a device is a *logical*,
  reversible operation: it revokes every active credential and retires the device
  (blocking heartbeat, inventory, tasks and re-enrollment), audited under
  `device.retire`. Reactivation reverses it — the machine must re-enroll for a
  fresh credential. There is deliberately **no destructive remote wipe**: an
  irreversible wipe would need its own guarded, explicitly-confirmed agent-side
  executor and is out of scope by design, not omission. Device detail Actions tab
  gains an offboard/reactivate control. Verified against real PostgreSQL:
  offboard revokes credentials + retires; a retired device is refused
  re-enrollment; reactivation restores it and re-enrollment issues exactly one
  fresh credential.
- **Phase 11 (software deployment): complete.** Approved MSI packages, deployed
  as typed `InstallPackage` tasks. Content-addressed package store (SHA-256 on
  write); the agent pulls the bytes, re-verifies the hash and the Authenticode
  signer (`WinVerifyTrust` + signer-subject pin), and installs through the
  Windows Installer service (`MsiInstallProduct`) - no process launch, no shell
  (ADR-0005 amendment). Idempotent by MSI ProductCode; reboots suppressed;
  `software.deploy` is high-risk and audited. Dashboard Packages panel (browser
  -computed SHA-256, register/deploy/withdraw). Verified live: an `InstallPackage`
  task for an already-present ProductCode was pulled, detected via the real
  `MsiQueryProductState`, and reported "already installed" - the full pipeline
  end-to-end with zero machine change. The install/download/signature paths are
  unit- and integration-tested; the real MSI detection and unsigned-file refusal
  are verified live; an actual install is never fired on the dev laptop.
- **Phase 8 (Windows Update visibility): complete.** Update history and
  reboot-pending state read via the Windows Update Agent (WUA) COM API
  (late-bound, no interop assembly) plus the reboot-pending registry keys -
  read-only, offline (local history store, never an online scan), no shell
  (ADR-0005). Ingested with the inventory snapshot (replace-wholesale, capped
  at 200 entries); failed-update count derived from Failed/Aborted results.
  Device Updates tab + fleet Updates page (reboot-pending and failed-update
  rollups). Verified live: this laptop's 26 real update-history entries
  (KB5121003, Defender intelligence updates, WindowsAppRuntime), reboot not
  pending, 2 genuine failed installs correctly counted.
- **Phase 12 (security posture): complete.** Defender/firewall/Secure Boot/TPM/
  BitLocker/local-admin-count read (read-only WMI + registry), null = "unknown"
  (never a false negative), compliance score over readable checks only, device
  Security tab + fleet Security page with score distribution. Verified live:
  this laptop scores 100% on readable checks; TPM/BitLocker correctly "unknown"
  unelevated.
- **Phase 9 (services & processes): complete.** Read-only service list and a
  capped point-in-time process snapshot in inventory (device Services/Processes
  tabs); controlled actions (service start/stop/restart, process terminate with
  an expected-image guard) as typed tasks via ServiceController/Process - no
  shell (ADR-0005, refined guard). Read side verified live (306 services, 60
  processes from this laptop); control executors unit-tested, not live-fired.
- **Phase 6 (policy engine v1): complete.** Desired-state architecture:
  Policy + immutable PolicyVersion (historical versions never mutated) +
  PolicyAssignment + PolicyComplianceResult; ScreenLockTimeout policy type;
  agent pulls effective policies, evaluates (read-only, never remediates),
  reports Compliant/NonCompliant/Unknown with deviations; heartbeat
  PoliciesPending handshake; Policies dashboard page. Verified live: create ->
  assign -> agent evaluated this laptop's real screen-lock -> reported Unknown
  with an honest deviation, audited. (EF key-generation fix: child entities
  added via a tracked parent's navigation now use ValueGeneratedNever so they
  insert rather than mis-update.)
- **Phase 13 (device groups): complete.** Static device groups with audited
  membership; policies can target a group, and GetEffectivePolicies resolves a
  device's policies through its group memberships (verified: group-targeted
  policy reaches members only, and leaves a device when it is removed). Groups
  dashboard page with membership management.
