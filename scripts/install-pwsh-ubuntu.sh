#!/usr/bin/env bash
set -euo pipefail

# Reusable Ubuntu installer for PowerShell 7.
#
# Source references:
# - https://learn.microsoft.com/en-us/powershell/scripting/install/install-ubuntu
# - https://learn.microsoft.com/en-us/dotnet/core/install/linux-package-mixup
#
# Design choice:
# - PowerShell is installed from Microsoft's package repository, which is the
#   preferred installation method in the PowerShell docs.
# - To avoid .NET package mix-ups on Ubuntu systems that use Ubuntu feeds for
#   .NET, this script also pins Microsoft-origin dotnet/aspnet/netstandard
#   packages to a negative priority. That lets `powershell` come from Microsoft
#   while keeping .NET package selection with Ubuntu feeds/backports.

SCRIPT_NAME="$(basename "$0")"
PREFERENCE_FILE="/etc/apt/preferences.d/packages-microsoft-dotnet"

usage() {
  cat <<EOF
Usage: $SCRIPT_NAME [options]

Installs PowerShell 7 on supported Ubuntu LTS releases.

Options:
  --skip-dotnet-pin   Do not add APT pinning for Microsoft .NET packages.
  -h, --help          Show this help text.

Examples:
  $SCRIPT_NAME
  $SCRIPT_NAME --skip-dotnet-pin
EOF
}

SKIP_DOTNET_PIN=0

log() {
  printf '[%s] %s\n' "$SCRIPT_NAME" "$*"
}

die() {
  printf '[%s] ERROR: %s\n' "$SCRIPT_NAME" "$*" >&2
  exit 1
}

run_as_root() {
  if [[ "${EUID}" -eq 0 ]]; then
    "$@"
  else
    sudo "$@"
  fi
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --skip-dotnet-pin)
        SKIP_DOTNET_PIN=1
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

require_supported_ubuntu() {
  [[ -r /etc/os-release ]] || die "/etc/os-release not found"
  # shellcheck disable=SC1091
  source /etc/os-release

  [[ "${ID:-}" == "ubuntu" ]] || die "This script only supports Ubuntu"
  [[ -n "${VERSION_ID:-}" ]] || die "Unable to determine Ubuntu version"

  UBUNTU_VERSION="$VERSION_ID"
  ARCH="$(dpkg --print-architecture)"

  case "$UBUNTU_VERSION" in
    22.04|24.04)
      ;;
    *)
      die "Unsupported Ubuntu version ${UBUNTU_VERSION}. Official PowerShell support currently covers Ubuntu 22.04 and 24.04."
      ;;
  esac

  [[ "$ARCH" == "amd64" ]] || die "This script installs the Microsoft APT package path, which should be treated as amd64/x64-only. Use a manual or archive-based install on other architectures."

  log "Detected Ubuntu ${UBUNTU_VERSION}, arch ${ARCH}"
}

install_prereqs() {
  run_as_root apt-get update
  run_as_root apt-get install -y \
    apt-transport-https \
    ca-certificates \
    software-properties-common \
    wget
}

ensure_microsoft_repo() {
  if dpkg -s packages-microsoft-prod >/dev/null 2>&1; then
    log "Microsoft package repository already configured"
    return
  fi

  local tmpdir
  tmpdir="$(mktemp -d)"
  trap 'rm -rf "$tmpdir"' RETURN

  log "Registering Microsoft package repository"
  wget -q "https://packages.microsoft.com/config/ubuntu/${UBUNTU_VERSION}/packages-microsoft-prod.deb" \
    -O "${tmpdir}/packages-microsoft-prod.deb"
  run_as_root dpkg -i "${tmpdir}/packages-microsoft-prod.deb"
  rm -f "${tmpdir}/packages-microsoft-prod.deb"
}

configure_dotnet_pin() {
  if [[ "$SKIP_DOTNET_PIN" -eq 1 ]]; then
    log "Skipping Microsoft .NET package pinning"
    return
  fi

  log "Pinning Microsoft-origin .NET packages away from APT selection"
  run_as_root tee "$PREFERENCE_FILE" >/dev/null <<'EOF'
Package: dotnet* aspnet* netstandard*
Pin: origin "packages.microsoft.com"
Pin-Priority: -10
EOF
}

install_powershell() {
  run_as_root apt-get update
  run_as_root apt-get install -y powershell
}

print_summary() {
  log "pwsh --version"
  pwsh --version || true
  echo
  log "APT policy for powershell"
  apt-cache policy powershell || true
  echo
  if [[ "$SKIP_DOTNET_PIN" -eq 0 ]]; then
    log "APT pin file written to ${PREFERENCE_FILE}"
  fi
}

main() {
  parse_args "$@"
  require_supported_ubuntu
  install_prereqs
  ensure_microsoft_repo
  configure_dotnet_pin
  install_powershell
  print_summary
  log "Done"
}

main "$@"
