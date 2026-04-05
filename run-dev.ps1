param(
    [switch]$DryRun,
    [switch]$FrontendRunStandalone
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$backendProject = Join-Path $repoRoot 'FinansalPusula.Server\FinansalPusula.Server.csproj'
$frontendProject = Join-Path $repoRoot 'FinansalPusula\FinansalPusula.csproj'
$backendLaunchSettings = Join-Path $repoRoot 'FinansalPusula.Server\Properties\launchSettings.json'
$frontendLaunchSettings = Join-Path $repoRoot 'FinansalPusula\Properties\launchSettings.json'

function Get-PreferredLaunchUrl {
    param(
        [string]$LaunchSettingsPath,
        [string]$ProfileName = 'https',
        [string]$PreferredScheme = 'https'
    )

    if (-not (Test-Path $LaunchSettingsPath)) {
        return $null
    }

    try {
        $settings = Get-Content -Path $LaunchSettingsPath -Raw | ConvertFrom-Json
        $applicationUrl = $settings.profiles.$ProfileName.applicationUrl

        if ([string]::IsNullOrWhiteSpace($applicationUrl)) {
            return $null
        }

        $urls = $applicationUrl -split ';' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
        $preferred = $urls | Where-Object { $_ -like "${PreferredScheme}://*" } | Select-Object -First 1

        if (-not [string]::IsNullOrWhiteSpace($preferred)) {
            return $preferred
        }

        return $urls | Select-Object -First 1
    }
    catch {
        return $null
    }
}

function Escape-SingleQuotes {
    param([string]$Value)
    return $Value.Replace("'", "''")
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet CLI not found. Install .NET 8 SDK and retry.'
}

if (-not (Test-Path $backendProject)) {
    throw "Backend project not found: $backendProject"
}

if (-not (Test-Path $frontendProject)) {
    throw "Frontend project not found: $frontendProject"
}

$backendUrl = Get-PreferredLaunchUrl -LaunchSettingsPath $backendLaunchSettings
if ([string]::IsNullOrWhiteSpace($backendUrl)) {
    $backendUrl = 'https://localhost:7015'
}

$frontendStandaloneUrl = Get-PreferredLaunchUrl -LaunchSettingsPath $frontendLaunchSettings
if ([string]::IsNullOrWhiteSpace($frontendStandaloneUrl)) {
    $frontendStandaloneUrl = 'https://localhost:7443'
}

$shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }

$repoRootEscaped = Escape-SingleQuotes $repoRoot
$backendProjectEscaped = Escape-SingleQuotes $backendProject
$frontendProjectEscaped = Escape-SingleQuotes $frontendProject

$backendCommand = "Set-Location -Path '$repoRootEscaped'; dotnet run --project '$backendProjectEscaped' --launch-profile https"

$frontendCommand = if ($FrontendRunStandalone) {
    "Set-Location -Path '$repoRootEscaped'; dotnet run --project '$frontendProjectEscaped' --launch-profile https"
}
else {
    "Set-Location -Path '$repoRootEscaped'; dotnet watch --project '$frontendProjectEscaped' build"
}

if ($DryRun) {
    Write-Host 'Dry run mode enabled. Commands that would run:' -ForegroundColor Yellow
    Write-Host "Shell   : $shell"
    Write-Host "Backend : $backendCommand"
    Write-Host "Frontend: $frontendCommand"
    return
}

$running = Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'FinansalPusula.Server.exe' -or
    ($_.Name -eq 'dotnet.exe' -and -not [string]::IsNullOrWhiteSpace($_.CommandLine) -and $_.CommandLine -match 'FinansalPusula.Server')
}

if ($running) {
    $running | ForEach-Object {
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
    }

    $ids = ($running.ProcessId | Sort-Object -Unique) -join ', '
    Write-Host ("Stopped existing backend process id(s): {0}" -f $ids) -ForegroundColor Yellow
}

Start-Process -FilePath $shell -ArgumentList '-NoProfile', '-NoExit', '-Command', $backendCommand | Out-Null
Start-Process -FilePath $shell -ArgumentList '-NoProfile', '-NoExit', '-Command', $frontendCommand | Out-Null

Write-Host 'Started backend and frontend terminals.' -ForegroundColor Green
Write-Host "Backend URL: $backendUrl"
if ($FrontendRunStandalone) {
    Write-Host "Frontend URL (standalone): $frontendStandaloneUrl"
}
else {
    Write-Host "Frontend mode: build watch only (served by backend at $backendUrl)."
}
Write-Host "OAuth/session endpoints are served by backend ($backendUrl)." -ForegroundColor Cyan
