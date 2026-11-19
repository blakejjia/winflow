@echo off
setlocal
cd /d "%~dp0"

set "EXE=%~dp0src\WinFlow.App\bin\Debug\net10.0-windows\WinFlow.App.exe"

echo [WinFlow] Checking build...
dotnet build "%~dp0src\WinFlow.App\WinFlow.App.csproj" -m:1 -nr:false --no-restore
if %ERRORLEVEL% neq 0 (
    echo [WinFlow] Build with --no-restore failed, trying full build...
    dotnet build "%~dp0src\WinFlow.App\WinFlow.App.csproj" -m:1 -nr:false
)

if exist "%EXE%" (
    echo [WinFlow] Starting WinFlow UI...
    start "" "%EXE%" %*
) else (
    echo [WinFlow] Error: WinFlow.App.exe not found at %EXE%
    exit /b 1
)
