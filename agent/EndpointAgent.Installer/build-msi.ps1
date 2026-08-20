<#
.SYNOPSIS
    Builds EndpointPlatformAgent-<version>-x64.msi.

.DESCRIPTION
    Publishes the agent self-contained (so the endpoint needs no .NET runtime)
    and packages it with WiX into an MSI that installs the Windows Service.

    The MSI contains NO secret. It is a common, universal binary: identical for
    every machine and every customer, so it can be cached, mirrored and signed
    once. Enrolment happens after installation and is gated by an administrator
    approving the machine in the dashboard.

    The management server URL is baked in at build time. That is not a secret -
    it is a public endpoint - and baking it keeps the double-click install free
    of any configuration step.

.PARAMETER ServerBaseUrl
    Management server the installed agent will contact. Must be https:// for any
    real deployment; http:// is permitted only for localhost development.

.PARAMETER Version
    Product version, e.g. 1.0.0. Defaults to the agent's VersionPrefix.

.EXAMPLE
    .\build-msi.ps1 -ServerBaseUrl https://65.2.37.254.nip.io
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$ServerBaseUrl,

    [string]$Version = '1.0.0',

    [switch]$SkipPublish
)

$ErrorActionPreference = 'Stop'
$installerDir = $PSScriptRoot
$repoRoot     = Split-Path -Parent (Split-Path -Parent $installerDir)
$publishDir   = Join-Path $repoRoot 'build\agent-publish'
$outputDir    = Join-Path $repoRoot 'build\installer'

# --- refuse to build an installer that would downgrade transport security ----
# An agent that talks plain HTTP to a real server would send its device
# credential in clear. Localhost is exempt because that is the development loop.
if ($ServerBaseUrl -notmatch '^https://') {
    $isLocal = $ServerBaseUrl -match '^http://(localhost|127\.0\.0\.1)(:\d+)?(/|$)'
    if (-not $isLocal) {
        throw "ServerBaseUrl must be https:// (got '$ServerBaseUrl'). Plain HTTP is only allowed for localhost."
    }
    Write-Host "WARNING: building a development installer against $ServerBaseUrl" -ForegroundColor Yellow
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must be three-part, e.g. 1.0.0 (got '$Version')."
}

# --- publish ----------------------------------------------------------------
if (-not $SkipPublish) {
    Write-Host "Publishing agent (self-contained, win-x64)..." -ForegroundColor Cyan
    if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

    & dotnet publish (Join-Path $repoRoot 'agent\EndpointAgent.Service\EndpointAgent.Service.csproj') `
        -c Release -p:PublishAgent=true -p:Version=$Version -o $publishDir --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}

$exe = Join-Path $publishDir 'EndpointAgent.Service.exe'
if (-not (Test-Path $exe)) { throw "Publish output is missing $exe." }
Write-Host ("  published {0} files, {1:N0} MB" -f `
    (Get-ChildItem $publishDir -Recurse -File).Count, `
    ((Get-ChildItem $publishDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))

# --- machine configuration written into the package -------------------------
# Generated from a template so the URL exists in exactly one place per build.
$configTemplate = Get-Content (Join-Path $installerDir 'agent.config.json.template') -Raw
$configPath     = Join-Path $installerDir 'agent.config.json'
$configTemplate.Replace('__SERVER_BASE_URL__', $ServerBaseUrl) | Set-Content -Path $configPath -Encoding UTF8 -NoNewline

# --- WiX --------------------------------------------------------------------
if (-not (Get-Command wix -ErrorAction SilentlyContinue)) {
    throw "The WiX CLI is not installed. Install it with: dotnet tool install --global wix"
}

# `wix extension list` returns one line per extension. Joining first matters:
# in PowerShell `$array -notmatch 'x'` FILTERS the array rather than returning a
# boolean, so the naive check is always truthy and re-adds an existing extension.
$installedExtensions = (& wix extension list --global 2>$null) -join "`n"
foreach ($ext in @('WixToolset.Util.wixext', 'WixToolset.UI.wixext')) {
    if ($installedExtensions -notmatch [regex]::Escape($ext)) {
        Write-Host "Adding WiX extension $ext..." -ForegroundColor Cyan
        & wix extension add --global $ext
        if ($LASTEXITCODE -ne 0) { throw "Failed to add WiX extension $ext." }
    }
}

New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
$msiPath = Join-Path $outputDir "EndpointPlatformAgent-$Version-x64.msi"

Write-Host "Building MSI..." -ForegroundColor Cyan
& wix build `
    (Join-Path $installerDir 'Package.wxs') `
    (Join-Path $installerDir 'AgentBinaries.wxs') `
    -arch x64 `
    -d "AgentPublishDir=$publishDir" `
    -d "ProductVersion=$Version" `
    -d "ServerBaseUrl=$ServerBaseUrl" `
    -ext WixToolset.Util.wixext `
    -ext WixToolset.UI.wixext `
    -bindpath $installerDir `
    -o $msiPath

if ($LASTEXITCODE -ne 0) { throw "wix build failed with exit code $LASTEXITCODE." }

$msi = Get-Item $msiPath
Write-Host ''
Write-Host "MSI: $($msi.FullName)" -ForegroundColor Green
Write-Host ("Size: {0:N1} MB" -f ($msi.Length / 1MB)) -ForegroundColor Green
Write-Host ''

# --- signing ----------------------------------------------------------------
# Deliberately not signed here. There is no organisation certificate, and a
# self-signed one would create the appearance of trust without the substance:
# Windows would still warn, and users would learn to click through warnings.
# To sign a real release, after this script:
#   signtool sign /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 `
#            /f <cert.pfx> /p <password> "$msiPath"
Write-Host 'NOTE: this MSI is UNSIGNED. SmartScreen and UAC will warn on first run.' -ForegroundColor Yellow
Write-Host '      See docs/agent-installation.md for signing a release build.' -ForegroundColor Yellow
