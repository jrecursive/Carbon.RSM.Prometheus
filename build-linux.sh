#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

: "${NUGET_PACKAGES:=/tmp/nuget-packages}"
: "${NUGET_HTTP_CACHE_PATH:=/tmp/nuget-http}"

mkdir -p "$NUGET_PACKAGES" "$NUGET_HTTP_CACHE_PATH"

export NUGET_PACKAGES
export NUGET_HTTP_CACHE_PATH

cd "$SCRIPT_DIR"

dotnet restore RustServerMetrics.sln
dotnet build RustServerMetrics.sln -c Linux
