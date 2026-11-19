$ErrorActionPreference = "Stop"

$Project = Join-Path $PSScriptRoot "src" "WinFlow.App" "WinFlow.App.csproj"
$Exe = Join-Path $PSScriptRoot "src" "WinFlow.App" "bin" "Debug" "net10.0-windows" "WinFlow.App.exe"

Write-Host "[WinFlow] Building WinFlow..." -ForegroundColor Cyan
try {
    dotnet build $Project -m:1 -nr:false --no-restore
} catch {
    dotnet build $Project -m:1 -nr:false
}

if (Test-Path $Exe) {
    Write-Host "[WinFlow] Launching WinFlow Settings Window..." -ForegroundColor Green
    Start-Process -FilePath $Exe -ArgumentList $args
} else {
    Write-Error "Could not find $Exe"
}
