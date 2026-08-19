<#
.SYNOPSIS
    Stops everything run-local.ps1 started.

.DESCRIPTION
    Stops the two APIs, the dashboard dev server and the Windows agent, and
    optionally the containers.

    The containers are LEFT RUNNING by default, and this script never removes
    their volumes: the PostgreSQL volume holds the audit trail, the enrolled
    devices and your admin account. Losing it means re-enrolling the machine and
    re-bootstrapping an administrator, which is a bad outcome for something as
    routine as "stop the app".

.EXAMPLE
    .\scripts\stop-local.ps1
    Stops the applications; PostgreSQL and Redis keep running.

.EXAMPLE
    .\scripts\stop-local.ps1 -Infra
    Also stops the containers (`docker compose stop`). Data is preserved.
#>

[CmdletBinding()]
param(
    # Also stop the PostgreSQL and Redis containers. Never deletes their volumes.
    [switch]$Infra
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# --- applications ----------------------------------------------------------
foreach ($name in 'EndpointPlatform.Api', 'EndpointPlatform.AgentApi', 'EndpointAgent.Service') {
    $processes = Get-Process -Name $name -ErrorAction SilentlyContinue
    if (-not $processes) {
        Write-Host ("  {0,-26} not running" -f $name) -ForegroundColor DarkGray
        continue
    }

    foreach ($process in $processes) {
        try {
            Stop-Process -Id $process.Id -Force -ErrorAction Stop
            Write-Host ("  {0,-26} stopped (PID {1})" -f $name, $process.Id) -ForegroundColor Green
        }
        catch {
            # The agent runs elevated, so an ordinary window cannot signal it.
            # That is the isolation working, not a bug - say so plainly.
            Write-Host ("  {0,-26} ACCESS DENIED (PID {1})" -f $name, $process.Id) -ForegroundColor Yellow
            Write-Host '      It runs elevated. Close its window, or re-run this script as administrator.' -ForegroundColor DarkGray
        }
    }
}

# --- dashboard -------------------------------------------------------------
# Matched by listening port rather than by process name: killing every `node`
# would take out unrelated work.
$listener = Get-NetTCPConnection -LocalPort 5173 -State Listen -ErrorAction SilentlyContinue |
    Select-Object -First 1

if ($listener) {
    try {
        Stop-Process -Id $listener.OwningProcess -Force -ErrorAction Stop
        Write-Host ("  {0,-26} stopped (PID {1})" -f 'Dashboard :5173', $listener.OwningProcess) -ForegroundColor Green
    }
    catch {
        Write-Host ("  {0,-26} could not stop PID {1}" -f 'Dashboard :5173', $listener.OwningProcess) -ForegroundColor Yellow
    }
}
else {
    Write-Host ("  {0,-26} not running" -f 'Dashboard :5173') -ForegroundColor DarkGray
}

# --- infrastructure --------------------------------------------------------
if ($Infra) {
    Write-Host ''
    Write-Host 'Stopping containers (volumes preserved)...' -ForegroundColor Cyan

    # `stop`, never `down -v`: the -v flag would delete the PostgreSQL volume and
    # with it the audit trail, the enrolled devices and the admin account.
    docker compose -f infra\docker-compose.yml stop | Out-Null
    Write-Host '  postgres + redis stopped. Data volumes are intact.' -ForegroundColor Green
}
else {
    Write-Host ''
    Write-Host 'PostgreSQL and Redis are still running (use -Infra to stop them too).' -ForegroundColor DarkGray
}

Write-Host ''
Write-Host 'Start again with: .\scripts\run-local.ps1 -WithAgent' -ForegroundColor DarkGray
