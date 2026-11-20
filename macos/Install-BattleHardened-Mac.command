#!/usr/bin/env bash
set -euo pipefail

# ==============================================================================
# Easy Red 2: Battle Hardened (v0.9.12) - Experimental macOS Installer
# Installs BepInEx 6 (BE 791 macOS x64) + EasyRed2HumanAI.dll alongside
# Easy Red 2.app without modifying anything inside Easy Red 2.app.
# ==============================================================================

SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd -P)"
GAME_DIR=""

has_app_bundle() {
    local dir="$1"
    [ -d "$dir" ] || return 1
    if [ -d "$dir/Easy Red 2.app" ]; then
        return 0
    fi
    local first_app=""
    first_app="$(find "$dir" -maxdepth 1 -name '*.app' -type d 2>/dev/null | head -n 1 || true)"
    [ -n "$first_app" ]
}

resolve_game_dir() {
    local candidates=()

    if [ -n "${EASY_RED_2_DIR:-}" ]; then
        candidates+=("${EASY_RED_2_DIR%/}")
    fi

    if has_app_bundle "$SCRIPT_DIR"; then
        candidates+=("$SCRIPT_DIR")
    fi

    local parent_dir
    parent_dir="$(cd "$SCRIPT_DIR/.." && pwd -P)"
    if has_app_bundle "$parent_dir"; then
        candidates+=("$parent_dir")
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
        if has_app_bundle "$cand"; then
            GAME_DIR="$(cd "$cand" && pwd -P)"
            return 0
        fi
    done

    return 1
}

echo "============================================================================"
echo "  Easy Red 2: Battle Hardened (v0.9.12) - Experimental macOS Installer"
echo "============================================================================"
echo ""

if ! resolve_game_dir; then
    echo "[!] Could not auto-detect your Easy Red 2 Steam folder."
    printf "Paste the folder path containing Easy Red 2.app (or press Enter to cancel): "
    IFS= read -r manual_dir || true
    manual_dir="${manual_dir%\"}"
    manual_dir="${manual_dir#\"}"
    manual_dir="${manual_dir%\'}"
    manual_dir="${manual_dir#\'}"
    manual_dir="${manual_dir%/}"
    if [ -n "$manual_dir" ] && has_app_bundle "$manual_dir"; then
        GAME_DIR="$(cd "$manual_dir" && pwd -P)"
    fi
fi

if [ -z "$GAME_DIR" ]; then
    echo "[ERROR] Could not find Easy Red 2.app."
    echo "        Set EASY_RED_2_DIR='/path/to/Easy Red 2' or copy the extracted"
    echo "        files next to Easy Red 2.app and run this installer again."
    exit 1
fi

APP_PATH=""
if [ -d "$GAME_DIR/Easy Red 2.app" ]; then
    APP_PATH="$GAME_DIR/Easy Red 2.app"
else
    APP_PATH="$(find "$GAME_DIR" -maxdepth 1 -name '*.app' -type d 2>/dev/null | head -n 1 || true)"
fi
APP_NAME="$(basename "$APP_PATH")"

if pgrep -f "${APP_NAME}/Contents/MacOS/" >/dev/null 2>&1; then
    echo "[ERROR] $APP_NAME appears to be running right now."
    echo "        Quit Easy Red 2 completely first, then re-run this installer."
    exit 1
fi

if [ -d "$GAME_DIR/_BepInEx_Disabled_Backup" ]; then
    echo "[ERROR] A disabled BepInEx backup exists at:"
    echo "        $GAME_DIR/_BepInEx_Disabled_Backup"
    echo "        Run Uninstall-BattleHardened-Mac.command -> [2] Restore Modded Game"
    echo "        or [3] Uninstall Completely first."
    exit 1
fi

# ------------------------------------------------------------------------------
# Pre-Flight Diagnostic Report (safe, read-only checks on Easy Red 2.app)
# ------------------------------------------------------------------------------
MAC_VER="$(sw_vers -productVersion 2>/dev/null || echo 'unknown')"
CPU_ARCH="$(uname -m 2>/dev/null || echo 'unknown')"
ROSETTA_STATUS="N/A (Intel Mac)"
if [ "$CPU_ARCH" = "arm64" ]; then
    if /usr/bin/arch -x86_64 /usr/bin/true >/dev/null 2>&1; then
        ROSETTA_STATUS="OK (x86_64 execution available)"
    else
        ROSETTA_STATUS="MISSING (run: softwareupdate --install-rosetta)"
    fi
fi

CF_BUNDLE_EXE="$(defaults read "$APP_PATH/Contents/Info" CFBundleExecutable 2>/dev/null || echo '')"
if [ -z "$CF_BUNDLE_EXE" ] && [ -x /usr/libexec/PlistBuddy ]; then
    CF_BUNDLE_EXE="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleExecutable' "$APP_PATH/Contents/Info.plist" 2>/dev/null || echo '')"
fi

MACHO_PATH="$APP_PATH/Contents/MacOS/$CF_BUNDLE_EXE"
FILE_INFO="(missing Mach-O at $MACHO_PATH)"
HAS_X64="no"
if [ -f "$MACHO_PATH" ]; then
    FILE_INFO="$(file -b "$MACHO_PATH" 2>/dev/null | tr '\n' ' ')"
    if file "$MACHO_PATH" 2>/dev/null | grep -q "x86_64"; then
        HAS_X64="yes"
    fi
fi

GAME_ASM_STATUS="MISSING"
if [ -f "$APP_PATH/Contents/Frameworks/GameAssembly.dylib" ]; then
    GAME_ASM_STATUS="Found (Contents/Frameworks/GameAssembly.dylib)"
elif [ -f "$APP_PATH/Contents/Frameworks/GameAssembly.so" ]; then
    GAME_ASM_STATUS="Found (Contents/Frameworks/GameAssembly.so)"
else
    alt_ga="$(find "$APP_PATH" -maxdepth 4 -name 'GameAssembly.*' 2>/dev/null | head -n 1 || true)"
    if [ -n "$alt_ga" ]; then
        GAME_ASM_STATUS="Found (${alt_ga#"$APP_PATH/"})"
    fi
fi

CODESIGN_SUMMARY="$(codesign -dvv "$APP_PATH" 2>&1 | grep -E '^(Identifier|Format|CodeDirectory|Signature|TeamIdentifier|Runtime Version|Flags)' | tr '\n' '; ' || echo 'codesign unavailable')"

echo "=== Pre-Flight Diagnostic Report ==="
echo "  macOS Version     : $MAC_VER ($CPU_ARCH)"
echo "  Rosetta 2         : $ROSETTA_STATUS"
echo "  Game Folder       : $GAME_DIR"
echo "  App Bundle        : $APP_NAME"
echo "  CFBundleExecutable: ${CF_BUNDLE_EXE:-unknown}"
echo "  Binary Arch (file): $FILE_INFO"
echo "  GameAssembly      : $GAME_ASM_STATUS"
echo "  Codesign Summary  : $CODESIGN_SUMMARY"
echo "===================================="
echo ""

if [ "$HAS_X64" != "yes" ]; then
    echo "[WARNING] The game executable does NOT report an x86_64 slice!"
    echo "          This experimental package uses BepInEx-Unity.IL2CPP-macos-x64 (Rosetta 2)."
    echo "          If the binary is arm64-only, ./run_bepinex.sh will fail to start under x86_64."
    echo "          Read MACOS_EXPERIMENTAL.md before proceeding."
    echo ""
fi

if [ "$CPU_ARCH" = "arm64" ] && ! /usr/bin/arch -x86_64 /usr/bin/true >/dev/null 2>&1; then
    echo "[WARNING] Rosetta 2 does not appear to be installed on this Apple Silicon Mac."
    echo "          Install it with: softwareupdate --install-rosetta --agree-to-license"
    echo ""
fi

# Verify required source payload files exist beside this script
for req in "libdoorstop.dylib" "run_bepinex.sh" "BepInEx/plugins/EasyRed2HumanAI.dll" "dotnet/libcoreclr.dylib"; do
    if [ ! -e "$SCRIPT_DIR/$req" ]; then
        echo "[ERROR] Missing package file: $SCRIPT_DIR/$req"
        echo "        Extract the entire EasyRed2HumanAI macOS zip before running the installer."
        exit 1
    fi
done

echo "[1/3] Installing BepInEx 6 + EasyRed2HumanAI.dll (leaving $APP_NAME untouched)..."
if [ "$SCRIPT_DIR" != "$GAME_DIR" ]; then
    mkdir -p "$GAME_DIR/BepInEx/core" "$GAME_DIR/BepInEx/patchers" "$GAME_DIR/BepInEx/plugins" "$GAME_DIR/BepInEx/unity-libs" "$GAME_DIR/BepInEx/config"

    cp -Rf "$SCRIPT_DIR/dotnet" "$GAME_DIR/"
    cp -f "$SCRIPT_DIR/libdoorstop.dylib" "$GAME_DIR/libdoorstop.dylib"
    [ -f "$SCRIPT_DIR/.doorstop_version" ] && cp -f "$SCRIPT_DIR/.doorstop_version" "$GAME_DIR/.doorstop_version"
    cp -f "$SCRIPT_DIR/run_bepinex.sh" "$GAME_DIR/run_bepinex.sh"

    cp -Rf "$SCRIPT_DIR/BepInEx/core/." "$GAME_DIR/BepInEx/core/"
    [ -d "$SCRIPT_DIR/BepInEx/patchers" ] && cp -Rf "$SCRIPT_DIR/BepInEx/patchers/." "$GAME_DIR/BepInEx/patchers/"
    cp -Rf "$SCRIPT_DIR/BepInEx/plugins/." "$GAME_DIR/BepInEx/plugins/"
    [ -d "$SCRIPT_DIR/BepInEx/unity-libs" ] && cp -Rf "$SCRIPT_DIR/BepInEx/unity-libs/." "$GAME_DIR/BepInEx/unity-libs/"

    # Preserve any existing user configs in BepInEx/config
    if [ -d "$SCRIPT_DIR/BepInEx/config" ]; then
        for cfg in "$SCRIPT_DIR/BepInEx/config/"*; do
            [ -e "$cfg" ] || continue
            cfg_base="$(basename "$cfg")"
            if [ ! -e "$GAME_DIR/BepInEx/config/$cfg_base" ]; then
                cp -R "$cfg" "$GAME_DIR/BepInEx/config/$cfg_base"
            fi
        done
    fi

    for helper in "Launch-BattleHardened-Mac.command" "Uninstall-BattleHardened-Mac.command" "MACOS_EXPERIMENTAL.md"; do
        if [ -f "$SCRIPT_DIR/$helper" ]; then
            cp -f "$SCRIPT_DIR/$helper" "$GAME_DIR/$helper"
        fi
    done
fi

echo "[2/3] Configuring run_bepinex.sh for \"$APP_NAME\" (x86_64 / Rosetta 2)..."
RUN_SH="$GAME_DIR/run_bepinex.sh"
TMP_SH="$RUN_SH.tmp.$$"
awk -v app="$APP_NAME" '
    /^executable_name=/ { print "executable_name=\"" app "\""; next }
    /^archpreference=/ { print "archpreference=\"x86_64\""; next }
    { print }
' "$RUN_SH" > "$TMP_SH"
mv "$TMP_SH" "$RUN_SH"

echo "[3/3] Setting executable permissions and clearing Gatekeeper quarantine..."
chmod +x "$GAME_DIR/run_bepinex.sh"
[ -f "$GAME_DIR/Launch-BattleHardened-Mac.command" ] && chmod +x "$GAME_DIR/Launch-BattleHardened-Mac.command"
[ -f "$GAME_DIR/Uninstall-BattleHardened-Mac.command" ] && chmod +x "$GAME_DIR/Uninstall-BattleHardened-Mac.command"

for target in \
    "$GAME_DIR/libdoorstop.dylib" \
    "$GAME_DIR/run_bepinex.sh" \
    "$GAME_DIR/dotnet" \
    "$GAME_DIR/BepInEx" \
    "$GAME_DIR/Launch-BattleHardened-Mac.command" \
    "$GAME_DIR/Uninstall-BattleHardened-Mac.command"; do
    if [ -e "$target" ]; then
        xattr -dr com.apple.quarantine "$target" 2>/dev/null || true
    fi
done

echo ""
echo "[OK] Installed Battle Hardened (v0.9.12 Experimental macOS) into:"
echo "     $GAME_DIR"
echo ""
echo "Next steps:"
echo "  1. Recommended first test (shows full dyld/BepInEx console output):"
echo "       \"$GAME_DIR/Launch-BattleHardened-Mac.command\""
echo "     (First launch takes ~30-60s to generate macOS BepInEx/interop/*.dll.)"
echo ""
echo "  2. Or launch directly via Steam -> Easy Red 2 -> Properties -> Launch Options:"
echo "       \"$GAME_DIR/run_bepinex.sh\" %command%"
echo ""
echo "  3. After testing, inspect:"
echo "       $GAME_DIR/BepInEx/LogOutput.log"
echo "     See $GAME_DIR/MACOS_EXPERIMENTAL.md for troubleshooting & report template."
