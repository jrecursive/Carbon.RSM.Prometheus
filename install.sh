#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "usage: $0 /path/to/server/carbon/managed/modules" >&2
  exit 1
fi

TARGET_DIR="$1"
SOURCE_DLL="src/RustServerMetrics/bin/Linux/net48/Carbon.Linux.RSM.dll"

mkdir -p "$TARGET_DIR"
cp "$SOURCE_DLL" "$TARGET_DIR/"
