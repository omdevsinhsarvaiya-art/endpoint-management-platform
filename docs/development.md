# Development guide

## Prerequisites

| Tool | Version used | Notes |
|---|---|---|
| .NET SDK | 10.0.400+ | `global.json` pins 10.0.x |
| Node.js | 24.x LTS | dashboard |
| Docker Desktop | any recent, Linux containers | PostgreSQL + Redis |
| Git | any recent | |

## First-time setup

```powershell
cd c:\Projects\endpoint-platform

# 1. Create your local infrastructure credentials (git-ignored).
Copy-Item infra\.env.example infra\.env
#    Edit infra\.env and replace every CHANGE_ME with a generated value:
#      $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
#      $b = New-Object byte[] 24; $rng.GetBytes($b)
#      [Convert]::ToBase64String($b) -replace '[+/=]',''

# 2. Start PostgreSQL (port 55432) and Redis (port 56379).
docker compose -f infra\docker-compose.yml up -d

# 3. Restore, build, test.
dotnet restore EndpointPlatform.slnx
dotnet build EndpointPlatform.slnx
dotnet test EndpointPlatform.slnx      # needs Docker running (Testcontainers)

# 4. Apply migrations + seed reference data (owner credentials).
#    Values below come from your infra\.env.
$cfg = @{}; Get-Content infra\.env | ? { $_ -match '=' } | % { $p = $_ -split '=',2; $cfg[$p[0]] = $p[1] }
$env:ENDPOINTPLATFORM_Database__ConnectionString =
  "Host=localhost;Port=$($cfg['POSTGRES_PORT']);Database=$($cfg['POSTGRES_DB']);Username=$($cfg['POSTGRES_SUPERUSER']);Password=$($cfg['POSTGRES_SUPERUSER_PASSWORD'])"
$env:ENDPOINTPLATFORM_Database__RuntimeRoleName = $cfg['POSTGRES_APP_USER']
dotnet run --project server\Migrations\EndpointPlatform.Migrations.csproj

# 5. Dashboard dependencies.
cd dashboard; npm install; cd ..
```

## Running the platform

Each API reads its connection strings from `ENDPOINTPLATFORM_`-prefixed
environment variables. The APIs use the **restricted** role, not the owner:

```powershell
$cfg = @{}; Get-Content infra\.env | ? { $_ -match '=' } | % { $p = $_ -split '=',2; $cfg[$p[0]] = $p[1] }
$env:ENDPOINTPLATFORM_Database__ConnectionString =
  "Host=localhost;Port=$($cfg['POSTGRES_PORT']);Database=$($cfg['POSTGRES_DB']);Username=$($cfg['POSTGRES_APP_USER']);Password=$($cfg['POSTGRES_APP_PASSWORD'])"
$env:ENDPOINTPLATFORM_Redis__ConnectionString =
  "localhost:$($cfg['REDIS_PORT']),password=$($cfg['REDIS_PASSWORD'])"

# Terminal 1 - Admin API on http://localhost:5080
dotnet run --project server\Api\EndpointPlatform.Api.csproj

# Terminal 2 - Agent API on http://localhost:5081
dotnet run --project server\AgentApi\EndpointPlatform.AgentApi.csproj

# Terminal 3 - dashboard on http://localhost:5173 (proxies /api -> :5080)
cd dashboard; npm run dev

# Terminal 4 (optional) - the Windows agent as a console app
dotnet run --project agent\EndpointAgent.Service\EndpointAgent.Service.csproj
```

Verify:

- http://localhost:5080/health/live and /health/ready
- http://localhost:5081/health/ready
- http://localhost:5080/swagger (Development only)
- http://localhost:5173 — the dashboard's "Platform status" card should show
  Admin API reachable, postgres Healthy, redis Healthy.

## Tests

```powershell
dotnet test EndpointPlatform.slnx
```

- `EndpointPlatform.Infrastructure.Tests` starts a real PostgreSQL container
  via Testcontainers — Docker must be running. Image is pinned to the same tag
  as `infra/docker-compose.yml`.
- `EndpointAgent.Windows.Tests` exercises real WMI and only runs on Windows.
- `EndpointPlatform.Architecture.Tests` enforces layering and the agent
  no-process-execution rule; if it fails, fix the dependency, don't loosen the
  test.

## Conventions

- Configuration: strongly-typed options, validated on start. New settings get
  a class in `Infrastructure/Configuration` (server) or
  `EndpointAgent.Core/Configuration` (agent).
- No secrets in committed files. Local secrets: `infra/.env` (infrastructure)
  or `dotnet user-secrets` (per-API). CI/deploy: environment variables.
- Database identifiers are snake_case (automatic; see
  `SnakeCaseNamingConvention`).
- New permissions go in `Permissions.cs` **and** `Permissions.All` — a test
  fails if the two diverge. Role changes go in `SystemRoles.cs`; tests pin
  what Helpdesk/Auditor must never hold.
- Every timestamp comes from an injected `TimeProvider`.
- Migrations: `dotnet ef migrations add <Name> --project server\Migrations\EndpointPlatform.Migrations.csproj --startup-project server\Migrations\EndpointPlatform.Migrations.csproj --output-dir Schema`

## Troubleshooting

- **API exits at startup complaining about ConnectionString** — the
  `ENDPOINTPLATFORM_*` variables aren't set in that terminal. This is by
  design; there are no default credentials.
- **`dotnet test` cannot start containers** — Docker Desktop isn't running,
  or is in Windows-container mode. Switch to Linux containers.
- **Port already in use** — 5080/5081/5173/55432/56379 are all overridable:
  APIs via `ASPNETCORE_URLS`, compose via `POSTGRES_PORT`/`REDIS_PORT` in
  `infra/.env`, dashboard in `vite.config.ts`.
