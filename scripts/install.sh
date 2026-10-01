#!/bin/bash
set -e

# SideHub Agent Installer for macOS/Linux
# Requires: Node.js (for PTY terminal support)
#
# Usage: curl -fsSL https://api.sidehub.io/agent/install.sh | SIDEHUB_SETUP_TOKEN=<token> bash -s -- [version]
#   SIDEHUB_SETUP_TOKEN (or --token <token>, visible in ps)  run from the project folder: after installing,
#   configure this folder for the agent and start it
#   --allow-root  configure and start it even as root (refused by default: the backend would control the machine)
#
#   SIDEHUB_INSTALL_DIR  install folder (default /usr/local/lib/sidehub-agent); an existing folder is only
#   replaced if it holds a previous agent install
#
# The archive is checked against the release's checksums.sha256, downloaded from GitHub Releases
# (SIDEHUB_GITHUB_REPO, default toregua/side_hub_agent); a missing or mismatching checksum aborts the install.
# checksums.sha256 itself must carry a valid signature (checksums.sha256.sig) from the release key below:
# write access to the GitHub release alone is not enough to ship a tampered archive.

# Downloads live under /agent at the API host root (REST routes are under /api): accept either form
SIDEHUB_API="${SIDEHUB_API:-https://api.sidehub.io}"
SIDEHUB_API="${SIDEHUB_API%/}"
SIDEHUB_API="${SIDEHUB_API%/api}"
# Checksums come straight from GitHub Releases, not through the SideHub API proxy that serves the archive:
# a compromised proxy cannot hand out both a tampered archive and a matching checksum.
GITHUB_REPO="${SIDEHUB_GITHUB_REPO:-toregua/side_hub_agent}"
INSTALL_DIR="${SIDEHUB_INSTALL_DIR:-/usr/local/lib/sidehub-agent}"
# Written in every install folder: proves a folder is ours before it is wiped on reinstall
INSTALL_MARKER=".sidehub-agent-install"
BIN_LINK="/usr/local/bin/sidehub-agent"
PROJECT_DIR="$(pwd)"
CLI_BIN_LINK="/usr/local/bin/sidehub-cli"

# Release signing key (RSA, PKCS#1 v1.5 / SHA-256 over checksums.sha256), the private half is the
# RELEASE_SIGNING_KEY secret of the release workflow. Keep in sync with install.ps1 (ReleaseSigningKeyTests).
RELEASE_SIGNING_PUBKEY='-----BEGIN PUBLIC KEY-----
MIIBojANBgkqhkiG9w0BAQEFAAOCAY8AMIIBigKCAYEAt9EUPG9pOyOv9va4UK8v
I4mJA4nPN7GUiE+Cj7aek6OI3WSl1ma5Bdp7rPYj/aYX+XiM4FMGbzqpLwiYauZz
Cr4Xlf4Nka/3AKzvl7PorFXCnj1Y5aSw5A5t7loAPEwC6SIQoCvSFmMnDUcbOKje
t/jhe2aHFk8AoKIhtD1OPP1fYunv9+pmFNYV3RaoTW4KqIvKviJlPOYiJF+eFu1k
2yXQt9js+hzOBztXIlm3FKR9Te/mzEEbdrZo0SCh4r51yxR/n2Gn4SDlHBqSGxrH
NqGSMmXNdqsyQuzb6Pu6fYIweRD7gW4gd7bLRDe7bFXHEI2VGlT+5hUEaAfl8Fbo
oLPHu2P7i+UkiPrDzcehNCQ0LQyyyNEZIxEL63zHWCEsmiXBoRXpaJg94i1Y1Zzm
nh4Q2DlpTCeVzgUGqgLGUaR4CiJdJRnFQlB+9Rm9UsRIJV5RihAW4R/fEGvBX2Nv
0WKMxs9stdwcYmYrk19LJGLlFfpL9c5t9TF0KDB2SiIVAgMBAAE=
-----END PUBLIC KEY-----'
# Releases published before signing: installed on the checksum alone (with a warning). Any later release
# without a signature is refused, so deleting checksums.sha256.sig does not downgrade the check.
LAST_UNSIGNED_VERSION="1.0.61"
# First release whose archive bundles pty-helper's node_modules (npm ci from the lockfile, in the CI).
# Older ones need `npm install` from the registry at install time, running package scripts: refused.
MIN_VERSION="1.0.59"

# Succeeds if version $1 > version $2 (dotted numbers, e.g. 1.0.61)
version_gt() {
    local IFS=.
    local -a a=($1) b=($2)
    local i x y
    for i in 0 1 2 3; do
        x=$((10#${a[i]:-0})); y=$((10#${b[i]:-0}))
        [ "$x" -gt "$y" ] && return 0
        [ "$x" -lt "$y" ] && return 1
    done
    return 1
}

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

# Verify checksums.sha256 against its detached signature with the embedded release key
verify_signature() {
    local checksums_file="$1" tag="$2" tmp_dir="$3"
    local sig_file="$tmp_dir/checksums.sha256.sig" pubkey_file="$tmp_dir/release-signing-key.pem"
    local sig_url="https://github.com/${GITHUB_REPO}/releases/download/${tag}/checksums.sha256.sig"

    local status
    status=$(curl -sSL -o "$sig_file" -w '%{http_code}' "$sig_url") || status="000"
    if [ "$status" = "404" ] && ! version_gt "${tag#v}" "$LAST_UNSIGNED_VERSION"; then
        echo "⚠️  $tag est antérieure aux releases signées : seul le checksum SHA256 est vérifié."
        return 0
    fi
    if [ "$status" != "200" ]; then
        echo "❌ Signature des checksums introuvable ($sig_url, HTTP $status)"
        return 1
    fi

    if ! command -v openssl &> /dev/null; then
        echo "❌ openssl est requis pour vérifier la signature de la release"
        return 1
    fi
    printf '%s\n' "$RELEASE_SIGNING_PUBKEY" > "$pubkey_file"
    if ! openssl dgst -sha256 -verify "$pubkey_file" -signature "$sig_file" "$checksums_file" > /dev/null 2>&1; then
        echo "❌ Signature invalide pour checksums.sha256 ($tag) : release non publiée par SideHub"
        return 1
    fi
    echo "✓ Signature de la release vérifiée ($tag)"
}

# Refuse to wipe a folder that is not a previous agent install (a mistyped SIDEHUB_INSTALL_DIR, /usr/local/lib…)
check_install_dir() {
    case "$INSTALL_DIR" in
        /*) ;;
        *) echo "❌ SIDEHUB_INSTALL_DIR doit être un chemin absolu : $INSTALL_DIR"; return 1 ;;
    esac
    case "$INSTALL_DIR" in
        */../*|*/..|*/./*|*/.) echo "❌ SIDEHUB_INSTALL_DIR ne doit pas contenir . ou .. : $INSTALL_DIR"; return 1 ;;
    esac
    [ -e "$INSTALL_DIR" ] || return 0
    if [ -L "$INSTALL_DIR" ] || [ ! -d "$INSTALL_DIR" ]; then
        echo "❌ $INSTALL_DIR existe et n'est pas un dossier"
        return 1
    fi
    # Empty, ours (marker), or an install from before the marker (agent binary + pty-helper)
    if [ -z "$(ls -A "$INSTALL_DIR")" ] || [ -f "$INSTALL_DIR/$INSTALL_MARKER" ] \
        || { [ -f "$INSTALL_DIR/sidehub-agent" ] && [ -d "$INSTALL_DIR/pty-helper" ]; }; then
        return 0
    fi
    echo "❌ $INSTALL_DIR n'est pas vide et ne contient pas d'installation de SideHub Agent : abandon"
    echo "   Choisissez un autre dossier (SIDEHUB_INSTALL_DIR) ou videz-le vous-même."
    return 1
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

    verify_signature "$checksums_file" "$tag" "$tmp_dir" || return 1

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
    local allow_root=""
    case "${SIDEHUB_ALLOW_ROOT:-}" in 1|true|TRUE|True) allow_root="--allow-root" ;; esac
    while [ $# -gt 0 ]; do
        case "$1" in
            --allow-root) allow_root="--allow-root"; shift ;;
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
    # The tag goes into URLs and messages: a plain version only
    if ! [[ "$tag" =~ ^v[0-9]+(\.[0-9]+){1,3}$ ]]; then
        echo "❌ Version invalide : $version (attendu : 1.0.61 ou v1.0.61)"
        exit 1
    fi
    if version_gt "$MIN_VERSION" "${tag#v}"; then
        echo "❌ $tag n'est plus installable : les versions antérieures à v${MIN_VERSION} installent leurs"
        echo "   dépendances Node.js depuis le registre npm à l'installation. Installez v${MIN_VERSION} ou plus récent."
        exit 1
    fi

    check_install_dir || exit 1

    local asset_name="sidehub-agent-${platform}.tar.gz"
    local url="${SIDEHUB_API}/agent/download/${platform}/${tag}"

    echo "📦 Téléchargement de SideHub Agent ${tag} (${platform})..."

    # Global (not local) so the EXIT trap still sees it once install() has returned
    TMP_DIR=$(mktemp -d)
    trap 'rm -rf "$TMP_DIR"' EXIT
    local archive_file="$TMP_DIR/agent.tar.gz"
    local extract_dir="$TMP_DIR/package"

    if ! curl -fsSL "$url" -o "$archive_file"; then
        echo "Erreur: Impossible de télécharger depuis $url"
        exit 1
    fi

    if ! verify_checksum "$archive_file" "$asset_name" "$tag" "$TMP_DIR"; then
        echo "   Installation annulée : l'archive n'a pas été extraite."
        exit 1
    fi

    echo "📁 Extraction..."
    mkdir -p "$extract_dir"
    tar -xzf "$archive_file" -C "$extract_dir"

    # node_modules ships prebuilt in the verified archive (npm ci from the lockfile, in the release CI):
    # the install never runs npm.
    if [ ! -d "$extract_dir/pty-helper/node_modules/node-pty" ]; then
        echo "❌ L'archive $tag ne contient pas les dépendances Node.js de pty-helper : abandon"
        exit 1
    fi
    echo "✓ Dépendances Node.js incluses dans l'archive"
    touch "$extract_dir/$INSTALL_MARKER"

    echo "🔧 Installation dans ${INSTALL_DIR}..."
    if [ -w "$(dirname "$INSTALL_DIR")" ]; then
        rm -rf "$INSTALL_DIR"
        mkdir -p "$INSTALL_DIR"
        cp -R "$extract_dir/." "$INSTALL_DIR/"
        rm -f "$BIN_LINK"
        ln -s "$INSTALL_DIR/sidehub-agent" "$BIN_LINK"
        if [ -f "$INSTALL_DIR/sidehub-cli" ]; then
            rm -f "$CLI_BIN_LINK"
            ln -s "$INSTALL_DIR/sidehub-cli" "$CLI_BIN_LINK"
        fi
    else
        sudo rm -rf "$INSTALL_DIR"
        sudo mkdir -p "$INSTALL_DIR"
        sudo cp -R "$extract_dir/." "$INSTALL_DIR/"
        sudo rm -f "$BIN_LINK"
        sudo ln -s "$INSTALL_DIR/sidehub-agent" "$BIN_LINK"
        if [ -f "$INSTALL_DIR/sidehub-cli" ]; then
            sudo rm -f "$CLI_BIN_LINK"
            sudo ln -s "$INSTALL_DIR/sidehub-cli" "$CLI_BIN_LINK"
        fi
    fi

    echo ""
    echo "✅ SideHub Agent installé avec succès!"
    echo ""

    if [ -n "$token" ] && [ "$(id -u)" -eq 0 ] && [ -z "$allow_root" ]; then
        # `curl … | sudo bash` with a token: installing system-wide needs root, running the agent must not.
        echo "⛔ Agent non configuré : ce script tourne en root, et l'agent refuse de tourner en root"
        echo "   (le backend SideHub pilote ses terminaux : en root, il contrôlerait toute la machine)."
        echo ""
        echo "   Depuis le dossier du projet, en tant qu'utilisateur non privilégié :"
        echo "     sidehub-agent setup --token-stdin   (puis collez le jeton copié depuis SideHub)"
        echo "   Service systemd (utilisateur dédié) : ${INSTALL_DIR}/contrib/systemd/sidehub-agent@.service"
        echo "   Pour forcer malgré tout : relancez avec --allow-root (ou SIDEHUB_ALLOW_ROOT=1)."
        exit 1
    fi

    if [ -n "$token" ]; then
        # Configure the folder the command was run from (the project), then start the agent in the background.
        cd "$PROJECT_DIR"
        echo "🔗 Configuration de l'agent dans ${PROJECT_DIR}..."
        # Token through the environment, not argv: argv is visible to every user in ps.
        SIDEHUB_API="$SIDEHUB_API" SIDEHUB_SETUP_TOKEN="$token" "$BIN_LINK" setup $allow_root
        echo ""
        echo "Commandes utiles : sidehub-agent status · sidehub-agent logs · sidehub-agent stop"
    else
        echo "Pour commencer, depuis le dossier de votre projet :"
        echo "  sidehub-agent setup --token-stdin   (puis collez le jeton copié depuis SideHub)"
        echo "  (en tant qu'utilisateur non privilégié : l'agent refuse de tourner en root)"
        echo ""
        echo "Modèles de service (systemd, launchd) : ${INSTALL_DIR}/contrib/"
        echo ""
    fi
}

install "$@"
