# Deployment

Status: **Phase 0.** The platform is not production-deployable yet — it has no
authentication. This document records the deployment shape the code is already
built around, so decisions made now (two DB roles, separate migration job,
strict configuration) survive into real deployment work in Phase 15.

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
| `ENDPOINTPLATFORM_Cors__AllowedOrigins__0` | Admin API | dashboard origin; API refuses to start without it |
| `ASPNETCORE_ENVIRONMENT` | all | `Production` outside dev |
| `ASPNETCORE_URLS` | APIs | listen addresses |

No configuration file in the repository contains a credential; there is
nothing to rotate out of source control.

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
- Signed binaries and the self-update channel are Phase 15 / agent-update
  work; do not distribute unsigned builds beyond a lab.

## Backup / restore

PostgreSQL is the only stateful store. `pg_dump` of the `endpoint_platform`
schema captures everything including audit history. Redis is disposable by
design. Full runbooks are Phase 15 deliverables.
