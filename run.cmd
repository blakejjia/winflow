@echo off
rem Build once, then launch the app detached (no lingering console window).
dotnet build "%~dp0WinFlow.csproj" -c Debug -v:q -nologo || exit /b 1
start "" "%~dp0bin\Debug\net10.0-windows10.0.19041.0\WinFlow.exe" %*
