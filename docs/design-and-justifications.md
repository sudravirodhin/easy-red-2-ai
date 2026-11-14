# Why Everything Was Changed — Vanilla Root Causes & Engineering Justifications

This document explains **what vanilla *Easy Red 2* (Game Build `921`, `GameAssembly.dll`) does under the hood**, **why each behavior caused gameplay, fairness, Co-op, or performance issues**, and **how [`EasyRed2HumanAI`](../src/EasyRed2AI/Plugin.cs) (`v0.8.6`) solves it**.

---

## 1. Vision, Camouflage, Reaction Time & Platform Optics ([`VisionAndMemoryModule.cs`](../src/EasyRed2AI/Modules/VisionAndMemoryModule.cs))

### 1.1. Stance Stealth & Individual Firing Reveal (`Soldier.CamoDistanceAwareEnemies` — RVA `0x18073db30`)
* **What Vanilla Did**:
  * When a squad is unspotted (`IsEnemyAwareOfThisSquad == false`), vanilla returns `220m` (Standing), `50m` (Crouching), and `37m` (Prone).
  * However, the moment **any** member of your squad fires a shot or is spotted (`IsEnemyAwareOfThisSquad == true`), vanilla jumps `CamoDistanceAwareEnemies` to a **flat `300.0m` for every member of the squad**, regardless of whether they are standing, crouching, or crawling prone in grass.
* **Why It Needed Changing**:
  * If a single AI rifleman 30 meters behind you fired a shot, every enemy bot within 300m immediately gained the ability to spot you crawling prone in tall grass (`37m -> 300m`). Taking cover or crawling during a firefight had zero stealth value.
* **Our Justification & Fix ([`Soldier_CamoDistanceAwareEnemies_Patch`](../src/EasyRed2AI/Modules/VisionAndMemoryModule.cs#L152-L202))**:
  * We preserve stance stealth during combat (`150m` Standing, `80m` Crouching, `40m` Prone) and apply only a modest `1.20x` squad-awareness multiplier (`48m` prone, `96m` crouched).
  * Instead of punishing the whole squad, [`Soldier_StartFire_Patch`](../src/EasyRed2AI/Modules/VisionAndMemoryModule.cs#L131-L149) records the **individual shooter's `IntPtr`** on weapon fire and applies a `+85m` visibility reveal for `4.0s` (representing muzzle flash, smoke, and gunshot report) to that specific soldier only.

### 1.2. Continuous Human "Squint" Vision Curves & Platform-Distinct Turret Optics (`Creature.CanSee` — RVA `0x180a4afb0` & `TurretAI.CanSee`)
* **What Vanilla Did**:
  * In `Creature.CanSee`, vanilla only checked `AngleToDirection <= 60°` when `dist >= 30m` **AND `!this.isAlerted`** (`if (dist >= 30f && !isAlerted && AngleToDirection(...) > 60f) return false;`).
  * Once alerted or suppressed (`isAlerted == true`), vanilla skipped the angle check completely, granting **360-degree omnidirectional vision out to 300m**—including to buttoned-up tank gunners peering through fixed hull/turret slits.
* **Why It Needed Changing**:
  * Flanking an alerted bot or sneaking into an enclosed tank's rear/flank blind spot at 100m was impossible because alerted units gained 360° vision. Conversely, harsh stepped angle cutoffs at fixed distances created jarring perception cliffs.
* **Our Justification & Fix ([`VisionAndMemoryModule.cs`](../src/EasyRed2AI/Modules/VisionAndMemoryModule.cs))**:
  * **Continuous Human Distance "Squint" (`Creature.CanSee`)**: Replaces binary FOV cliffs with a continuous distance curve (`<= 12m: 360°` close hearing/peripheral awareness; `12m–34m: ±136° -> ±90°` never clamped by distant threat sectors, with dual body + barrel angle checks inside `34m`; `34m–75m: ±90° -> ±56°`; `75m–210m+: ±56° -> ±32°` focused long-range gaze).
  * **Platform-Distinct Turret Optics (`TurretAI.CanSee`)**: Differentiates optical hardware physically: **Tank Commander Cupolas (`TurretCommander`)** scan wide with a **`1.45x` binocular boost**; **Enclosed Tank Gunners** peer through a narrow **`±28°–34° -> ±15°–19°` gun slit** (no 360° bubble); **Static AT/Field Guns** respect their gun shield (`<= 8.5m` close ear awareness + forward arc); and **AA Mounts** scan wide `±90°` sky sectors.

### 1.3. Scope & Turret Optic Magnification Scaling (`Creature.CanSee` & `TurretAI.CanSee`)
* **What Vanilla Did**:
  * A sniper with a 4x–8x scope or a tank gunner looking through magnified turret optics used the exact same `target.CamoDistance()` as an un-scoped SMG trooper.
* **Why It Needed Changing**:
  * Lowering un-scoped infantry sight ranges to realistic human eye distances (`150m/80m/40m`) would unintentionally nerf snipers and tanks unless magnified optics actually extended sight range.
* **Our Justification & Fix ([`GetObserverOpticZoomMultiplier`](../src/EasyRed2AI/Modules/VisionAndMemoryModule.cs#L49-L90))**:
  * Queries `heldGun.AimingFOV()` (or `turret.opticFov`) and scales sight range by `60f / opticFov` (capped at `2.25x`) strictly inside a narrow `25°` half-angle optic cone.

### 1.4. Stationary Threat-Sector Gaze & Window-Aperture Alignment (`SoldierAI.SequentialUpdate`)
* **What Vanilla Did**:
  * Once a stationary defender finishes moving (`StopMove`), vanilla never updates their facing direction until a target is already inside their sight cone, and `SoldierAI.SequentialUpdate` enforces a `2.5s` reaction freeze (`Time.time - alertStartTime > 2.5f`) before acquiring targets beyond `30m`.
  * Indoor bots standing next to a window frequently stared parallel to the interior wall instead of looking out the window.
* **Why It Needed Changing**:
  * Enforcing human FOV cones meant stationary defenders staring at an interior bedroom wall were completely blind to enemies advancing outside their window.
* **Our Justification & Fix ([`TryMaintainThreatSectorGaze`](../src/EasyRed2AI/Modules/VisionAndMemoryModule.cs#L430-L512))**:
  * Stationary defenders orient toward known enemy contact sectors (`175m`), scan for **Window Apertures** (wall at `0.52m` windowsill height + open exterior view at `1.28m` eye height, cached in `_watchWindowCache`), center their gaze squarely out the window, and reduce `alertStartTime` latency from `2.5s` down to `0.15s–0.55s` along their watched sector while leaving quiet flanks open to flanking.

### 1.5. Local `15m` Contact Callouts & Last-Known Suppressive Fire / Grenades
* **What Vanilla Did**:
  * When a target ducked behind a wall or crater lip (`CanSee == false`), bots immediately stopped firing and forgot the contact position.
* **Why It Needed Changing**:
  * Real infantry call out spotted contacts to nearby squadmates (`15m`) and keep machine guns pinned on the cover edge where an enemy just ducked while riflemen toss a grenade to flush them out.
* **Our Justification & Fix ([`RecordAndBroadcastVisualContact`](../src/EasyRed2AI/Modules/VisionAndMemoryModule.cs#L521-L580) & [`TryMaintainSuppressiveFireOnLastKnown`](../src/EasyRed2AI/Modules/VisionAndMemoryModule.cs#L648-L779))**:
  * Shares a `12s` threat-sector gaze vector (never X-ray wallhack targeting) with friendly bots within `15m`, and lets stationary MGs/automatic rifles fire a `3.8s` suppressive burst at unobstructed cover edges (`HasClearSuppressionLineToPoint`) while nearby infantry within throwing range flush the cover with a fragmentation grenade.

### 1.6. Adaptive AI Brain Tick Scheduler (`Creature.ProcessSequentialUpdates` — RVA `0x180a53760`)
* **What Vanilla Did**:
  * `Creature.ProcessSequentialUpdates` (`0x180a53760`) hardcodes `cmp ebp, 1` (`0x180a539b6`), processing only **1 slot** of `Creature.allCreatures` per frame.
  * In a `120–140` unit battle at `60 FPS`, each soldier waits **`2.0–2.5+ seconds`** between `SoldierAI.SequentialUpdate` brain ticks (which drive target acquisition, `MovementRoutine`, cover selection, and weapon checks).
  * Worse still, whenever `Creature.sequentialUpdateIndex` lands on a `Vehicle`, a dead/inactive unit, or a passenger seated inside a landing craft/truck/tank (`Soldier.IsOnVehicle() == true`, where `SoldierAI.SequentialUpdate` immediately returns at `0x18055eabb`), vanilla wastes that entire frame's AI budget on a no-op slot.
* **Our Justification & Fix ([`Creature_ProcessSequentialUpdates_Patch`](../src/EasyRed2AI/Core/AiCore.cs#L276-L370))**:
  * Dynamically scales active on-foot `SoldierAI` brain ticks per frame with battlefield population (`1` tick/frame at `<= 45` units, `2` ticks/frame at `46–95` units, `3` ticks/frame at `96+` units), while skipping non-soldier / seated-passenger slots without counting them against the active foot-soldier quota.
  * Includes an automatic frame-time guard (`Time.unscaledDeltaTime > 0.025f`) that backs off to vanilla's single tick during heavy explosion/physics frames (`< 40 FPS`), cutting AI reaction latency in `120–140` bot Singleplayer and Co-op battles from `2.0–2.5s` down to **`~0.6s`** with zero extra Photon network bandwidth.

---

## 2. Infantry Combat, Stance, Cover & CQB ([`InfantryTacticsModule.cs`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs))

### 2.1. Committed Fire-and-Movement Locomotion, `"Shot-At"` Drop & Low-Wall / Trench / Pillbox Peeking
* **What Vanilla Did**:
  * `SoldierAI.GetFavouriteFightingPose` (`0x18055ba50`) constantly fought with `SoldierAI.MovementRoutine` (`0x18055cc40`) and `DestinationWithoutCover..ctor` (`0x180550bf0`, rolling a **50% chance to force `SoldierPose.Prone`** whenever no cover node was found).
  * Vanilla's `7.0s` `IsMoveTime` clock, `MoveFPSOptimized` `50%` slow-walk/slide gap between `MoveDirectlyToward` ticks, `ShouldLookTargetWhileMoving` sideways strafe-shooting, and casual `<= 60m` firefight strolling created stuttery `crouch -> 1-step run -> shoot -> crouch` loops—while bots behind waist-high sandbags stood exposed instead of ducking between bursts.
* **Why It Needed Changing**:
  * Real infantry sprint upright between cover bounds, drop immediately when shot at in the open, peek up over waist-high walls only to fire bursts, and never stand upright into concrete pillbox headers.
* **Our Justification & Fix ([`InfantryTacticsModule.cs`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs))**:
  * **Committed Fire-and-Movement (`SoldierAI.IsMoveTime` + `MovementRoutine` + `AiCore.MoveBotTowardPosition`)**: Replaces mid-stride crouch-walking with hysteresis-locked **upright forward sprints (`4.8s–6.8s`)** into cover (`<= 12m` uninterrupted finish) alternated with **deliberate kneeling firing halts (`3.7s–5.35s`)**, restricts moving fire strictly to forward CQB (`<= 8.5m` / `<= 16m` charge within `52°`), smooths `MoveFPSOptimized` gaps, and adds blind-corner pieing (`>= 28°` occluded turn within `4.6m`).
  * **`"Shot-At"` Startle Drop & Re-Plan**: Removes the inverted open-field suppression sprint override; `OnSuppressed` expires GOAP `NextReplanTime` after `0.16s–0.43s` human startle, drops to `Crouch`/`Prone`, and scrambles to cover or returns fire.
  * **Low-Wall Pop-Up, Trench Sentry Peeking & Pillbox Lintel Protection**: Bots behind waist-high walls (`~0.75m–1.00m`) stay crouched between jittered `3.5s–4.6s` firing bursts; deep-trench defenders run staggered `3.4s` sentry peeks every `7.2s–8.6s`; and pillbox/attic gunners verify `1.56m`/`1.04m` lintel clearance while locking indoors to functional crouch stances (`2.8s` / `3.5s` pose locks).

### 2.2. Indoor Ceiling Prone Prevention (`EnableIndoorCeilingPronePrevention`)
* **What Vanilla Did**:
  * Suppressed or defending soldiers inside buildings, bunkers, and pillboxes dropped `Prone` directly underneath windowsills (`0.8m` high) or on staircases, blinding themselves and clipping their legs through exterior walls.
* **Why It Needed Changing**:
  * A prone soldier inside a room cannot see or shoot over a windowsill and becomes helpless dead weight.
* **Our Justification & Fix ([`AiCore.HasLowCeilingOverhead`](../src/EasyRed2AI/Core/AiCore.cs#L156-L185))**:
  * Uses a quantized `1m × 1m` spatial cache (`_ceilingCache`, `30s` TTL) to check `ResourcesManager.groundMask` up to `4.2m` overhead and keeps indoor soldiers in a functional `Crouch` stance (`SetBotPose` + `SoldierAI.SequentialUpdate`).

### 2.3. Standing BEHIND Walls Instead of IN FRONT — Gunfire-Driven Leeward Cover & Tank Hull Cover (`CombatCover.IsCoverAvailable`)
* **What Vanilla Did**:
  * `CombatCover` nodes placed along walls or hedges only checked distance to the cover point, frequently placing defenders on the **enemy-facing side of the wall** (with the wall at their back and open air toward the enemy), while exposed gunfire came from flanking fire vectors.
* **Why It Needed Changing**:
  * Defenders stood exposed in front of sandbags and stone walls with their backs pressed against cover, and advancing riflemen had no concept of using idle or wrecked armored hulls (`CoverManager.vehicleCovers`) as mobile cover.
* **Our Justification & Fix ([`InfantryTacticsModule.cs`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs), [`TryGetDominantEnemyGunfireDirection`](../src/EasyRed2AI/Modules/VisionAndMemoryModule.cs))**:
  * **Gunfire-Driven Leeward Cover (`_gunfireRing`)**: Computes dominant incoming enemy gunfire direction (`TryGetDominantEnemyGunfireDirection` with enemy-contact/frontline fallback), probes `+0.46m`/`+0.82m` windward and leeward offsets (`CombatCover.IsCoverAvailable`, budgeted to `40`/frame and cached in `_coverExposureCache`), rejects front-of-wall nodes, wraps `2.85m` around corners, and enables **Tank Hull Cover & Walking-Pace Escort (`0.45–5.2 m/s`)** in the sheltered rear-quarter wake of friendly armor.

### 2.4. Dual Barrel-Height Wall-Shooting Prevention, Auto-Peek & Doorway Jamb Sidesteps (`SoldierAI.RotateAndFireToward`)
* **What Vanilla Did**:
  * `Creature.CanSee` raycasts from the soldier's **head/eyes** (`~1.6m` standing, `~1.0m` crouched, `~0.4m` prone), whereas bullets spawn from the **weapon barrel** (`0.22m` prone, `0.68m` crouched). Furthermore, bots snapped to `CombatCover` behind a wall next to a doorway could never step `0.90m–1.85m` into the jamb/embrasure to clear their barrel.
* **Why It Needed Changing**:
  * Bots saw enemies over a sandbag, windowsill, or beside a doorframe and dumped entire magazines straight into stone/brick.
* **Our Justification & Fix ([`InfantryTacticsModule.cs`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs))**:
  * Checks `AiCore.RaycastEnvironment` from both spine and `heldGun.transform.position` out to `2.2m`. When blocked by low cover, bots automatically step up one stance (`Prone -> Crouch -> Stand`) to peek (`EnableAutoPeekOverCover`) or sidestep `0.90m–1.85m` around door jambs and vertical corners (even while snapped to `CombatCover`).

### 2.5. Point-Blank Bayonet/Melee CQB, Anti-Spin Pincers, Close-Gunfire Discipline & Safe Bandaging (`SoldierAI.SequentialUpdate` & `TryStopBleeding`)
* **What Vanilla Did**:
  * Vanilla only checked melee within a tiny `1.6m` radius, `MeleeWeaponCheck` (`0x18055c8bf`) refused to draw a knife while holding any gun with ammo (`!HasAnyGunWithAmmo`), `MoveDirectlyToward` caused attackers to spiral endlessly on top of crouched/prone targets (`boltActionRequired` even pushed bolt-action riflemen to melee-charge active close gunfire `<= 15m`!), and `TryStopBleeding` holstered the soldier's rifle to bandage even while enemies stood `5m` away shooting at them.
* **Our Justification & Fix ([`InfantryTacticsModule.cs`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs))**:
  * **Melee Anti-Spin, Stance Matching & `±52°` Pincers**: Mounts integrated bayonets once via `integratedBayonet.SetMounted(true)` (zero RPC bursts), caps simultaneous melee attackers to `2` per target on `±52°` pincer angles, closes straight (`0°` offset) and brakes cleanly at bayonet standoff (`Stop(true)` + close yaw damping), matches crouch/prone stance before swinging at low enemies, draws knives strictly when firearms are dry, and suppresses melee charges into `<= 15m` active gunfire while holding a loaded firearm (`boltActionRequired` fix).
  * **Combat Bandage Safety (`CanSafelyBandageNow`)**: Blocks bleeding bots from holstering their weapon to bandage while an enemy has line-of-fire within `26m` or hostile gunfire is within `10.5m–18m`, pushing them to seek cover or clear close shooters first (`BandageUnsafeUnderFire`).

### 2.6. Strategic Door & Window Shutter Discipline (`Soldier.CheckAndOpenDoor` — RVA `0x180743970`)
* **What Vanilla Did**:
  * In `Soldier.CheckAndOpenDoor` (`0x180743970`), called from `CheckInteractionAround` / `CheckInteractionInFront` / `CheckInteractionInWalkRange`, vanilla uncachedly called `GetComponent<InteragibleDoor>()` and `GetComponent<InteragibleDoorAdditionalInteractor>()` on every collider in range and unconditionally called `door.SetDoor(true)` whenever `!door.isOpen`. Bots popped open every closed window shutter and side door they walked near and **never** closed a single door or shutter (`SetDoor(false)` was never called by AI).
* **Why It Needed Changing**:
  * Indoor defenders inadvertently exposed themselves by popping open closed window shutters along walls they were merely walking past, left entry doors wide open behind their squad, and never pulled shutters closed when pinned down, reloading, or bandaging.
* **Our Justification & Fix ([`Soldier_CheckAndOpenDoor_Patch`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs#L2680-L2885))**:
  * Piggybacks on the `Collider[]` array already passed into `Soldier.CheckAndOpenDoor` (zero extra `Physics.OverlapSphere` queries), throttles checks per bot to `0.30s`, and caches `Collider.Pointer -> CachedDoorInfo` (`_colliderDoorCache`) so `GetComponent` and collider bounds classification (`IsWindowShutter`: `b.min.y - botPos.y > 0.45f || b.size.y < 1.35f`) run only once per collider.
  * **Doors**: Opens a closed door only if it actually lies in the bot's movement direction (`dot > 0.20f`) or close in front (`<= 1.4m`), and closes an open door behind the bot (`dot < -0.35f`) once no squadmate is approaching (`IsAnySquadmateApproachingDoor`), or when defending near an exposed doorway while vulnerable.
  * **Window Shutters**: Stops walking bots from accidentally popping open closed shutters; stationary defenders only open a shutter if it faces their active target or threat-sector gaze (`dot > 0.40f`), and actively pull open shutters **closed** (`SetDoor(false)`) when heavily suppressed (`>= 32f`), bleeding, or reloading—which switches `InteragibleDoorAdditionalInteractor.SetDoorLayer` back to the solid obstacle layer, physically blocks enemy `Creature.CanSee` linecasts, and invalidates `_watchWindowCache` so gaze re-aligns immediately.

### 2.7. Bridge & Narrow Crossing Discipline (`SoldierAI.GetSpacingFromSquad` — RVA `0x18055af40`)
* **What Vanilla Did**:
  * While `CPathFinding` generates valid A* pathfinding nodes across bridge decks (`terrainNodesUnderBridges`), right before `SoldierAI.UpdatePath` (`0x180561570`) requests a path, it calls `SoldierAI.GetSpacingFromSquad()` (`0x180561675`).
  * Whenever a squadmate is nearby, `GetSpacingFromSquad` takes an intermediate point along `MoveDestination - myPos`, crosses it with `Vector3.up`, and shifts the pathfinding target sideways by **`Random.Range(5, 35)` meters** (`0x18055b45c`) while keeping `y = myPos.y`.
  * When a squad approaches or crosses a bridge, shifting `5m–35m` sideways flings the pathfinding waypoint out into mid-air over the river/ravine (`y = bridgeDeckY`), causing `CPathFinding.A` (`0x180561771`) to snap the path down to the riverbed/water nodes below the bridge instead of following the bridge deck.
* **Why It Needed Changing**:
  * Squads ordered across a bridge routinely veered off the approaches and waded/swam through the river underneath the bridge, or got stuck belly-crawling / nudging off bridge edges.
* **Our Justification & Fix ([`AiCore.IsOnBridgeOrNarrowCrossing`](../src/EasyRed2AI/Core/AiCore.cs#L156-L200) & [`SoldierAI_GetSpacingFromSquad_Patch`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs#L2115-L2169))**:
  * Uses a quantized `2m × 1m × 2m` spatial grid cache (`_bridgeCellCache`) to detect bridge/causeway/pontoon colliders and elevated non-terrain deck surfaces spanning above water (`BattleManager.GetWaterLevel()`).
  * Cancels vanilla's `5m–35m` lateral waypoint scatter (`__result = moveDest`) whenever the soldier is on a bridge, moving to/across a bridge, or when the laterally-scattered waypoint lands over a river/ravine/cliff drop-off (`> 2.1m` drop with no solid ground below).
  * While on a bridge (`isOnBridge`), prevents non-bleeding bots from dropping `Prone`, allows bridge infantry to seek deck-safe `CombatCover`, tank hulls, or hug stone parapets/pillars (`IsSafeBridgeDeckStep` + parapet-to-parapet bounds), props MG bipods onto waist/chest-high bridge walls and `±28°` diagonal railings (`BipodCheck(true)`), and disables blind open-span `SmartWallSideCover` nudges off bridge edges.

### 2.8. Shell-Shocked & Skittish Dynamic Surrender (`Soldier.SurrenderSynched` — RVA `0x18076a3c0`)
* **What Vanilla Did**:
  * XREF analysis of `Soldier.SurrenderSynched` (`0x18076a3c0`) confirmed it was **only** called by `BattleManager.SurrenderUnitsOnMissionAreas` (when an entire objective phase flips), `BattleManager.OnWin`, `Pause.Surrender`, and `Soldier.<UseSyryngeCR>`. AI bots never dynamically surrendered during live combat, even when completely cut off, out of ammo, bleeding, and shell-shocked with enemy troops 5 meters away.
* **Why It Needed Changing**:
  * Allowing isolated, broken, shell-shocked skittish or green rookie soldiers to surrender when overrun at close range creates authentic WWII combat moments and feeds directly into `EnableComradeLiberation` (`UnSurrenderSynched`) if friendly forces counterattack.
* **Our Justification & Fix ([`TryEvaluateShellShockSurrender`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs#L2063-L2225))**:
  * Strictly restricts dynamic surrender to **Skittish archetypes (`[Cautious]` Riflemen & `[Triage]` Medics with `vet01 < 0.35f`)** under heavy shell-shock (`>= 68` suppression, or `>= 52` if bleeding/out of ammo), **Green Rookies (`vet01 < 0.20f`)** under extreme shell-shock (`>= 86` suppression, or `>= 70` if bleeding/out of ammo), and **isolated non-fanatic Regulars (`vet01 < 0.45f`) who are completely out of reserve ammo (`IsOutOfAmmosFor`)** under heavy suppression (`>= 62`).
  * **Historical Doctrine Resistance**: Fanatic doctrines (`Japan`, `Gurkha`, `Finland` where `PreferAmbushWhenAlone && BayonetBreachCostMult <= 0.78f`) never surrender unless critically wounded/bleeding under extreme suppression (`+22f` threshold).
  * Squad Leaders, `[Spearhead]`, `[Tactician]`, `[Old Guard]`, `[Shock]` Assaulters, `[Anchor]` MG Gunners, `[Heroic]` Medics, and hardened veterans **never** surrender.
  * Requires at least one live enemy closing within **`18m`** (`<= 6.5m` vertical separation), local isolation (`<= 1` friendly within `16m`), and **no Squad Leader or `[Spearhead]` / `[Old Guard]` officer within `22m`** (an inspiring officer keeps wavering troops in the fight).

### 2.9. Cross-Subsystem Priority Hierarchy & Conflict Resolution
* To ensure tactical subsystems never fight over `MoveDirectlyToward`, `SetBotPose`, `visibleTarget`, or grenade throws, `Battle Hardened` enforces a strict humanlike priority hierarchy:
  1. **Movement Priority Chain**:
     * *(1) Survival, Shell-Shock Surrender / Bleeding Cover-Seek* → *(2) Point-Blank CQB Melee (`<= 5.2m`)* → *(3) Evading Heavy Armor (`isEvadingArmor`)* → *(4) Dedicated Vehicle Repair / Emplacement Staffing* → *(5) Bridge Crossing Advance (`isOnBridge`)* → *(6) Attacker Bounding Overwatch (`dist >= 18m`)* → *(7) Out-of-Combat Comrade Liberation (`!HasEnemyAndCanFireAtHim`)*.
     * Also protects active GOAP flankers, scavengers, tank-cover seekers, and emplacement boarders (`TryGetProtectedBotMoveOverride` & `SoldierAI.CalculateMoveOrderDestination`) from vanilla's `2.0s` `SquadOrdersRoutine` rubber-banding.
  2. **Indoor Room vs. Exterior Wall Disambiguation**: Checks `AiCore.HasLowCeilingOverhead(pos, 3.5f)` and forward room enclosure (`4.5m`) so leeward wall cover never mistakes interior room/bunker walls for exposed exterior faces.
  3. **Centralized Grenade Friendly-Fire, Indoor Arc & Frag-vs-Armor Safety**: Every mod-triggered grenade throw verifies an unobstructed overhead throw arc and zero friendlies inside blast radius (`8.5m–9.5m`), and `CanThrowableDamageOrFlushVehicle` (`Soldier.Throw`) blocks `< 12mm` fragmentation grenades against buttoned-up heavy armor (`>= 8mm`) while preserving AT grenades, Molotovs, satchels, and smoke.
  4. **Blind Suppressive Fire Discipline**: Restricts last-known cover suppression to MGs/automatic weapons with ample ammo (`>= 12` rounds within `55m`) and an unobstructed sightline (`dist - 2.5m`).

### 2.10. `InvisibleTrepassableWall` (`Layer 14`) Vaulting & Trench-Exit Fix (`IsValidVaultObstacle`)
* **What Vanilla Did**:
  * Two compounding movement/vault bugs surfaced in `2.2 hours` of Co-op telemetry (`Vault: 0 | Rej: 30,577`):
    1. Easy Red 2 places sandbags, low stone walls, trench parapets, and fences on **Layer 14 (`InvisibleTrepassableWall`, `vaultMask = 0x4101`)**, which is excluded from `ResourcesManager.groundMask` (`0x101`)—so any vault check using `GetSolidEnvironmentMask()` missed `100%` of vaultable obstacles!
    2. Vanilla also passes `vaultPos = Vector3.zero` when its landing probe misses or hits a thin fence, and its `+0.35m` crest probe overshot thin `< 0.25m` wooden/wire fences. Conversely, open terrain hills and barbed-wire fences caused bots to vault flat craters or hop wire prematurely.
* **Our Justification & Fix ([`AiCore.GetMovementObstacleMask`](../src/EasyRed2AI/Core/AiCore.cs) & [`IsValidVaultObstacle`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs))**:
  * Combines `vaultMask | groundMask` into `AiCore.GetMovementObstacleMask()` for both `UpdateMoveDirection` and `IsValidVaultObstacle` (`SphereCast`/`Raycast`), probes thin-wall crests cleanly (`+0.03m` and `+0.20m` + `+1.76m` ceiling clearance), synthesizes safe landing `vaultPos` values when vanilla passes `Vector3.zero`, enables trench-exit & battered-revetment vaulting while keeping sloped terrain hills and barbed wire unvaultable (unless wedged for `>= 3.5s`), and keeps `Soldier.ForceControllerEnabled()` active only inside tight trench/wall guard windows so grounded bots never phase horizontally through trench walls.

---

## 3. Macro Objectives, Economy, Emplacements, Armor & Aircraft ([`MacroAndVehiclesModule.cs`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs))

### 3.1. Universal Objectives, Full-Army Staffing, Smoke-Screened Attrition Pivots & `ConquestEconomy` Quartermaster (`Squad.TaskCanStillAttract`)
* **What Vanilla Did**:
  * The millisecond an objective was captured, `MissionObjective.attractor` dropped and **100% of friendly AI squads abandoned the newly captured point**; `Squad.SendUnitsToCovers` scattered defenders outside the cap circle; `Squad.TaskCanStillAttract` kept squads stuck on deactivated Lua objectives (`canAttractAllies == false && canAttractAxis == false`); and `SpawnManager` bypassed `ConquestEconomy` completely so AI spawned heavy tanks/planes for `0` tickets while players paid full cost.
  * Meanwhile, HQ directors that counted only connected AI foot squads mistook crowded `Autonomous`/Player/Armor meatgrinders for `0`-attacker objectives, trickled pivots `1` squad per `75s`, and dropped `Leader_OrderAttackCurrentTask` when multiple squads pivoted on the same frame.
* **Our Justification & Fix ([`MacroAndVehiclesModule.cs`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs) & [`ClassifyMissionObjectiveForFaction`](../src/EasyRed2AI/Core/AiCore.cs))**:
  * **All 216 Missions + Reworked `sp_ME_` Campaigns**: Audited all `216` `ME_*` maps and `178` Lua scripts to build `AiCore.ClassifyMissionObjectiveForFaction`, supporting Phase attacks, `sp_ME_` streaming campaigns (`currentPhase == null`), Dynamic Conquest, Wave Defense, `#retreat` finales, and custom `AiParams` scripted units.
  * **Garrisons, Back-Cap Alarms & Inside-Circle Play**: Keeps `16%–28%` doctrine garrison share on held points, reacts to `GetTaskCompletationPercentage() > 0.05f` back-cap progress, clamps `Squad.SendUnitsToCovers` to `0.80 * GetTaskAreaRadius()` so troops fight *inside* the circle, and uses live cap headcounts (`FriendlyUnitsInsideObjective` vs `EnemyUnitsInsideObjective`) to drive break-ins.
  * **Dynamic Operations `ConquestEconomy` Quartermaster (`EnableConquestEconomyQuartermaster`)**: MasterClient charges `ConquestEconomy.Charge` on AI spawns, protects a dynamic `ConquestPlayerTicketBuffer` (`135` base, `+75`/extra Co-op player, `+60` while a teammate is on the respawn screen), surges infantry when low, and banks surplus (`<= 50s`) for heavy armor/air counters.
  * **`v0.8.6` Full-Army Objective Staffing, Smoke-Screened Attrition Pivots & Staggered HQ Queue**:
    * [`CountAlliedGroundSquadsOnObjective`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs) counts **all** friendly ground squads per point (connected AI, cut-off `Autonomous`, Human Players, coalition allies, and nearby ground armor).
    * [`TryExecuteTeamNetAttritionPivot`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs) enforces comparative loss-differential anti-ping-pong (`>= 3.0` and `< 55%` losses unless an alternate flag has `>= 40%` capture urgency; `AvoidUntil = 105s`, `135s` per-squad cooldown, `2.85x` weight) and pops retreat smoke (`TryPopSquadRetreatSmoke`) for pivoting squads via `CanSquadDisengageForAttritionPivot`.
    * [`_pendingMacroSquadOrders` / `DrainOnePendingMacroSquadOrderIfReady`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs) drains `1` reassigned squad per non-heavy frame behind `AiCore.TryConsumeBotRpcBudget(0.45f)` while clearing stale `sq.timed_order_end` and releasing stale covers (`OrderLeaveAllVehiclesAndCovers`).

### 3.2. Proactive Static Gun Staffing, Turret-Only Seat Discipline & Abandoned Tank Re-Crewing (`EnableProactiveEmplacementStaffing`)
* **What Vanilla Did**:
  * Defending squads stood 10 meters away from empty tripod MGs, bunker MGs, AT guns, and Flak guns without crewing them unless explicitly scripted by a mission file.
  * Conversely, because every `Vehicle` (including movable trucks, halftracks, jeeps, and tanks) is registered in `CoverManager.staticCovers`:
    1. `Vehicle.IsCoverAvailable` (`0x18085F9A0`) returned `true` for **driverless movable vehicles** (abandoned halftracks, jeeps, tanks) whenever they had an empty `TurretGun` seat.
    2. `Vehicle.IsCoverOccupied` (`0x18085FD40`) returned `false` ("not occupied") as long as **any** seat on the vehicle/emplacement—even a **non-turret passenger, loader, or driver seat (`!seat.hasTurret`)**—was empty, causing `Squad.SendUnitsToCovers` to keep sending more infantrymen to the same emplacement or vehicle!
    3. When a foot soldier reached the vehicle (`Vehicle.OnCoverReached` at `0x180860560`), `VehicleWithWheels.GetOnVehicleBestPos` (`0x18087B070`) picked whichever empty seat was physically closest (`seat.seat.transform.position`) without checking `seat.hasTurret`—putting infantrymen into driver seats or passenger bench seats instead of the turret! Similarly, `Vehicle.GetOnVehicleBestPos` (`0x18085E430`) put additional soldiers into non-turret loader/spotter seats on static guns.
* **Why It Needed Changing**:
  * Pre-placed defensive emplacements sat unused, while random foot soldiers climbed into driverless vehicles or sat helplessly in non-turret loader/passenger seats.
* **Our Justification & Fix ([`RunProactiveEmplacementStaffing`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L2727-L2949), [`Vehicle_IsCoverAvailable_Patch`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L3055-L3079), [`Vehicle_IsCoverOccupied_Patch`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L3086-L3110) & [`Vehicle_OnCoverReached_Patch`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L3121-L3185))**:
  * Scans empty static guns (`veh.TryCast<MovableVehicle>() == null`) within `55m–75m` of defending/garrisoning squads, verifies that the gun's turret arc (`IsPositionInDirection`) can bear on the active threat sector and that the emplacement is within `<= 4.5m` vertical elevation, and assigns one available non-leader/non-medic/non-radioman bot (`CoverPosition`) to crew it—with a `24s` boarding timeout and `120s` unreachable cooldown (`_unreachableEmplacementUntil`).
  * **Turret-Only Seat & Driverless Vehicle Guards**: `Vehicle_IsCoverAvailable_Patch` and `Vehicle_IsCoverOccupied_Patch` block driverless `MovableVehicle` instances from acting as octree cover for foot soldiers and mark emplacements as occupied as soon as all **turret seats** (`TryGetAvailableTurretSeatIndex`) are filled. `Vehicle_OnCoverReached_Patch` intercepts `Vehicle.OnCoverReached` so regular AI foot soldiers **never** climb into driverless vehicles or non-turret seats, and directly seats them in the open turret seat via `GetOnVehicle(turretSeatIdx, soldier)`.
  * **Bailed-Out Vehicle Crew Re-Mounting & 2-Seat Static Emplacements**: Forces foot infantry strictly into turret seats (`TryGetAvailableTurretSeatIndex`) while staffing **both turret seats** on 2-seat emplacements, lets human players always swap into AI-occupied vehicle seats (`PlayerController.SwitchSeat` + `AIVehicle.SwitchSeatAi`), rejects locked/empty/`!HasAmmo()`/hostile-occupied static guns, and lets bailed crews (`>= 2` alive within `85m`) re-mount empty combat vehicles (driver first, then turrets).

### 3.3. Smart APC / Transport Dismounting Under Fire (`EnableSmartApcDismount`)
* **What Vanilla Did**:
  * AI-driven APCs, halftracks, and trucks drove straight into enemy tanks, AT guns, and entrenched infantry before stopping to unload passengers, getting the entire embarked squad blown up inside the transport.
* **Our Justification & Fix ([`MacroAndVehiclesModule.cs`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs))**:
  * Hooks `AIVehicle.MovableVehicleRoutine` (`SomeoneIsSuppressed()`) and `VehicleWithWheels.OnVehicleSuppressed` on pure transports and immediately dismounts embarked passengers (`DropInfantry` + `OrderLeaveVehicle`) when suppressed, damaged under fire, or within `48m` of an enemy tank/AT gun/squad.

### 3.4. Tank Standoff, Reverse & Anti-Bombing Echelon Dispersion (`AIVehicle.MoveTowardCurrentNodeTank`)
* **What Vanilla Did**:
  * AI tanks rolled straight into point-blank infantry AT-grenade range (`< 25m`) and stacked bumper-to-bumper on the same objective or bridge.
* **Our Justification & Fix ([`MacroAndVehiclesModule.cs`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs))**:
  * Holds personality-scaled standoff (`50m` Brawler, `72m` Support, `95m` Sniper) via `StopAndRotateTowardTarget` when having a clear main-gun LOS, engages reverse gear (`ForceRetroFor` + `MoveTankRetro`) within `35m` of AT/armor threats, and spreads multiple friendly tanks into `40m+` lateral echelons / *Breitkeil* wedges with `18m–24.5m` convoy spacing so dive bombers cannot wipe two tanks in one pass.

### 3.5. Smart Anti-Armor Targeting, Threat-Arc Tank Fear & Munition Selection (`TurretGun.SelectBestAmmo` & `IsTankThreateningPosition`)
* **What Vanilla Did**:
  * Riflemen mag-dumped at 50mm+ tank hulls, ignored enemy infantry behind the tank, and blindly bolted in panic whenever any friendly/neutral-facing tank sat anywhere within `85m`—even with its gun and hull turned completely away.
  * Meanwhile, `TurretGun.GetEfficiencyAgainstInfantry` tied 75mm/40mm solid AP cannons with Coaxial/Wing MGs (and ranked `APHE`/`HEAT` above MGs!), wasting anti-tank shells on lone foot soldiers.
* **Our Justification & Fix ([`MacroAndVehiclesModule.cs`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs))**:
  * Redirects small arms at exposed crew (`Seats.showUnitSet`) and un-flattened tires, aims AT rifles at unbroken tracks (`VehicleDamageableTrackPart`), re-targets visible infantry screens (`TryFindVisibleFootEnemy`), blocks wasteful `< 12mm` frags against buttoned tanks (`CanThrowableDamageOrFlushVehicle`), fixes `TurretGun.SelectBestAmmo` so Coaxial/Wing MGs engage infantry instead of wasting AP shells, and gates infantry **Tank Fear** strictly to active armor threat arcs (`IsTankThreateningPosition`).

### 3.6. Combined-Arms Known Target Support, On-Nose Air Superiority & Smooth `10–12 Hz` Multiplayer Plane Sync
* **What Vanilla Did**:
  1. `TurretAI.FindBestTarget` (`0x180831a80`) ignored `isMarked` and radio requests, `AIVehicle.UpdatePath` wandered randomly (`RandomPointInCircleXZ`), and `AIPlane.GetBestAircraftVisibleEnemy` (`0x1805718f0`) passed `maxDistance = 850m` into `PointOctree.EnumerateNearby`, hiding distant aircraft.
  2. Fighters broke off dogfights prematurely (`anti-chicken`), spiraled upward without an altitude ceiling, and on the **Host**, camera-LOD (`Vehicle.SetPhysicsEnabled`) zeroed `rb.velocity`—sending `speed = 0` packets at `0.40s`/`1.50s` intervals that caused `allowedOutOfSyncDist = 0` teleporting and vertical gravity-fall columns on Co-op clients!
* **Our Justification & Fix ([`MacroAndVehiclesModule.cs`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs) & [`BallisticsAndImmersionModule.cs`](../src/EasyRed2AI/Modules/BallisticsAndImmersionModule.cs))**:
  * **Turrets, Tanks & `RepairStation` Resupply**: Prioritizes Marked/Radio-Requested/Engaged squads, aims armored overwatch positions, and routes low-ammo or `>= 35%` damaged mobile armor to the nearest safe `RepairStation` (`<= 650m` / `240m–360m` tactical cap), routing crews leeward before repairing (`2.35m–3.25m` + `getOnVehicleAfter = true`).
  * **"Clear Skies Only" On-Nose Dogfights (`125m–170m`) & `10–12 Hz` MP Sync**: Restores `2200m`/`4000m` air detection, aborts ground dives to clear enemy aircraft first, keeps fighters on-nose down to `125m–170m` reciprocal merges under a `maxFlyHeight` ceiling (`TryExecuteDogfightAntiChickenBreakOff`), stops Host LOD velocity zeroing, raises airborne `PhotonStream` sync frequency to `~10–12 Hz` with 100% vanilla wire compatibility (even unmodded clients get smoother planes!), disables non-owned `rb.useGravity` on modded clients, and stabilizes `2.5s` airborne spawns (`isGrounded` sync + skip FMOD seek).

### 3.7. Friendly Infantry Right-of-Way, Dual-Sided Roadkills & Grounded Pilot/Bomber/Tank Crew `"Retreat"` (`v0.8.1` / `v0.8.5`)
* **What Vanilla Did**:
  1. In `VehicleWithWheels.OnTriggerEnter` (`0x18087C7F0`), `&& !soldier.IsPlayer() && !this.PlayerIsInside()` let AI vehicles roadkill players and player-occupied AI vehicles roadkill friendly teammates; meanwhile `VehicleWithWheels.FixedUpdate` zeroed `this.speed = 0` on the Host whenever `!OwnDriverOrVehicle()`, blocking **100% of remote Co-op client roadkills against enemies**.
  2. `AIVehicle.RetreatCrew` (`0x18057B980`) crashed with `NullReferenceException` when `currentPhase == null` (`sp_ME_`), `AIPlane.RetreatCrew` sampled sky coordinates while still airborne, and vanilla's `Squad.IsVehicleCrew` (`!isOpenForJoiners`) misclassified `6–10` seat heavy bomber crews and closed infantry squads.
* **Our Justification & Fix**:
  * **Corridor Yield & Dual-Sided Roadkills**: Moving AI armor pauses cleanly via `resumeDriveTime` (`VehicleWithWheels.OnTriggerEnter` blocks friendly roadkill damage while fixing Host/Client forward & reverse enemy roadkills).
  * **Grounded Crew `"Retreat"` (`HandlePlaneRetreatCrew`, `AssignCrewSquadRetreatTask`, `UpdateGroundedCrewRetreatTasks`)**: Classifies `6–10` seat bomber crews accurately (`AiCore.IsTrueVehicleCrewSquad`), never despawns planes mid-flight, and assigns on-foot `"Retreat"` objectives (`MissionTaskRetreat`) right where bailed/crash-landed Human Players (Host & Client) and AI pilots/tankers touch down—including out-of-bounds meshes and **`85m` water-surface extraction spheres** when ditching at sea.

---

## 4. Veterancy & 30 Role-Based Personalities ([`VeterancyAndPersonalitiesModule.cs`](../src/EasyRed2AI/Modules/VeterancyAndPersonalitiesModule.cs))

* **What Vanilla Did**:
  * Every AI bot spawned with identical static accuracy, identical suppression thresholds, and identical reaction timers, regardless of whether they had survived 10 seconds or 15 minutes or whether they were an SMG shock trooper, an MG gunner, or a field medic.
* **Why It Needed Changing**:
  * Combat lacked emergent narrative and role distinction, and surviving veterans felt no different from fresh replacements.
* **Our Justification & Fix**:
  * **15-Minute Linear Veteran Curve (`900s`)**: Scales weapon dispersion down by up to `20%` max (never laser-accurate), tightens suppression cover-reaction cooldowns (`4.8s -> 2.2s`), promotes surviving soldiers in rank (`Pvt -> Cpl -> Sgt -> 1st Sgt`), updates their **3D uniform shoulder rank decals** (`UpdateRanksDecal`), and renders `[Vet I–III ★]` Squad Roster tags and 3D hover chevrons.
  * **30 Role-Based Personalities (`10 Roles × 3 Archetypes`)**: Assigns deterministic per-soldier archetypes (`[Spearhead]`, `[Breacher]`, `[Deadeye]`, `[Heroic]`, `[Brawler Crew]`, `[Ace]`, etc.) that tailor suppression grit, peek cadence, spacing, grenade usage, tank standoff distance, and dogfight pursuit leashes.

---

## 5. Ballistics, Vehicle Armor Realism, Plane Audio & Visual Polish ([`BallisticsAndImmersionModule.cs`](../src/EasyRed2AI/Modules/BallisticsAndImmersionModule.cs))

### 5.1. Client-Side Plane Engine Loop De-Throbber (`VehiclePlane.UpdateMotorSound`)
* **What Vanilla Did**:
  * Every `VehiclePlane` started its motor loop (`engine_move` / `engine_move_far`) at `time = 0.0s` on spawn and near/far switches, while high `dopplerLevel` and un-smoothed pitch updates created rhythmic "wah-wah" flyby throbbing.
* **Our Justification & Fix ([`BallisticsAndImmersionModule.cs`](../src/EasyRed2AI/Modules/BallisticsAndImmersionModule.cs))**:
  * Offsets `engine_source.time` to a deterministic per-plane phase, clamps `dopplerLevel <= 0.22f`, applies a `±2.2%` per-plane detune, and low-pass smooths `engine_source.pitch`.

### 5.2. Material-Aware Thin-Cover Penetration, Enclosed Crew Armor, Aircraft Top-Attack AP & Molotov Engine Seepage
* **Thin-Cover Bullet Penetration (`BulletInstance.OnHit`)**: Full-powered rifle and MG rounds (`mmPenetration >= 6`, `>= 220m/s`) penetrate thin wooden fences, shutters, and doors (`<= 0.35m`; `<= 0.48m` for `12.7mm+`) via single-collider back-face raycasts (`col.Raycast`), while guarding native `BulletInstance.OnHit` NREs on dead/despawning shooters.
* **Enclosed Tank Crew Blast, Spall & Collider-Clip Protection**: Buttoned-up crew (`!seat.showUnitSet`) inside intact enclosed armor (`>= 8mm`) take concussive suppression instead of dying through `50mm+` steel from non-penetrating frags/mortars or `< 12mm` collider-clipping small arms (while AT grenades `30mm+`, HEAT, satchels, heavy HE, bombs, and open seats remain lethal, and solid kinetic AP spall scales by caliber). Also prevents non-penetrating ricochets from overwriting `DeathPanel` attribution and guards vanilla's `ResourcesManager.FactionCanDamageFaction(null, ...)` shrapnel NRE hitch (`~176ms`).
* **Aircraft Top-Attack AP (`TryPenetrateArmor` + `"bh_ver"` Host-Assist) & Airplane Canopy vs. Armored Floor**: Diving aircraft cannons (`>= 12mm`) normalize top-deck roof angles (`<= 15°`, `0.45x` deck thickness) with automatic Host-Assist for unmodded Co-op clients; meanwhile `< 12.5mm` rifle rounds fired up from steep below are blocked by armored plane floors while canopy/level/`.50 cal+` rounds remain lethal and vanilla's oversized `1.2m × 2.0m × 1.2m` fuselage spall box is tightened (`0.42m` for `.50 cal`, eliminated for rifles).
* **Molotov / Phosphorus Track Immunity & Engine-Deck Seepage**: Reclassifies burning Molotov/phosphorus bottle shatters from `HitType.Explosion` to `HitType.Fire` so steel tank tracks never snap from burning fuel while upper-hull fire seeps into `DamageEngine` / `DamageFuelTank`.
* **Indirect Mortar/Artillery Danger-Close Safety & Trail Fix**: Protects friendlies (`27m–30m`, `+6m` for players) across handheld `KneeMortar`, static mortar pits, and artillery (`TurretAI`, `TurretGun.StartFire`), clears pooled `TracerBehaviour` streaks, and blocks 90° roof/foliage ricochets.
* **Smoke & Water Blood Occlusion (`ImpactFX_flesh_noBlood`) + Connected 3P FPS Shadow Rig**: Eliminates floating red blood mists over opaque smoke or underwater (`EnableBloodSmokeWaterOcclusion`) and replaces camera-pitched floating `FPSGunManager` viewmodel shadows with a local-player-only `ShadowsOnly` 3rd-person shadow rig (`BH_FpsBodyShadowProxy`).

---

## 6. Death Screen Obituary, Spectator/HUD, Co-op Stat Sync & Zero-Hitch 60 FPS Architecture

* **Instant Death Screen Obituary + Soldier Age ([`DeathScreenAndQoLModule.cs`](../src/EasyRed2AI/Modules/DeathScreenAndQoLModule.cs))**: Forces immediate 100% alpha on `DeathPanel`, calculates **Age at death (`1925 - 1944 (Age 19)`)**, attributes exact Weapon / Ordnance caliber / Hit Zone / Distance (`[FRIENDLY FIRE]`, `[SELF-INFLICTED]`, `Bled out`), credits vehicle `LostVehicles` when crews bail or die, and omits cause-of-death on surrender.
* **Authoritative Multiplayer Casualties, Prisoners & Lost Vehicles Sync (`EnableMultiplayerBattleStatSyncFix`)**: Prevents async Photon `SetCustomProperties` clobbering using `"BS.0.1".."BS.1.3"` `Int32`-boxed keys on `MasterClient`, coalescing at `4 Hz` + `2.0s` mid-match sync + pre-`SyncWin` `BattleManager.OnWin` surrender/stat flush and live `EndBattleGUI` refresh on Host & Clients.
* **Spectator HUD, 4K Scaling, `F` Cycle, Top-Center Bot Inspector & `F5` Map Picker ([`FeatureMenuAndHud.cs`](../src/EasyRed2AI/UI/FeatureMenuAndHud.cs))**: Scales all IMGUI panels for 1440p/4K (`UiScaleMultiplier`), keeps `MissionStatusGUI` and 3D `A`/`B`/`C` markers live in Spectator (`F` cycles `Full Tactical` -> `Objectives Only` -> `Clean Cinema`), displays the live hovered **Spectator Bot GOAP Inspector**, inserts formatted Faction + Side names into expanding `MissionStatusGUI.mainRow` (`Germany (Axis)`), supports Quick-Tap `Tab` spotting & `64`-char room names, and adds **`F5` Campaign & Mission Picker** with campaign unlock masking (`???`) and `★`/`★★` completion stars.
* **Sustained Zero-Hitch 60 FPS (`140+` Bots) & Host Ping Governor ([`AiCore.cs`](../src/EasyRed2AI/Core/AiCore.cs))**:
  * Combines double-buffered chunked `WorldSnapshot` updates, single-raycast `GetSolidEnvironmentMask()` queries, persistent IL2CPP fast-handles (`GetCachedMoveEndpoint`, `ResolveTargetDestination`, `HasHeldMortarCached`) eliminating `80–128 KB/frame` `GCHandle` churn and `120ms` Gen-1 GC pauses, strict trench/wall-window gating on `ForceControllerEnabled()` eliminating `285ms` `FixedSteps: 5` PhysX freezes, `ShouldSkipBotCycleForNetworkPing()` + `TryClaimHeavyFrameWorkSlot()` pacing for large Co-op lobbies, and `TryConsumeBotRpcBudget()` RPC throttling.

---

## 7. F.E.A.R.-Style 3-Tier Chain of Command, Emergent Squad Interactions & 20-Nation Doctrines ([`GoapAndBlackboardModule.cs`](../src/EasyRed2AI/Modules/GoapAndBlackboardModule.cs))

### 7.1. "HQ Suggests $\rightarrow$ NCO Decides $\rightarrow$ Soldier Survives" + Emergent Radio/Medic Inheritance
* **What Vanilla Did**:
  * Vanilla `SoldierAI` and `Squad` used independent, single-step reactive branches with no shared squad blackboard, no multi-step tactical prerequisites (e.g., popping smoke *before* bounding, or suppressing cover *before* flanking), no casualty aversion (`NearFreshCasualty`), and `100%` global omniscient radio/target sharing.
  * Even worse, two native engine quirks blocked battlefield scavenging: `inv.FindItemOfType<VirtualClothing>()` stopped at index-0 uniforms (`isRadio == false`), and **`InventoryManager.TakeIntoInventory` (`0x1807E50E0`) only auto-equipped `VirtualHelmet` (+0x113..+0x1BF)** while jumping `VirtualClothing` straight to `AddVirtualItem` at `+0x1C1` **without calling `Soldier.SetWerable(...)`**—leaving looted corpse radio packs unworn in inventory (`IsRadioman() == false`, `sq.HasRadioman() == false`)!
* **Our Justification & Architecture**:
  * **Tier 3 — Radioman-Gated Team Intel Net (`RunTeamNetGoapCoordinator`)**: Squads only join the Team Net if they have an alive Radioman within `32m` of their Squad Leader, ride in a ground vehicle, or relay through a connected unit within `RadioRelayRadius` (`36m–55m`). Every `6.0s`, Team Net posts **advisory** directives and pairs connected **Anvils** (including pinned Human Player squads!) with nearby AI **Hammer** squads (`AttackFromSide`)—or dispatches `Regroup_SectorWithdrawal` when mauled.
  * **Tier 2 — Squad Blackboard & NCO Execution GOAP**: Refreshes every `0.60s` per squad, assigning `Suppressor`, `Left/Right Flanker`, `Breacher`, `Rescuer`, and executing cost-weighted `Squad.Charge` / covert `SetHoldFireOrder` / `FollowLeader` orders. NCOs weigh HQ suggestions against `DecayedRecentDeaths`, suppression, smoke, and Officer personality—**balking/refusing suicidal pushes** (`"Keep your head down!"`, converting locally to `Anvil` base-of-fire) and aborting charges if `>= 2` men fall.
  * **Tier 1 — Individual 32-Bit `uint` Bitmask GOAP Brain, Micro-Variance & Personal Survival**: Evaluates 1–2 step chains across `GoapActionDef` actions using zero-allocation bitwise masks, continuous `BotMicroVariance` (`AggressionCostMult`, `CautionCostMult`, `ReactionJitterSeconds`, `SuppressionJitter`), 2-stage moving-stall recovery (`2.4s`/`5.2s` + `8.5s` squad watchdog), and `NearFreshCasualty` (`20m` / `18s`) hazard memory so individual soldiers can **personally balk/disobey suicidal orders (even mid-`Charge!`)** or pop smoke / break contact (`3-Tier Tactical Retreats`).
  * **Emergent Field Radio Inheritance & Enemy Radio Frequency Retuning (`v0.8.6`)**: Opens `wantRadioPack` to **all cut-off infantry squads** (`!sq.HasRadioman()`), scans `clothItems[0..24]` for `wInfo.IsRadio`, allows immediate pickup from **friendly corpses (`<= 14m`)** or **floor packs (`<= 12m`)**, and allows scavenging **enemy radioman corpses (`<= 10m`, `suppression < 8`, `>= 20s` post-cutoff)** to retune the captured set onto the squad's frequency. Explicitly calls **`soldier.SetWerable(myRadio, true)`** after corpse `TakeIntoInventory` (invoking `SetWerableCR` -> `SetClothingObject` -> `GetSyncher().OnEquipmentChanged()`), visually strapping on the 3D pack, syncing Co-op, and flipping `soldier.IsRadioman()` / `sq.HasRadioman()` live!
  * **Floor/Crate Scavenging, AT Re-Arming, Buddy Resupply & Atomic 3-of-3 Medic Inheritance**: Scans floor items (`ItemObject.spawnedItems`), corpse inventories (`InventoryManager.activeInventories`), `DroppableAmmoBox`, and 32-slot `AmmoRefillCrate` containers (bot-side `35s` cooldowns that never lock out players); equips Slot 1/2 AT launchers against armor; supports `2.5m` buddy ammo/bandage borrowing, helmet recovery/gifting (`VoiceClip.thankYou`), casualty dragging (`CarryBodySynched`), anti-dogpile `Squad.MedicRoutine`, atomic 3-of-3 syringe transfers (`TakeIntoInventoryBatch` + player-proximity `25m` dead-medic protection), and `Squad.Join` lone-survivor / bailed-crew merging.
  * **20-Nation Doctrines (`FactionCommandProfile`) & Crippled-Only Japanese *Tokkōtai* (`TryExecuteJapaneseKamikazeDive`)**: Precomputed `O(1)` doctrine profiles across all `20` ER2 nations govern relay radii, autonomous *Auftragstaktik*/*Motti* vs. centralized doctrine, garrisons, attrition pivot thresholds, and surrender grit; meanwhile Japanese *Tokkōtai* Kamikaze triggers **only on genuinely crippled aircraft (`>= 80` structural damage or missing wing/structural parts + one-time personality roll)**, never on healthy planes out of bombs/ammo, prioritizing **enemy armor and aircraft** via terrain-cleared high-angle dives (`>= 36°` roll-in cone + gravity compensation).
