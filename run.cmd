@echo off
rem Kill any existing running instance to free file locks
taskkill /F /IM WinFlow.exe >nul 2>&1
timeout /t 1 /nobreak >nul 2>&1

echo Building WinFlow...
dotnet build "%~dp0WinFlow.csproj" -c Debug --no-restore -nologo || exit /b 1

echo Launching WinFlow...
start "" "%~dp0bin\Debug\net10.0-windows10.0.19041.0\WinFlow.exe" %*
echo WinFlow is running in the background. Check your Taskbar / System Tray or press CapsLock!
