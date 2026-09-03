# Deployment

Status: **demo-deployable, not production-hardened.** Administrator
authentication (ADR-0009) and agent authentication (ADR-0008) are implemented.
What is still outstanding for production is hardening rather than function:
managed secret storage, full CSP, backup automation and observability. The
[AWS demo topology](#aws-demo-topology) below is a deliberately small
single-host shape for demonstrating the platform, not a production design.

## Topology

- One host (or container pair) runs the two API processes:
  - Admin API — reachable by administrators/dashboard only.
  - Agent API — reachable by endpoints; this is the only surface exposed to
    the endpoint network.
- PostgreSQL 17.x and Redis 8.x as backing services.
- The dashboard is a static build (`dashboard/dist`) served by any web server,
  pointed at the Admin API.
- TLS terminates in front of both APIs (reverse proxy or Kestrel certs). The
  hosts already enable HSTS + HTTPS redirection outside Development.

## Order of operations per deployment

1. Run the **migration job** once, with the owner database credential:
   `EndpointPlatform.Migrations` applies migrations, re-applies runtime role
   grants, reseeds reference data (idempotent). Non-zero exit aborts the
   deployment.
2. Start/replace the API processes, configured with the **runtime** database
   credential (restricted role).

## Configuration contract

Everything is environment variables with the `ENDPOINTPLATFORM_` prefix:

| Variable | Used by | Notes |
|---|---|---|
| `ENDPOINTPLATFORM_Database__ConnectionString` | APIs (runtime role), migration job (owner role) | required |
| `ENDPOINTPLATFORM_Database__RuntimeRoleName` | migration job | enables grant application |
| `ENDPOINTPLATFORM_Redis__ConnectionString` | APIs | required |
| `ENDPOINTPLATFORM_SecretProtection__Key` | **both APIs** | **required; identical value in both.** See below |
| `ENDPOINTPLATFORM_Cors__AllowedOrigins__0` | Admin API | dashboard origin; API refuses to start without it |
| `ASPNETCORE_ENVIRONMENT` | all | `Production` outside dev |
| `ASPNETCORE_URLS` | APIs | listen addresses |

The Windows agent is configured separately (it is not an
`ENDPOINTPLATFORM_`-prefixed process):

| Variable | Used by | Notes |
|---|---|---|
| `ENDPOINTAGENT_Agent__ServerBaseUrl` | Windows agent | Agent API base URL; **must be `https://` outside a local lab** |

Optional — every one of these has a working default, listed because they matter
for a demo host rather than because they must be set:

| Variable | Used by | Notes |
|---|---|---|
| `ENDPOINTPLATFORM_PackageStorage__Directory` | both APIs | uploaded package content; **needs a persistent volume**, or deployed installers vanish on restart |
| `ENDPOINTPLATFORM_Bootstrap__AdminEmail` | migration job | only for `-- bootstrap-admin`, to create the first administrator |
| `ENDPOINTPLATFORM_Bootstrap__AdminPassword` | migration job | same; minimum 12 characters. Supply at run time, never persist |
| `ENDPOINTPLATFORM_AdminAuth__*` | Admin API | session lifetime, lockout threshold, login rate limit |
| `ENDPOINTPLATFORM_AgentServer__*` | Agent API | heartbeat interval, offline threshold |

No configuration file in the repository contains a credential; there is
nothing to rotate out of source control.

### `ENDPOINTPLATFORM_SecretProtection__Key` (required)

This key protects the short-lived secrets used to deliver a new or reset local
Windows account password to an endpoint. The password itself is never stored in
PostgreSQL, never placed in the task payload, and never written to a log; it is
sealed with AES-GCM, held in Redis under a one-time device-bound reference, and
redeemed exactly once.

**The Admin API seals and the Agent API redeems, so the two processes must be
given the same key.** They are separate processes — separate containers in the
demo topology — and each derives its cipher from this configuration value alone.

The failure mode is why this deserves its own section: **when the key is unset,
each process silently generates its own random key at startup.** Nothing fails
and nothing is logged. A single-process deployment works by luck. A two-process
deployment starts cleanly, serves every page, and then fails the first
create-user or reset-password task with *"the ephemeral secret could not be
unsealed"* — an error that points at Redis rather than at configuration. Set it
explicitly, always.

Requirements:

- Cryptographically random, base64-encoded **32 bytes (256-bit)**. A value that
  is not 32 bytes is rejected at startup.
- **Identical** in the Admin API and the Agent API.
- Supplied through the environment or a secrets manager — never committed,
  never logged, never placed in `appsettings.json`.
- Held in `infra/.env` as `SECRET_PROTECTION_KEY` for local runs
  (`scripts/run-local.ps1` maps it to the `ENDPOINTPLATFORM_` variable).

Generate one with:

```powershell
$rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
$b = New-Object byte[] 32; $rng.GetBytes($b); [Convert]::ToBase64String($b)
```

Rotating the key invalidates only secrets that are in flight at that moment.
They fail their task safely and can be re-issued; no stored data is affected.

## AWS demo topology

**This is the DEMO shape, not a production architecture.** It is one EC2 host
running Docker Compose, chosen because it is the smallest thing that
demonstrates the whole platform end to end. It has no autoscaling, no managed
database, no orchestrator and no multi-AZ story, and it is not intended to
acquire them — production topology is a separate exercise.

```
                        AWS EC2
                  ┌─────────────────────────┐
                  │ Docker Compose          │
                  │                         │
   public :443 ──▶│ nginx  (TLS, /api ──▶ Admin API)
                  │   └── Dashboard (static dist)
                  │ Admin API   :8080  internal
                  │ Agent API   :8081  published
                  │ PostgreSQL  :5432  internal only
                  │ Redis       :6379  internal only
                  └────────────┬────────────┘
                               │  HTTPS (outbound from the endpoint)
                               ▼
                       Windows Endpoint
                               │
                        Windows Agent  (native Windows service, LocalSystem)
                               │
                               ▼
                        Windows APIs  (netapi32 / SAM)
```

**Management plane on AWS.** Dashboard, Admin API, Agent API, PostgreSQL and
Redis all run on the one EC2 host.

**PostgreSQL and Redis are private.** They are on the compose-internal network
with no published ports — reachable by the two APIs and by nothing else. They
are never exposed to the internet, and the demo does not need them to be.

**nginx is the only public HTTPS entry point.** It terminates TLS, serves the
dashboard's static build, and reverse-proxies `/api/*` to the Admin API.

**The dashboard and the Admin API share one origin.** This is a requirement,
not a preference. The dashboard calls `/api/...` as a relative path, and the
session cookie is issued with the `__Host-` prefix, `Secure`, and
`SameSite=Strict`. The `__Host-` prefix forbids a `Domain` attribute, so the
cookie is pinned to exactly the host that set it. Serving the dashboard from a
different hostname than the API breaks sign-in outright. The nginx proxy is
what keeps them on one origin.

**HTTPS is mandatory.** `Secure` cookies are only exempted for `localhost`.
Over plain HTTP on a public hostname the browser discards the session cookie
and no one can sign in.

**The Windows agent is NOT containerized and must not be.** It manages local
Windows accounts through `netapi32` and requires real Windows elevation
(LocalSystem as a service). It stays installed natively on each managed
Windows endpoint.

**The agent connects outbound.** It dials the AWS Agent API over HTTPS; AWS
never initiates a connection to the endpoint. Only the Agent API port needs to
be reachable from the endpoint network — no inbound firewall rule, VPN or
public IP is required on the Windows machine.

**Privileged Windows work stays local.** AWS only ever queues a typed task. The
decision to act, the Windows API call, and the verification of the resulting
state all happen on the endpoint itself. Nothing in this topology gives the
cloud direct control of the machine.

Agent certificate validation is enforced: the "accept any certificate" escape
hatch is gated on both an explicit option and a Debug build, so a Release agent
requires a genuinely trusted certificate. Use a publicly trusted certificate
(ACM behind the proxy, or Let's Encrypt) — a self-signed certificate will be
rejected, and that check must not be weakened to make the demo easier.

### Demo configuration values

| Variable | Value for the demo |
|---|---|
| `ENDPOINTPLATFORM_Cors__AllowedOrigins__0` | `https://<demo-host>` |
| `ENDPOINTPLATFORM_SecretProtection__Key` | one generated key, **same in both APIs** |
| `ENDPOINTPLATFORM_Database__ConnectionString` | `Host=postgres;...` (compose service name) |
| `ENDPOINTPLATFORM_Redis__ConnectionString` | `redis:6379,password=...` |
| `ASPNETCORE_ENVIRONMENT` | `Production` |
| `ENDPOINTAGENT_Agent__ServerBaseUrl` | `https://<demo-host>:8081` (on the Windows endpoint) |

Compose file: `infra/docker-compose.demo.yml`. It reads the same `infra/.env`
contract as local development, plus the demo host name.

## Windows agent

- `EndpointAgent.Service` runs as a Windows Service (`EndpointPlatformAgent`),
  LocalSystem.
- Minimum OS: Windows 10 1809 / Server 2019.
- Configuration: `appsettings.json` next to the binary
  (`Agent:ServerBaseUrl`, heartbeat interval). No credential material in the
  file — enrollment (Phase 1) stores the device credential DPAPI-protected.
- Install (elevated):
  ```powershell
  dotnet publish agent\EndpointAgent.Service\EndpointAgent.Service.csproj -c Release -p:PublishAgent=true -o "C:\Program Files\EndpointPlatformAgent"
  sc.exe create EndpointPlatformAgent binPath= "C:\Program Files\EndpointPlatformAgent\EndpointAgent.Service.exe" start= auto obj= LocalSystem
  sc.exe start EndpointPlatformAgent
  ```
- Agent releases and remote self-update are shipped, not future work: upload the
  built MSI on the dashboard's Agent page, publish it, and queue `UpdateAgent`
  per device. See [agent-updates.md](agent-updates.md).
- **Release trust mode** — `ENDPOINTPLATFORM_AgentReleases__TrustMode`:
  `Internal` (the default, and what this deployment runs) or `Public`. Internal
  requires no Authenticode certificate and reads no signature; integrity is the
  server-computed SHA-256, re-verified over the stored bytes at publish and
  again by the agent over the downloaded bytes before install, over HTTPS, under
  authorization and audit. `Public` additionally requires an Authenticode
  signature whose subject matches
  `ENDPOINTPLATFORM_AgentReleases__ExpectedSignerSubject`, and the API refuses
  to start in that mode without one configured.
- An Internal build is trusted by *this platform, for these machines* — not by
  Windows. SmartScreen, AppLocker and WDAC will treat the MSI as an unsigned
  installer, because it is one. That, not publishability, is what a code-signing
  certificate buys; distributing beyond the managed estate is the case that
  needs `Public`, which is a configuration change rather than a code change.

## Backup / restore

PostgreSQL is the only stateful store. `pg_dump` of the `endpoint_platform`
schema captures everything including audit history. Redis is disposable by
design. Full runbooks are Phase 15 deliverables.
