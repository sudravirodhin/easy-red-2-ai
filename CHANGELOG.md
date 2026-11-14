# Changelog — Easy Red 2: Battle Hardened

All notable versions of **Easy Red 2: Battle Hardened** (`EasyRed2HumanAI.dll`), newest first.
For pre-compiled downloads, see **[GitHub Releases](https://github.com/sudravirodhin/easy-red-2-ai/releases)**. For the native `GameAssembly.dll` root causes and engineering details behind each system, see **[`docs/design-and-justifications.md`](docs/design-and-justifications.md)**.

---

## `v0.8.6` — Field Radio Inheritance & Retuning, Full-Army Staffing & Attrition Pivots
* **Emergent Field Radio Inheritance & Enemy Radio Frequency Retuning (`GoapAndBlackboardModule.cs`)**:
  * Fixed two engine/scan bugs that left corpse `RadioLoot` at `0`: iterated all `VirtualClothing` slots (`clothItems[0..24]`) instead of stopping at index-0 uniforms, and explicitly called **`Soldier.SetWerable(myRadio, true)`** after `InventoryManager.TakeIntoInventory` (`0x1807E50E0`, which natively only equips `VirtualHelmet` and left looted `VirtualClothing` radio packs sitting unworn in inventory with `IsRadioman() == false`).
  * Opened field-radio recovery to **all cut-off infantry squads** (`!sq.HasRadioman()`, not only squads that spawned with a Radioman), allowing immediate recovery from **fallen friendly comrades (`<= 14m`)** or **floor packs (`<= 12m`)**, and allowing cut-off squads out of immediate combat (`suppression < 8`, `>= 20s` post-cutoff) to capture and retune **enemy radioman packs (`<= 10m`)** onto their faction net (`RadioLoot:` telemetry).
* **Full-Army Objective Staffing, Smoke-Screened Attrition Pivots & Staggered HQ Order Queue (`MacroAndVehiclesModule.cs`)**:
  * Added `CountAlliedGroundSquadsOnObjective` to count all same-side ground forces per objective (connected AI squads, cut-off `Autonomous` squads, Human Player squads, coalition allies, and nearby ground armor) so HQ never mistakes an entrenched meatgrinder for an unstaffed `0`-attacker flag.
  * Upgraded `TryExecuteTeamNetAttritionPivot` with comparative loss-differential hysteresis (`>= 3.0` and `< 55%` losses unless an alternate point has `>= 40%` capture urgency; `AvoidUntil = 105s`, `135s` per-squad cooldown, `2.85x` avoidance weight) and smoke-screened multi-squad breakouts (`CanSquadDisengageForAttritionPivot` + `TryPopSquadRetreatSmoke`).
  * Replaced frame-gated drops with a 1-per-frame RPC-paced `_pendingMacroSquadOrders` queue (`DrainOnePendingMacroSquadOrderIfReady`) that clears stale `timed_order_end` timers and stale covers (`OrderLeaveAllVehiclesAndCovers`).

## `v0.8.5` — 140-Bot Hitch/GC Elimination, Layer-14 Vaulting, Grounded Crew Retreat & Threat-Arc Tank Fear
* **Zero-Hitch 140-Bot Co-op & GC Elimination**: Fixed the `285ms` `FixedSteps: 5` PhysX `CharacterController` catch-up freeze (gating `ForceControllerEnabled()` strictly to active trench/wall guard windows), eliminated `80–128 KB/frame` `.NET 6` `Il2CppObjectBase` wrapper allocations (`ai.path` `CPath`, `targetDestination`, `HeldMortar`, and duplicate `GetComponentsInChildren<MeshFilter>` queries) to stop `120ms` Gen-1 GC pauses, pre-warmed snapshots off hitch frames, and guarded vanilla `BulletInstance.OnHit` native NREs.
* **`InvisibleTrepassableWall` (`Layer 14`) Vaulting Fix**: Resolved `Vault: 0 | Rej: 30,577` by including Layer 14 `InvisibleTrepassableWall` (`AiCore.GetMovementObstacleMask()`), probing thin fence crests cleanly (`+0.03m` / `+0.20m`), and synthesizing valid landing positions whenever vanilla passed `Vector3.zero`.
* **Dogfight Anti-Chicken & Gunfire-Driven Leeward Cover**: Tightened `TryExecuteDogfightAntiChickenBreakOff` (`125m–170m` reciprocal merges only + `maxFlyHeight` ceiling) so fighters shoot on-nose; oriented leeward cover against live incoming gunfire (`_gunfireRing` + `TryGetDominantEnemyGunfireDirection`); added `2.4s`/`5.2s` bot + `8.5s` squad stall recovery; and flushed victory surrenders/stats before `SyncWin`.
* **Grounded Pilot/Bomber & Tank Crew `"Retreat"` (`MissionTaskRetreat`)**: Restored and overhauled the on-foot `"Retreat"` objective for ejected/grounded Pilots, `6–10` seat Heavy Bomber crews (`AiCore.IsTrueVehicleCrewSquad` bomber fix), and Human/AI Tank crews across `sp_ME_` maps, manual bailouts, out-of-bounds landings, and `85m` water-surface extraction zones when ditching at sea — without ever despawning planes mid-flight.
* **Armor/Air-Priority Kamikaze & Threat-Arc Tank Fear**: Restricted Japanese *Tokkōtai* Kamikaze strictly to severely crippled aircraft (`>= 80` structural damage or missing wing/structural parts + one-time personality roll) prioritizing enemy armor and aircraft with terrain-cleared high-angle dives; gated infantry Tank Fear (`IsTankThreateningPosition`) to actual tank threat arcs.

## `v0.8.4` — Co-op Battle-Stat Keys, Atomic 3-of-3 Medic Kits, Trench Vaulting & Molotov Engine Seepage
* Fixed remote Co-op client end-of-match casualty/prisoner/lost-vehicle keys (`"BS.0.1"`..`"BS.1.3"` matching native `Int32` boxing).
* Protected fallen Medics within `25m` of friendly human players and transferred all `3` unique `VirtualSyringe` tools atomically (`TakeIntoInventoryBatch` + rollback so dead Medics never keep unusable 2-of-3 partial kits).
* Kept `Soldier.ForceControllerEnabled()` active near steep trench/wall faces to prevent horizontal wall clipping, enabled **trench-exit & battered-revetment vaulting** while keeping barbed wire unvaultable unless wedged `>= 3.5s`.
* Reclassified Molotov/phosphorus grenade shatters from `HitType.Explosion` to `HitType.Fire` (tank-track immunity + upper-hull engine-grille & fuel-tank seepage) and completed the connected 3rd-person `ShadowsOnly` rig in First Person.

## `v0.8.3` — 4K UI Scaling, Spectator `F` View Cycle & Bot Inspector, Objective Bar Factions & MP Stat Sync
* Added automatic 1440p/4K/High-DPI IMGUI scaling (`UiScaleMultiplier` + live `-10% / Auto / +10%` buttons in `F6`).
* Anchored the Spectator Bot Inspector at Top-Center below the Objective Bar (zero `"Free Camera"` overlap, decoupled from Photo Mode UI hide) and bound **`F`** in Spectator / Free Camera to cycle **`Full Tactical` -> `Objectives Only` -> `Clean Cinema`** with live 3D `A`/`B`/`C` world markers.
* Inserted formatted **Faction + Side names** (`Germany (Axis)`, `USSR (Allies)`, etc.) directly into `MissionStatusGUI.mainRow` (`EnableObjectiveBarFactionNames`), fixed Multiplayer Casualties/Prisoners/Lost Vehicles desyncs (`EnableMultiplayerBattleStatSyncFix`), fixed closed infantry squads being misclassified as `IsVehicleCrew`, fixed thin-wall crest vault probes, and eliminated `TacticalFallbackRetreat` chaining, `IsCharging()` self-balk deadlocks, and `0.5s` post-contact casual scavenging.

## `v0.8.2` — Spectator HUD & Live Bot GOAP Inspector, Safe Bandaging, Close-Gunfire Melee Discipline & Action Trace
* Restored live `MissionStatusGUI` and suppressed broken unspawned `PlayerGUI` weapon boxes in Free Camera / Auto Battle Camera, and added the live **Spectator Bot Inspector** (`EnableSpectatorBotInspector`) displaying GOAP Goals, 2-step Action chains, Role/Personality, Veterancy, Net Directive, Squad Order, Suppression, and threat distances.
* Blocked bleeding soldiers from holstering weapons to bandage next to active shooters (`CanSafelyBandageNow`), suppressed needless melee charges into `<= 15m` active gunfire while holding loaded firearms (`boltActionRequired` fix), rejected flat-ground/`TerrainCollider`/sloped-face vaults (`IsValidVaultObstacle`), and added lock-free `BepInEx/BotActionTrace.jsonl` telemetry (`EnableBotActionTrace`).

## `v0.8.1` — Bailed-Out Tank & Vehicle Crew Evacuation Overhaul
* Fixed three vanilla `AIVehicle.RetreatCrew` (`0x18057B980`) bugs: `NullReferenceException` crashes on `sp_ME_` / V2 maps (`currentPhase == null`), null spawn crashes, and silent river/pond retreat-task drops.
* Prevented bailed-out crews from huddling behind 1-slot trees next to burning hulls or mount-looping immobilized tanks; pistol-only crews bolt rearward to dry friendly spawns (`MissionTaskRetreat`) while rifle/SMG-armed crews link up and merge with nearby friendly infantry.

## `v0.8.0` — Continuous "Squint" Vision Curves, Dynamic Human Reaction, Tank Intercoms & 3-Tier Chain of Command
* **Continuous Distance "Squint" Vision & Platform Optics**: Replaced stepped FOV tiers with smooth continuous curves (`<= 12m: 360°`, `12m–34m: ±136° -> ±90°`, `34m–75m: ±90° -> ±56°`, `75m–210m+: ±56° -> ±32°`), giving Tank Commander cupolas `1.45x` binoculars while enclosed tank gunners use narrow `±15°–34°` slits (no `360°` bubble!).
* **Dynamic Human Reaction Time & Tank Commander -> Gunner Intercom (`0.65s–1.48s`)**: Eliminated vanilla's robotic `2.5s` `alertStartTime` statue freeze (`0.18s–0.32s` pre-aimed vs. `0.38s–0.65s+` caught sprinting/flanked) and added realistic Commander/spotter/infantry-escort intercom callout delays + turret slew/lay settle before enclosed gunners engage flank contacts.
* **"Shot-At" Startle Drop & Zero-Bounce Tank Pass-Through**: Removed open-ground suppression sprint overrides (`OnSuppressed` drops to `Crouch`/`Prone` after human startle latency) and replaced discrete nudges around friendly armor with auto-restoring `Physics.IgnoreCollision`.
* **3-Tier Human Chain of Command ("HQ Suggests -> NCO Decides -> Soldier Survives")**: Added NCO casualty balking (`DecayedRecentDeaths` -> refusing suicidal pushes / aborting early charges into local `Anvil` base-of-fire) and Tier 1 `NearFreshCasualty` (`20m` / `18s`) fatal-funnel hazard memory + individual balking (`Balk:` telemetry).

## `v0.7.9` — Sustained 60 FPS Optimization, Committed Fire-and-Movement, Melee Anti-Spin & Objective Attrition Pivots
* Eliminated single-frame spikes (`GoapScavenge` `114.5ms -> <= 0.25ms`, `GoapFloorSnap` `95.5ms -> <= 0.15ms`) via double-buffered chunked snapshots, zero-alloc faction interning, `FixedSteps: 5` guards, and config-gated `EnableHitchFlightRecorder`.
* Overhauled `SoldierAI.IsMoveTime` + `MovementRoutine` into hysteresis-locked `4.8s–6.8s` upright sprints alternated with `3.7s–5.35s` kneeling firing halts (restricting sideways strafe-shooting to point-blank forward CQB).
* Added **Friendly-in-Player-Crosshair Yield**, **`0°` straight-line Point-Blank Melee anti-spinning & crouch stance matching**, **Emergent Team-Net Objective Attrition Pivots** (`AttritionPivotThreshold` per national doctrine), and initial **Connected 3rd-Person FPS Shadow Rig**.

## `v0.7.8` — End-to-End GOAP, Locomotion & Anti-Armor Anti-Paralysis Pass
* Protected raw-position GOAP/retreat/scavenge/radio/drag/lone-wolf moves (`TryGetProtectedBotMoveOverride`) from `2.0s` `SquadOrdersRoutine` rubber-banding, preserved *Pin & Flank* overwatch near objectives, staggered `HoldOverwatchFire` cycles, auto-equipped stowed Slot 1/2 AT launchers against armor, prevented re-throwing smoke into already-smoked sectors, and shielded `17s` retreats from Macro/TeamNet overwrites.

## `v0.7.7` — Bridge Parapets & Bipods, Conquest Ticket Quartermaster, Frag-vs-Armor Gate & 3-Tier Retreats
* Added **Bridge Parapet Cover, Wall Hugging & Bipod Propping** (`IsSafeBridgeDeckStep` + `BipodCheck`), **Host-Authoritative `ConquestEconomy` Net Quartermaster** with protected `ConquestPlayerTicketBuffer` (`135` base, `+75`/extra Co-op player), **Strict Frag-vs-Armor Gate** (`CanThrowableDamageOrFlushVehicle`), **3-Tier Tactical Retreats** (`Regroup_SectorWithdrawal` + NCO retreat smoke + individual break-contact sprints), and **Inside-Circle Cover Clamping** (`0.80 * GetTaskAreaRadius()`).

## `v0.7.6` — `F5` Campaign Unlock Masking (`???`), Enclosed Crew Small-Arms Protection & Tier 2 SL GOAP
* Added vanilla campaign unlock masking (`???`) and `★`/`★★` completion stars to the `F5` Map Picker, protected buttoned-up enclosed tank crews (`>= 8mm`) from `< 12mm` collider-clipping small arms, added Tier 2 Squad Leader Execution GOAP (`Charge` / covert `HoldFire` / `Follow`), and fixed vanilla `ResourcesManager.FactionCanDamageFaction` `NullReferenceException` hitches (`~176ms`).

## `v0.7.5` — Smoke & Water Blood Spray Occlusion (`EnableBloodSmokeWaterOcclusion`)
* Eliminated floating "ghost" `"ImpactFX_flesh"` sprays underwater, across water surfaces, and through smoke screens (swapping cleanly to pooled `"ImpactFX_flesh_noBlood"` with full impact audio preserved) and normalized URP transparent sorting on pooled blood particles.

## `v0.7.4` — `Squad.Join` Lone-Survivor Merging & Emplacement Staffing Anti-Stall
* Switched lone-survivor and bailed-crew merging to ownership-verified, RPC-paced `Squad.Join(self)` + immediate order handoff (`TryLinkUpOrJoinFriendlySquad`), rejected locked/empty/hostile-occupied static guns, and protected emplacement approaches inside `SoldierAI.CalculateMoveOrderDestination`.

## `v0.7.3` — Smooth `10–12 Hz` Multiplayer Aircraft Sync, On-Nose Dogfights & Indirect Mortar Safety
* Fixed Host LOD `rb.velocity` zeroing and raised airborne `PhotonStream` plane sync from `0.40s`/`1.50s` to `~10–12 Hz` (`0.08s–0.10s`) with 100% vanilla wire compatibility, added `2.5s` airborne hot-start, `125m–170m` dogfight break-offs + ceiling, `27m–30m` indirect mortar/artillery danger-close safety, `±52°` 2-man melee pincers, `LostVehicles` crew-bailout attribution, and dual-sided tank roadkills.

## `v0.7.2` — All-Mode / All-216-Mission Dynamic Operations Support & Zero-Alloc Pacing
* Audited all 216 `ME_*` missions and 178 Lua scripts to build `AiCore.ClassifyMissionObjectiveForFaction` supporting Conquest economies, Wave Defense, `#retreat` finales, and custom `AiParams` scripted soldiers; fixed `Squad.TaskCanStillAttract` on deactivated Lua objectives; eliminated debug logger allocations; and added global heavy-work frame staggering + `MaxFrame`/`Hitches` HUD counters.

## `v0.7.1` — Reworked `sp_ME_` Mission Support, Smoke Cooldowns & `F5` Localization
* Enabled Macro Garrisons, Emplacement Staffing, TeamNet Anvils, and `RepairStation` resupply on reworked `sp_ME_` missions where `BattleManager.currentPhase == null`; added `32s` soldier / `22s` squad smoke cooldowns; and localized `F5` titles via `localization.xml`.

## `v0.7.0` — Low-Wall & Trench Peeking, Pillbox Lintel Alignment, Crate Scavenging & Tank Echelons
* Added staggered low-wall pop-up / trench sentry peeking (`3.5s–4.6s` burst rhythm), pillbox lintel & open doorway jamb sidesteps, `DroppableAmmoBox` & 32-slot `AmmoRefillCrate` scavenging with non-blocking bot cooldowns, smooth `MoveFPSOptimized` locomotion, and `40m+` anti-bombing tank echelons.

## `v0.6.5` – `v0.6.8` — Emergent Squad Interactions, Anti-Dogpile Medics, `F5` Map Picker & `90°` Gaze Fix
* **`v0.6.8`**: Switched `F6` to 5 clean tabs, expanded `F10` into a 5-line tactical card, added standalone **`F5` Campaign & Mission Picker**, restored first-person self-shadows, fixed tank crew repairs (`2.35m–3.25m`), tank rock-unsticking, and `TurretGun.SelectBestAmmo` (MG vs. infantry instead of AP).
* **`v0.6.7`**: Added **Emergent Field Radio & Medic Syringe Inheritance**, **Helmet Recovery/Gifting (`VoiceClip.thankYou`)**, **Buddy Ammo/Bandage Borrowing**, **Casualty Dragging (`CarryBodySynched`)**, **Anti-Dogpile `Squad.MedicRoutine`**, and Co-op air superiority `isGrounded` sync.
* **`v0.6.6`**: Zero-GC `AiCore.WorldSnapshot`, eliminating `FindObjectsOfType` / `Squad.AllSquads` hitches and ghost-trigger / live-weapon stealing bugs.
* **`v0.6.5`**: Fixed vanilla `Soldier.AngleToDirection` (`0x18073D4E0`) casting `Vector3` to `Vector2(x, y)` (dropping `Z` and freezing bots `90°` off East/West firefights!), added blind-corner pieing, `320m` audible battle gaze, trench scanning, tank hull cover, role-smart weapon upgrades, and Spectator-safe `VoiceClip.enemyDown` kill callouts.

## `v0.6.0` – `v0.6.4` — 3-Tier Bitmask GOAP, Armor Ballistics, MP Host Governor & Client Roadkills
* **`v0.6.4`**: Realistic **Aircraft Canopy vs. Armored Floor & Tightened Fuselage Spall**, **Enclosed Tank Crew Blast & AP Spall Realism**, **Host-Assist Aircraft Top-Attack AP** (`"bh_ver"` detection for vanilla Co-op clients), and **Multiplayer Client Vehicle Roadkill Fix** (`VehicleWithWheels.OnTriggerEnter`).
* **`v0.6.3`**: **Multiplayer Host Ping Governor**, 2-seat static emplacements, player turret seat priority (`SwitchSeat`), and initial aircraft top-attack roof normalization.
* **`v0.6.2`**: Centralized friendly-fire & danger-close prevention across emplacements, turrets, and grenades.
* **`v0.6.1`**: Squad Leader objective commitment hysteresis, **Player SL Radio Net Objective Suggestions**, Combat Medic / Emplacement de-confliction, and Turret-Only vehicle cover guards.
* **`v0.6.0`**: **F.E.A.R.-Inspired 3-Tier 32-Bit `uint` GOAP Planner**, **BotMicroVariance**, **Shared Squad Blackboard**, **Radioman-Gated Team Intel Net**, **20-Nation Historical Command Doctrines**, **Macro Objective Director**, **Floor/Corpse AT & Supply Scavenging**, **Tank HE Facade Demolition**, **Bailed Crew / Lone Survivor Link-Ups**, and **Japanese *Tokkōtai* Easter Egg**.

## `v0.5.0` – `v0.5.3` — Vision, Cover, Crossing Discipline, 30 Personalities, Veterancy & Obituary
* **`v0.5.2` – `v0.5.3`**: Strategic Door & Window Shutter Discipline, gun-bobbing anti-oscillation locks, Client-Side **Plane Engine Loop De-Throbber**, window-aperture gaze alignment, and defender balance polish.
* **`v0.5.0` – `v0.5.1`**: Bridge & Narrow Crossing Discipline (`SoldierAI.GetSpacingFromSquad` `5m–35m` scatter fix), Shell-Shocked Dynamic Surrender & Comrade Liberation, **Air Superiority First ("Clear Skies Only" CAS)**, **Tank Standoff & Reverse**, **Friendly Vehicle Right-of-Way & Anti-Roadkill**, **Thin-Cover Wood Bullet Penetration**, **15-Minute Veteran Progression (`Pvt -> 1st Sgt` + 3D rank decals)**, **30 Role Personalities**, **Instant Death Screen Obituary + Age**, **`F6` Live Menu**, **`Tab` Quick-Tap Spot**, **64-Char Room Names**, and **Adaptive AI Brain Tick Scaling**.
