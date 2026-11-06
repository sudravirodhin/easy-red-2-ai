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
  * While on a bridge (`isOnBridge`), prevents non-bleeding bots from dropping `Prone`, skips mid-bridge **Bounding Overwatch** pauses (`moveLookingTarget = true` to fire on the move across the span), and disables lateral **Anti-Stacking**, **Smart Wall-Side Cover**, and **Wall-Sidestepping** so bots never nudge off bridge edges.

### 2.8. Shell-Shocked & Skittish Dynamic Surrender (`Soldier.SurrenderSynched` — RVA `0x18076a3c0`)
* **What Vanilla Did**:
  * XREF analysis of `Soldier.SurrenderSynched` (`0x18076a3c0`) confirmed it was **only** called by `BattleManager.SurrenderUnitsOnMissionAreas` (when an entire objective phase flips), `BattleManager.OnWin`, `Pause.Surrender`, and `Soldier.<UseSyryngeCR>`. AI bots never dynamically surrendered during live combat, even when completely cut off, out of ammo, bleeding, and shell-shocked with enemy troops 5 meters away.
* **Why It Needed Changing**:
  * Allowing isolated, broken, shell-shocked skittish or green rookie soldiers to surrender when overrun at close range creates authentic WWII combat moments and feeds directly into `EnableComradeLiberation` (`UnSurrenderSynched`) if friendly forces counterattack.
* **Our Justification & Fix ([`TryEvaluateShellShockSurrender`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs#L1786-L1964))**:
  * Strictly restricts dynamic surrender to **Skittish archetypes (`[Cautious]` Riflemen & `[Triage]` Medics with `vet01 < 0.35f`)** under heavy shell-shock (`>= 68` suppression, or `>= 52` if bleeding/out of ammo) and **Green Rookies (`vet01 < 0.20f`)** under extreme shell-shock (`>= 86` suppression, or `>= 70` if bleeding/out of ammo).
  * Squad Leaders, `[Spearhead]`, `[Tactician]`, `[Old Guard]`, `[Shock]` Assaulters, `[Anchor]` MG Gunners, `[Heroic]` Medics, and hardened veterans **never** surrender.
  * Requires at least one live enemy closing within **`18m`** (`<= 6.5m` vertical separation), local isolation (`<= 1` friendly within `16m`), and **no Squad Leader or `[Spearhead]` / `[Old Guard]` officer within `22m`** (an inspiring officer keeps wavering troops in the fight).

### 2.9. Cross-Subsystem Priority Hierarchy & Conflict Resolution
* To ensure tactical subsystems never fight over `MoveDirectlyToward`, `SetBotPose`, `visibleTarget`, or grenade throws, `Battle Hardened` enforces a strict humanlike priority hierarchy:
  1. **Movement Priority Chain**:
     * *(1) Survival & Shell-Shock Surrender / Bleeding Cover-Seek* → *(2) Point-Blank CQB Melee (`<= 5.2m`)* → *(3) Evading Heavy Armor (`isEvadingArmor`)* → *(4) Dedicated Vehicle Repair / Emplacement Staffing (`isRepairingVehicle` / `isStaffingEmplacement`)* → *(5) Bridge Crossing Advance (`isOnBridge`)* → *(6) Attacker Bounding Overwatch (`dist >= 18m`)* → *(7) Out-of-Combat Comrade Liberation (`!HasEnemyAndCanFireAtHim`)*.
     * Lower-priority impulses (such as Bounding Overwatch firing holds or walking to liberate a distant comrade) are explicitly blocked from overriding a soldier who is fleeing a tank, engaged in point-blank melee, crossing a bridge, or running to crew an emplacement.
  2. **Indoor Room vs. Exterior Wall Disambiguation ([`IsPositionOnExposedSideOfWall`](../src/EasyRed2AI/Modules/InfantryTacticsModule.cs#L614-L639))**:
     * Checks `AiCore.HasLowCeilingOverhead(pos, 3.5f)` and forward room enclosure (`4.5m`) so `SmartWallSideCover` never mistakes the **interior back wall of a room or bunker** for an exposed exterior wall face.
  3. **Centralized Grenade Friendly-Fire & Indoor Arc Safety ([`IsSafeToThrowGrenadeAt`](../src/EasyRed2AI/Modules/VisionAndMemoryModule.cs#L632-L685))**:
     * Every mod-triggered grenade throw (last-known cover flushes, `[Breacher]`/`[Grenadier]`/`[Demo]` combat throws, and anti-vehicle frag throws) verifies both an unobstructed overhead throw arc (preventing indoor window-frame bounces) and zero friendly soldiers within the blast radius (`8.5m–9.5m`).
  4. **Blind Suppressive Fire Discipline ([`TryMaintainSuppressiveFireOnLastKnown`](../src/EasyRed2AI/Modules/VisionAndMemoryModule.cs#L748-L824))**:
     * Restricts last-known cover suppression to dedicated MGs/gunners or close-mid range automatic weapons with ample ammo (`>= 12` rounds within `55m`), verifies an unobstructed sightline up to the enemy's cover edge (`dist - 2.5m`) so bots never blind-fire into intermediate hills/buildings, and blocks `AutoPeek` / `TrySidestepWallObstruction` when `visibleTarget == null`.

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
* **Our Justification & Fix ([`TryDismountTransportUnderThreat`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L1011-L1113))**:
  * Hooks `AIVehicle.MovableVehicleRoutine` (`SomeoneIsSuppressed()`) and `VehicleWithWheels.OnVehicleSuppressed` on pure transports (`VehicleHasNoTurrets` or transport halftracks carrying passengers) and immediately dismounts embarked passengers (`DropInfantry` + `OrderLeaveVehicle`) when suppressed, damaged under fire, or within `48m` of an enemy tank/AT gun/squad.

### 3.4. Tank Standoff & Reverse Discipline (`AIVehicle.MoveTowardCurrentNodeTank`)
* **What Vanilla Did**:
  * AI tanks continued driving forward along their waypoint path even when they already had a clear main-gun line of sight (`CurrentVisibleTarget`) at 70m, rolling straight into point-blank infantry AT-grenade range (`< 25m`).
* **Why It Needed Changing**:
  * Tanks threw away their ranged armor advantage and died to infantry hand-thrown explosives within seconds.
* **Our Justification & Fix ([`AIVehicle_MoveTowardCurrentNodeTank_Patch`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L1057-L1184))**:
  * When an AI tank has a confirmed main-gun target in sight (`18m–110m`), it holds standoff distance scaled by crew personality (`50m` Brawler, `72m` Support, `95m` Sniper) using `StopAndRotateTowardTarget`, and engages reverse gear (`ForceRetroFor` + `MoveTankRetro`) if an enemy tank or AT threat closes within `35m`.

### 3.5. Smart Anti-Armor Target, Fire & Evade Discipline (`PreventSmallArmsFireAtTanks`)
* **What Vanilla Did**:
  * Riflemen and SMG troopers fired endless magazines at 50mm+ steel tank hulls, revealing themselves to the tank's gunner, or knelt helplessly in the middle of the street when a tank approached. Worse, because `Soldier.GetBestVisibleEnemy` returns the closest enemy in the octree, whenever an enemy tank led an infantry push, defending riflemen locked onto the tank and ignored the enemy infantry advancing right behind it.
* **Why It Needed Changing**:
  * Infantry needed to target actual vulnerabilities (exposed crew, rubber tires, tracks with AT rifles), throw grenades when close, **re-target visible enemy foot soldiers advancing with the tank** (`TryFindVisibleFootEnemy`), and **scramble to cover** when they have no way to hurt an approaching tank and no foot enemies in sight.
* **Our Justification & Fix ([`TryEvaluateVehicleTarget`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L151-L351), [`TryFindVisibleFootEnemy`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L376-L433) & [`TryEvadeArmorToCover`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L435-L509))**:
  * Redirects small arms at exposed crew (`Seats.showUnitSet == true`) and un-flattened tires (`VehicleDamageableWheelPart`), aims AT rifles at unbroken tracks (`VehicleDamageableTrackPart`), throws frag/AT grenades within range, switches **any** rifleman/MG gunner/AT unit to visible enemy foot soldiers advancing with the tank so defenders strip away the tank's infantry screen, and makes un-equipped infantry within `85m` scramble behind walls/cover or bound diagonally out of the tank's sightline.

### 3.6. Combined-Arms Known Target Priority Support & Air Superiority First (`EnableCombinedArmsPrioritySupport`)
* **What Vanilla Did**:
  1. `TurretAI.FindBestTarget` (`0x180831a80`) scored every visible enemy solely by raw distance `Vector3.Distance(turretPos, targetPos)`, completely ignoring `sq.isMarked` / `isHalfMarked`, active friendly radio requests (`RadioRequestActiveAndConfirmed`), and `sq.IsEnemyAwareOfThisSquad`. Worse, if `FindBestTarget` found *any* lone visible enemy, `TurretAI.RefreshTargets` (`0x180832b3e`) skipped `FindBestArtilleryRequest` completely.
  2. `AIVehicle.UpdatePath` (`0x18057dab0`) drove tanks to random points inside the objective circle (`ResourcesManager.RandomPointInCircleXZ`) instead of orienting toward known enemy squads or friendly `tankSupport` requests.
  3. `AIPlane.SetFlyAroundConquerArea` (`0x1805740f0`) & `SetFlyAroundObjectiveArea` (`0x180574530`) always orbited the **very first non-null objective** in `BattleManager.ActiveObjectives(true)`.
  4. In `AIPlane.GetBestAircraftVisibleEnemy` (`0x1805718f0`), vanilla intended planes to spot enemy fighters out to `1700m` and enemy bombers out to `4000m` (`0x180571e56`), **but** passed `maxDistance = 850m` (`effectiveMaxDist`, `0x180571bc6`) into `PointOctree.EnumerateNearby`—so any enemy plane between `850m` and `4000m` was never even returned by the octree! Furthermore, once a fighter entered `preparingDive` or `doingDive` against a ground target, `AIPlane.Update` (`0x180574fc0`) stopped calling `GetBestAircraftVisibleEnemy` and ignored enemy fighters until the ground dive finished.
* **Why It Needed Changing**:
  * Tanks and emplacements attacked random lone targets instead of assisting friendly infantry against **KNOWN (Spotted/Marked/Radio-Requested/Engaged)** enemy squads, while aircraft initiated ground strafing runs even when enemy fighters were contesting the skies.
* **Our Justification & Fix ([`RefreshCombinedArmsIntelIfNeeded`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L679-L891), [`TryPrioritizeKnownTargetForTurret`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L945-L1109), [`TrySteerVehicleTowardKnownThreat`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L1111-L1240), [`TrySteerAircraftOrbitTowardKnownThreat`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L1287-L1401) & [`TrySelectKnownAircraftTarget`](../src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs#L1403-L1525))**:
  * Caches active radio support requests, known enemy squad clusters, and airborne aircraft once every `1.0s` globally (`RefreshCombinedArmsIntelIfNeeded`).
  * **Turrets & Tanks**: Re-targets turrets on `TurretAI.RefreshTargets` ticks toward higher-priority Marked/Radio-Requested/Engaged squads (while preserving `< 32m` self-defense and `< 130m` armor duels) and steers combat vehicles' `setVehicleDestination` within `GetCurrentSquadDestinationAreaSize()` (`rerollDestination = false`) toward tactical overwatch positions.
  * **Air Superiority First ("Clear Skies Only" Ground Support)**: Restores full air-to-air detection (`2200m` fighters / `4000m` bombers, prioritizing enemy fighters over bombers), blocks ground target selection while enemy aircraft contest the airspace, and immediately aborts ground patrols/dives (`DoAttackTarget(bestEnemyAir)` -> `targetingPlane` when safely above pull-up altitude) to dogfight enemy aircraft first. Only once the skies are clear do aircraft shift patrol orbits (`SetFlyAroundPos`) and CAS dive selection toward active radio requests and Marked/Known enemy squads.

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
