#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT_DIR"

echo "[1/3] Build Avalonia project"
dotnet build src/Rejector.Avalonia/Rejector.Avalonia.csproj

echo "[2/3] Build cross-platform solution"
dotnet build Rejector.CrossPlatform.slnx

echo "[3/3] Run core tests"
dotnet test tests/Rejector.Core.Tests/Rejector.Core.Tests.csproj

echo "Parity verification command set completed successfully."
