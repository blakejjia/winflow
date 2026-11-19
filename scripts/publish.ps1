# WinFlow Single-File Release Publisher
$ErrorActionPreference = "Stop"

$ProjectDir = Resolve-Path "$PSScriptRoot/../src/WinFlow.App"
$OutputDir = Resolve-Path "$PSScriptRoot/../dist" -ErrorAction SilentlyContinue
if (-not $OutputDir) {
    New-Item -ItemType Directory -Path "$PSScriptRoot/../dist" | Out-Null
    $OutputDir = Resolve-Path "$PSScriptRoot/../dist"
}

Write-Host "Publishing WinFlow (win-x64 self-contained single-file)..." -ForegroundColor Cyan

$env:DOTNET_CLI_HOME = "$PSScriptRoot/../.dotnet"

dotnet publish "$ProjectDir/WinFlow.App.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o "$OutputDir"

Write-Host "`nWinFlow published successfully to: $OutputDir\WinFlow.App.exe" -ForegroundColor Green
