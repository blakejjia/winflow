param(
    [switch]$Foreground,
    [switch]$Wait
)

$ErrorActionPreference = "Stop"

$Project = Join-Path $PSScriptRoot "src" "WinFlow.App" "WinFlow.App.csproj"
$Exe = Join-Path $PSScriptRoot "src" "WinFlow.App" "bin" "Debug" "net10.0-windows" "WinFlow.App.exe"

# Gracefully terminate any already-running WinFlow instances before rebuilding
$runningExisting = Get-Process -Name "WinFlow.App" -ErrorAction SilentlyContinue
if ($runningExisting) {
    Write-Host "[WinFlow] Stopping previous running instance(s)..." -ForegroundColor Yellow
    $runningExisting | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 400
}

Write-Host "[WinFlow] Building WinFlow..." -ForegroundColor Cyan
try {
    dotnet build $Project -m:1 -nr:false --no-restore
} catch {
    dotnet build $Project -m:1 -nr:false
}

if (-not (Test-Path $Exe)) {
    Write-Error "Could not find $Exe"
    exit 1
}

Write-Host "[WinFlow] Launching WinFlow Settings Window..." -ForegroundColor Green
$proc = Start-Process -FilePath $Exe -PassThru

Start-Sleep -Milliseconds 1200

$running = Get-Process -Id $proc.Id -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "`n========================================================" -ForegroundColor Green
    Write-Host " [OK] WinFlow is running successfully (PID: $($proc.Id))!" -ForegroundColor Green
    Write-Host " - The 'WinFlow Settings' window is now open on your screen." -ForegroundColor Cyan
    Write-Host " - WinFlow also lives in your System Tray (bottom-right notification area)." -ForegroundColor Cyan
    Write-Host " - Hold CapsLock anywhere to test speech-to-text dictation!" -ForegroundColor Yellow
    Write-Host "========================================================`n" -ForegroundColor Green

    if ($Foreground -or $Wait) {
        Write-Host "Running in foreground. Press Ctrl+C or close WinFlow to exit..." -ForegroundColor Gray
        Wait-Process -Id $proc.Id
    }
} else {
    Write-Warning "[WinFlow] Process exited unexpectedly."
    $log = "$env:APPDATA\WinFlow\winflow-error.log"
    if (Test-Path $log) {
        Write-Host "`nError Log Contents ($log):" -ForegroundColor Red
        Get-Content $log
    } else {
        Write-Host "No error log was generated." -ForegroundColor Yellow
    }
}
