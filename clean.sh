#!/usr/bin/env bash
set -euo pipefail

SCRIPT_NAME="$(basename "$0")"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$SCRIPT_DIR"

DRY_RUN=0
AUTO_YES=0

usage() {
  cat <<EOF
Usage: $SCRIPT_NAME [options]

Removes local build and dependency artifacts for a fresh build of this repo.

This script removes generated content such as:
- deps/
- raw-deps/
- carbon/
- temp/
- build/
- publish/
- artifacts/
- .vs/
- nested bin/ and obj/ directories under the repo

Options:
  --dry-run   Show what would be removed without deleting anything.
  --yes       Do not prompt for confirmation.
  -h, --help  Show this help text.

Examples:
  ./$SCRIPT_NAME --dry-run
  ./$SCRIPT_NAME --yes
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
      --dry-run)
        DRY_RUN=1
        shift
        ;;
      --yes)
        AUTO_YES=1
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

require_repo_root() {
  [[ -d "${REPO_ROOT}/.git" ]] || die "This script must live at the repository root"
}

gather_paths() {
  local explicit=(
    "${REPO_ROOT}/deps"
    "${REPO_ROOT}/raw-deps"
    "${REPO_ROOT}/carbon"
    "${REPO_ROOT}/temp"
    "${REPO_ROOT}/build"
    "${REPO_ROOT}/publish"
    "${REPO_ROOT}/artifacts"
    "${REPO_ROOT}/.vs"
  )

  TARGETS=()

  local path
  for path in "${explicit[@]}"; do
    if [[ -e "$path" ]]; then
      TARGETS+=("$path")
    fi
  done

  while IFS= read -r path; do
    [[ -n "$path" ]] || continue
    TARGETS+=("$path")
  done < <(
    find "$REPO_ROOT" \
      -path "${REPO_ROOT}/.git" -prune -o \
      -type d \( -name bin -o -name obj \) -print |
      sort
  )
}

print_targets() {
  if [[ "${#TARGETS[@]}" -eq 0 ]]; then
    log "Nothing to clean"
    return
  fi

  log "Paths selected for cleanup:"
  local path
  for path in "${TARGETS[@]}"; do
    printf '  %s\n' "${path#${REPO_ROOT}/}"
  done
}

confirm_if_needed() {
  if [[ "${#TARGETS[@]}" -eq 0 ]]; then
    return
  fi

  if [[ "$DRY_RUN" -eq 1 || "$AUTO_YES" -eq 1 ]]; then
    return
  fi

  local reply
  printf 'Proceed with deletion? [y/N] '
  read -r reply
  case "$reply" in
    y|Y|yes|YES)
      ;;
    *)
      log "Aborted"
      exit 0
      ;;
  esac
}

remove_targets() {
  if [[ "${#TARGETS[@]}" -eq 0 ]]; then
    return
  fi

  if [[ "$DRY_RUN" -eq 1 ]]; then
    log "Dry run only; no files were removed"
    return
  fi

  local path
  for path in "${TARGETS[@]}"; do
    rm -rf -- "$path"
    log "Removed ${path#${REPO_ROOT}/}"
  done
}

main() {
  parse_args "$@"
  require_repo_root
  gather_paths
  print_targets
  confirm_if_needed
  remove_targets
  log "Done"
}

main "$@"
