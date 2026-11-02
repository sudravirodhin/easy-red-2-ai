# Easy Red 2 — Humanized AI & Veteran Tactics (`v0.2.8`)

> [!IMPORTANT]
> **Disclosure — Vibecoded Project:**
> This mod was **vibecoded** (designed, reverse-engineered, and written collaboratively with an AI coding agent). If you are averse to AI-assisted or AI-generated software/mods, please be aware of this upfront before downloading or using this repository.

A comprehensive **BepInEx 6 (IL2CPP)** AI and fairness overhaul for **Easy Red 2** designed to reduce frustrating *"how did they see me?"* and *"what just killed me?"* moments. It replaces vanilla's robotic X-ray spotting, erratic stance oscillation, suicidal open-field bandaging/repairs, standing in front of walls, and wasteful small-arms fire against tanks with human-like perception, self-preservation, cover intelligence, a 15-minute linear Veteran progression system, and full Death Screen Kill Attribution.

---

## Key Improvements Over Vanilla

### 1. Humanized Vision, Stance Stealth & FOV Cones ([`VisionPatches.cs`](src/EasyRed2AI/VisionPatches.cs))

| Feature | Vanilla Behavior | Humanized AI (`v0.2.8`) |
| :--- | :--- | :--- |
| **Stance Sight Ranges** | `220m` Standing, `50m` Crouching, `37m` Prone when unspotted — but jumps **all stances to a flat `300m`** the instant `IsEnemyAwareOfThisSquad` is true. | **150m Standing**, **80m Crouching**, **40m Prone**. Squad combat awareness applies only a **1.20x multiplier** (`48m` prone, `96m` crouched) so taking cover in grass/craters actually conceals you during firefights. |
| **Individual Firing Reveal** | One soldier firing alerts the enemy to the entire squad's crouched/prone members out to `300m`. | Only the **specific soldier who fired** gets a temporary **+85m visibility penalty for 4.0 seconds** (muzzle flash, smoke, and gunshot sound). Silent squadmates stay hidden. |
| **Human FOV Cones** | Alerted or suppressed bots bypass the FOV check and gain **360-degree omnidirectional vision** out to `300m`. | Enforces realistic human vision cones even in combat: **20m** 360° close hearing/awareness, **80° half-angle** (160° cone) mid-range out to **75m**, and **45° half-angle** (90° cone) beyond **75m**. |
| **Scopes & Tank Optics** | Sniper scopes and tank turret optics do not dynamically scale spotting distance by magnification. | Scales AI spotting range by actual optic magnification (`60 / AimingFOV`, capped at **2.25x**) within a focused **25° half-angle** optic gaze cone. |
| **Aircraft Spotting** | AI pilots spot unmarked individual infantry across the map through foliage. | Pilots only spot ground infantry from afar if their squad is **Marked/Spotted** or near a radio request; unmarked standing infantry are capped at **180m**, and unmarked crouching/prone infantry remain hidden unless actively firing. |

---

### 2. 15-Minute Veteran Progression System ([`VeteranAndTacticsPatches.cs`](src/EasyRed2AI/VeteranAndTacticsPatches.cs))

* **Linear Survival Curve (`900s` / 15 Minutes)**: Every AI soldier tracks their time survived in battle plus combat experience bonuses (suppressing fire, liberating comrades, capped at 25% of maturity).
* **Realistic Human Caps (Never Superhuman)**:
  * Weapon spread/inaccuracy scales down linearly by up to **20% max** at 100% veterancy (`SoldierAI.ProcessAiAccuracy`).
  * Suppression cover-reaction cooldown tightens from vanilla's sluggish `8.0s` down to **4.8s (Rookie) → 2.2s (Veteran)**.
  * Friendly-fire awareness cone widens from **5.0° → 6.5°**.
* **Dynamic Field Promotions & Visual Recognition**:
  * Surviving soldiers earn field promotions (**Private → Corporal [Vet I] at 3m → Sergeant [Vet II] at 7.5m → First Sergeant [Vet III ★] at 12m**), dynamically updating their **3D uniform shoulder rank decals** (`UpdateRanksDecal`).
  * Displays color-coded `[Vet I]`, `[Vet II]`, and `[Vet III ★]` tags in the bottom-left **Squad Roster UI** and draws a **3D overhead chevron badge** when hovering your crosshair over friendly veterans.

---

### 3. Self-Preservation, Cover Intelligence & Stance Discipline ([`VeteranAndTacticsPatches.cs`](src/EasyRed2AI/VeteranAndTacticsPatches.cs))

* **Stance Redirection & Anti-Bobbing**:
  * Vanilla assigns every bot a random `"favourite fighting pose"` at spawn (`66%` Crouch/Prone), causing bots to crawl across open fields out of combat or shoot while standing upright in the open (`33%`), and oscillates `IsMoveTime` on a 7.0s square wave (`2.5s` move / `4.5s` stop), causing bots to bob `Stand <-> Crouch` when lower eye height loses line-of-sight.
  * **Out of Combat / Moving**: Bots move at full standing speed (`Idle`) unless stealth-following a crouched squad leader or locked in combat crouch.
  * **Engaging Enemies**: Bots never fire standing in the open—they drop to **Crouch** (or **Prone** for Machine Gunners / heavy suppression `>= 35`) *only* after verifying a clear line-of-fire from that lower stance, and maintain a combat crouch lock so brief LOS flickers behind ridges or bushes never cause `Stand <-> Crouch` bobbing.
  * **Stationary Defenders**: Soldiers occupying `CombatCover`, holding inside an objective capture zone (`PositionIsInsideValidConquerZone`), sheltered behind front walls, or in alerted squads stay **Crouched** rather than standing tall or randomly lying prone in tall grass (`DestinationWithoutCover` 50% prone override fixed).
* **Stand BEHIND Walls, Never IN FRONT (`EnableSmartWallSideCover`)**:
  * Vanilla `CombatCover.IsCoverAvailable` only checks node look-angle wideness (often 180°–360°), and vanilla `Squad.HoldArea` often leaves `formationDirection` pointing along the squad's travel vector rather than toward the enemy—causing defenders to stand on the **exposed front side** of walls with the wall at their back facing incoming fire.
  * Computes the true enemy threat direction (visible enemies → squad targets → attacker/defender objective frontline) and raycasts both in front (`+threatDir`) and behind (`-threatDir`) candidate positions.
  * Rejects cover nodes with a wall at the soldier's back and open air toward the enemy, and automatically repositions exposed defenders (`TryFindShelteredSideOfWall`) to the **sheltered leeward side** of the wall.
* **Seek Cover First When Bleeding (`BandageOnlyAfterFindingCover`)**:
  * Instead of freezing upright in the middle of an open road to bandage the instant they start bleeding, wounded soldiers sprint to nearby cover first (up to a `3.5s` safety window) and drop to `Crouch`/`Prone` before bandaging.
* **Sheltered Tank & Vehicle Crew Repairs (`EnableSmartTankCrewRepair`)**:
  * Vanilla `VehicleRepairTask` targets the center of the tank and considers the repair point reached anywhere within `5.5m`—meaning tank crewmen pop out of the hatch on the exposed front/side hull and immediately start welding in the enemy firing line.
  * Routes repairing crewmen around the hull to the **sheltered leeward/rear side** opposite the enemy threat vector and keeps them **Crouched** behind the tank's armor while repairing (with a close-range self-defense override if flanked within `35m` under heavy suppression).

---

### 4. Muzzle Discipline, Bipods, Anti-Armor & Mobility ([`VeteranAndTacticsPatches.cs`](src/EasyRed2AI/VeteranAndTacticsPatches.cs))

* **Smart Anti-Armor Target & Fire Discipline (`PreventSmallArmsFireAtTanks`)**:
  * **Fixes Vanilla's Small-Arms Tank Plinking**: In vanilla, riflemen set `CurrentVisibleTarget` to enemy tanks whenever no foot soldier is visible, mark `targetInWeaponRange = true` based solely on rifle range (`300m`), and fire 15% random bursts into tank hulls (`RotateAndFireToward`), giving away their position for zero damage.
  * **Targets Exposed Crew (`Seats.showUnitSet`)**: If an enemy vehicle has a visible exposed crew member or passenger (open-topped tank destroyers like Marder/Wolverine/SU-76, halftracks, jeeps, pintle MG gunners), infantry redirect `visibleTarget` and aim directly at the **exposed crewman's chest/head** instead of the steel hull.
  * **Targets Vulnerable Weak Spots (Tires, Tracks & Light Armor)**: Evaluates actual projectile penetration at distance (`BulletData.CalculateMmPenetrationWithFallof`). Within `60m`, small arms aim directly at **un-flattened rubber tires** (`VehicleDamageableWheelPart`) on wheeled vehicles until flattened; AT rifles (`25–35mm` pen) aim directly at penetrable armor plates or **unbroken tank tracks** (`VehicleDamageableTrackPart`).
  * **Throws Normal & AT Grenades at Close Range**: Vanilla only checks `VirtualATGrenade` against vehicles. Infantry within throwing range (`7.5m` to `GetGrenadeThrowRange`) will now throw **both normal grenades (`VirtualGrenade`) and anti-tank grenades (`VirtualATGrenade`)** at enemy tanks and vehicles.
  * **Holds Useless Small-Arms Fire & Re-Targets Foot Soldiers**: If a soldier's weapon cannot harm the tank and no exposed crewman is visible, they hold fire, stay crouched in cover, and (if flagged `isAtUnit` after expending rockets) automatically re-query `GetBestVisibleEnemy` to engage enemy foot soldiers with their rifle/sidearm.
* **Wall-Shooting Prevention & Auto-Peek (`EnableWallShootingPrevention` & `EnableAutoPeekOverCover`)**:
  * Raycasts `2.2m` ahead of the weapon muzzle before pulling the trigger so bots stop dumping ammo into windowsills, sandbags, or hill crests right in front of their gun barrel.
  * When blocked by low cover, bots automatically step up one stance (`Prone → Crouch` or `Crouch → Stand`) to **peek over the obstacle** if the higher stance has a clear line of fire, then duck back down when reloading, suppressed (`> 30`), or after `4.0s`.
* **Machine Gunner Bipod Wall & Ground Mounting (`EnableGunnerBipodWallMounting`)**:
  * Gunners (`MG34`, `MG42`, `BAR`, `Bren`, `DP-27`, etc.) actively seek nearby walls, sandbags, and windowsills and deploy their bipods (`BipodCheck(true)`) on wall ledges or prone on the ground.
* **Friendly Fire Avoidance & Anti-Stacking (`EnableFriendlyFireAvoidance` & `EnableAntiStacking`)**:
  * Holds fire when a friendly soldier is within a **5.0°–6.5° cone** or **0.65m close-range corridor** of the muzzle and sidesteps laterally to clear the line of fire.
  * Maintains **2.4m personal spacing** along the firing line so bots don't stack inside each other on cover points.
* **Wall & Fence Vaulting (`EnableWallVaulting`)**:
  * Advancing AI soldiers automatically detect and vault over low walls, fences, and sandbags (`CanVault` → `Vault`).
* **Comrade Liberation (`EnableComradeLiberation`)**:
  * Unpinned friendly bots within **30m** move toward surrendered comrades and free them (`UnSurrenderSynched`).

---

### 5. Death Screen Kill Attribution (`EnableDeathScreenAttribution`)

Eliminates *"what killed me?"* guesswork when you go down by displaying a detailed obituary on both the native `DeathPanel` and a centered HUD banner (compatible with `ragdoll_overhaul`'s fade-to-black death screen):

* **Who Killed You**: Attacker's rank (`Pvt.`, `Cpl.`, `Sgt.`, `1st Sgt.`, `Lt.`), full name, and any `[Vet I / II / III ★]` badge.
* **Friendly Fire & Self-Inflicted Tags**: Clearly highlights `[FRIENDLY FIRE]` in coral red if killed by an allied soldier/vehicle/artillery, or `[SELF-INFLICTED]` in amber if killed by your own explosive.
* **What Was Used**: Localized weapon name (e.g., `Karabiner 98k`, `M1 Garand`, `MG 42`), thrown grenade (`Stielhandgranate 24`, `Mk 2 Grenade`), vehicle/tank (`Pz.Kpfw. IV Ausf. H`), or `Artillery / Mortar Barrage`.
* **Hit Location, Bleed-Out & Distance**: Shows the exact impact zone (`Headshot`, `Headshot (Helmet)`, `Torso`, `Arm`, `Leg`, `Shrapnel`, `Explosion`), distinguishes instant kills (`Killed by...`) from delayed bleed-out deaths (`Bled out from wounds by...`), and reports the exact distance in meters (`[Headshot • 114m]`).

---

## Installation

1. Install **[BepInEx 6 Bleeding Edge (Unity IL2CPP win-x64)](https://builds.bepinex.dev/projects/bepinex_be)** into your `Easy Red 2` game directory and launch the game once so BepInEx generates its `interop` assemblies.
2. Download `EasyRed2HumanAI.dll` from the **[Releases](../../releases)** page (or build it from source) and place it in:
   ```text
   Easy Red 2\BepInEx\plugins\EasyRed2HumanAI.dll
   ```

---

## In-Game Hotkeys & Configuration

Configuration file (auto-generated on first launch): `BepInEx\config\com.antigravity.easyred2.humanai.cfg`

* **`F8`**: Toggle Mod **ENABLED / DISABLED** live in-game.
* **`F9`**: **Hot-Reload** `com.antigravity.easyred2.humanai.cfg` live in-game without restarting.
* **`F10`**: Display **Live Tactical Telemetry Toast** (Active Veterans, Field Promotions, Survival Drops, Bipods Deployed, Wall Blocks/Peeks, Wall-Side Repositions, Useless Tank Fire Blocked / Exposed Crew Targeted, Friendly Fire Saved, and Sheltered Vehicle Repairs).

---

## Building From Source

Run [`build.ps1`](build.ps1) from PowerShell (automatically checks that `Easy Red 2.exe` is not running before deploying):

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Deploy
```
