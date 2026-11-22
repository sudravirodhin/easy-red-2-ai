# Performance Benchmarking & Live Self-Verification Guide

You do not have to take any performance claim on faith. ***Easy Red 2: Battle Hardened*** ships with an opt-in, zero-allocation profiler (`HitchProfiler`) and a **Live Performance & Mod CPU Overlay** so anyone can measure the exact millisecond overhead against pure Vanilla mid-match.

---

## 1. Two-Minute Uncapped `F8 OFF` vs. `F8 ON` Benchmark Protocol

Why **VSync OFF**? If your monitor caps at `60 FPS`, a 140-bot battle sitting at `60.0 FPS` hides whether you have `2ms` or `9ms` of CPU headroom left. Uncapping frame rate lets both **`[VANILLA AI — F8 OFF]`** and **`[BATTLE HARDENED]`** run at their true ceiling.

1. **Uncap FPS**: Turn **VSync OFF** (and remove any `60 FPS` frame cap) in *Easy Red 2* video settings.
2. **Load a Heavy Reference Mission**: Launch a dense infantry + armor battle (`120–145` active bots — e.g., an amphibious beach assault or urbanized town map) and settle into Free Camera / Spectator (`F`) or steady gameplay at the front.
3. **Enable Live Telemetry (`F6`)**: Press **`F6` -> `5. Optimizations & QoL` -> `Performance & Diagnostics (Default OFF)`** and enable:
   * **`Live Performance & Mod CPU Overlay (F8 A/B)`** (displays the compact top-right performance pill)
   * **`Hitch Flight Recorder (LogOutput + 15s Benchmarks)`** (writes `15s` `[HitchSummary 15s]` baselines and any `>28ms` `[HitchAutopsy]` breakdown to `BepInEx\LogOutput.log`)
4. **Record `Battle Hardened (F8 ON)` (`60–90s`)**: Let 4–6 rolling `[HitchSummary 15s]` windows log while the battle rages. Take your screenshot (`PrintScreen` / Steam `F12`) while **`[BATTLE HARDENED]`** is green in the top-right pill.
5. **Flip `F8` to `Pure Vanilla (F8 OFF)` (`60–90s`)**: Press **`F8`** without restarting or moving cameras. *Battle Hardened* automatically waits `1.0s` for config/toast settle and resets all rolling windows so no summary window ever straddles both modes. Wait `60–90s` (`4–6` windows) and capture your matching screenshot while **`[VANILLA AI — F8 OFF]`** is active (`Mod CPU: 0.00ms/f`).
6. **Generate the Markdown & BBCode Comparison Table (or Query Bot Traces)**:
   ```powershell
   dotnet tools/gen_interop/InspectInterop.dll --hitch-compare
   dotnet tools/gen_interop/InspectInterop.dll --trace <BotId|sq:SquadId|ev:EventKind>
   ```
   (`--hitch-compare` reads `BepInEx\LogOutput.log` safely with shared read access and outputs copy-pasteable GitHub Markdown and NexusMods BBCode blocks; `--trace` inspects individual bots, squads, or events in `BepInEx\BotActionTrace.jsonl` when `Bot Action Trace` is enabled.)

7. **Turn Both Toggles Back `OFF`** in `F6` when finished playing so normal matches stay clutter-free (`0.00ms` profiler work when disabled).

---

## 2. What Every Metric Means

| Metric | How It Is Measured | Why It Matters |
| :--- | :--- | :--- |
| **`Avg FPS` / `Avg Frame ms`** | Hardware `Stopwatch` ticks across full main-thread frames (`Time.unscaledDeltaTime`), focused gameplay only | Excludes loading screens, paused menus, and Alt-Tab background throttling (`45 FPS` Host idle cap). |
| **`1% Low` / `P99`** | 99th-percentile single-frame duration across all focused gameplay frames in each `15s` window (`ms` and `FPS`) | Smoothness is defined by tail frames, not averages. Lower `P99 ms` means fewer micro-stutters. |
| **`Max Frame` / `Hitches (>28ms)`** | Peak focused frame time + count of frames exceeding `28.0ms` | Flags any single-frame GC pause, pathfinding spike, or explosion burst. |
| **`Mod CPU (ms/f)`** | Exact non-double-counted `Stopwatch` inclusive time (`BeginModScope` / `EndModScope`) across all pure-mod hooks | Separates *Battle Hardened* AI + UI CPU time from vanilla engine rendering, physics, and vanilla routines. Drops to `0.00 ms/f` on `F8 OFF`. |
| **`Top:` Subsystems** | Leaf/subsystem buckets (`CanSeeMod`, `GoapSoldier`, `OnGUI`, `WorldSnapshot`, `CoverAvail`, etc.) sorted by `ms/frame` | Pinpoints the exact subsystem responsible for any CPU cost down to `0.01ms`. |
| **`.NET 6` / `IL2CPP` Alloc (`KB/f`)** | Managed `.NET 6` & Boehm `IL2CPP` heap delta + GC collection counts | Kept tiny via pooled/reused buffers and primitive handle caches (`AiCore.SoldierFastHandles`) so GC pauses stay rare in 45-minute matches. |
| **`PatchErrs`** | Count of exceptions caught cleanly by Harmony prefixes/postfixes (`AiCore.ReportPatchException`) | Always visible in `F10`, the Live Perf Pill, and `[HitchSummary 15s]`; guarantees `0` silent exception spam loops. |

---

## 3. Architectural Performance Guarantees

* **Zero Allocation When Diagnostics Are `OFF`**: Every `HitchProfiler` timing call checks a static `bool Enabled` fast-path and returns immediately (`1` branch), while debug logs gate string interpolation behind `CanEmitDebugLog(...)`.
* **Built-In Anti-Stutter Caps**:
  * **H.A.R.M. 3-Tier Emergent Decision Core**: Zero-allocation bit-packed stimulus/world structs and fixed-size `DangerStamp` / acoustic ring buffers (`1` heap-free evaluation pass).
  * **Cover Exposure Cache**: Spatial cover wall-side probes are octant-cached by `IntPtr` + capped at `<= 40` uncached raycasts per frame.
  * **Progressive Vault Slope Backoff**: Steep `TerrainCollider` slopes back off exponentially (`0.85s -> 1.50s -> 2.35s`) so squads walking up hills never spam obstacle probes every tick.
  * **Co-op & Frame-Time Governors**: Automatic `< 40 FPS` (`> 25ms`) brain-tick backoff, Co-op Host Ping Governor (scales extra ticks down in `4+` player lobbies), `1`-per-frame RPC pacing, and `45 FPS` unfocused Host cap.
* **Zero Long-Session Leak Audit (`VerifyPatches.cs`)**: Every build statically checks that `80/80` static `Dictionary<IntPtr, ...>` caches have explicit `Clear()` / `Remove()` / `PruneDict()` cleanup on scene change, plus `0` IL2CPP reflection/GC footguns.

---

## 4. Low / Mid-Spec CPU Tuning Guide

1. **Check `F5` Bot Scale First**: Raising **Allies / Axis Bot Scale** to `200%–300%` multiplies **vanilla** Unity ragdolls, skeletal animators, `CharacterController`/pathfinding, and Photon network traffic regardless of AI mod settings. Keep Bot Scale at `100%` if CPU-bound.
2. **Keep `Adaptive AI Brain Tick Scaling` (`F6 -> Tab 5`) Enabled or Disabled Depending on Headroom**: It improves responsiveness up to `2` active foot ticks/frame when frame time is healthy (`< 17.5ms` SP / `< 14.5ms` Co-op) and already backs off automatically above `25ms` (`< 40 FPS`). Disabling it locks strictly to vanilla's `1` sequential tick/frame for minimum AI CPU load on older CPUs.
3. **Optional Lighter Settings (`F6`)**: If pushing massive modded maps on an older 4/6-core CPU, turning off `3D Overhead Own-Squad Names`, `Shell-Shock Surrender & Liberation`, or `Battlefield Scavenging` recovers marginal extra headroom while keeping full H.A.R.M. combat & humanized vision intact.
