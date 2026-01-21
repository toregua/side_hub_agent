#!/bin/bash
set -e

# SideHub Agent Installer for macOS/Linux

SIDEHUB_API="${SIDEHUB_API:-https://www.sidehub.io/api}"
INSTALL_DIR="${INSTALL_DIR:-/usr/local/bin}"

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
    local platform=$(detect_platform)
    local version="${1:-latest}"
    local url

    if [ "$version" = "latest" ]; then
        url="${SIDEHUB_API}/agent/download/${platform}"
    else
        url="${SIDEHUB_API}/agent/download/${platform}/${version}"
    fi

    echo "Téléchargement de SideHub Agent (${platform})..."

    local tmp_file=$(mktemp)
    if ! curl -fsSL "$url" -o "$tmp_file"; then
        echo "Erreur: Impossible de télécharger depuis $url"
        rm -f "$tmp_file"
        exit 1
    fi

    chmod +x "$tmp_file"

    echo "Installation dans ${INSTALL_DIR}..."
    if [ -w "$INSTALL_DIR" ]; then
        mv "$tmp_file" "${INSTALL_DIR}/sidehub-agent"
    else
        sudo mv "$tmp_file" "${INSTALL_DIR}/sidehub-agent"
    fi

    echo ""
    echo "SideHub Agent installé avec succès!"
    echo ""
    echo "Pour commencer:"
    echo "  1. Créez un fichier agent.json avec votre configuration"
    echo "  2. Lancez: sidehub-agent"
    echo ""
}

install "$@"
