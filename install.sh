#!/usr/bin/env bash
# A security-toolkit CLI gépi szintű telepítése Linuxra (az install.bat megfelelője).
#   sudo bash install.sh [--sonar] [--mssql] [--no-infra]
# A CLI ide kerül: /opt/security-toolkit/bin, a PATH-ra pedig a /usr/local/bin/security-toolkit
# szimbolikus link teszi. A /usr/local/bin minden felhasználó és szolgáltatás (pl. az Azure
# DevOps agent) alapértelmezett PATH-ján rajta van.
set -euo pipefail

if [ "$(id -u)" -ne 0 ]; then
    echo "Ez a telepítő rendszerszinten telepít, minden felhasználó és szolgáltatás-fiók számára," >&2
    echo "ezért root jog kell hozzá: sudo bash install.sh" >&2
    exit 1
fi

repo_root=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)
cd "$repo_root"

case "$(uname -m)" in
    x86_64 | amd64) rid=linux-x64 ;;
    aarch64 | arm64) rid=linux-arm64 ;;
    *) echo "Nem támogatott architektúra: $(uname -m)" >&2; exit 1 ;;
esac

# sudo alatt a publikálás a hívó felhasználó nevében fut: így a bin/ és obj/ mappák nem lesznek
# root tulajdonúak, és a felhasználó saját dotnet-telepítése (pl. ~/.dotnet) is használható.
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
    echo "A dotnet nem található (a global.json szerinti .NET SDK kell). Telepítsd, majd futtasd újra." >&2
    exit 1
fi

echo "Publikálás: dotnet publish src/SecurityToolkit.Cli -c Release -r $rid -p:SelfContained=true"
if ! run_as_caller dotnet publish src/SecurityToolkit.Cli -c Release -r "$rid" -p:SelfContained=true; then
    echo "Publikálás sikertelen, telepítés megszakítva." >&2
    exit 1
fi

target=/opt/security-toolkit/bin
install -d -m 755 "$target"
install -m 755 "src/SecurityToolkit.Cli/bin/Release/net10.0/$rid/publish/security-toolkit" "$target/security-toolkit"
ln -sf "$target/security-toolkit" /usr/local/bin/security-toolkit
echo "Telepítve: $target/security-toolkit (PATH: /usr/local/bin/security-toolkit)"

# --- Gépi szintű, projektfüggetlen infrastruktúra (opcionális) ---
infra_args=()
ask_infra=1
for arg in "$@"; do
    case "$arg" in
        --sonar) infra_args+=(--sonar); ask_infra=0 ;;
        --mssql) infra_args+=(--mssql); ask_infra=0 ;;
        --no-infra) ask_infra=0 ;;
        *) echo "Ismeretlen kapcsoló: $arg (használható: --sonar, --mssql, --no-infra)" >&2; exit 1 ;;
    esac
done

ask() {
    local answer
    read -r -p "$1 [i/n] " answer
    case "$answer" in i | I | y | Y) return 0 ;; *) return 1 ;; esac
}

if [ "$ask_infra" -eq 1 ] && [ -t 0 ]; then
    echo
    echo "Gépi szintű infrastruktúra (Docker kell hozzá; fejlesztői gépen általában kihagyható):"
    if ask "Közös SonarQube + PostgreSQL telepítése/frissítése ezen a gépen?"; then infra_args+=(--sonar); fi
    if ask "Közös teszt-SQL Server (MSSQL) telepítése/frissítése ezen a gépen?"; then infra_args+=(--mssql); fi
fi

if [ "${#infra_args[@]}" -gt 0 ]; then
    echo
    echo "Infrastruktúra: security-toolkit infra install ${infra_args[*]}"
    if ! "$target/security-toolkit" infra install "${infra_args[@]}"; then
        echo "Az infrastruktúra telepítése sikertelen, lásd a fenti üzeneteket. A CLI telepítése ettől még érvényes." >&2
        exit 2
    fi
fi

echo
echo "Kész: security-toolkit --help"
