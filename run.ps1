$Project = Join-Path $PSScriptRoot "src" "WinFlow.App" "WinFlow.App.csproj"
dotnet run --project $Project @args
