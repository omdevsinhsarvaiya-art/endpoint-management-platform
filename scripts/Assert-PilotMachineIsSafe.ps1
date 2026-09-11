<#
.SYNOPSIS
    Refuses to let a machine be used for an agent upgrade/removal pilot if that
    machine has ever been enrolled in production.

.DESCRIPTION
    An agent pilot repoints a machine's agent at an isolated pilot server. If that
    machine already has a production enrollment, the pilot install overwrites the
    device credential and the production device silently stops reporting. The
    production database is never written to, so every pilot-side check still says
    PASS while a production endpoint goes dark.

    That is exactly what happened on 2026-09-11 with DESKTOP-PJCC143
    (machine identifier 5A4839FE-996A-4748-9411-7EA29DC6978A). The pilot database
    was checked and was clean. Production was not checked, and held an Active
    device on the same machine identifier, last seen 59 minutes before the pilot
    enrollment.

    CHECKING ONLY THE PILOT DATABASE IS NOT SUFFICIENT. The pilot database cannot
    know that a machine belongs to production; only production can answer that.
    This script asks production, read-only, before anything is installed.

    Run it BEFORE installing a pilot agent. Treat a non-zero exit as a stop.

.PARAMETER MachineIdentifier
    The candidate machine's identifier, as the agent reports it. On Windows:
        (Get-CimInstance Win32_ComputerSystemProduct).UUID

.PARAMETER Hostname
    The candidate machine's hostname.

.PARAMETER ApproveHostnameCollision
    Permits a hostname that matches an ACTIVE production device, for the case
    where two genuinely different machines share a name. Requires -Reason.
    This switch never permits a machine-identifier match: that is always fatal,
    because a machine identifier collision means it is the same machine.

.PARAMETER Reason
    Written to the transcript alongside an approved hostname collision so the
    decision is attributable. Required with -ApproveHostnameCollision.

.NOTES
    WHAT THIS TOUCHES
    -----------------
    Production: three SELECT statements, run read-only over SSM. No INSERT,
    UPDATE, DELETE, task, token, session or audit entry. The script has no
    switch that makes it write, and deliberately never will.

    Pilot: nothing at all. It does not need the pilot stack to be running.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $MachineIdentifier,
    [Parameter(Mandatory)][string] $Hostname,
    [string] $InstanceId = 'i-0859e6fa7161a49b3',
    [string] $Region     = 'ap-south-1',
    [switch] $ApproveHostnameCollision,
    [string] $Reason
)

$ErrorActionPreference = 'Stop'

if ($ApproveHostnameCollision -and [string]::IsNullOrWhiteSpace($Reason)) {
    throw '-ApproveHostnameCollision requires -Reason.'
}

# The AWS CLI dies on a BOM in command output unless these are set.
$env:PYTHONIOENCODING = 'utf-8'
$env:PYTHONUTF8       = '1'

# Reject anything that is not plainly a machine identifier or a hostname before
# it is embedded in the remote script. Validating is safer than escaping: these
# two shapes cover every real value, so nothing legitimate is turned away.
if ($MachineIdentifier -notmatch '^[A-Za-z0-9-]{1,64}$') {
    throw "MachineIdentifier '$MachineIdentifier' is not a plain identifier; refusing to build a remote command from it."
}
if ($Hostname -notmatch '^[A-Za-z0-9._-]{1,255}$') {
    throw "Hostname '$Hostname' is not a plain hostname; refusing to build a remote command from it."
}

# Read-only. psql does NOT interpolate -v variables inside -c, so the statements
# go in on stdin, where :'mid' and :'h' are substituted and properly quoted.
$remote = @"
set -e
PW=`$(docker exec epp-demo-postgres printenv POSTGRES_PASSWORD)
MID='$MachineIdentifier'
HOST_NAME='$Hostname'
Q() { docker exec -i -e PGPASSWORD="`$PW" epp-demo-postgres psql -U endpoint_owner -d endpoint_platform -t -A -v mid="`$MID" -v h="`$HOST_NAME" -f -; }
echo "MACHINE_ANY=`$(echo "SELECT count(*) FROM endpoint_platform.devices WHERE machine_identifier = :'mid';" | Q)"
echo "MACHINE_ACTIVE=`$(echo "SELECT count(*) FROM endpoint_platform.devices WHERE machine_identifier = :'mid' AND status = 'Active';" | Q)"
echo "HOST_ACTIVE=`$(echo "SELECT count(*) FROM endpoint_platform.devices WHERE hostname = :'h' AND status = 'Active';" | Q)"
echo "DETAIL<<EOF"
echo "SELECT id || ' | ' || hostname || ' | ' || agent_version || ' | ' || status || ' | last_seen ' || coalesce(last_seen_at::text,'never') FROM endpoint_platform.devices WHERE machine_identifier = :'mid' OR hostname = :'h' ORDER BY enrolled_at;" | Q
echo "EOF"
"@

$bytes  = [System.Text.Encoding]::UTF8.GetBytes(($remote -replace "`r`n", "`n"))
$base64 = [Convert]::ToBase64String($bytes)

Write-Host "Asking production (read-only) about $Hostname / $MachineIdentifier ..."

$commandId = aws ssm send-command --region $Region --instance-ids $InstanceId `
    --document-name AWS-RunShellScript `
    --parameters "commands=[`"echo $base64 | base64 -d > /tmp/pilot-preflight.sh && bash /tmp/pilot-preflight.sh`"]" `
    --query 'Command.CommandId' --output text

if ([string]::IsNullOrWhiteSpace($commandId)) { throw 'SSM send-command returned no command id.' }

do {
    Start-Sleep -Seconds 3
    $status = aws ssm get-command-invocation --region $Region --command-id $commandId `
        --instance-id $InstanceId --query 'Status' --output text
} while ($status -in @('InProgress', 'Pending', 'Delayed'))

if ($status -ne 'Success') {
    $err = aws ssm get-command-invocation --region $Region --command-id $commandId `
        --instance-id $InstanceId --query 'StandardErrorContent' --output text
    throw "Production preflight could not run (status $status). Do NOT proceed blind. $err"
}

$out = aws ssm get-command-invocation --region $Region --command-id $commandId `
    --instance-id $InstanceId --query 'StandardOutputContent' --output text

# The AWS CLI hands PowerShell one array element per line. Join it back up, or
# every multi-line match below silently finds nothing.
if ($out -is [array]) { $out = $out -join "`n" }

function Get-Count([string] $key) {
    $line = ($out -split "`n" | Where-Object { $_ -match "^$key=" } | Select-Object -First 1)
    if (-not $line) { throw "Production preflight returned no $key line; refusing to guess." }
    return [int]($line -split '=', 2)[1].Trim()
}

$machineAny    = Get-Count 'MACHINE_ANY'
$machineActive = Get-Count 'MACHINE_ACTIVE'
$hostActive    = Get-Count 'HOST_ACTIVE'

Write-Host ''
Write-Host 'Production says:'
Write-Host ("  rows with this machine identifier ... {0} ({1} Active)" -f $machineAny, $machineActive)
Write-Host ("  Active devices with this hostname .. {0}" -f $hostActive)
if ($out -match '(?s)DETAIL<<EOF\r?\n(.*?)\r?\nEOF') {
    $detail = $Matches[1].Trim()
    if ($detail) { Write-Host ''; Write-Host 'Matching production rows:'; $detail -split "`n" | ForEach-Object { Write-Host "  $_" } }
}
Write-Host ''

$failures = @()

# Fatal and unconditional. A shared machine identifier is the same machine, so
# installing a pilot agent here would overwrite a production device credential.
if ($machineAny -gt 0) {
    $failures += "This machine identifier is already enrolled in production ($machineAny row(s), $machineActive Active). " +
                 'A pilot agent install would overwrite the production device credential and take that endpoint offline. ' +
                 'Use a machine that has never been enrolled in production.'
}

if ($hostActive -gt 0 -and $machineAny -eq 0) {
    if ($ApproveHostnameCollision) {
        Write-Warning "Hostname '$Hostname' matches an Active production device, approved: $Reason"
    }
    else {
        $failures += "Hostname '$Hostname' is an Active production device, though the machine identifier differs. " +
                     'Confirm these are genuinely different machines, then re-run with ' +
                     '-ApproveHostnameCollision -Reason "<why>".'
    }
}

if ($failures.Count -gt 0) {
    Write-Host 'PILOT PREFLIGHT: FAIL' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    Write-Host ''
    Write-Host 'Do not install a pilot agent on this machine.'
    exit 1
}

Write-Host 'PILOT PREFLIGHT: PASS' -ForegroundColor Green
Write-Host 'This machine has no production enrollment. Safe to use for an agent pilot.'
exit 0
