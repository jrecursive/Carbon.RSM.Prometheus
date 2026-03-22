#!/usr/bin/env bash
set -euo pipefail
shopt -s nullglob

# Populate this repo's local build dependency folders from an existing Rust
# dedicated server install on disk. The input path may be a real directory or a
# symlink to one.
#
# Outputs:
# - raw-deps/linux/RustDedicated_Data/Managed/*.dll
# - deps/linux/*.dll
# - carbon/*.dll             (when Carbon managed DLLs are found or specified)

SCRIPT_NAME="$(basename "$0")"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"

SERVER_ROOT=""
CARBON_MANAGED_DIR=""
SKIP_CARBON=0
SKIP_PUBLICIZE=0

RAW_MANAGED_DIR="${REPO_ROOT}/raw-deps/linux/RustDedicated_Data/Managed"
PUBLIC_DEPS_DIR="${REPO_ROOT}/deps/linux"
CARBON_OUTPUT_DIR="${REPO_ROOT}/carbon"

usage() {
  cat <<EOF
Usage: $SCRIPT_NAME --server-root /path/to/RustDedicated [options]

Copies Rust managed DLLs from a local Rust dedicated server install into the
repo layout expected by this project, then publicizes the required assemblies
into deps/linux. If Carbon managed DLLs are found, it also mirrors them into
repo-root carbon/.

Options:
  --server-root PATH         Rust dedicated server root. May be a symlink.
  --carbon-managed-dir PATH  Explicit Carbon managed DLL directory to copy from.
  --skip-carbon              Do not copy Carbon DLLs into repo-root carbon/.
  --skip-publicize           Copy raw managed DLLs only; skip deps/linux output.
  -h, --help                 Show this help text.

Examples:
  $SCRIPT_NAME --server-root ~/servers/rust-live
  $SCRIPT_NAME --server-root ~/servers/rust-live-link
  $SCRIPT_NAME --server-root ~/servers/rust-carbon --carbon-managed-dir ~/servers/rust-carbon/carbon/managed
EOF
}

log() {
  printf '[%s] %s\n' "$SCRIPT_NAME" "$*"
}

die() {
  printf '[%s] ERROR: %s\n' "$SCRIPT_NAME" "$*" >&2
  exit 1
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --server-root)
        [[ $# -ge 2 ]] || die "--server-root requires a value"
        SERVER_ROOT="$2"
        shift 2
        ;;
      --carbon-managed-dir)
        [[ $# -ge 2 ]] || die "--carbon-managed-dir requires a value"
        CARBON_MANAGED_DIR="$2"
        shift 2
        ;;
      --skip-carbon)
        SKIP_CARBON=1
        shift
        ;;
      --skip-publicize)
        SKIP_PUBLICIZE=1
        shift
        ;;
      -h|--help)
        usage
        exit 0
        ;;
      *)
        die "Unknown argument: $1"
        ;;
    esac
  done
}

resolve_server_root() {
  [[ -n "$SERVER_ROOT" ]] || die "--server-root is required"
  [[ -d "$SERVER_ROOT" ]] || die "Server root does not exist: $SERVER_ROOT"

  SERVER_ROOT="$(readlink -f "$SERVER_ROOT")"
  log "Using server root: $SERVER_ROOT"
}

require_tools() {
  command -v cp >/dev/null 2>&1 || die "cp not found"
  command -v find >/dev/null 2>&1 || die "find not found"

  if [[ "$SKIP_PUBLICIZE" -eq 0 ]]; then
    command -v pwsh >/dev/null 2>&1 || die "pwsh is required to populate deps/linux with the repo's publicizing script"
  fi
}

clean_dir_files() {
  local dir="$1"
  local pattern="$2"
  mkdir -p "$dir"
  find "$dir" -maxdepth 1 -type f -name "$pattern" -delete
}

copy_raw_rust_managed() {
  local source_managed_dir="${SERVER_ROOT}/RustDedicated_Data/Managed"
  [[ -d "$source_managed_dir" ]] || die "Rust managed directory not found: $source_managed_dir"

  clean_dir_files "$RAW_MANAGED_DIR" "*.dll"

  local copied=0
  local dll
  for dll in "$source_managed_dir"/*.dll; do
    cp -f "$dll" "$RAW_MANAGED_DIR/"
    copied=$((copied + 1))
  done

  [[ "$copied" -gt 0 ]] || die "No DLLs found in $source_managed_dir"
  log "Copied ${copied} Rust managed DLLs into raw-deps"
}

publicize_rust_managed() {
  if [[ "$SKIP_PUBLICIZE" -eq 1 ]]; then
    log "Skipping deps/linux population because --skip-publicize was set"
    return
  fi

  log "Populating deps/linux via scripts/unprivate-dependencies.ps1"
  (
    cd "$REPO_ROOT"
    pwsh scripts/unprivate-dependencies.ps1 \
      -outputPath "deps/linux/" \
      -inputPath "raw-deps/linux/RustDedicated_Data/Managed"
  )

  local count
  count="$(find "$PUBLIC_DEPS_DIR" -maxdepth 1 -name '*.dll' | wc -l)"
  log "Populated deps/linux with ${count} DLLs"
}

detect_carbon_managed_dir() {
  if [[ -n "$CARBON_MANAGED_DIR" ]]; then
    [[ -d "$CARBON_MANAGED_DIR" ]] || die "Carbon managed directory does not exist: $CARBON_MANAGED_DIR"
    CARBON_MANAGED_DIR="$(readlink -f "$CARBON_MANAGED_DIR")"
    return
  fi

  local candidate
  for candidate in \
    "${SERVER_ROOT}/carbon/managed" \
    "${SERVER_ROOT}/Carbon/managed"; do
    if [[ -d "$candidate" ]]; then
      CARBON_MANAGED_DIR="$(readlink -f "$candidate")"
      return
    fi
  done
}

copy_carbon_managed() {
  if [[ "$SKIP_CARBON" -eq 1 ]]; then
    log "Skipping Carbon DLL copy because --skip-carbon was set"
    return
  fi

  detect_carbon_managed_dir

  if [[ -z "$CARBON_MANAGED_DIR" ]]; then
    log "No Carbon managed directory detected under server root; skipping carbon/ population"
    return
  fi

  clean_dir_files "$CARBON_OUTPUT_DIR" "*.dll"

  local copied=0
  local dll
  for dll in "$CARBON_MANAGED_DIR"/*.dll; do
    cp -f "$dll" "${CARBON_OUTPUT_DIR}/"
    copied=$((copied + 1))
  done

  if [[ "$copied" -eq 0 ]]; then
    log "Carbon managed directory exists but contains no DLLs: $CARBON_MANAGED_DIR"
    return
  fi

  log "Copied ${copied} Carbon DLLs into repo-root carbon/ from $CARBON_MANAGED_DIR"
}

print_next_steps() {
  cat <<EOF

Next steps:
  1. Verify outputs:
     - ${RAW_MANAGED_DIR}
     - ${PUBLIC_DEPS_DIR}
     - ${CARBON_OUTPUT_DIR}
  2. Run:
     ./build-linux.sh
EOF
}

main() {
  parse_args "$@"
  resolve_server_root
  require_tools
  copy_raw_rust_managed
  publicize_rust_managed
  copy_carbon_managed
  print_next_steps
}

main "$@"
