#!/usr/bin/env bash
# Machine-wide installation of the security-toolkit CLI on Linux (the counterpart of install.bat).
#   sudo bash install.sh [--sonar] [--mssql] [--no-infra]
# The CLI goes to /opt/security-toolkit/bin and onto PATH through the /usr/local/bin/security-toolkit
# symlink. /usr/local/bin is on the default PATH of every user and service (e.g. the Azure
# DevOps agent).
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
    echo "This installer installs machine-wide, for every user and service account," >&2
    echo "so it needs root: sudo bash install.sh" >&2
    exit 1
fi

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$repo_root"

case "$(uname -m)" in
    x86_64 | amd64) rid=linux-x64 ;;
    aarch64 | arm64) rid=linux-arm64 ;;
    *) echo "Unsupported architecture: $(uname -m)" >&2; exit 1 ;;
esac

# Under sudo the publish runs as the calling user, so bin/ and obj/ don't end up owned by root,
# and the user's own dotnet installation (e.g. ~/.dotnet) can be used.
run_as_caller() {
    if [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != "root" ]; then
        sudo -u "$SUDO_USER" -H env "PATH=$caller_path" "$@"
    else
        "$@"
    fi
}

caller_path="$PATH"
if [ -n "${SUDO_USER:-}" ] && [ "$SUDO_USER" != "root" ]; then
    caller_home=$(getent passwd "$SUDO_USER" | cut -d: -f6)
    caller_path="$PATH:$caller_home/.dotnet"
fi

if ! run_as_caller sh -c 'command -v dotnet' >/dev/null 2>&1; then
    echo "dotnet was not found (the .NET SDK named in global.json is required). Install it, then run this again." >&2
    exit 1
fi

echo "Publishing: dotnet publish src/SecurityToolkit.Cli -c Release -r $rid -p:SelfContained=true"
if ! run_as_caller dotnet publish src/SecurityToolkit.Cli -c Release -r "$rid" -p:SelfContained=true; then
    echo "Publish failed, installation aborted." >&2
    exit 1
fi

target=/opt/security-toolkit/bin
install -d -m 755 "$target"
install -m 755 "src/SecurityToolkit.Cli/bin/Release/net11.0/$rid/publish/security-toolkit" "$target/security-toolkit"
ln -sf "$target/security-toolkit" /usr/local/bin/security-toolkit
echo "Installed: $target/security-toolkit (PATH: /usr/local/bin/security-toolkit)"

# --- Machine-level, project-independent infrastructure (optional) ---
infra_args=()
ask_infra=1
for arg in "$@"; do
    case "$arg" in
        --sonar) infra_args+=(--sonar); ask_infra=0 ;;
        --mssql) infra_args+=(--mssql); ask_infra=0 ;;
        --no-infra) ask_infra=0 ;;
        *) echo "Unknown switch: $arg (supported: --sonar, --mssql, --no-infra)" >&2; exit 1 ;;
    esac
done

ask() {
    local answer
    read -r -p "$1 [y/n] " answer
    case "$answer" in y | Y | yes | YES) return 0 ;; *) return 1 ;; esac
}

if [ "$ask_infra" -eq 1 ] && [ -t 0 ]; then
    echo
    echo "Machine-level infrastructure (needs Docker; usually skipped on a developer machine):"
    if ask "Install/update the shared SonarQube + PostgreSQL on this machine?"; then infra_args+=(--sonar); fi
    if ask "Install/update the shared test SQL Server (MSSQL) on this machine?"; then infra_args+=(--mssql); fi
fi

if [ "${#infra_args[@]}" -gt 0 ]; then
    echo
    echo "Infrastructure: security-toolkit infra install ${infra_args[*]}"
    if ! "$target/security-toolkit" infra install "${infra_args[@]}"; then
        echo "Installing the infrastructure failed, see the messages above. The CLI installation is still valid." >&2
        exit 2
    fi
fi

echo
echo "Done: security-toolkit --help"
