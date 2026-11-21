# Build once, then launch the app detached (no lingering console window).
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

# Clean up any existing instances to prevent mutex conflicts and file locks
Get-Process -Name WinFlow -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 300

Write-Host "Building WinFlow..." -ForegroundColor Cyan
dotnet build "$root/WinFlow.csproj" -c Debug --no-restore -nologo
if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed! Please check the errors above." -ForegroundColor Red
    exit $LASTEXITCODE
}

Write-Host "Launching WinFlow..." -ForegroundColor Green
Start-Process -FilePath "$root/bin/Debug/net10.0-windows10.0.19041.0/WinFlow.exe" -ArgumentList $args
Write-Host "WinFlow is running in the background. Check your Taskbar / System Tray or press CapsLock!" -ForegroundColor DarkGray
