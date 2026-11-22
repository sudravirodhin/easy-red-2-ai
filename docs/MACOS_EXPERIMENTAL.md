# Easy Red 2: Battle Hardened (`v0.10.3`) — Experimental macOS Build & Reviewer Guide

> **Audience:** Prepared specifically for **Eric** and **Eric's Claude** to inspect **before** running anything.  
> **Status:** Experimental test build — compiled and verified on Windows, packaged with official macOS BepInEx 6 (`x64`), and awaiting first real-hardware smoke test on macOS.

---

## 1. Overview & Trust / Safety Summary

### What Is in `EasyRed2HumanAI-v0.10.3-macOS-Experimental.zip`
Every binary and script inside the zip comes from one of three transparent sources:

| Path in Zip | Source & Purpose |
| :--- | :--- |
| `dotnet/` (incl. `libcoreclr.dylib`), `libdoorstop.dylib`, `.doorstop_version`, `BepInEx/core/` (incl. `libdobby.dylib`, `BepInEx.*.dll`, `0Harmony.dll`, `Il2CppInterop.*.dll`), `BepInEx/patchers/` | **Unmodified official BepInEx 6.0.0-be.791** (`BepInEx-Unity.IL2CPP-macos-x64-6.0.0-be.791+b782da4.zip` from `https://builds.bepinex.dev/projects/bepinex_be/791/`). |
| `run_bepinex.sh` | **Official BE 791 `run_bepinex.sh` with a minimal 5-line macOS fix** (exact diff explained in [Section 3](#3-exact-diff-vs-upstream-run_bepinexsh)). |
| `BepInEx/unity-libs/2022.3.62.zip` | **Official Unity 2022.3.62 base stub libraries** (pre-bundled from `https://unity.bepinex.dev/libraries/2022.3.62.zip` so first-boot IL2CPP interop generation works offline). |
| `BepInEx/plugins/EasyRed2HumanAI.dll` | **Battle Hardened `v0.10.3`** compiled from `src/EasyRed2AI/` (`VerifyPatches`: `194` Harmony signatures & `16` Animator strings verified, `0` mismatches). |
| `Install-BattleHardened-Mac.command`<br>`Launch-BattleHardened-Mac.command`<br>`Uninstall-BattleHardened-Mac.command` | **Plain-text, LF-only (`bash 3.2`-compatible) helper scripts** (readable in any editor; also tracked in `macos/` in the repo). |
| `MACOS_EXPERIMENTAL.md` | This review & smoke-test guide. |

### Safety Guarantees
* **`Easy Red 2.app` is NOT modified** by `Install-BattleHardened-Mac.command`. All files sit *alongside* `Easy Red 2.app` inside the Steam game folder.
* **No Windows files are bundled:** No `winhttp.dll`, no `.bat` files, and **no Windows `BepInEx/interop/*.dll`**. BepInEx must generate macOS-specific interop assemblies from `GameAssembly.dylib` on first boot.
* **Clean 1-step disable / uninstall:** `Uninstall-BattleHardened-Mac.command` backs up or deletes only the BepInEx/mod files beside the `.app` bundle (and preserves your `BepInEx/config/*.cfg` on temporary disable).

---

## 2. Pre-Flight Check for Eric's Claude (Run Read-Only First)

Before installing or running anything, run these **strictly read-only** commands in Terminal (or ask Claude to run them) to inspect Eric's local `Easy Red 2.app`:

```bash
GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Easy Red 2"
# (Adjust GAME_DIR if installed on another drive / Steam library)

sw_vers
uname -m
/usr/bin/arch -x86_64 /usr/bin/true && echo "Rosetta2: OK" || echo "Rosetta2: MISSING"

APP="$GAME_DIR/Easy Red 2.app"
EXE_NAME="$(defaults read "$APP/Contents/Info" CFBundleExecutable)"
echo "CFBundleExecutable=$EXE_NAME"
file "$APP/Contents/MacOS/$EXE_NAME"
ls -la "$APP/Contents/Frameworks/"
codesign -dvv --entitlements - "$APP" 2>&1
```

### 3-Branch Decision Table

| Pre-Flight Result | Meaning | Action |
| :--- | :--- | :--- |
| **Branch A (Expected Happy Path):** `file` shows both `arm64` and `x86_64` (Universal binary), `GameAssembly.dylib` (or `.so`) is present, `Rosetta2: OK`, and `codesign` does **not** enforce strict Library Validation. | The game can run its `x86_64` slice under Rosetta 2 alongside official `BepInEx-Unity.IL2CPP-macos-x64`. | **Proceed to Section 5** (`Install-BattleHardened-Mac.command` + Terminal launch). |
| **Branch B (Code Signing / Library Validation Rejection):** `dyld` aborts on launch with `code signature ... not valid for use in process`, `mapping process and mapped file (non-platform) have different Team IDs`, or `library load disallowed by system policy` (caused by macOS Hardened Runtime / `library-validation`). | `DYLD_INSERT_LIBRARIES` (`libdoorstop.dylib`) cannot inject into a Team-ID-restricted signed binary without either an entitlement override or re-signing a copy. | **Do not modify the `.app` silently.** See the **Opt-In Troubleshooting** box below if Eric explicitly wants to test ad-hoc re-signing a backup. |
| **Branch C (`arm64`-Only Mach-O):** `file` reports **only** `Mach-O 64-bit executable arm64` with **no `x86_64` slice**. | `arch -x86_64` cannot execute an `arm64`-only binary, so `BepInEx-Unity.IL2CPP-macos-x64` (`libcoreclr.dylib` / `libdobby.dylib`) cannot load into it. | **Stop here** and send the pre-flight output back. Next options would be testing an `arm64` Doorstop/BepInEx build or running the Windows Steam build via CrossOver/Whisky. |

<details>
<summary><b>Opt-In Troubleshooting for Branch B (Hardened Runtime / Library Validation Block Only)</b></summary>

Only do this if `Launch-BattleHardened-Mac.command` fails specifically with a `dyld` Team ID / code-signature error **and** Eric explicitly consents:

1. Back up the untouched Steam bundle first:
   ```bash
   cp -R "$GAME_DIR/Easy Red 2.app" "$GAME_DIR/Easy Red 2.app.vanilla.bak"
   ```
2. Ad-hoc re-sign `Easy Red 2.app` (which strips Hardened Runtime / Library Validation so `DYLD_INSERT_LIBRARIES` can load `libdoorstop.dylib`):
   ```bash
   codesign --force --deep --sign - "$GAME_DIR/Easy Red 2.app"
   ```
3. Re-test `"$GAME_DIR/Launch-BattleHardened-Mac.command"`.
4. To restore the exact original Steam-signed app anytime, swap the backup back or use **Steam → Easy Red 2 → Properties → Installed Files → Verify integrity of game files**.

</details>

---

## 3. Exact Diff vs. Upstream `run_bepinex.sh`

The bundled `run_bepinex.sh` is byte-for-byte official BepInEx `6.0.0-be.791` (`b782da4`) **except for 4 targeted fixes** needed so macOS Apple Silicon + Steam launches don't break:

```diff
--- upstream/BepInEx-6.0.0-be.791/run_bepinex.sh
+++ macos-experimental/run_bepinex.sh
@@ -12,13 +12,13 @@
 # LINUX: name of Unity executable
 # MACOS: name of the .app directory
-executable_name=""
+executable_name="Easy Red 2.app"
 
 # MACOS: architectures to run the game as, most preferred first
 # arch runs the game as the first one the executable actually has, so an
 # x86_64-only game still starts under Rosetta with the default below.
 # Set this to "x86_64" or "x86_64,arm64" for a game or a loader whose native
 # dependencies have no arm64 build. It must name at least one architecture:
 # arch exits with "Can't find any plists" on an empty value.
-archpreference="arm64,x86_64"
+archpreference="x86_64"
@@ -286,6 +286,8 @@
 target_assembly="$(abs_path "$target_assembly")"
+coreclr_path="$(abs_path "$coreclr_path")"
+corlib_dir="$(abs_path "$corlib_dir")"
@@ -317,9 +319,9 @@
         inherited_libraries="${DYLD_INSERT_LIBRARIES:-${STEAM_DYLD_INSERT_LIBRARIES}}"
         if [ -z "$inherited_libraries" ]; then
-            dyld_insert_libraries="${doorstop_name}"
+            dyld_insert_libraries="${BASEDIR}/${doorstop_name}"
         else
-            dyld_insert_libraries="${doorstop_name}:${inherited_libraries}"
+            dyld_insert_libraries="${BASEDIR}/${doorstop_name}:${inherited_libraries}"
         fi
```

**Why each line matters:**
1. **`archpreference="x86_64"`**: Upstream defaults to `"arm64,x86_64"`. On Apple Silicon, a Universal `.app` would boot in native `arm64`, immediately failing to load BepInEx's `x86_64`-only `libdoorstop.dylib` / `libcoreclr.dylib` / `libdobby.dylib`. Pinning `x86_64` runs the game cleanly under Rosetta 2.
2. **`executable_name="Easy Red 2.app"`**: Allows launching `./run_bepinex.sh` directly without arguments (and `Install-BattleHardened-Mac.command` automatically updates this if the `.app` name ever differs).
3. **`coreclr_path` / `corlib_dir` `abs_path`**: Ensures CoreCLR paths resolve reliably even if Steam or Finder starts the script with a different working directory.
4. **`${BASEDIR}/${doorstop_name}` in `Darwin*)`**: macOS `dyld` and Gatekeeper often reject bare relative `libdoorstop.dylib` paths in `DYLD_INSERT_LIBRARIES`; passing the absolute path avoids `SIGABRT` / image-not-found crashes while keeping the Linux `LD_PRELOAD` path untouched.

---

## 4. macOS Compatibility in `EasyRed2HumanAI.dll` (`v0.10.3`)

A full codebase audit through `v0.10.3` confirmed that >98% of the mod is platform-agnostic `.NET 6` IL (`Path.Combine`, `BepInEx.Paths`, `UnityEngine`, `HarmonyLib`) with zero new `DllImport`s or hardcoded native struct offsets:
- **Metal-safe rendering**: Mode 4 Command Net uses cross-platform Unity `Universal Render Pipeline/Unlit` / `Sprites/Default` / `Unlit/Color` shaders, and the `v0.10.2` blood/smoke/water occlusion normalizer uses standard Unity `Material.renderQueue` / `Renderer.sortingOrder` properties safe on Metal.
- **Cross-platform Match Settings & Emergent AI**: `ProtectedFloat`/`ProtectedBool` XOR helpers for `F5` Match Settings & the 9 Battle Atmosphere presets, Squad `M`/3D markers (`SquadMapAndWorldMarkers`), and the H.A.R.M. 3-Tier Emergent Decision Engine run as pure `.NET 6` IL (compatible with x86_64 Rosetta 2).

Key cross-platform hardening in `EasyRed2HumanAI.dll`:

1. **Cross-Platform Window Focus & Alt-Tab FPS Cap (`AiCore.cs`)**:
   * Previously used `user32.dll` (`GetForegroundWindow`, `GetWindowThreadProcessId`) + `kernel32.dll` (`GetCurrentProcessId`) every `0.20s`.
   * Now guarded with `if (OperatingSystem.IsWindows())` and falls back cleanly on macOS/Linux to Unity's cross-platform `Application.isFocused`.
2. **Dynamic IL2CPP Struct Field Offsets (`AiCore.cs`, `LocomotionAndVaulting.cs`)**:
   * Previously read hardcoded Windows x64 offsets `+0x570` (`Soldier.targetDestination`), `+0x48` (`SoldierAI.visibleTarget`), and `+0x90` (`SoldierAI.path`).
   * Now resolves each offset once at startup via `IL2CPP.GetIl2CppField` + `IL2CPP.il2cpp_field_get_offset` (keeping the zero-alloc `Marshal.ReadIntPtr` fast path while falling back safely to managed property access if any field fails to resolve).
3. **Configurable `ConfigMenuHotkey` (`VisionConfig.cs`, `ConfigMenuAndHud.cs`)**:
   * `F6` (the 5-Tab Feature & Settings Menu) is now configurable via `ConfigMenuHotkey` in `BepInEx/config/com.antigravity.easyred2.humanai.cfg` (alongside `MapPickerHotkey = F5` and `DogTagArchiveHotkey = F7`).
   * **MacBook Keyboard Tip:** On MacBooks without `Fn`-lock enabled, press **`Fn + F5`..`Fn + F10`**, or change `ConfigMenuHotkey` / `MapPickerHotkey` / `DogTagArchiveHotkey` to e.g. `Backslash`, `Home`, or `Insert` in the `.cfg`.
4. **Startup Diagnostic Banner (`Plugin.cs`)**:
   * Logs one clear summary line into `BepInEx/LogOutput.log` on startup:
     `Platform: macOS (64-bit) | Patches: 194/194 OK (0 failed) | Offsets: targetDest=+0x..., visibleTarget=+0x..., path=+0x...`

---

## 5. Step-by-Step Install, First Boot & Smoke Test Checklist

### Step A: Extract & Install
1. Unzip `EasyRed2HumanAI-v0.10.3-macOS-Experimental.zip` to a temporary folder (e.g., `~/Downloads/ER2-BH-Mac/`).
2. Review the 3 `.command` scripts and `run_bepinex.sh` if desired.
3. Run the installer from Terminal (or double-click it):
   ```bash
   chmod +x ~/Downloads/ER2-BH-Mac/Install-BattleHardened-Mac.command
   ~/Downloads/ER2-BH-Mac/Install-BattleHardened-Mac.command
   ```
   * Copy/save the **Pre-Flight Diagnostic Report** printed at the top!
   * Verify `Easy Red 2.app` itself was untouched and `BepInEx/`, `dotnet/`, `libdoorstop.dylib`, and `run_bepinex.sh` now sit beside it.

### Step B: First Boot (Terminal Launch Recommended First)
Run the Terminal launcher so all `dyld`, `UnityDoorstop`, `CoreCLR`, and `Il2CppInterop` output is visible immediately:
```bash
"$HOME/Library/Application Support/Steam/steamapps/common/Easy Red 2/Launch-BattleHardened-Mac.command"
```
* **Be patient on Launch #1 (~30–60s):** `Il2CppInterop` has to decompile/generate macOS `BepInEx/interop/*.dll` from `GameAssembly.dylib` on the very first run (subsequent boots are normal speed).
* *(Optional Steam launch after Terminal boot succeeds)*: In **Steam → Easy Red 2 → Properties → General → Launch Options**, paste:
  ```bash
  "/path/to/Easy Red 2/run_bepinex.sh" %command%
  ```
  (Use the exact quoted path printed at the end of `Install-BattleHardened-Mac.command`.)

### Step C: Inspect `BepInEx/LogOutput.log`
Once the game reaches the Main Menu (even if you quit right after), inspect:
```text
<GameDir>/BepInEx/LogOutput.log
```
Check specifically for:
1. **`Platform: macOS (64-bit) | Patches: 194/194 OK (0 failed)`**
   * Look closely for any `Failed to apply patch ...` errors!
   * **Watch-items on macOS `Assembly-CSharp.dll`:**
     * **5 compiler-generated coroutine classes:** `_InstantiateAndThrowAfter_d__594`, `_ReloadCR_d__159`, `_SpawnAISquad_d__45`, `_SpawnUnit_d__37`, `_WaitAnthemEnd_d__46` (the 6th coroutine hook, `_MeleeDamageCR_d__489`, was eliminated in `v0.10.3`).
     * **Obfuscated A* pathfinder types:** infantry path reads (`A.d` / `A.g`) and **4 vehicle road/corridor pathing patches on `A.f`** (`A.F.vehicleGrid` / `A.G`). Every patch is wrapped in `SafePatch`, so if the macOS `Assembly-CSharp.dll` differs on any single coroutine or obfuscated symbol, `LogOutput.log` will identify it cleanly without crashing the rest of the mod.
2. **`Offsets: targetDest=+0x..., visibleTarget=+0x..., path=+0x...`**
   * Confirm none say `fallback` (or let us know what offsets printed!).
3. **`Easy Red 2 - Battle Hardened v0.10.3 loaded!`**

### Step D: Quick 5-Minute In-Game Smoke Test
1. **Menus & Hotkeys** (`Fn+F5`..`Fn+F10` on MacBook):
   * Press **`F6`** (or `Fn+F6`) → verify the 5-tab Feature Menu renders cleanly and mouse cursor unlocks.
   * Press **`F5`** → verify the **2-Tab Campaign/Map & Match Settings Picker** window opens, loads campaigns/settings, and shows the 9 Battle Atmosphere presets on Tab 2.
2. **Short Skirmish (any small Normandy/Tunisia mission, 30–60 seconds)**:
   * Verify bots spawn, move into cover, crouch/sprint normally, vault obstacles, and shoot without immediate `NullReferenceException` / `MissingMethodException` spam in `LogOutput.log`.
   * Enter Free Camera and press **`F`** to cycle into **Mode 4 (`Command Net Overhead`)** + toggle **`F8`** OFF/ON to verify live Vanilla A/B Observer rendering on Metal.
   * Press **`F10`** → verify the 6-line Telemetry HUD toast appears (including live `HARM:` decision telemetry).
3. **Window Focus / Alt-Tab (`Cmd+Tab`)**:
   * Switch away from Easy Red 2 (`Cmd+Tab`) and back — verify no freezes or audio/FPS stuck issues.
4. **Multiplayer Co-op (Optional)**:
   * Join a Windows host running `v0.10.3` (or vice versa) and confirm lobby join, scoreboard, and match transition work cleanly.

---

## 6. Test Report Template (Copy-Paste Back)

```markdown
### macOS Experimental Smoke Test Report (v0.10.3)
- **Mac Model / Chip:** (e.g., MacBook Pro M2 Pro / Intel i7)
- **macOS Version:** (e.g., 15.x Sequoia)
- **Pre-Flight Output (`file`, `GameAssembly`, `codesign`):**
  (paste Install script's Pre-Flight Diagnostic Report block)
- **Did `Launch-BattleHardened-Mac.command` boot to Main Menu?** Yes / No (paste Terminal error if No)
- **Needed ad-hoc `codesign` workaround (Branch B)?** Yes / No
- **`BepInEx/LogOutput.log` Diagnostic Line:**
  - `Platform: ... | Patches: ... | Offsets: ...`
  - Any `Failed to apply patch ...` lines? (list them or "None")
  - Any runtime exceptions in `LogOutput.log` during a 1-min skirmish? (attach/paste tail)
- **In-Game Checklist:**
  - [ ] `F6` Config Menu opens & cursor works
  - [ ] `F5` Map Picker opens & lists missions
  - [ ] `F10` Live Telemetry toast shows
  - [ ] `F8` Toggle ON/OFF works
  - [ ] 1-min Skirmish bots move/fight normally
  - [ ] `Cmd+Tab` focus/unfocus works smoothly
  - [ ] Steam Launch Options (`run_bepinex.sh %command%`) tested (Optional)
  - [ ] Co-op with Windows host tested (Optional)
- **Notes / Visual Issues / FPS Feel:**
```
