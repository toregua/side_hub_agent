#!/bin/bash
set -e

# SideHub Agent Installer for macOS/Linux
# Requires: Node.js (for PTY terminal support)
#
# Usage: curl -fsSL https://api.sidehub.io/agent/install.sh | SIDEHUB_SETUP_TOKEN=<token> bash -s -- [version]
#   SIDEHUB_SETUP_TOKEN (or --token <token>, visible in ps)  run from the project folder: after installing,
#   configure this folder for the agent and start it
#
# The archive is checked against the release's checksums.sha256, downloaded from GitHub Releases
# (SIDEHUB_GITHUB_REPO, default toregua/side_hub_agent); a missing or mismatching checksum aborts the install.

SIDEHUB_API="${SIDEHUB_API:-https://www.sidehub.io/api}"
# Checksums come straight from GitHub Releases, not through the SideHub API proxy that serves the archive:
# a compromised proxy cannot hand out both a tampered archive and a matching checksum.
GITHUB_REPO="${SIDEHUB_GITHUB_REPO:-toregua/side_hub_agent}"
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

# Resolve the latest release tag from GitHub (redirect of /releases/latest to /releases/tag/<tag>)
resolve_latest_tag() {
    local effective
    effective=$(curl -fsSL -o /dev/null -w '%{url_effective}' "https://github.com/${GITHUB_REPO}/releases/latest") || return 1
    local tag="${effective##*/}"
    case "$tag" in
        v[0-9]*) echo "$tag" ;;
        *) return 1 ;;
    esac
}

sha256_of() {
    if command -v sha256sum &> /dev/null; then
        sha256sum "$1" | awk '{print $1}'
    elif command -v shasum &> /dev/null; then
        shasum -a 256 "$1" | awk '{print $1}'
    else
        return 1
    fi
}

# Verify the archive against the release's checksums.sha256; any failure aborts the install
verify_checksum() {
    local archive_file="$1" asset_name="$2" tag="$3" tmp_dir="$4"
    local checksums_file="$tmp_dir/checksums.sha256"
    local checksums_url="https://github.com/${GITHUB_REPO}/releases/download/${tag}/checksums.sha256"

    if ! curl -fsSL "$checksums_url" -o "$checksums_file"; then
        echo "❌ Impossible de télécharger les checksums depuis $checksums_url"
        return 1
    fi

    # sha256sum format: "<hash>  <name>" (or "<hash> *<name>" in binary mode)
    local expected
    expected=$(awk -v name="$asset_name" '{ f = $2; sub(/^\*/, "", f); if (f == name) { print tolower($1); exit } }' "$checksums_file")
    if [ -z "$expected" ]; then
        echo "❌ Aucun checksum pour $asset_name dans checksums.sha256 ($tag)"
        return 1
    fi

    local actual
    if ! actual=$(sha256_of "$archive_file"); then
        echo "❌ sha256sum ou shasum est requis pour vérifier l'archive"
        return 1
    fi

    if [ "$actual" != "$expected" ]; then
        echo "❌ Checksum invalide pour $asset_name : attendu $expected, obtenu $actual"
        return 1
    fi

    echo "✓ Checksum SHA256 vérifié ($asset_name, $tag)"
}

# Download and install
install() {
    check_nodejs

    local platform=$(detect_platform)
    local version="latest"
    local token="${SIDEHUB_SETUP_TOKEN:-}"
    while [ $# -gt 0 ]; do
        case "$1" in
            --token) token="$2"; shift 2 ;;
            --token=*) token="${1#--token=}"; shift ;;
            *) version="$1"; shift ;;
        esac
    done

    # Pin "latest" to a tag so the archive and its checksum come from the same release
    local tag
    if [ "$version" = "latest" ]; then
        if ! tag=$(resolve_latest_tag); then
            echo "❌ Impossible de déterminer la dernière version depuis https://github.com/${GITHUB_REPO}/releases"
            exit 1
        fi
    else
        tag="v${version#v}"
    fi

    local asset_name="sidehub-agent-${platform}.tar.gz"
    local url="${SIDEHUB_API}/agent/download/${platform}/${tag}"

    echo "📦 Téléchargement de SideHub Agent ${tag} (${platform})..."

    local tmp_dir=$(mktemp -d)
    local archive_file="$tmp_dir/agent.tar.gz"
    local extract_dir="$tmp_dir/package"

    if ! curl -fsSL "$url" -o "$archive_file"; then
        echo "Erreur: Impossible de télécharger depuis $url"
        rm -rf "$tmp_dir"
        exit 1
    fi

    if ! verify_checksum "$archive_file" "$asset_name" "$tag" "$tmp_dir"; then
        echo "   Installation annulée : l'archive n'a pas été extraite."
        rm -rf "$tmp_dir"
        exit 1
    fi

    echo "📁 Extraction..."
    mkdir -p "$extract_dir"
    tar -xzf "$archive_file" -C "$extract_dir"

    echo "📦 Installation des dépendances Node.js..."
    cd "$extract_dir/pty-helper" && npm install --silent

    echo "🔧 Installation dans ${INSTALL_DIR}..."
    if [ -w "$(dirname "$INSTALL_DIR")" ]; then
        rm -rf "$INSTALL_DIR"
        mkdir -p "$INSTALL_DIR"
        cp -r "$extract_dir/"* "$INSTALL_DIR/"
        rm -f "$BIN_LINK"
        ln -s "$INSTALL_DIR/sidehub-agent" "$BIN_LINK"
        if [ -f "$INSTALL_DIR/sidehub-cli" ]; then
            rm -f "$CLI_BIN_LINK"
            ln -s "$INSTALL_DIR/sidehub-cli" "$CLI_BIN_LINK"
        fi
    else
        sudo rm -rf "$INSTALL_DIR"
        sudo mkdir -p "$INSTALL_DIR"
        sudo cp -r "$extract_dir/"* "$INSTALL_DIR/"
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
        # Token through the environment, not argv: argv is visible to every user in ps.
        SIDEHUB_API="$SIDEHUB_API" SIDEHUB_SETUP_TOKEN="$token" "$BIN_LINK" setup
        echo ""
        echo "Commandes utiles : sidehub-agent status · sidehub-agent logs · sidehub-agent stop"
    else
        echo "Pour commencer, depuis le dossier de votre projet :"
        echo "  sidehub-agent setup --token-stdin   (puis collez le jeton copié depuis SideHub)"
        echo ""
    fi
}

install "$@"
