#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/../.."
dotnet run --project samples/deep-validation/TypeSharp.DeepValidation.csproj
