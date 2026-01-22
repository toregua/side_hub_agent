#!/bin/bash
set -e

# SideHub Agent Installer for macOS/Linux
# Requires: Node.js (for PTY terminal support)

SIDEHUB_API="${SIDEHUB_API:-https://www.sidehub.io/api}"
INSTALL_DIR="${INSTALL_DIR:-/usr/local/lib/sidehub-agent}"
BIN_LINK="/usr/local/bin/sidehub-agent"

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
    local version="${1:-latest}"
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
    else
        sudo rm -rf "$INSTALL_DIR"
        sudo mkdir -p "$INSTALL_DIR"
        sudo cp -r "$tmp_dir/"* "$INSTALL_DIR/"
        sudo rm -f "$BIN_LINK"
        sudo ln -s "$INSTALL_DIR/sidehub-agent" "$BIN_LINK"
    fi

    rm -rf "$tmp_dir"

    echo ""
    echo "✅ SideHub Agent installé avec succès!"
    echo ""
    echo "Pour commencer:"
    echo "  1. Créez un fichier agent.json avec votre configuration"
    echo "  2. Lancez: sidehub-agent"
    echo ""
}

install "$@"
