# Why Everything Was Changed — Vanilla Root Causes & Engineering Justifications

This document explains **what vanilla *Easy Red 2* (Game Build `921`, `GameAssembly.dll`) does under the hood**, **why each behavior caused gameplay, fairness, Co-op, or performance issues**, and **how [`EasyRed2HumanAI`](../src/EasyRed2AI/Plugin.cs) solves it**.

---

## 1. Vision, Camouflage, Threat Sectors & Memory ([`VisionAndMemoryModule.cs`](../src/EasyRed2AI/Modules/VisionAndMemoryModule.cs))

### 1.1. Stance Stealth & Individual Firing Reveal (`Soldier.CamoDistanceAwareEnemies` — RVA `0x18073db30`)
* **What Vanilla Did**:
  * When a squad is unspotted (`IsEnemyAwareOfThisSquad == false`), vanilla returns `220m` (Standing), `50m` (Crouching), and `37m` (Prone).
  * However, the moment **any** member of your squad fires a shot or is spotted (`IsEnemyAwareOfThisSquad == true`), vanilla jumps `CamoDistanceAwareEnemies` to a **flat `300.0m` for every member of the squad**, regardless of whether they are standing, crouching, or crawling prone in grass.
* **Why It Needed Changing**:
  * If a single AI rifleman 30 meters behind you fired a shot, every enemy bot within 300m immediately gained the ability to spot you crawling prone in tall grass (`37m -> 300m`). Taking cover or crawling during a firefight had zero stealth value.
* **Our Justification & Fix ([`Soldier_CamoDistanceAwareEnemies_Patch`](../src/EasyRed2AI/Modules/VisionAndMemoryModule.cs#L152-L202))**:
  * We preserve stance stealth during combat (`150m` Standing, `80m` Crouching, `40m` Prone) and apply only a modest `1.20x` squad-awareness multiplier (`48m` prone, `96m` crouched).
  * Instead of punishing the whole squad, [`Soldier_StartFire_Patch`](../src/EasyRed2AI/Modules/VisionAndMemoryModule.cs#L131-L149) records the **individual shooter's `IntPtr`** on weapon fire and applies a `+85m` visibility reveal for `4.0s` (representing muzzle flash, smoke, and gunshot report) to that specific soldier only.

### 1.2. Human FOV Cones When Alerted (`Creature.CanSee` — RVA `0x180a4afb0`)
* **What Vanilla Did**:
  * In `Creature.CanSee`, vanilla only checks `AngleToDirection <= 60°` if `dist >= 30m` **AND `!this.isAlerted`** (`if (dist >= 30f && !isAlerted && AngleToDirection(...) > 60f) return false;`).
  * Once a soldier is alerted or suppressed (`isAlerted == true`), vanilla skips the angle check completely, granting **360-degree omnidirectional vision out to 300m**.
* **Why It Needed Changing**:
  * Flanking an alerted bot from directly behind (`180°`) at 120m was impossible because they had eyes in the back of their head.
* **Our Justification & Fix ([`Creature_CanSee_Patch`](../src/EasyRed2AI/Modules/VisionAndMemoryModule.cs#L208-L353))**:
  * Enforces human distance-tiered vision cones even when alerted:
    * **`0m–20m`**: `360°` close hearing / peripheral awareness.
    * **`20m–75m`**: `80°` half-angle (`160°` mid-range peripheral cone).
    * **`75m+`**: `45°` half-angle (`90°` focused long-range gaze cone).

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

---

## 2. Infantry Combat, Stance, Cover & CQB ([`InfantryTacticsModule.cs`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs))

### 2.1. Stance Oscillation ("Teabagging"), Open-Field Standing Fire & `DestinationWithoutCover` Prone Bug
* **What Vanilla Did**:
  * `SoldierAI.GetFavouriteFightingPose` (RVA `0x18055ba50`) constantly fought with `SoldierAI.MovementRoutine` (RVA `0x18055cc40`) and `DestinationWithoutCover..ctor` (RVA `0x180550bf0`), which randomly rolled a **50% chance to force `SoldierPose.Prone`** whenever no cover node was found.
  * When line-of-sight flickered across a bush or ridge, bots rapidly oscillated `Stand <-> Crouch` every 0.25s.
* **Why It Needed Changing**:
  * Bots either stood bolt upright in the middle of an open road while shooting, randomly lay prone in tall grass where their muzzle was blocked by terrain, or bobbed up and down uncontrollably.
* **Our Justification & Fix ([`SetBotPose`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs#L265-L348))**:
  * Synchronizes `ai.pose`, `ai.actualPose`, and `soldier.SetPose` with a `2.8s` transition lock (`_poseLockUntil`) and `3.5s` combat crouch lock (`_combatCrouchLockUntil`), overrides `DestinationWithoutCover`'s random 50% prone roll to `Crouch` unless suppressed, and ensures bots drop to `Crouch`/`Prone` before returning fire in the open.

### 2.2. Indoor Ceiling Prone Prevention (`EnableIndoorCeilingPronePrevention`)
* **What Vanilla Did**:
  * Suppressed or defending soldiers inside buildings, bunkers, and pillboxes dropped `Prone` directly underneath windowsills (`0.8m` high) or on staircases, blinding themselves and clipping their legs through exterior walls.
* **Why It Needed Changing**:
  * A prone soldier inside a room cannot see or shoot over a windowsill and becomes helpless dead weight.
* **Our Justification & Fix ([`AiCore.HasLowCeilingOverhead`](../src/EasyRed2AI/Core/AiCore.cs#L156-L185))**:
  * Uses a quantized `1m × 1m` spatial cache (`_ceilingCache`, `30s` TTL) to check `ResourcesManager.groundMask` up to `4.2m` overhead and keeps indoor soldiers in a functional `Crouch` stance (`SetBotPose` + `SoldierAI.SequentialUpdate`).

### 2.3. Standing BEHIND Walls Instead of IN FRONT (`CombatCover.IsCoverAvailable` — RVA `0x18075bc40`)
* **What Vanilla Did**:
  * `CombatCover` nodes placed along walls or hedges only checked distance to the cover point, frequently placing defenders on the **enemy-facing side of the wall** (with the wall at their back and open air toward the enemy).
* **Why It Needed Changing**:
  * Defenders stood exposed in front of sandbags and stone walls with their backs pressed against the cover.
* **Our Justification & Fix ([`CombatCover_IsCoverAvailable_Patch`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs#L2189-L2231) & [`TryFindShelteredSideOfWall`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs#L602-L657))**:
  * Caches wall exposure per `(cover.Pointer, threatOctant)` (`_coverExposureCache`), rejects cover nodes that place a wall behind the soldier while leaving the front exposed, and repositions exposed defenders around to the leeward side of the wall.

### 2.4. Dual Barrel-Height Wall-Shooting Prevention & Auto-Peek (`SoldierAI.RotateAndFireToward`)
* **What Vanilla Did**:
  * `Creature.CanSee` raycasts from the soldier's **head/eyes** (`~1.6m` standing, `~1.0m` crouched, `~0.4m` prone), whereas bullets spawn from the **weapon barrel** (`0.22m` prone, `0.68m` crouched).
  * As a result, bots saw enemies over a sandbag, windowsill, or terrain crest and dumped entire magazines straight into the stone wall in front of their chest.
* **Why It Needed Changing**:
  * Wasted ammo, revealed the bot's position, and made entrenched defenders ineffective.
* **Our Justification & Fix ([`IsMuzzleBlockedByWall`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs#L440-L503))**:
  * Checks `AiCore.RaycastEnvironment` from both the soldier's spine and `heldGun.transform.position` out to `2.2m`. When blocked by low cover, bots automatically step up one stance (`Prone -> Crouch -> Stand`) to peek over the obstacle (`EnableAutoPeekOverCover`) or sidestep `1.25m–1.80m` around vertical doorframes/corners (`TrySidestepWallObstruction`).

### 2.5. Point-Blank Bayonet, Melee & Butt-Stroke CQB (`SoldierAI.SequentialUpdate` — RVA `0x18055fa61`)
* **What Vanilla Did**:
  * Vanilla only checked for melee inside `SequentialUpdate` within a tiny `1.6m` radius, and `MeleeWeaponCheck` (RVA `0x18055c8bf`) refused to draw a knife if the soldier had any gun with ammo (`!HasAnyGunWithAmmo`).
  * Furthermore, calling `heldGun.InstallAttachment` across bots triggered `SyncSoldierEquipment` Photon RPC bursts that caused Co-op lag spikes.
* **Why It Needed Changing**:
  * Two bots meeting inside a trench or room crouched 2 meters apart mag-dumping hip-fire around each other instead of lunging with fixed bayonets, rifle butts, or trench knives.
* **Our Justification & Fix ([`TryEnsureBayonetMounted`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs#L783-L806) & [`IsEngagedInPointBlankMelee`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs#L808-L892))**:
  * Mounts integrated bayonets once per soldier via `integratedBayonet.SetMounted(true)` (zero `SyncSoldierEquipment` RPC burst), stands bots up within `2.5m–3.8m` (`5.0m` when reloading/empty), closes distance (`MoveDirectlyToward`), draws knives when empty (`ToggleInventoryKnife`), and executes bayonet stabs/butt-strokes (`Soldier.Melee()`).

---

## 3. Macro Objectives, Emplacements, APCs & Armor ([`MacroAndVehiclesModule.cs`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs))

### 3.1. Macro Objective Garrison (`~20%`) & Counterattack Director (`Squad.TaskCanStillAttract`)
* **What Vanilla Did**:
  * The millisecond an objective was captured, `MissionObjective.attractor` dropped for the capturing faction and **100% of friendly AI squads abandoned the newly captured point**, allowing a single sneaking enemy squad to walk in unopposed and recap it behind their backs.
* **Why It Needed Changing**:
  * Conquest and multi-point Frontline phases turned into a "merry-go-round" where neither side ever left a rear guard on captured territory.
* **Our Justification & Fix ([`RunMacroObjectiveDirector`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L533-L685) & [`Squad_TaskCanStillAttract_Patch`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L823-L853))**:
  * Runs a single `6.0s` MasterClient director tick that keeps **~20% of autonomous friendly squads** garrisoning newly captured objectives (scoring contested or frontline points highest) while directing the remaining 80% to attack/counterattack uncaptured objectives.

### 3.2. Proactive Static Gun & Armed Halftrack Staffing (`EnableProactiveEmplacementStaffing`)
* **What Vanilla Did**:
  * Defending squads stood 10 meters away from empty tripod MGs, bunker MGs, AT guns, Flak guns, and parked armed halftracks without crewing them unless explicitly scripted by a mission file.
* **Why It Needed Changing**:
  * Pre-placed defensive emplacements sat unused during unscripted defenses and counterattacks.
* **Our Justification & Fix ([`RunProactiveEmplacementStaffing`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L691-L819))**:
  * Scans empty static guns and parked armed halftracks within `42m` of defending/garrisoning squads, verifies that the gun's turret arc (`maxHorizontaAngle`) can bear on the active threat sector, and assigns one available non-leader/non-medic/non-radioman bot (`ForceAssignedVehicle`) to crew it.

### 3.3. Smart APC / Transport Dismounting Under Fire (`EnableSmartApcDismount`)
* **What Vanilla Did**:
  * AI-driven APCs, halftracks, and trucks drove straight into enemy tanks, AT guns, and entrenched infantry before stopping to unload passengers, getting the entire embarked squad blown up inside the transport.
* **Why It Needed Changing**:
  * Mechanized infantry transports acted like suicide taxis instead of dismounting troops at the edge of the danger zone.
* **Our Justification & Fix ([`TryDismountTransportUnderThreat`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L860-L980))**:
  * Hooks `AIVehicle.MovableVehicleRoutine` and `Vehicle.OnVehicleSuppressed` on pure transports (`VehicleHasNoTurrets` or transport halftracks carrying passengers) and immediately dismounts embarked passengers (`LeaveVehicle` + `SendUnitsToCovers`) when suppressed, damaged under fire (`HullLife < MaxHullLife * 0.92f`), or within `48m` of an enemy tank/AT gun/squad.

### 3.4. Tank Standoff & Reverse Discipline (`AIVehicle.MoveTowardCurrentNodeTank`)
* **What Vanilla Did**:
  * AI tanks continued driving forward along their waypoint path even when they already had a clear main-gun line of sight (`CurrentVisibleTarget`) at 70m, rolling straight into point-blank infantry AT-grenade range (`< 25m`).
* **Why It Needed Changing**:
  * Tanks threw away their ranged armor advantage and died to infantry hand-thrown explosives within seconds.
* **Our Justification & Fix ([`AIVehicle_MoveTowardCurrentNodeTank_Patch`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L1057-L1184))**:
  * When an AI tank has a confirmed main-gun target in sight (`18m–110m`), it holds standoff distance scaled by crew personality (`50m` Brawler, `72m` Support, `95m` Sniper) using `StopAndRotateTowardTarget`, and engages reverse gear (`ForceRetroFor` + `MoveTankRetro`) if an enemy tank or AT threat closes within `35m`.

### 3.5. Smart Anti-Armor Target, Fire & Evade Discipline (`PreventSmallArmsFireAtTanks`)
* **What Vanilla Did**:
  * Riflemen and SMG troopers fired endless magazines at 50mm+ steel tank hulls, revealing themselves to the tank's gunner, or knelt helplessly in the middle of the street when a tank approached.
* **Why It Needed Changing**:
  * Infantry needed to target actual vulnerabilities (exposed crew, rubber tires, tracks with AT rifles), throw grenades when close, hold useless small-arms fire against heavy armor, and **scramble to cover** when they have no way to hurt an approaching tank.
* **Our Justification & Fix ([`TryEvaluateVehicleTarget`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L151-L351) & [`TryEvadeArmorToCover`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L372-L444))**:
  * Redirects small arms at exposed crew (`Seats.showUnitSet == true`) and un-flattened tires (`VehicleDamageableWheelPart`), aims AT rifles at unbroken tracks (`VehicleDamageableTrackPart`), throws frag/AT grenades within range, re-targets enemy foot soldiers, and makes un-equipped infantry within `85m` scramble behind walls/cover or bound diagonally out of the tank's sightline.

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

## 5. Ballistics, Plane Audio De-Throbber & Immersion ([`BallisticsAndImmersionModule.cs`](../src/EasyRed2AI/Modules/BallisticsAndImmersionModule.cs))

### 5.1. Client-Side Plane Engine Loop De-Throbber (`VehiclePlane.UpdateMotorSound`)
* **What Vanilla Did**:
  * Every `VehiclePlane` started its motor loop (`engine_move` / `engine_move_far`) at `time = 0.0s` on spawn and on every near/far distance switch (`SwitchMotorSound`), while high `dopplerLevel` settings and un-smoothed frame-to-frame pitch updates created a loud, rhythmic "wah-wah" throbbing/flanging noise on flybys.
* **Why It Needed Changing**:
  * Aircraft audio loops were fatiguing and phase-locked whenever multiple planes flew overhead.
* **Our Justification & Fix ([`VehiclePlane_UpdateMotorSound_Patch`](../src/EasyRed2AI/Modules/BallisticsAndImmersionModule.cs#L61-L128))**:
  * 100% client-side patch that offsets `engine_source.time` to a deterministic per-plane phase on every clip start/switch, clamps `dopplerLevel <= 0.22f`, applies a `±2.2%` per-plane harmonic detune, and low-pass smooths `engine_source.pitch`.

### 5.2. Material-Aware Thin-Cover Bullet Penetration (`BulletInstance.OnHit`)
* **What Vanilla Did**:
  * A thin wooden picket fence, shutter, or interior door completely stopped full-power `7.92mm` / `.30-06` rifle and `.50 BMG` rounds in `BulletInstance.OnHit`.
* **Why It Needed Changing**:
  * Thin wooden props acted like solid concrete bunkers against rifle and machine-gun fire.
* **Our Justification & Fix ([`BulletInstance_OnHit_Patch`](../src/EasyRed2AI/Modules/BallisticsAndImmersionModule.cs#L137-L290))**:
  * Caches collider `ImpactType` (`_colliderImpactCache`) and uses an O(1) single-collider back-face raycast (`col.Raycast`) to measure exact obstacle thickness (`<= 0.35m` wood for rifle/MG rounds, `<= 0.48m` wood/generic/light metal for high-caliber rounds), spawning a single continuation `BulletInstance` (`canRicochet: false`, `42%–64%` velocity) from the exit point.

---

## 6. Death Screen Obituary, Player QoL & Zero-Spike Co-op Architecture

* **Instant Full-Opacity Death Screen Obituary + Soldier Age ([`DeathScreenAndQoLModule.cs`](../src/EasyRed2AI/Modules/DeathScreenAndQoLModule.cs))**:
  * Vanilla's `DeathPanel` used a slow staggered `Animation` where birth/death years faded in right as the panel closed, and gave zero indication of who killed you, from where, or with what weapon. We force immediate 100% alpha across all `DeathPanel` text labels, compute the fallen soldier's exact **Age at death (`1925 - 1944 (Age 19)`)**, and display **Killer Rank + Name + `[FRIENDLY FIRE]` + Weapon • Hit Zone • Distance (`42m`)**.
* **Quick-Tap `Tab` (`OrdersMenu`) Spotting & 64-Character Multiplayer Room Names**:
  * Lets Squad Leaders and Radiomen short-press `Tab` (`< 0.24s`) to spot (`TrySpotInLookDirection`) without opening the radial wheel, and lifts `ServerListMenu.maxServerNameLength` + `InputField.characterLimit` from `8` to **`64` characters**.
* **Zero-Spike `AiCore` & Photon Co-op Ownership Safety ([`AiCore.cs`](../src/EasyRed2AI/Core/AiCore.cs))**:
  * Replaces multi-hop `GetComponentInParent<Creature>()` raycast loops with single-raycast `ResourcesManager.groundMask` queries (`GetSolidEnvironmentMask`), guards every AI mutation with `IsLocallyOwnedBot` / `IsLocallyOwnedVehicle` so Co-op hosts and clients never fight over remote unit state, and enforces a global Photon RPC budget (`TryConsumeBotRpcBudget`).
