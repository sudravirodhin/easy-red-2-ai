#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Easy Red 2: Battle Hardened - macOS Toggle / Uninstaller
# [1] Play Vanilla Temporarily (moves mod/BepInEx to _BepInEx_Disabled_Backup/)
# [2] Restore Modded Game
# [3] Uninstall Completely (never touches Easy Red 2.app)
# ==============================================================================

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd -P)"
GAME_DIR=""

has_mod_or_app() {
    local dir="$1"
    [ -d "$dir" ] || return 1
    [ -d "$dir/BepInEx" ] || [ -d "$dir/_BepInEx_Disabled_Backup" ] || [ -d "$dir/Easy Red 2.app" ]
}

resolve_game_dir() {
    local candidates=()
    if [ -d "$SCRIPT_DIR/BepInEx" ] || [ -d "$SCRIPT_DIR/_BepInEx_Disabled_Backup" ]; then
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
        if has_mod_or_app "$cand"; then
            GAME_DIR="$(cd "$cand" && pwd -P)"
            return 0
        fi
    done
    return 1
}

if ! resolve_game_dir; then
    echo "[ERROR] Could not locate your Easy Red 2 folder."
    echo "        Place this script in your Easy Red 2 folder or set EASY_RED_2_DIR."
    exit 1
fi

if pgrep -f "Easy Red 2.app/Contents/MacOS/" >/dev/null 2>&1; then
    echo "[ERROR] Easy Red 2 is currently running. Quit the game first."
    exit 1
fi

BACKUP_DIR="$GAME_DIR/_BepInEx_Disabled_Backup"

echo "============================================================================"
echo "  Easy Red 2: Battle Hardened - macOS Toggle / Uninstaller"
echo "  Game Folder: $GAME_DIR"
echo "============================================================================"
echo ""
if [ -d "$BACKUP_DIR" ]; then
    echo "  Status: CURRENTLY DISABLED (Vanilla backup in _BepInEx_Disabled_Backup/)"
elif [ -d "$GAME_DIR/BepInEx" ] || [ -f "$GAME_DIR/libdoorstop.dylib" ]; then
    echo "  Status: MODDED (BepInEx + Battle Hardened installed)"
else
    echo "  Status: CLEAN VANILLA (no BepInEx files found)"
fi
echo ""
echo "  [1] Play Vanilla Temporarily (preserve config, dogtags & mods in backup)"
echo "  [2] Restore Modded Game (from _BepInEx_Disabled_Backup/)"
echo "  [3] Uninstall Completely (remove BepInEx + Battle Hardened files)"
echo "  [4] Exit"
echo ""
printf "Choose an option [1-4]: "
IFS= read -r choice || true

case "$choice" in
    1)
        if [ -d "$BACKUP_DIR" ]; then
            echo "[!] Already disabled in $BACKUP_DIR."
            exit 0
        fi
        mkdir -p "$BACKUP_DIR"
        for item in BepInEx dotnet libdoorstop.dylib .doorstop_version run_bepinex.sh; do
            if [ -e "$GAME_DIR/$item" ]; then
                mv "$GAME_DIR/$item" "$BACKUP_DIR/$item"
            fi
        done
        echo ""
        echo "[OK] Moved BepInEx + mod files into _BepInEx_Disabled_Backup/."
        echo "     REMINDER: If you set Steam Launch Options to use run_bepinex.sh,"
        echo "     clear Steam -> Easy Red 2 -> Properties -> Launch Options so Steam"
        echo "     launches Easy Red 2.app directly in vanilla mode!"
        ;;
    2)
        if [ ! -d "$BACKUP_DIR" ]; then
            echo "[ERROR] No _BepInEx_Disabled_Backup/ folder found to restore."
            exit 1
        fi
        for item in BepInEx dotnet libdoorstop.dylib .doorstop_version run_bepinex.sh; do
            if [ -e "$BACKUP_DIR/$item" ]; then
                rm -rf "$GAME_DIR/$item"
                mv "$BACKUP_DIR/$item" "$GAME_DIR/$item"
            fi
        done
        rmdir "$BACKUP_DIR" 2>/dev/null || true
        [ -f "$GAME_DIR/run_bepinex.sh" ] && chmod +x "$GAME_DIR/run_bepinex.sh"
        echo ""
        echo "[OK] Restored BepInEx + Battle Hardened! Remember to set Steam Launch Options if needed:"
        echo "     \"$GAME_DIR/run_bepinex.sh\" %command%"
        ;;
    3)
        printf "Are you sure you want to delete BepInEx and mod files (Easy Red 2.app is untouched)? [y/N]: "
        IFS= read -r confirm || true
        case "$confirm" in
            y|Y|yes|YES)
                for item in BepInEx dotnet libdoorstop.dylib .doorstop_version run_bepinex.sh _BepInEx_Disabled_Backup Launch-BattleHardened-Mac.command MACOS_EXPERIMENTAL.md; do
                    if [ -e "$GAME_DIR/$item" ]; then
                        rm -rf "$GAME_DIR/$item"
                    fi
                done
                echo ""
                echo "[OK] Uninstalled BepInEx + Battle Hardened cleanly."
                echo "     REMINDER: Clear Steam -> Easy Red 2 -> Properties -> Launch Options if set."
                ;;
            *)
                echo "Cancelled."
                ;;
        esac
        ;;
    4)
        echo "Exited without changes."
        ;;
    *)
        echo "Invalid option: $choice"
        exit 1
        ;;
esac
