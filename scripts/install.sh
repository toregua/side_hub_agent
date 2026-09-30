#!/bin/bash
set -e

# SideHub Agent Installer for macOS/Linux
# Requires: Node.js (for PTY terminal support)
#
# Usage: curl -fsSL https://api.sidehub.io/agent/install.sh | bash -s -- [--token <token>] [version]
#   --token  run from the project folder: after installing, configure this folder for the agent and start it

SIDEHUB_API="${SIDEHUB_API:-https://www.sidehub.io/api}"
INSTALL_DIR="${INSTALL_DIR:-/usr/local/lib/sidehub-agent}"
BIN_LINK="/usr/local/bin/sidehub-agent"
PROJECT_DIR="$(pwd)"
CLI_BIN_LINK="/usr/local/bin/sidehub-cli"

# Check Node.js
check_nodejs() {
    if ! command -v node &> /dev/null; then
        echo "❌ Node.js is required but not installed."
        echo "   Install it from https://nodejs.org or via your package manager."
        exit 1
    fi
    echo "✓ Node.js $(node --version) found"
}

# Detect platform
detect_platform() {
    local os=$(uname -s | tr '[:upper:]' '[:lower:]')
    local arch=$(uname -m)

    case "$os" in
        darwin) os="osx" ;;
        linux) os="linux" ;;
        *) echo "OS non supporté: $os" && exit 1 ;;
    esac

    case "$arch" in
        x86_64|amd64) arch="x64" ;;
        arm64|aarch64) arch="arm64" ;;
        *) echo "Architecture non supportée: $arch" && exit 1 ;;
    esac

    echo "${os}-${arch}"
}

# Download and install
install() {
    check_nodejs

    local platform=$(detect_platform)
    local version="latest"
    local token=""
    while [ $# -gt 0 ]; do
        case "$1" in
            --token) token="$2"; shift 2 ;;
            --token=*) token="${1#--token=}"; shift ;;
            *) version="$1"; shift ;;
        esac
    done
    local url

    if [ "$version" = "latest" ]; then
        url="${SIDEHUB_API}/agent/download/${platform}"
    else
        url="${SIDEHUB_API}/agent/download/${platform}/${version}"
    fi

    echo "📦 Téléchargement de SideHub Agent (${platform})..."

    local tmp_dir=$(mktemp -d)
    local archive_file="$tmp_dir/agent.tar.gz"

    if ! curl -fsSL "$url" -o "$archive_file"; then
        echo "Erreur: Impossible de télécharger depuis $url"
        rm -rf "$tmp_dir"
        exit 1
    fi

    echo "📁 Extraction..."
    tar -xzf "$archive_file" -C "$tmp_dir"

    echo "📦 Installation des dépendances Node.js..."
    cd "$tmp_dir/pty-helper" && npm install --silent

    echo "🔧 Installation dans ${INSTALL_DIR}..."
    if [ -w "$(dirname "$INSTALL_DIR")" ]; then
        rm -rf "$INSTALL_DIR"
        mkdir -p "$INSTALL_DIR"
        cp -r "$tmp_dir/"* "$INSTALL_DIR/"
        rm -f "$BIN_LINK"
        ln -s "$INSTALL_DIR/sidehub-agent" "$BIN_LINK"
        if [ -f "$INSTALL_DIR/sidehub-cli" ]; then
            rm -f "$CLI_BIN_LINK"
            ln -s "$INSTALL_DIR/sidehub-cli" "$CLI_BIN_LINK"
        fi
    else
        sudo rm -rf "$INSTALL_DIR"
        sudo mkdir -p "$INSTALL_DIR"
        sudo cp -r "$tmp_dir/"* "$INSTALL_DIR/"
        sudo rm -f "$BIN_LINK"
        sudo ln -s "$INSTALL_DIR/sidehub-agent" "$BIN_LINK"
        if [ -f "$INSTALL_DIR/sidehub-cli" ]; then
            sudo rm -f "$CLI_BIN_LINK"
            sudo ln -s "$INSTALL_DIR/sidehub-cli" "$CLI_BIN_LINK"
        fi
    fi

    rm -rf "$tmp_dir"

    echo ""
    echo "✅ SideHub Agent installé avec succès!"
    echo ""

    if [ -n "$token" ]; then
        # Configure the folder the command was run from (the project), then start the agent in the background.
        cd "$PROJECT_DIR"
        echo "🔗 Configuration de l'agent dans ${PROJECT_DIR}..."
        SIDEHUB_API="$SIDEHUB_API" "$BIN_LINK" setup --token "$token"
        echo ""
        echo "Commandes utiles : sidehub-agent status · sidehub-agent logs · sidehub-agent stop"
    else
        echo "Pour commencer, depuis le dossier de votre projet :"
        echo "  sidehub-agent setup --token <jeton copié depuis SideHub>"
        echo ""
    fi
}

install "$@"
