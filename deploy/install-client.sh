#!/usr/bin/env bash
set -euo pipefail

# ── Defaults ──────────────────────────────────────────────────────────────────
DEFAULT_INSTALL_DIR="/opt/ngino-client"
DEFAULT_SERVICE_NAME="ngino-client"
DEFAULT_UPSTREAM="http://localhost:11434"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

INSTALL_DIR="$DEFAULT_INSTALL_DIR"
SERVICE_NAME="$DEFAULT_SERVICE_NAME"
SERVER_URL=""
TOKEN=""
CLIENT_ID="$(hostname -s 2>/dev/null || echo "linux-client")"
UPSTREAM="$DEFAULT_UPSTREAM"
SKIP_OLLAMA=false

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

Installs the ReverseLlama client as a systemd service on Linux.

Required:
  --server <url>       ReverseLlama server URL (e.g. http://my-server:5050)
  --token <value>      Shared secret token for the server

Optional:
  --client-id <name>   Client identifier; defaults to hostname
  --upstream <url>     Local Ollama URL; defaults to $DEFAULT_UPSTREAM
  --install-dir <dir>  Install directory; defaults to $DEFAULT_INSTALL_DIR
  --service-name <n>   systemd service name; defaults to $DEFAULT_SERVICE_NAME
  --no-ollama          Skip Ollama installation and status check
  -h, --help           Show this help message

Examples:
  $0 --server http://gpu-server:5050 --token "my-secret"
  $0 --server http://gpu-server:5050 --token "my-secret" --no-ollama
EOF
    exit 0
}

# ── Argument parsing ──────────────────────────────────────────────────────────
while [[ $# -gt 0 ]]; do
    case "$1" in
        --server)       SERVER_URL="$2"; shift 2 ;;
        --token)        TOKEN="$2"; shift 2 ;;
        --client-id)    CLIENT_ID="$2"; shift 2 ;;
        --upstream)     UPSTREAM="$2"; shift 2 ;;
        --install-dir)  INSTALL_DIR="$2"; shift 2 ;;
        --service-name) SERVICE_NAME="$2"; shift 2 ;;
        --no-ollama)    SKIP_OLLAMA=true; shift ;;
        -h|--help)      usage ;;
        *)              die "Unknown option: $1" ;;
    esac
done

# ── Prompt for missing required values ───────────────────────────────────────
if [[ -z "$SERVER_URL" ]]; then
    read -rp "ReverseLlama server URL (e.g. http://my-server:5050): " SERVER_URL
fi
if [[ -z "$SERVER_URL" ]]; then
    die "Server URL is required."
fi

if [[ -z "$TOKEN" ]]; then
    read -rsp "Server token: " TOKEN
    echo
fi
if [[ -z "$TOKEN" ]]; then
    die "Token is required."
fi

# ── Root check ────────────────────────────────────────────────────────────────
if [[ $EUID -ne 0 ]]; then
    die "This script must be run as root (or with sudo)."
fi

# ── Find dotnet binary ──────────────────────────────────────────────────────
find_dotnet() {
    local candidates=(
        "$(command -v dotnet 2>/dev/null || true)"
        /usr/share/dotnet/dotnet
        /usr/bin/dotnet
        /usr/local/bin/dotnet
        /usr/local/share/dotnet/dotnet
        "$HOME/.dotnet/dotnet"
    )
    if [[ -n "${SUDO_USER:-}" ]]; then
        local invoke_home
        invoke_home="$(eval echo "~$SUDO_USER")"
        candidates+=("$invoke_home/.dotnet/dotnet")
    fi
    for c in "${candidates[@]}"; do
        if [[ -n "$c" && -x "$c" ]]; then
            echo "$c"
            return 0
        fi
    done
    return 1
}

# ── Check / install .NET 10 SDK ──────────────────────────────────────────────
install_dotnet() {
    info "Installing .NET 10 SDK..."

    if command -v apt-get &>/dev/null; then
        apt-get update -qq
        apt-get install -y -qq wget apt-transport-https

        # Detect distro codename for the Microsoft package repo
        if grep -qi "ubuntu" /etc/os-release 2>/dev/null; then
            DISTRO="ubuntu"
            CODENAME="$(. /etc/os-release && echo "$VERSION_CODENAME")"
            case "$CODENAME" in
                noble|jammy|focal) ;; # supported
                *)
                    warn "Ubuntu $CODENAME may not have .NET 10 packages yet; using latest available."
                    CODENAME="noble"
                    ;;
            esac
        elif grep -qi "debian" /etc/os-release 2>/dev/null; then
            DISTRO="debian"
            CODENAME="$(. /etc/os-release && echo "$VERSION_CODENAME")"
            case "$CODENAME" in
                trixie|bookworm|bullseye) ;;
                *)
                    warn "Debian $CODENAME may not have .NET 10 packages; using latest available."
                    CODENAME="trixie"
                    ;;
            esac
        else
            die "Unsupported distro. Install .NET 10 SDK manually: https://dotnet.microsoft.com/download/dotnet/10.0"
        fi

        # Add Microsoft package repository GPG key and repo
        curl -fsSL "https://packages.microsoft.com/config/$DISTRO/$CODENAME/packages-microsoft-prod.deb" \
            -o /tmp/packages-microsoft-prod.deb
        dpkg -i /tmp/packages-microsoft-prod.deb
        rm -f /tmp/packages-microsoft-prod.deb

        apt-get update -qq
        apt-get install -y -qq dotnet-sdk-10.0

    elif command -v dnf &>/dev/null || command -v yum &>/dev/null; then
        PKG_MGR="dnf"
        command -v dnf &>/dev/null || PKG_MGR="yum"

        # Add Microsoft package repository for RHEL/CentOS/Fedora
        cat > /etc/yum.repos.d/microsoft-prod.repo <<'REPO'
[microsoft-prod]
name=Microsoft Production Repository
baseurl=https://packages.microsoft.com/rhel/9/prod/
enabled=1
gpgcheck=1
gpgkey=https://packages.microsoft.com/keys/microsoft.asc
REPO

        $PKG_MGR install -y dotnet-sdk-10.0

    elif command -v pacman &>/dev/null; then
        die "Arch Linux: install dotnet-sdk from the AUR or manually: https://dotnet.microsoft.com/download/dotnet/10.0"

    else
        die "Unsupported package manager. Install .NET 10 SDK manually: https://dotnet.microsoft.com/download/dotnet/10.0"
    fi

    if ! DOTNET_CMD="$(find_dotnet)"; then
        die ".NET SDK installation succeeded but dotnet binary not found. Please add it to PATH."
    fi

    DOTNET_VER="$("$DOTNET_CMD" --version 2>/dev/null || true)"
    info ".NET SDK installed: $DOTNET_CMD ($DOTNET_VER)"
}

DOTNET_CMD=""
if DOTNET_CMD="$(find_dotnet)"; then
    DOTNET_VER="$("$DOTNET_CMD" --version 2>/dev/null || true)"
    if [[ "$DOTNET_VER" == 10.* ]]; then
        info "dotnet 10 is already installed: $DOTNET_CMD ($DOTNET_VER)"
    else
        warn "dotnet is installed ($DOTNET_VER) but version 10.x is required."
        install_dotnet
    fi
else
    install_dotnet
fi

# ── Check / install Ollama ───────────────────────────────────────────────────
if [[ "$SKIP_OLLAMA" == "false" ]]; then
    if command -v ollama &>/dev/null; then
        info "Ollama is installed: $(command -v ollama)"
        if systemctl is-active --quiet ollama 2>/dev/null; then
            info "Ollama service is running."
        else
            warn "Ollama is installed but not running. Starting..."
            systemctl start ollama
            systemctl enable ollama 2>/dev/null || true
            info "Ollama started."
        fi
    else
        info "Ollama is not installed. Installing..."
        curl -fsSL https://ollama.com/install.sh | sh
        info "Ollama installed."
        systemctl start ollama
        systemctl enable ollama 2>/dev/null || true
        info "Ollama started."
    fi
else
    info "Skipping Ollama check (--no-ollama)."
fi

# ── Build client from source ─────────────────────────────────────────────────
CLIENT_SRC="$REPO_ROOT/src/ReverseLlama.Client"
if [[ ! -d "$CLIENT_SRC" ]]; then
    die "Client source not found at $CLIENT_SRC. Run this script from the repository or pass --install-dir."
fi

ARCH="$(uname -m)"
case "$ARCH" in
    x86_64)  DOTNET_RID="linux-x64" ;;
    aarch64) DOTNET_RID="linux-arm64" ;;
    armv7l)  DOTNET_RID="linux-arm" ;;
    *)       die "Unsupported architecture: $ARCH" ;;
esac

info "Building ReverseLlama client (self-contained, $DOTNET_RID)..."
BUILD_DIR="$(mktemp -d /tmp/ngino-build.XXXXXX)"
trap 'rm -rf "$BUILD_DIR"' EXIT

"$DOTNET_CMD" publish "$CLIENT_SRC/ReverseLlama.Client.csproj" \
    -c Release \
    -r "$DOTNET_RID" \
    --self-contained true \
    -o "$BUILD_DIR"

if [[ ! -f "$BUILD_DIR/ReverseLlama.Client" ]]; then
    die "Build failed. ReverseLlama.Client binary not found in output."
fi

info "Build successful."

# ── Install ───────────────────────────────────────────────────────────────────
info "Installing to $INSTALL_DIR..."
mkdir -p "$INSTALL_DIR"
cp -a "$BUILD_DIR"/. "$INSTALL_DIR/"
chmod +x "$INSTALL_DIR/ReverseLlama.Client"

info "Client installed to $INSTALL_DIR."

# ── Write environment file (avoids shell injection in unit file) ─────────────
ENV_DIR="/etc/ngino-client"
mkdir -p "$ENV_DIR"
printf 'REVERSE_LLAMA_TOKEN=%s\n' "$TOKEN" > "$ENV_DIR/env"
chmod 600 "$ENV_DIR/env"
info "Environment file written to $ENV_DIR/env (mode 0600)."

# ── Create systemd service ───────────────────────────────────────────────────
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"

if systemctl list-unit-files "$SERVICE_NAME.service" &>/dev/null 2>&1; then
    info "Stopping existing service $SERVICE_NAME..."
    systemctl stop "$SERVICE_NAME" 2>/dev/null || true
fi

cat > "$SERVICE_FILE" <<EOF
[Unit]
Description=ReverseLlama Tunnel Client
After=network-online.target
Wants=network-online.target
$([ "$SKIP_OLLAMA" = "false" ] && echo "After=ollama.service")
$([ "$SKIP_OLLAMA" = "false" ] && echo "Wants=ollama.service")

[Service]
Type=simple
ExecStart=$INSTALL_DIR/ReverseLlama.Client --server "$SERVER_URL" --upstream "$UPSTREAM" --client-id "$CLIENT_ID"
Restart=always
RestartSec=5
Environment=DOTNET_CLI_TELEMETRY_OPTOUT=1
Environment=DOTNET_NOLOGO=1
EnvironmentFile=$ENV_DIR/env
WorkingDirectory=$INSTALL_DIR

[Install]
WantedBy=multi-user.target
EOF

systemctl daemon-reload
systemctl enable "$SERVICE_NAME"
systemctl start "$SERVICE_NAME"

sleep 1
if systemctl is-active --quiet "$SERVICE_NAME"; then
    info "Service $SERVICE_NAME is running."
else
    warn "Service $SERVICE_NAME was started but may not be healthy. Check: systemctl status $SERVICE_NAME"
fi

# ── Done ──────────────────────────────────────────────────────────────────────
echo
info "Installation complete."
echo "  Server:      $SERVER_URL"
echo "  Client ID:   $CLIENT_ID"
echo "  Upstream:    $UPSTREAM"
echo "  Service:     $SERVICE_NAME"
echo "  Install dir: $INSTALL_DIR"
echo
echo "  Manage:  systemctl {start|stop|restart|status} $SERVICE_NAME"
echo "  Logs:    journalctl -u $SERVICE_NAME -f"
echo "  Uninstall: systemctl stop $SERVICE_NAME && systemctl disable $SERVICE_NAME && rm $SERVICE_FILE $INSTALL_DIR -rf && systemctl daemon-reload"
