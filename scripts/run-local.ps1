<#
.SYNOPSIS
    Starts the whole platform locally: infrastructure, both APIs, and the dashboard.

.DESCRIPTION
    Reads every credential from infra/.env, so no secret is ever typed on a command
    line or baked into this file. Each service runs in its own window, which keeps
    their logs readable and lets you stop one without killing the rest.

    The Windows agent is deliberately NOT started here. It needs administrator
    privilege to manage local accounts, and elevation should be a conscious act -
    see the instructions this script prints at the end.

.EXAMPLE
    .\scripts\run-local.ps1
#>

[CmdletBinding()]
param(
    # Skip `docker compose up` when the containers are already healthy.
    [switch]$SkipInfra,

    # Skip the migration job when the schema is known to be current.
    [switch]$SkipMigrations,

    # Also start the Windows agent, elevated. Raises one UAC prompt: managing local
    # accounts requires administrator privilege, and nothing here can or should
    # bypass that.
    [switch]$WithAgent
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# --- configuration ---------------------------------------------------------
$envFile = Join-Path $root 'infra\.env'
if (-not (Test-Path $envFile)) {
    throw "infra/.env not found. Copy infra/.env.example and fill in the values."
}

$cfg = @{}
Get-Content $envFile | Where-Object { $_ -match '^\s*[^#].*=' } | ForEach-Object {
    $pair = $_ -split '=', 2
    $cfg[$pair[0].Trim()] = $pair[1].Trim()
}

$pgPort    = $cfg['POSTGRES_PORT']
$pgDb      = $cfg['POSTGRES_DB']
$appUser   = $cfg['POSTGRES_APP_USER']
$appPass   = $cfg['POSTGRES_APP_PASSWORD']
$ownerUser = $cfg['POSTGRES_SUPERUSER']
$ownerPass = $cfg['POSTGRES_SUPERUSER_PASSWORD']
$redisPort = $cfg['REDIS_PORT']
$redisPass = $cfg['REDIS_PASSWORD']
$secretKey = $cfg['SECRET_PROTECTION_KEY']

if (-not $secretKey) {
    # Both API processes must seal and unseal with the SAME key, so a per-process
    # fallback key would break local-account password delivery across hosts.
    throw "SECRET_PROTECTION_KEY is missing from infra/.env. Generate one with: [Convert]::ToBase64String((1..32|%{Get-Random -Max 256}))"
}

# --- infrastructure --------------------------------------------------------
if (-not $SkipInfra) {
    Write-Host 'Starting PostgreSQL + Redis...' -ForegroundColor Cyan
    docker compose -f infra\docker-compose.yml up -d | Out-Null

    Write-Host 'Waiting for containers to report healthy...' -ForegroundColor Cyan
    foreach ($name in 'endpoint-platform-postgres', 'endpoint-platform-redis') {
        for ($i = 0; $i -lt 30; $i++) {
            $state = docker inspect -f '{{.State.Health.Status}}' $name 2>$null
            if ($state -eq 'healthy') { break }
            Start-Sleep -Seconds 2
        }
        Write-Host "  $name : $state"
    }
}

# --- migrations ------------------------------------------------------------
if (-not $SkipMigrations) {
    Write-Host 'Applying migrations + seeding (owner role)...' -ForegroundColor Cyan
    $env:ENDPOINTPLATFORM_Database__ConnectionString =
        "Host=localhost;Port=$pgPort;Database=$pgDb;Username=$ownerUser;Password=$ownerPass"
    $env:ENDPOINTPLATFORM_Database__RuntimeRoleName = $appUser
    dotnet run --project server\Migrations\EndpointPlatform.Migrations.csproj | Out-Null
    Remove-Item Env:\ENDPOINTPLATFORM_Database__ConnectionString
    Remove-Item Env:\ENDPOINTPLATFORM_Database__RuntimeRoleName
}

# --- services --------------------------------------------------------------
# The APIs run under the RESTRICTED role: they never need DDL, and running them
# as the owner would quietly undo the privilege split the audit trail relies on.
$apiEnv = @{
    ENDPOINTPLATFORM_Database__ConnectionString =
        "Host=localhost;Port=$pgPort;Database=$pgDb;Username=$appUser;Password=$appPass"
    ENDPOINTPLATFORM_Redis__ConnectionString    = "localhost:$redisPort,password=$redisPass"
    ENDPOINTPLATFORM_SecretProtection__Key      = $secretKey
    ENDPOINTPLATFORM_PackageStorage__Directory  = (Join-Path $root '.package-content')
    ASPNETCORE_ENVIRONMENT                      = 'Development'
}

function Start-Service-Window {
    param([string]$Title, [string]$Command, [hashtable]$EnvVars)

    $prelude = ($EnvVars.GetEnumerator() | ForEach-Object {
        "`$env:$($_.Key)='$($_.Value)'"
    }) -join '; '

    Start-Process powershell -ArgumentList @(
        '-NoExit', '-Command',
        "`$Host.UI.RawUI.WindowTitle='$Title'; Set-Location '$root'; $prelude; $Command"
    ) | Out-Null
}

Write-Host 'Starting Admin API (5080)...' -ForegroundColor Cyan
Start-Service-Window 'Admin API :5080' 'dotnet run --project server\Api\EndpointPlatform.Api.csproj' $apiEnv

Write-Host 'Starting Agent API (5081)...' -ForegroundColor Cyan
Start-Service-Window 'Agent API :5081' 'dotnet run --project server\AgentApi\EndpointPlatform.AgentApi.csproj' $apiEnv

Write-Host 'Starting dashboard (5173)...' -ForegroundColor Cyan
Start-Service-Window 'Dashboard :5173' 'npm run dev --prefix dashboard' @{}

# --- windows agent (elevated) ----------------------------------------------
# Started via -Verb RunAs rather than by elevating this whole script: only the
# agent needs administrator privilege, and the APIs deliberately do not run with
# it. Declining the UAC prompt leaves the rest of the stack running.
if ($WithAgent) {
    Write-Host 'Starting Windows agent (elevated - approve the UAC prompt)...' -ForegroundColor Cyan

    $agentCommand =
        "`$Host.UI.RawUI.WindowTitle='Windows Agent (elevated)'; " +
        "Set-Location '$root'; " +
        "`$env:ENDPOINTAGENT_Agent__ServerBaseUrl='http://localhost:5081'; " +
        'dotnet run --project agent\EndpointAgent.Service\EndpointAgent.Service.csproj'

    try {
        Start-Process powershell -Verb RunAs -ArgumentList '-NoExit', '-Command', $agentCommand | Out-Null
    }
    catch {
        Write-Host '  UAC declined - the agent is not running.' -ForegroundColor Yellow
        Write-Host '  Everything else is still up; local account tasks will queue until an agent checks in.' -ForegroundColor Yellow
    }
}

# --- readiness -------------------------------------------------------------
Write-Host ''
Write-Host 'Waiting for services...' -ForegroundColor Cyan
foreach ($svc in @(
    @{ Name = 'Admin API'; Url = 'http://localhost:5080/health/ready' },
    @{ Name = 'Agent API'; Url = 'http://localhost:5081/health/ready' },
    @{ Name = 'Dashboard'; Url = 'http://localhost:5173' }
)) {
    $ok = $false
    for ($i = 0; $i -lt 40; $i++) {
        try {
            $null = Invoke-WebRequest -Uri $svc.Url -UseBasicParsing -TimeoutSec 3
            $ok = $true; break
        } catch { Start-Sleep -Seconds 2 }
    }
    $colour = if ($ok) { 'Green' } else { 'Red' }
    $status = if ($ok) { 'ready' } else { 'NOT READY - check its window' }
    Write-Host ("  {0,-12} {1}" -f $svc.Name, $status) -ForegroundColor $colour
}

Write-Host ''
Write-Host 'Dashboard : http://localhost:5173' -ForegroundColor Green
Write-Host 'Swagger   : http://localhost:5080/swagger' -ForegroundColor Green
Write-Host ''
if ($WithAgent) {
    $agent = Get-Process -Name EndpointAgent.Service -ErrorAction SilentlyContinue
    if ($agent) {
        Write-Host ("Windows agent : running (PID {0}, elevated)" -f $agent.Id) -ForegroundColor Green
    }
    else {
        Write-Host 'Windows agent : still starting - check its window for the first heartbeat.' -ForegroundColor Yellow
    }
}
else {
    Write-Host 'The Windows agent is NOT running.' -ForegroundColor Yellow
    Write-Host 'Local-account management needs it. Re-run with -WithAgent, or start it' -ForegroundColor Yellow
    Write-Host 'yourself from an ELEVATED PowerShell window:' -ForegroundColor Yellow
    Write-Host ''
    Write-Host "  cd $root" -ForegroundColor DarkGray
    Write-Host "  `$env:ENDPOINTAGENT_Agent__ServerBaseUrl='http://localhost:5081'" -ForegroundColor DarkGray
    Write-Host '  dotnet run --project agent\EndpointAgent.Service\EndpointAgent.Service.csproj' -ForegroundColor DarkGray
}

Write-Host ''
Write-Host 'Stop everything with: .\scripts\stop-local.ps1' -ForegroundColor DarkGray
