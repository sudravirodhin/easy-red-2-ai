#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Easy Red 2: Battle Hardened (v0.9.12) - Experimental macOS Terminal Launcher
# Runs ./run_bepinex.sh directly in Terminal so dyld/Doorstop/BepInEx logs
# are visible immediately during testing.
# ==============================================================================

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd -P)"
GAME_DIR=""

has_installed_bepinex() {
    local dir="$1"
    [ -f "$dir/run_bepinex.sh" ] && [ -f "$dir/libdoorstop.dylib" ]
}

resolve_game_dir() {
    local candidates=()
    if has_installed_bepinex "$SCRIPT_DIR"; then
        candidates+=("$SCRIPT_DIR")
    fi
    if [ -n "${EASY_RED_2_DIR:-}" ]; then
        candidates+=("${EASY_RED_2_DIR%/}")
    fi

    local steam_base="$HOME/Library/Application Support/Steam"
    candidates+=("$steam_base/steamapps/common/Easy Red 2")

    local vdf_path="$steam_base/steamapps/libraryfolders.vdf"
    if [ -f "$vdf_path" ]; then
        while IFS= read -r lib_path; do
            [ -n "$lib_path" ] && candidates+=("$lib_path/steamapps/common/Easy Red 2")
        done < <(awk '
            /"path"/ {
                line = $0
                sub(/.*"path"[[:space:]]+"/, "", line)
                sub(/".*/, "", line)
                gsub(/\\\\/, "\\", line)
                print line
            }
        ' "$vdf_path" 2>/dev/null || true)
    fi

    local cand
    for cand in "${candidates[@]}"; do
        if [ -d "$cand" ] && (has_installed_bepinex "$cand" || [ -d "$cand/_BepInEx_Disabled_Backup" ]); then
            GAME_DIR="$(cd "$cand" && pwd -P)"
            return 0
        fi
    done
    return 1
}

if ! resolve_game_dir; then
    echo "[ERROR] Could not locate an installed Battle Hardened macOS folder."
    echo "        Run Install-BattleHardened-Mac.command first."
    exit 1
fi

if [ -f "$GAME_DIR/_BepInEx_Disabled_Backup/libdoorstop.dylib" ]; then
    echo "[ERROR] BepInEx is currently disabled inside $GAME_DIR/_BepInEx_Disabled_Backup/."
    echo "        Run Uninstall-BattleHardened-Mac.command option [2] to restore it first."
    exit 1
fi

if [ ! -f "$GAME_DIR/run_bepinex.sh" ] || [ ! -f "$GAME_DIR/libdoorstop.dylib" ]; then
    echo "[ERROR] BepInEx files are missing in $GAME_DIR."
    echo "        Run Install-BattleHardened-Mac.command first."
    exit 1
fi

for target in "$GAME_DIR/libdoorstop.dylib" "$GAME_DIR/run_bepinex.sh" "$GAME_DIR/dotnet" "$GAME_DIR/BepInEx"; do
    [ -e "$target" ] && xattr -dr com.apple.quarantine "$target" 2>/dev/null || true
done
chmod +x "$GAME_DIR/run_bepinex.sh"

cd "$GAME_DIR"

echo "============================================================================"
echo "  Easy Red 2: Battle Hardened (v0.9.12) - Experimental macOS Launcher"
echo "  Game Dir : $GAME_DIR"
echo "============================================================================"
echo ""
if [ ! -f "$GAME_DIR/BepInEx/interop/Assembly-CSharp.dll" ]; then
    echo "[First Launch Note] macOS BepInEx/interop/ has not been generated yet."
    echo "                    Expect a 30-60 second pause before the main menu appears."
    echo ""
fi
echo "[Launch] Starting ./run_bepinex.sh (stdout/stderr will appear below)..."
echo ""

exec ./run_bepinex.sh "$@"
