# WinFlow Single-File Release Publisher
$ErrorActionPreference = "Stop"

$Project = Resolve-Path "$PSScriptRoot/../WinFlow.csproj"
$OutputDir = Resolve-Path "$PSScriptRoot/../dist" -ErrorAction SilentlyContinue
if (-not $OutputDir) {
    New-Item -ItemType Directory -Path "$PSScriptRoot/../dist" | Out-Null
    $OutputDir = Resolve-Path "$PSScriptRoot/../dist"
}

Write-Host "Publishing WinFlow (win-x64 self-contained single-file)..." -ForegroundColor Cyan

$env:DOTNET_CLI_HOME = "$PSScriptRoot/../.dotnet"

dotnet publish "$Project" `
    -c Release `
    -r win-x64 `
    --no-restore `
    --self-contained true `
    -m:1 `
    -nr:false `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o "$OutputDir"

Write-Host "`nWinFlow published successfully to: $OutputDir\WinFlow.exe" -ForegroundColor Green
