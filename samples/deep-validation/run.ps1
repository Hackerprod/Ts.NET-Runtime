$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..\..")
dotnet run --project .\samples\deep-validation\TypeSharp.DeepValidation.csproj
