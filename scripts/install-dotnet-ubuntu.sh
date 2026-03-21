#!/usr/bin/env bash
set -euo pipefail

# Reusable Ubuntu installer for the .NET SDK/runtimes needed to build SDK-style
# projects, plus Mono tooling used by this repo's AssemblyPublicizer flow.
#
# Source references:
# - https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-install
# - https://learn.microsoft.com/en-us/dotnet/core/install/linux-ubuntu-decision
#
# Notes:
# - On Ubuntu 22.04+ this script prefers Ubuntu feeds / Ubuntu .NET backports,
#   which is the current Microsoft guidance.
# - On Ubuntu versions earlier than 22.04 it falls back to the Microsoft feed.
# - PowerShell is intentionally not installed here; this repo's dependency
#   helpers use pwsh, but that's a separate product and package source decision.

SCRIPT_NAME="$(basename "$0")"
PREFERENCE_FILE="/etc/apt/preferences.d/packages-microsoft-dotnet"

CHANNELS=("10.0" "9.0" "8.0")
INSTALL_DOTNET_RUNTIME=1
INSTALL_ASPNET_RUNTIME=1
INSTALL_MONO=1
REPAIR_MIXUP=0

usage() {
  cat <<EOF
Usage: $SCRIPT_NAME [options]

Installs .NET SDKs/runtimes on Ubuntu, plus Mono tooling by default.

Options:
  --channels "10.0 9.0 8.0"  Space-separated .NET channels to install.
  --no-runtime               Skip dotnet-runtime packages.
  --no-aspnet               Skip aspnetcore-runtime packages.
  --no-mono                 Skip Mono tooling.
  --repair-mixup            Remove existing dotnet/aspnet/netstandard packages
                            before reinstalling from the chosen feed.
  -h, --help                Show this help text.

Examples:
  $SCRIPT_NAME
  $SCRIPT_NAME --channels "10.0 8.0"
  $SCRIPT_NAME --channels "8.0" --no-aspnet
EOF
}

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

version_ge() {
  dpkg --compare-versions "$1" ge "$2"
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --channels)
        [[ $# -ge 2 ]] || die "--channels requires a value"
        read -r -a CHANNELS <<<"$2"
        shift 2
        ;;
      --no-runtime)
        INSTALL_DOTNET_RUNTIME=0
        shift
        ;;
      --no-aspnet)
        INSTALL_ASPNET_RUNTIME=0
        shift
        ;;
      --no-mono)
        INSTALL_MONO=0
        shift
        ;;
      --repair-mixup)
        REPAIR_MIXUP=1
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

require_ubuntu() {
  [[ -r /etc/os-release ]] || die "/etc/os-release not found"
  # shellcheck disable=SC1091
  source /etc/os-release

  [[ "${ID:-}" == "ubuntu" ]] || die "This script only supports Ubuntu"
  [[ -n "${VERSION_ID:-}" ]] || die "Unable to determine Ubuntu version"

  UBUNTU_VERSION="$VERSION_ID"
  UBUNTU_CODENAME="${VERSION_CODENAME:-}"
  ARCH="$(dpkg --print-architecture)"

  log "Detected Ubuntu ${UBUNTU_VERSION} (${UBUNTU_CODENAME:-unknown codename}), arch ${ARCH}"
}

install_common_prereqs() {
  run_as_root apt-get update
  run_as_root apt-get install -y \
    apt-transport-https \
    ca-certificates \
    curl \
    gnupg \
    software-properties-common \
    wget
}

ensure_ubuntu_backports_if_needed() {
  if [[ "$UBUNTU_VERSION" == "22.04" || "$UBUNTU_VERSION" == "24.04" ]]; then
    if ! grep -Rqs "ppa.launchpadcontent.net/dotnet/backports" /etc/apt/sources.list /etc/apt/sources.list.d 2>/dev/null; then
      log "Adding Ubuntu .NET backports PPA"
      run_as_root add-apt-repository -y ppa:dotnet/backports
    else
      log "Ubuntu .NET backports PPA already configured"
    fi
  fi
}

configure_microsoft_dotnet_pin() {
  log "Pinning Microsoft-origin .NET packages away from APT selection"
  run_as_root tee "$PREFERENCE_FILE" >/dev/null <<'EOF'
Package: dotnet* aspnet* netstandard*
Pin: origin "packages.microsoft.com"
Pin-Priority: -10
EOF
}

ensure_microsoft_repo_if_needed() {
  if version_ge "$UBUNTU_VERSION" "24.04"; then
    die "Ubuntu ${UBUNTU_VERSION} should use Ubuntu feeds/backports, not the Microsoft feed"
  fi

  [[ "$ARCH" == "amd64" ]] || die "Microsoft's Ubuntu .NET feed only supports x64/amd64"

  if dpkg -s packages-microsoft-prod >/dev/null 2>&1; then
    log "Microsoft package repository already configured"
    return
  fi

  local tmpdir
  tmpdir="$(mktemp -d)"
  trap 'rm -rf "$tmpdir"' RETURN

  log "Registering Microsoft package repository"
  wget "https://packages.microsoft.com/config/ubuntu/${UBUNTU_VERSION}/packages-microsoft-prod.deb" \
    -O "${tmpdir}/packages-microsoft-prod.deb"
  run_as_root dpkg -i "${tmpdir}/packages-microsoft-prod.deb"
  rm -f "${tmpdir}/packages-microsoft-prod.deb"
}

repair_mixup_if_requested() {
  if [[ "$REPAIR_MIXUP" -ne 1 ]]; then
    return
  fi

  log "Removing existing dotnet/aspnet/netstandard packages to reset package ownership"
  run_as_root apt-get remove -y 'dotnet*' 'aspnet*' 'netstandard*' || true
  run_as_root apt-get update
}

install_package_set() {
  local packages=()
  local channel

  for channel in "${CHANNELS[@]}"; do
    packages+=("dotnet-sdk-${channel}")
    if [[ "$INSTALL_DOTNET_RUNTIME" -eq 1 ]]; then
      packages+=("dotnet-runtime-${channel}")
    fi
    if [[ "$INSTALL_ASPNET_RUNTIME" -eq 1 ]]; then
      packages+=("aspnetcore-runtime-${channel}")
    fi
  done

  if [[ "$INSTALL_MONO" -eq 1 ]]; then
    packages+=("mono-complete")
  fi

  log "Installing packages: ${packages[*]}"
  run_as_root apt-get update
  run_as_root apt-get install -y "${packages[@]}"
}

print_summary() {
  log "dotnet --list-sdks"
  dotnet --list-sdks || true
  echo
  log "dotnet --list-runtimes"
  dotnet --list-runtimes || true
  echo
  if command -v mono >/dev/null 2>&1; then
    log "mono --version"
    mono --version || true
  fi
}

main() {
  parse_args "$@"
  require_ubuntu
  install_common_prereqs

  if version_ge "$UBUNTU_VERSION" "22.04"; then
    log "Using Ubuntu feeds / Ubuntu .NET backports where applicable"
    ensure_ubuntu_backports_if_needed
    configure_microsoft_dotnet_pin
    repair_mixup_if_requested
  else
    log "Using Microsoft package repository for pre-22.04 Ubuntu"
    if [[ "${#CHANNELS[@]}" -eq 3 && "${CHANNELS[*]}" == "10.0 9.0 8.0" ]]; then
      log "Adjusting default channels for older Ubuntu to 8.0"
      CHANNELS=("8.0")
    fi
    ensure_microsoft_repo_if_needed
    repair_mixup_if_requested
  fi

  install_package_set
  print_summary
  log "Done"
}

main "$@"
