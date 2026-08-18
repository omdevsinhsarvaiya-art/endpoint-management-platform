# Endpoint Management Platform

Internal enterprise endpoint management for organization-owned Windows
computers: central management backend, web administrator dashboard, and a
Windows endpoint agent.

**Status: Phases 0–3 complete** (foundation; secure enrollment + heartbeat;
device inventory; authentication + RBAC + audit). See
[docs/architecture.md](docs/architecture.md) for the design and
[docs/adr/](docs/adr/) for the decisions behind it.

| Component | Tech | Where |
|---|---|---|
| Admin API (administrators) | ASP.NET Core, .NET 10 | `server/Api` · http://localhost:5080 |
| Agent API (machine identities) | ASP.NET Core, .NET 10 | `server/AgentApi` · http://localhost:5081 |
| Domain / Infrastructure | C#, EF Core 10, PostgreSQL, Redis | `server/Domain`, `server/Infrastructure` |
| Migration + seed runner | .NET console | `server/Migrations` |
| Windows agent | .NET 10 Windows Service | `agent/` |
| Dashboard | React + TypeScript + Vite | `dashboard/` · http://localhost:5173 |
| Dev infrastructure | Docker Compose (PostgreSQL 17, Redis 8) | `infra/` |

## Quick start

Prerequisites: .NET SDK 10.0.4xx, Node.js 24 LTS, Docker Desktop (Linux
containers), Git. All commands from the repository root on Windows PowerShell.

```powershell
# 1. Local infrastructure credentials (one-time; file is git-ignored)
Copy-Item infra\.env.example infra\.env
#    -> edit infra\.env, replace every CHANGE_ME with a generated secret
#       (generation snippet is inside the file)

# 2. Start PostgreSQL (55432) + Redis (56379)
docker compose -f infra\docker-compose.yml up -d

# 3. Build and test
dotnet restore EndpointPlatform.slnx
dotnet build EndpointPlatform.slnx
dotnet test EndpointPlatform.slnx          # Docker must be running

# 4. Migrate + seed (uses the OWNER db role)
$cfg = @{}; Get-Content infra\.env | ? { $_ -match '=' } | % { $p = $_ -split '=',2; $cfg[$p[0]] = $p[1] }
$env:ENDPOINTPLATFORM_Database__ConnectionString = "Host=localhost;Port=$($cfg['POSTGRES_PORT']);Database=$($cfg['POSTGRES_DB']);Username=$($cfg['POSTGRES_SUPERUSER']);Password=$($cfg['POSTGRES_SUPERUSER_PASSWORD'])"
$env:ENDPOINTPLATFORM_Database__RuntimeRoleName = $cfg['POSTGRES_APP_USER']
dotnet run --project server\Migrations\EndpointPlatform.Migrations.csproj

# 5. Run the APIs (uses the RESTRICTED db role) - one terminal each
$env:ENDPOINTPLATFORM_Database__ConnectionString = "Host=localhost;Port=$($cfg['POSTGRES_PORT']);Database=$($cfg['POSTGRES_DB']);Username=$($cfg['POSTGRES_APP_USER']);Password=$($cfg['POSTGRES_APP_PASSWORD'])"
$env:ENDPOINTPLATFORM_Redis__ConnectionString = "localhost:$($cfg['REDIS_PORT']),password=$($cfg['REDIS_PASSWORD'])"
dotnet run --project server\Api\EndpointPlatform.Api.csproj        # terminal 1
dotnet run --project server\AgentApi\EndpointPlatform.AgentApi.csproj  # terminal 2

# 6. Dashboard
cd dashboard; npm install; npm run dev     # http://localhost:5173
```

Health checks: `GET /health/live`, `GET /health/ready` on both APIs. Swagger
UI at `/swagger` (Development only). Full instructions and troubleshooting:
[docs/development.md](docs/development.md).

## Security posture (Phase 0)

- Two API hosts = two trust boundaries; enforced by architecture tests.
- Append-only audit trail: EF interceptor + restricted DB role + database
  triggers (UPDATE/DELETE/TRUNCATE all rejected; verified against live
  PostgreSQL, including as the schema owner).
- Permission-based RBAC catalogue seeded and reconciled on every deployment;
  out-of-band role grants are reverted automatically.
- No secrets in the repository; no default credentials anywhere. Startup
  fails with instructions if configuration is missing.
- The agent cannot launch processes or run PowerShell — its assemblies do not
  reference process creation, enforced by tests.
- Admin API: PBKDF2 passwords, revocable HttpOnly-cookie sessions, permission
  policies on every endpoint, denial auditing, lockout + login rate limiting.
  Bootstrap the first administrator with:
  `ENDPOINTPLATFORM_Bootstrap__AdminEmail=... ENDPOINTPLATFORM_Bootstrap__AdminPassword=... dotnet run --project server\Migrations -- bootstrap-admin`

## Documentation

- [Architecture](docs/architecture.md)
- [Threat model](docs/threat-model.md)
- [Agent protocol](docs/agent-protocol.md)
- [Development guide](docs/development.md)
- [Deployment](docs/deployment.md)
- [Decision records](docs/adr/)
