#!/usr/bin/env bash
set -euo pipefail

# ── Defaults ──────────────────────────────────────────────────────────────────
DEFAULT_INSTALL_DIR="/opt/ngino-client"
DEFAULT_SERVICE_NAME="ngino-client"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

INSTALL_DIR="$DEFAULT_INSTALL_DIR"
SERVICE_NAME="$DEFAULT_SERVICE_NAME"

# ── Colors ────────────────────────────────────────────────────────────────────
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[0;33m'
NC='\033[0m'

info()    { echo -e "${GREEN}[INFO]${NC}  $*"; }
warn()    { echo -e "${YELLOW}[WARN]${NC}  $*"; }
err()     { echo -e "${RED}[ERROR]${NC} $*" >&2; }
die()     { err "$@"; exit 1; }

# ── Usage ─────────────────────────────────────────────────────────────────────
usage() {
    cat <<EOF
Usage: $0 [OPTIONS]

Uninstalls the Ngino client systemd service and removes its files.
Does NOT remove .NET SDK, Ollama, or Docker.

Optional:
  --install-dir <dir>  Install directory; defaults to $DEFAULT_INSTALL_DIR
  --service-name <n>   systemd service name; defaults to $DEFAULT_SERVICE_NAME
  -h, --help           Show this help message

Examples:
  $0
  $0 --install-dir /opt/ngino-client --service-name ngino-client
EOF
    exit 0
}

# ── Argument parsing ──────────────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
    case "$1" in
        --install-dir)              INSTALL_DIR="$2"; shift 2 ;;
        --service-name)             SERVICE_NAME="$2"; shift 2 ;;
        -h|--help)                  usage ;;
        *)                          die "Unknown option: $1" ;;
    esac
done

# ── Root check ────────────────────────────────────────────────────────────────
if [[ $EUID -ne 0 ]]; then
    die "This script must be run as root (or with sudo)."
fi

SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
ENV_DIR="/etc/ngino-client"

# ── Stop and disable the service ──────────────────────────────────────────────
if systemctl list-unit-files "$SERVICE_NAME.service" &>/dev/null 2>&1; then
    info "Stopping service $SERVICE_NAME..."
    systemctl stop "$SERVICE_NAME" 2>/dev/null || true
    info "Disabling service $SERVICE_NAME..."
    systemctl disable "$SERVICE_NAME" 2>/dev/null || true
fi

# ── Remove service unit file ──────────────────────────────────────────────────
if [[ -f "$SERVICE_FILE" ]]; then
    info "Removing service unit file $SERVICE_FILE..."
    rm -f "$SERVICE_FILE"
fi

systemctl daemon-reload

# ── Remove install directory ──────────────────────────────────────────────────
if [[ -d "$INSTALL_DIR" ]]; then
    info "Removing install directory $INSTALL_DIR..."
    rm -rf "$INSTALL_DIR"
fi

# ── Remove environment file ───────────────────────────────────────────────────
if [[ -d "$ENV_DIR" ]]; then
    info "Removing environment directory $ENV_DIR..."
    rm -rf "$ENV_DIR"
fi

# ── Done ──────────────────────────────────────────────────────────────────────
echo
info "Uninstall complete."
echo "  Service:     $SERVICE_NAME (stopped, disabled, unit removed)"
echo "  Install dir: $INSTALL_DIR (removed)"
echo "  Env dir:     $ENV_DIR (removed)"
