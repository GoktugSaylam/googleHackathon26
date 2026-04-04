param(
    [switch]$DryRun,
    [switch]$FrontendRunStandalone
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSCommandPath
$backendProject = Join-Path $repoRoot 'FinansalPusula.Server\FinansalPusula.Server.csproj'
$frontendProject = Join-Path $repoRoot 'FinansalPusula\FinansalPusula.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet CLI not found. Install .NET 8 SDK and retry.'
}

if (-not (Test-Path $backendProject)) {
    throw "Backend project not found: $backendProject"
}

if (-not (Test-Path $frontendProject)) {
    throw "Frontend project not found: $frontendProject"
}

$backendCommand = "Set-Location `"$repoRoot`"; dotnet run --project `"$backendProject`" --launch-profile https"

$frontendCommand = if ($FrontendRunStandalone) {
    "Set-Location `"$repoRoot`"; dotnet run --project `"$frontendProject`" --launch-profile https"
}
else {
    "Set-Location `"$repoRoot`"; dotnet watch --project `"$frontendProject`" build"
}

if ($DryRun) {
    Write-Host 'Dry run mode enabled. Commands that would run:' -ForegroundColor Yellow
    Write-Host "Backend : $backendCommand"
    Write-Host "Frontend: $frontendCommand"
    return
}

Start-Process -FilePath 'pwsh' -ArgumentList '-NoProfile', '-NoExit', '-Command', $backendCommand | Out-Null
Start-Process -FilePath 'pwsh' -ArgumentList '-NoProfile', '-NoExit', '-Command', $frontendCommand | Out-Null

Write-Host 'Started backend and frontend terminals.' -ForegroundColor Green
Write-Host 'Backend URL: https://localhost:7015'
if ($FrontendRunStandalone) {
    Write-Host 'Frontend URL (standalone): https://localhost:7443'
}
else {
    Write-Host 'Frontend mode: build watch only (served by backend at 7015).'
}
Write-Host 'OAuth/session endpoints are served by backend (7015).' -ForegroundColor Cyan
