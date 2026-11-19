# Build once, then launch the app detached (no lingering console window).
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
dotnet build "$root/WinFlow.csproj" -c Debug -v:q -nologo
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
Start-Process -FilePath "$root/bin/Debug/net10.0-windows10.0.19041.0/WinFlow.exe" -ArgumentList $args
