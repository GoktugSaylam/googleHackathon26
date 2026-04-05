$ErrorActionPreference = 'Stop'

$running = Get-CimInstance Win32_Process | Where-Object {
    $_.Name -eq 'FinansalPusula.Server.exe' -or
    ($_.Name -eq 'dotnet.exe' -and -not [string]::IsNullOrWhiteSpace($_.CommandLine) -and $_.CommandLine -match 'FinansalPusula.Server')
}

if (-not $running) {
    Write-Host 'No running FinansalPusula.Server process found.'
    exit 0
}

$ids = $running.ProcessId | Sort-Object -Unique

$running | ForEach-Object {
    Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
}

Write-Host ("Stopped process id(s): {0}" -f ($ids -join ', ')) -ForegroundColor Green