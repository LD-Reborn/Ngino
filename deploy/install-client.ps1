#Requires -Version 5.1
#Requires -RunAsAdministrator

[CmdletBinding()]
param(
    [Parameter(Mandatory = $false)]
    [string]$Server,

    [Parameter(Mandatory = $false)]
    [string]$Token,

    [string]$ClientId = $(if ($env:COMPUTERNAME) { $env:COMPUTERNAME.ToLowerInvariant() } else { "windows-client" }),
    [string]$Upstream = "http://localhost:11434",
    [string]$InstallDir = "$env:ProgramFiles\Ngino Client",
    [string]$ServiceName = "NginoClient",
    [switch]$InsecureSkipTlsVerify,
    [switch]$NoOllama,
    [switch]$UseLlamaCppViaDocker,
    [string]$UseOllamaModelsPath = "",
    [string]$LlamaCppDockerImage = "",
    [int]$LlamaCppBasePort = 0
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

function Write-Info([string]$Message) { Write-Host "[INFO]  $Message" -ForegroundColor Green }
function Write-Warn([string]$Message) { Write-Host "[WARN]  $Message" -ForegroundColor Yellow }

function Find-DotNet {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($command) { return $command.Source }

    $candidate = Join-Path $env:ProgramFiles "dotnet\dotnet.exe"
    if (Test-Path -LiteralPath $candidate) { return $candidate }
    return $null
}

function Install-WingetPackage([string]$Id, [string]$Name) {
    if (-not (Get-Command winget.exe -ErrorAction SilentlyContinue)) {
        throw "$Name is required but winget is unavailable. Install $Name manually and run this script again."
    }

    Write-Info "Installing $Name..."
    & winget.exe install --id $Id --exact --accept-package-agreements --accept-source-agreements --silent
    if ($LASTEXITCODE -ne 0) { throw "winget failed to install $Name (exit code $LASTEXITCODE)." }
}

if ([string]::IsNullOrWhiteSpace($Server)) {
    $Server = Read-Host "Ngino server URL (e.g. http://my-server:5050)"
}
if ([string]::IsNullOrWhiteSpace($Server)) { throw "Server URL is required." }

if ([string]::IsNullOrWhiteSpace($Token)) {
    $secureToken = Read-Host "Client key (create it in the admin UI)" -AsSecureString
    $tokenPointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secureToken)
    try { $Token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($tokenPointer) }
    finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($tokenPointer) }
}
if ([string]::IsNullOrWhiteSpace($Token)) { throw "Token is required." }

if ($UseLlamaCppViaDocker -and [string]::IsNullOrWhiteSpace($UseOllamaModelsPath)) {
    $UseOllamaModelsPath = Read-Host "Ollama models path (e.g. C:\Users\user\.ollama\models)"
}
if ($UseLlamaCppViaDocker -and [string]::IsNullOrWhiteSpace($UseOllamaModelsPath)) {
    throw "Ollama models path is required with -UseLlamaCppViaDocker."
}

if ($ServiceName -notmatch '^[A-Za-z0-9_.-]+$') { throw "ServiceName contains unsupported characters." }

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir
$clientProject = Join-Path $repoRoot "src\Ngino.Client\Ngino.Client.csproj"
if (-not (Test-Path -LiteralPath $clientProject)) {
    throw "Client source not found at $clientProject. Run this script from the repository."
}

$dotnet = Find-DotNet
$dotnetVersion = if ($dotnet) { & $dotnet --version } else { $null }
if (-not $dotnetVersion -or -not $dotnetVersion.StartsWith("10.")) {
    if ($dotnetVersion) { Write-Warn "dotnet $dotnetVersion is installed, but version 10.x is required." }
    Install-WingetPackage "Microsoft.DotNet.SDK.10" ".NET 10 SDK"
    $dotnet = Find-DotNet
    if (-not $dotnet) { throw ".NET was installed, but dotnet.exe could not be found." }
    $dotnetVersion = & $dotnet --version
}
Write-Info "Using dotnet $dotnetVersion ($dotnet)."

if ($NoOllama) {
    Write-Info "Skipping Ollama check (-NoOllama)."
} else {
    $ollama = Get-Command ollama.exe -ErrorAction SilentlyContinue
    if (-not $ollama) {
        $ollamaCandidate = Join-Path $env:LOCALAPPDATA "Programs\Ollama\ollama.exe"
        if (Test-Path -LiteralPath $ollamaCandidate) { $ollama = Get-Item $ollamaCandidate }
    }
    if (-not $ollama) {
        Install-WingetPackage "Ollama.Ollama" "Ollama"
        $ollamaCandidate = Join-Path $env:LOCALAPPDATA "Programs\Ollama\ollama.exe"
        if (Test-Path -LiteralPath $ollamaCandidate) { $ollama = Get-Item $ollamaCandidate }
    }
    if (-not $ollama) { Write-Warn "Ollama was installed, but ollama.exe was not found in the current session." }
    else {
        $ollamaPath = if ($ollama.Source) { $ollama.Source } elseif ($ollama.FullName) { $ollama.FullName } else { $ollama.Path }
        Write-Info "Ollama is installed ($ollamaPath)."
    }
}

$architecture = $env:PROCESSOR_ARCHITECTURE
$runtimeId = switch ($architecture) {
    { $_ -in "AMD64", "x64" } { "win-x64"; break }
    { $_ -in "ARM64", "Arm64" } { "win-arm64"; break }
    { $_ -in "x86", "X86" } { "win-x86"; break }
    default { throw "Unsupported architecture: $architecture" }
}

$buildDir = Join-Path ([IO.Path]::GetTempPath()) ("ngino-build-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $buildDir | Out-Null
try {
    Write-Info "Building Ngino client (self-contained, $runtimeId)..."
    & $dotnet publish $clientProject -c Release -r $runtimeId --self-contained true -o $buildDir
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

    $executable = Join-Path $buildDir "Ngino.Client.exe"
    if (-not (Test-Path -LiteralPath $executable)) { throw "Build failed: Ngino.Client.exe was not produced." }

    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existingService -and $existingService.Status -ne "Stopped") {
        Write-Info "Stopping existing service $ServiceName..."
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus("Stopped", [TimeSpan]::FromSeconds(30))
    }

    Write-Info "Installing to $InstallDir..."
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Copy-Item -Path (Join-Path $buildDir "*") -Destination $InstallDir -Recurse -Force
} finally {
    if (Test-Path -LiteralPath $buildDir) { Remove-Item -LiteralPath $buildDir -Recurse -Force }
}

$installedExecutable = Join-Path $InstallDir "Ngino.Client.exe"
$binaryPath = '"{0}"' -f $installedExecutable
if (-not (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)) {
    Write-Info "Creating Windows service $ServiceName..."
    & sc.exe create $ServiceName "binPath=" $binaryPath "start=" "auto" "DisplayName=" "Ngino Tunnel Client"
    if ($LASTEXITCODE -ne 0) { throw "Could not create Windows service $ServiceName." }
} else {
    & sc.exe config $ServiceName "binPath=" $binaryPath "start=" "auto" "DisplayName=" "Ngino Tunnel Client" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not update Windows service $ServiceName." }
}

# A service-specific environment keeps the token out of the process command line.
$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$insecureTlsValue = $InsecureSkipTlsVerify.IsPresent.ToString().ToLowerInvariant()
$serviceEnvironment = @(
    "NGINO_SERVER=$Server",
    "NGINO_TOKEN=$Token",
    "NGINO_CLIENT_ID=$ClientId",
    "NGINO_UPSTREAM=$Upstream",
    "NGINO_INSECURE_SKIP_TLS_VERIFY=$insecureTlsValue",
    "DOTNET_CLI_TELEMETRY_OPTOUT=1",
    "DOTNET_NOLOGO=1"
)
if ($UseLlamaCppViaDocker) {
    $serviceEnvironment += "NGINO_USE_LLAMA_CPP_VIA_DOCKER=true"
    if (-not [string]::IsNullOrWhiteSpace($UseOllamaModelsPath)) {
        $serviceEnvironment += "NGINO_USE_OLLAMA_MODELS_PATH=$UseOllamaModelsPath"
    }
    if (-not [string]::IsNullOrWhiteSpace($LlamaCppDockerImage)) {
        $serviceEnvironment += "NGINO_LLAMA_CPP_DOCKER_IMAGE=$LlamaCppDockerImage"
    }
    if ($LlamaCppBasePort -gt 0) {
        $serviceEnvironment += "NGINO_LLAMA_CPP_BASE_PORT=$LlamaCppBasePort"
    }
}
if ($InsecureSkipTlsVerify) {
    Write-Warn "Server TLS certificate validation is disabled for $ServiceName."
}
New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString -Value $serviceEnvironment -Force | Out-Null
& sc.exe description $ServiceName "Ngino outbound tunnel client" | Out-Null
& sc.exe failure $ServiceName "reset=" "86400" "actions=" "restart/5000/restart/5000/restart/5000" | Out-Null

Start-Service -Name $ServiceName
$service = Get-Service -Name $ServiceName
try { $service.WaitForStatus("Running", [TimeSpan]::FromSeconds(15)) } catch { }
if ($service.Status -eq "Running") { Write-Info "Service $ServiceName is running." }
else { Write-Warn "Service $ServiceName did not reach Running state. Check: Get-WinEvent -LogName Application" }

Write-Host ""
Write-Info "Installation complete."
Write-Host "  Server:      $Server"
Write-Host "  Client ID:   $ClientId"
Write-Host "  Upstream:    $Upstream"
Write-Host "  Service:     $ServiceName"
Write-Host "  Install dir: $InstallDir"
if ($UseLlamaCppViaDocker) {
    Write-Host "  llama.cpp:   enabled (models: $UseOllamaModelsPath)"
}
Write-Host ""
Write-Host "  Manage:  Get-Service $ServiceName | Start-Service/Stop-Service/Restart-Service"
Write-Host "  Logs:    Get-WinEvent -LogName Application | Where-Object ProviderName -eq NginoClient"
