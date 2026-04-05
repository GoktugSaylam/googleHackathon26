param(
    [string]$LaunchProfile = 'https',
    [string]$Urls,
    [switch]$NoRestart
)

$ErrorActionPreference = 'Stop'

$repoRoot = $PSScriptRoot
$projectPath = Join-Path $repoRoot 'FinansalPusula.Server\FinansalPusula.Server.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'dotnet CLI not found. Install .NET 8 SDK and retry.'
}

if (-not (Test-Path $projectPath)) {
    throw "Server project not found: $projectPath"
}

if (-not $NoRestart) {
    $running = Get-CimInstance Win32_Process | Where-Object {
        $_.Name -eq 'FinansalPusula.Server.exe' -or
        ($_.Name -eq 'dotnet.exe' -and ($_.CommandLine ?? '') -match 'FinansalPusula.Server')
    }

    if ($running) {
        $running | ForEach-Object {
            Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        }

        $ids = ($running.ProcessId | Sort-Object -Unique) -join ', '
        Write-Host ("Stopped existing process id(s): {0}" -f $ids) -ForegroundColor Yellow
    }
}

$dotnetArgs = @('run', '--project', $projectPath)

if (-not [string]::IsNullOrWhiteSpace($LaunchProfile)) {
    $dotnetArgs += @('--launch-profile', $LaunchProfile)
}

if (-not [string]::IsNullOrWhiteSpace($Urls)) {
    $dotnetArgs += @('--urls', $Urls)
}

Set-Location $repoRoot
Write-Host 'Starting FinansalPusula.Server...' -ForegroundColor Cyan
& dotnet @dotnetArgs