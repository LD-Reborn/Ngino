#Requires -Version 5.1
#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\Ngino Client",
    [string]$ServiceName = "NginoClient"
)

$ErrorActionPreference = "Stop"

function Write-Info([string]$Message) { Write-Host "[INFO]  $Message" -ForegroundColor Green }

if ($ServiceName -notmatch '^[A-Za-z0-9_.-]+$') { throw "ServiceName contains unsupported characters." }

# ── Stop and delete the Windows service ───────────────────────────────────────
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne "Stopped") {
        Write-Info "Stopping service $ServiceName..."
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }
    Write-Info "Deleting service $ServiceName..."
    & sc.exe delete $ServiceName
    if ($LASTEXITCODE -ne 0) { throw "Could not delete Windows service $ServiceName." }
} else {
    Write-Info "Service $ServiceName does not exist; skipping."
}

# ── Remove install directory ──────────────────────────────────────────────────
if (Test-Path -LiteralPath $InstallDir) {
    Write-Info "Removing install directory $InstallDir..."
    Remove-Item -LiteralPath $InstallDir -Recurse -Force
} else {
    Write-Info "Install directory $InstallDir does not exist; skipping."
}

Write-Host ""
Write-Info "Uninstall complete."
Write-Host "  Service:     $ServiceName (stopped and deleted)"
Write-Host "  Install dir: $InstallDir (removed)"
