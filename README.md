# Easy Red 2: Battle Hardened (`v0.5.0`)
*Tactical AI, Veterancy, Personalities & Combined-Arms Overhaul*

> [!IMPORTANT]
> **Disclosure — Vibecoded Project:**
> This mod was **vibecoded** (designed, reverse-engineered, and written collaboratively with an AI coding agent). If you are averse to AI-assisted or AI-generated software/mods, please be aware of this upfront before downloading or using this repository.
>
> **Credit & Inspiration:**
> Special credit and thanks to **[AtlasActual (`Easy-Red-2-Realism-Overhaul`)](https://github.com/AtlasActual/Easy-Red-2-Realism-Overhaul)** for pioneering several of the combined-arms, objective-coordination, and tactical realism concepts that inspired parts of this project's roadmap and feature sets.

A comprehensive **BepInEx 6 (IL2CPP)** AI, combined-arms, and fairness overhaul for **Easy Red 2** designed to reduce frustrating *"how did they see me?"* and *"what just killed me?"* moments while making battlefield AI behave like coordinated human squads. It replaces vanilla's robotic X-ray spotting, erratic stance oscillation, indoor floor-clipping prone drops, unguarded rear objectives, suicidal APC charges, and point-blank tank rushes with human-like perception, last-known suppression, bounding overwatch, a `6.0s` Macro Objective & Emplacement Director, Tank Standoff & Reverse discipline, a 15-minute linear Veteran progression system, **30 Role-Based AI Personalities**, **Client-Side Plane Audio Smoothing**, **Thin-Cover Wood Bullet Penetration**, an **in-game `F6` Feature Bundle Menu**, and full Death Screen Kill Attribution.

---

## Compatibility & Tested Version

| Property | Details |
| :--- | :--- |
| **Target Branch** | Steam `public` (Default Branch) |
| **Confirmed Game Version** | **Easy Red 2 — Game Build `921`** (Steam Build ID `26375879`, Unity `2022.3.62f3` IL2CPP x64) |
| **Mod Loader** | **[BepInEx 6 Bleeding Edge (`BepInEx-Unity.IL2CPP-win-x64`, build `#791`+)](https://builds.bepinex.dev/projects/bepinex_be)** |
| **Last Confirmed Test Date** | **November 5, 2026** (`2026-11-05`) |

---

## Installation (Two Release Options)

Download your preferred package from the **[Releases](https://github.com/sudravirodhin/easy-red-2-ai/releases)** page:

### Option A: With BepInEx Included (`EasyRed2HumanAI-v0.5.0-with-BepInEx.zip`) — *Recommended for Fresh Installs*
Includes **[BepInEx 6.0.0-be.791 (Unity.IL2CPP-win-x64)](https://builds.bepinex.dev/projects/bepinex_be)**, the `.NET 6` runtime, Unity `2022.3.62` base libraries, pre-generated IL2CPP `interop` assemblies for Easy Red 2 Build `921`, and `EasyRed2HumanAI.dll` (with no other third-party mods).
1. Open your **Easy Red 2** installation folder (e.g. `Steam\steamapps\common\Easy Red 2`, where `Easy Red 2.exe` is located).
2. Extract all contents of `EasyRed2HumanAI-v0.5.0-with-BepInEx.zip` directly into the `Easy Red 2` folder.
3. Launch the game!

### Option B: Without BepInEx / Plugin Only (`EasyRed2HumanAI-v0.5.0-PluginOnly.zip` or `EasyRed2HumanAI.dll`)
For users who already have **[BepInEx 6 Bleeding Edge (`BepInEx-Unity.IL2CPP-win-x64`)](https://builds.bepinex.dev/projects/bepinex_be)** installed:
1. Download `EasyRed2HumanAI-v0.5.0-PluginOnly.zip` (or `EasyRed2HumanAI.dll` directly).
2. Place `EasyRed2HumanAI.dll` into:
   ```text
   Easy Red 2\BepInEx\plugins\EasyRed2HumanAI.dll
   ```

### Switching to Vanilla or Uninstalling (`Uninstall-BepInEx-Mod.bat`)
Included in `EasyRed2HumanAI-v0.5.0-with-BepInEx.zip` (and in the root of this repository) is **[`Uninstall-BepInEx-Mod.bat`](Uninstall-BepInEx-Mod.bat)**. Double-click it inside your `Easy Red 2` folder when the game is closed to:
* **`[1] Play Vanilla Temporarily`**: Moves `BepInEx`, `dotnet`, and `winhttp.dll` into `_BepInEx_Disabled_Backup\` so the game runs 100% pure vanilla without losing your configs or other plugins.
* **`[2] Restore Modded Game`**: Moves everything back from `_BepInEx_Disabled_Backup\` in one click.
* **`[3] Uninstall Completely`**: Permanently deletes `BepInEx`, `dotnet`, `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`, and `changelog.txt`.
*(Tip: If you only want to toggle this AI mod off mid-match without closing the game, press **`F8`** in-game!)*

---

## What's Different? (Original Systems & Zero-Spike Architecture)

Alongside shared tactical realism goals (some inspired by [`Easy-Red-2-Realism-Overhaul`](https://github.com/AtlasActual/Easy-Red-2-Realism-Overhaul)), **`Battle Hardened`** is built from the ground up around **original progression, targeting, audio, and QoL systems** and a **"Less is More" zero-spike Co-op architecture**:

* **15-Minute Veteran Progression & 3D Shoulder Rank Promotions**: Surviving AI soldiers progress linearly over 15 minutes (`900s`) from Rookie to `[Vet I]`, `[Vet II]`, and `[Vet III ★]`, dynamically promoting in rank (`Pvt. → Cpl. → Sgt. → 1st Sgt.`), updating their **3D uniform shoulder rank decals** (`UpdateRanksDecal`), and displaying veterancy tags in the Squad Roster UI and 3D crosshair hover badges.
* **30 Role-Based AI Personalities (10 Roles × 3 Archetypes)**: Every Infantry, Tank Crew, AA/Light Vehicle, and Aircraft Pilot bot receives one of 3 role-matched personalities (`[Spearhead]`, `[Breacher]`, `[Skirmisher]`, `[Deadeye]`, `[Stalker]`, `[Heroic]`, `[Triage]`, `[Brawler Crew]`, `[Ace]`, etc.) governing suppression grit, peek cadence, tank standoff distance, and combat habits.
* **Death Screen Kill Attribution & Soldier Age Obituary**: Instant full-opacity obituary on the native `DeathPanel` showing **who killed you** (Rank + Full Name + `[FRIENDLY FIRE]`), **Weapon + Hit Zone (`Headshot (Helmet)`, `Torso`, etc.) + Distance (`42m`)**, and the fallen soldier's calculated **Age at death (`1925 - 1944 (Age 19)`)**.
* **Vehicle Weak-Spot & Exposed-Crew Targeting**: Small arms actively aim at **exposed vehicle crew (`Seats.showUnitSet`)** and **un-flattened rubber tires (`VehicleDamageableWheelPart`)**, while AT rifles aim at **unbroken tank tracks (`VehicleDamageableTrackPart`)** and penetrable armor plates rather than center-mass hull armor.
* **Window-Aperture Gaze Alignment & Dual Barrel-Height Auto-Peek**: Stationary indoor defenders detect `0.52m–1.28m` window openings to center their sight cone squarely out windows instead of staring at room walls, and evaluate both `spine` and `heldGun` barrel clearance to step up (`Prone → Crouch → Stand`) or sidestep corners.
* **Sheltered Leeward Tank Crew Repairs & Behind-Wall Cover Filtering**: Tank crews route around the hull to crouch on the sheltered side opposite enemy fire before repairing, and `CombatCover` filters out exposed front-of-wall cover nodes.
* **Client-Side Plane Engine Loop De-Throbber**: Desynchronizes aircraft motor loop playback offsets across near/far audio clips, clamps Doppler pitch warble (`<= 0.22`), and low-pass smooths engine pitch transitions to eliminate vanilla's repetitive "wah-wah" flyby throbbing.
* **Quick-Tap `Tab` Spotting & 64-Character Multiplayer Room Names**: Short-press your Squad Orders keybind (`Tab`) to immediately spot in your look direction without opening the radial wheel, and lift vanilla's 8-character multiplayer room name cap up to **64 characters**.
* **Zero-Spike Performance & Co-op RPC Budget**: Single-raycast `ResourcesManager.groundMask` environment queries (zero `GetComponentInParent` hierarchy walks), O(1) vision/cover caches, and global Photon RPC pacing designed for 100+ active bots in Co-op.

> *(Want the deep-dive engineering breakdown of what vanilla's `GameAssembly.dll` did under the hood and why each change was made? Read **[`docs/design-and-justifications.md`](docs/design-and-justifications.md)**!)*

---

## Key Improvements Over Vanilla

### 1. Humanized Vision, Stance Stealth, Local Callouts & Last-Known Suppression ([`VisionAndMemoryModule.cs`](src/EasyRed2AI/Modules/VisionAndMemoryModule.cs))

| Feature | Vanilla Behavior | Battle Hardened (`v0.5.0`) |
| :--- | :--- | :--- |
| **Stance Sight Ranges** | `220m` Standing, `50m` Crouching, `37m` Prone when unspotted — but jumps **all stances to a flat `300m`** the instant `IsEnemyAwareOfThisSquad` is true. | **150m Standing**, **80m Crouching**, **40m Prone**. Squad combat awareness applies only a **1.20x multiplier** (`48m` prone, `96m` crouched) so taking cover in grass/craters actually conceals you during firefights. |
| **Individual Firing Reveal** | One soldier firing alerts the enemy to the entire squad's crouched/prone members out to `300m`. | Only the **specific soldier who fired** gets a temporary **+85m visibility penalty for 4.0 seconds** (muzzle flash, smoke, and gunshot sound). Silent squadmates stay hidden. |
| **Human FOV Cones** | Alerted or suppressed bots bypass the FOV check and gain **360-degree omnidirectional vision** out to `300m`. | Enforces realistic human vision cones even in combat: **20m** 360° close hearing/awareness, **80° half-angle** (160° cone) mid-range out to **75m**, and **45° half-angle** (90° cone) beyond **75m**. |
| **Threat-Sector Gaze, Window Alignment & Flanking** | Stationary defenders never rotate once `StopMove` is called (often staring perpendicular to a window along an interior room wall) and suffer a **2.5s freeze** (`alertStartTime`) before locking onto spotted targets `> 30m`. | Stationary defenders orient toward **Known Enemy Contact Sectors** (`175m`), automatically detect **Window Apertures** (open exterior sightline at `1.28m` over a `0.52m` windowsill) and center their vision cone squarely out the window, spot targets in their central `28°` overwatch cone **1.20x further** and **4x faster (`0.15s–0.55s`)**, and leave **quiet flanks unwatched** so flanking is rewarded. |
| **Local Contact Callouts (`15m`) & Last-Known Suppression** | Only the individual spotter reacts unless the entire squad becomes globally aware; bots instantly stop firing the millisecond a target ducks behind cover. | When a bot visually confirms an enemy, nearby friendly bots within **15m** receive a 12-second **threat-sector gaze callout** (orienting toward the contact without X-ray wallhacks). When a confirmed enemy ducks behind cover, stationary **MG gunners/automatic riflemen** fire a 3.8s suppressive burst at the cover edge and nearby infantry within throwing range flush the cover with a fragmentation grenade. |
| **Scopes & Tank Optics** | Sniper scopes and tank turret optics do not dynamically scale spotting distance by magnification. | Scales AI spotting range by actual optic magnification (`60 / AimingFOV`, capped at **2.25x**) within a focused **25° half-angle** optic gaze cone. |
| **Aircraft Spotting** | AI pilots spot unmarked individual infantry across the map through foliage. | Pilots only spot ground infantry from afar if their squad is **Marked/Spotted** or near a radio request; unmarked standing infantry are capped at **180m**, and unmarked crouching/prone infantry remain hidden unless actively firing. |

---

### 2. 15-Minute Veteran Progression System ([`VeterancyAndPersonalitiesModule.cs`](src/EasyRed2AI/Modules/VeterancyAndPersonalitiesModule.cs))

* **Linear Survival Curve (`900s` / 15 Minutes)**: Every AI soldier tracks their time survived in battle plus combat experience bonuses (suppressing fire, liberating comrades, capped at 25% of maturity).
* **Realistic Human Caps (Never Superhuman)**:
  * Weapon spread/inaccuracy scales down linearly by up to **20% max** at 100% veterancy (`SoldierAI.ProcessAiAccuracy`).
  * Suppression cover-reaction cooldown tightens from vanilla's sluggish `8.0s` down to **4.8s (Rookie) → 2.2s (Veteran)**.
  * Friendly-fire awareness cone widens from **5.0° → 6.5°**.
* **Dynamic Field Promotions & Visual Recognition**:
  * Surviving soldiers earn field promotions (**Private → Corporal [Vet I] at 3m → Sergeant [Vet II] at 7.5m → First Sergeant [Vet III ★] at 12m**), dynamically updating their **3D uniform shoulder rank decals** (`UpdateRanksDecal`).
  * Displays color-coded `[Vet I]`, `[Vet II]`, and `[Vet III ★]` tags in the bottom-left **Squad Roster UI** and draws a **3D overhead chevron badge** when hovering your crosshair over friendly veterans.

---

### 3. Role-Based AI Personalities — 3 Archetypes Per Role (`EnableRolePersonalities`)

Instead of every bot using identical suppression thresholds, peeking rhythms, and engagement habits, each soldier and vehicle crewman is assigned one of **3 role-matched personalities** across **10 Infantry, Vehicle & Aircraft roles (30 personalities total)**:

| Role | Personality 1 | Personality 2 | Personality 3 |
| :--- | :--- | :--- | :--- |
| **1. Squad Leader** | **`[Spearhead]`**: Grants nearby squadmates (`< 22m`) `+12` suppression grit and leads aggressive pushes. | **`[Tactician]`**: Faster artillery/radio support checks (`next_requestingSupport`) and tight cover discipline. | **`[Old Guard]`**: Defensive anchor; prioritizes squad survival behind walls and rescues pinned comrades. |
| **2. Assaulter / SMG** | **`[Shock]`**: Resists dropping `Prone` until extreme suppression (`>= 68`) and bounds cover-to-cover aggressively. | **`[Breacher]`**: Rapid `2.0s` peek cooldown (`<= 38` suppression) and eagerly tosses frag grenades at close range. | **`[Skirmisher]`**: Expands lateral spacing by `1.45x` (`3.5m`) to flank wide and fires on the move (`moveLookingTarget`). |
| **3. Rifleman** | **`[Regular]`**: Balanced baseline fire-and-maneuver (`35` suppression threshold). | **`[Cautious]`**: Drops `Prone` at light suppression (`>= 18`), stays ducked longer (`6.2s`), and sheds nerve jitter as Veterancy rises (`[Composed]`). | **`[Grenadier]`**: Prioritizes rifle-grenades (`hasGrenadeLauncher`) and hand grenades within throwing range. |
| **4. Machine Gunner** | **`[Anchor]`**: Prioritizes wall/sandbag bipod mounting and holds mounted positions under heavy fire (`>= 60` suppression). | **`[Assault MG]`**: Advances with the rifle squad and rapidly deploys ground bipods in `Crouch`/`Prone`. | **`[Overwatch]`**: Holds medium-long range (`50m–160m`) overwatch cover to suppress enemies engaging squadmates. |
| **5. Marksman / Sniper** | **`[Deadeye]`**: **12% tighter accuracy** (`ProcessAiAccuracy`) when firing from `Crouch` or `Prone`. | **`[Stalker]`**: **35% shorter firing-reveal duration** when shooting from concealed crouch/prone cover. | **`[Spotter]`**: **+5° wider scoped FOV cone** for counter-sniping distant enemies. |
| **6. Anti-Tank / Sapper** | **`[Tank Hunter]`**: Stalks armor in crouch stealth and targets tracks/weak spots. | **`[Ambusher]`**: Patiently holds fire behind cover until armor closes within kill range (`< 68m`). | **`[Demo]`**: High suppression grit (`>= 60`) when closing in to hurl AT and frag grenades. |
| **7. Field Medic** | **`[Heroic]`**: Braver under fire (`<= 52` suppression) and searches `1.5x` wider (`45m`) to liberate/rescue comrades. | **`[Combat Medic]`**: Balanced combat-first medic (`35` suppression threshold); clears threats before rescuing. | **`[Triage]`**: High self-preservation (`20` suppression threshold, `4.5s` cover-seek window when bleeding) so the squad's medic survives. |
| **8. Tank / Armor Crew** | **`[Brawler Crew]`**: **20% faster turret target switching** (`TurretAI.RefreshTargets`) and `50m` close-support standoff. | **`[Sniper Crew]`**: **+5° wider long-range turret optic cone** and `95m` long-range standoff for armor duels. | **`[Support Crew]`**: Prioritizes infantry support (`supportedSquad`), `72m` standoff, and faster response to friendly radio requests. |
| **9. Light Vehicle / AA** | **`[Outrider]`**: Fast-reacting mobile gunner/driver (`20%` faster turret target refresh). | **`[Escort]`**: Cautious stand-off support and higher self-preservation when repairing. | **`[Flak Watch]`**: **+12° wider scan sector** in `TurretAI.CanSee` for tracking aircraft and elevated threats. |
| **10. Aircraft Pilot** | **`[Ace]`**: Extended dogfight pursuit leash (`fighterDogfightExtraLeash = 450m`) when chasing enemy planes. | **`[Daredevil]`**: Commits closer on CAS dive-bombing runs (`0.90x diveAlignDistance`). | **`[Disciplined]`**: Pulls out of dives earlier (`1.08x diveAlignDistance`) for safer high-altitude bomb releases. |

---

### 4. Self-Preservation, Indoor Ceiling Crouch, Bounding Overwatch & Cover Intelligence ([`InfantryTacticsModule.cs`](src/EasyRed2AI/Modules/InfantryTacticsModule.cs))

* **Stance Redirection, Anti-Bobbing & Indoor Ceiling Prone Prevention**:
  * **Out of Combat / Moving**: Bots move at full standing speed (`Idle`) unless stealth-following a crouched squad leader or locked in combat crouch.
  * **Indoor Ceiling Prone Prevention (`EnableIndoorCeilingPronePrevention`)**: Detects low interior ceilings (`<= 4.2m` overhead via quantized spatial cache) inside buildings, bunkers, and pillboxes so indoor soldiers stay in a functional **Crouch** fighting stance instead of lying `Prone` underneath windowsills or clipping into staircases.
  * **Attacker Bounding Overwatch (`EnableBoundingOverwatch`)**: Attacking infantry advancing toward an objective under suppression split into two alternating fireteams—executing **5-second crouched bounds** alternated with **4-second overwatch firing pauses** so half the squad provides covering fire while the other half advances.
* **Stand BEHIND Walls, Never IN FRONT (`EnableSmartWallSideCover`)**:
  * Computes the true enemy threat direction (visible enemies → squad targets → attacker/defender objective frontline) and raycasts both in front (`+threatDir`) and behind (`-threatDir`) candidate positions, rejecting exposed front-of-wall cover nodes and repositioning exposed defenders to the **sheltered leeward side** of the wall.
* **Seek Cover First When Bleeding (`BandageOnlyAfterFindingCover`) & Sheltered Tank Repairs (`EnableSmartTankCrewRepair`)**:
  * Wounded soldiers sprint to nearby cover before bandaging; repairing tank crews route around the hull to crouch on the sheltered side opposite enemy fire.
* **Point-Blank Bayonet/Melee CQB (`EnablePointBlankMelee`), Muzzle Discipline, Auto-Peek & Bipod Mounting**:
  * Automatic Co-op-safe bayonet fixing (`integratedBayonet.SetMounted(true)`), CQB lunging/knife-drawing/butt-strokes within `2.5m–3.8m`, dual barrel-height (`spine` + `heldGun`) wall-shooting prevention and auto-peek (`Prone → Crouch → Stand` + lateral corner sidestep), active MG bipod mounting (`BipodCheck(true)`), friendly-fire hold-fire cones (`5.0°–6.5°`), `2.4m` anti-stacking spacing, wall vaulting, and surrendered comrade liberation.
* **Strategic Door & Window Shutter Discipline (`EnableStrategicDoorDiscipline`)**:
  * Prevents bots from mindlessly popping open closed window shutters and side doors simply by walking past them (`Soldier.CheckAndOpenDoor`), automatically closes doors behind bots once they and any trailing squadmates have passed through, and pulls open window shutters closed when heavily suppressed, reloading, or bandaging to block enemy line of sight (`SetDoorLayer`).

---

### 5. Macro Objective Director, Emplacement Staffing, Smart APC Dismounts & Tank Standoff ([`MacroAndVehiclesModule.cs`](src/EasyRed2AI/Modules/MacroAndVehiclesModule.cs))

* **Macro Objective Garrison & Counterattack Director (`EnableMacroObjectiveDirector`)**:
  * Lightweight `6.0s` MasterClient director that assigns **~20% of autonomous friendly squads** to garrison newly captured objectives (prioritizing contested or frontline-threatened points via `Squad_TaskCanStillAttract_Patch`) while directing the remaining squads to attack or counterattack uncaptured objectives.
* **Proactive Static Gun & Armed Halftrack Staffing (`EnableProactiveEmplacementStaffing`)**:
  * Defending and garrisoning squads automatically assign an available rifleman/gunner to crew nearby empty static weapons (`MG tripods`, `bunker MGs`, `AT guns`, `AA guns`) or parked armed halftracks within `42m` that face the active enemy threat sector.
* **Combined-Arms Known Target Priority Support (`EnableCombinedArmsPrioritySupport`)**:
  * **Turrets & Emplacements (`TurretAI.RefreshTargets`)**: Prioritizes **Marked/Spotted enemy squads (`isMarked` / `isHalfMarked` / `isMarkedPinPoint`)**, **Active Friendly Radio Support Requests (`RadioRequestActiveAndConfirmed`)**, and **enemy squads actively engaging friendly infantry (`IsEnemyAwareOfThisSquad`)** over random lone unspotted targets, and allows HE-capable turrets to answer friendly radio support requests even when a distant unmarked soldier is visible.
  * **Tanks & Combat Vehicles (`AIVehicle.MovableVehicleRoutine`)**: Steers `setVehicleDestination` toward standoff overwatch positions facing known/marked enemy squads or friendly `tankSupport` radio requests inside the objective radius (`rerollDestination = false`) instead of wandering to random points via `RandomPointInCircleXZ`.
  * **CAS Aircraft (`AIPlane.Update` & `AIPlane.GetBestAircraftVisibleEnemy`)**: Shifts aircraft patrol orbits (`SetFlyAroundPos`) toward active friendly radio support requests and Marked/Known enemy squads instead of always orbiting `ActiveObjectives[0]`, and prioritizes Marked squads, active radio targets, and enemy vehicles during CAS dive/strafing passes.
* **Smart APC / Transport Dismount Under Fire (`EnableSmartApcDismount`)**:
  * Infantry riding in AI-driven APCs, halftracks, and trucks dismount immediately (`LeaveVehicle` + `SendUnitsToCovers`) when the transport is suppressed, takes hull/engine damage under fire, or comes within `48m` of an enemy tank/AT gun/squad—preventing entire squads from riding into point-blank destruction.
* **Tank Standoff & Reverse Discipline (`EnableTankStandoffAndReverse`)**:
  * AI tanks with a clear main-gun line of sight hold practical standoff distance (`50m` Brawler, `72m` Support, `95m` Sniper) using `StopAndRotateTowardTarget` instead of driving into infantry AT-grenade range, and engage reverse gear (`ForceRetroFor` / `MoveTankRetro`) when enemy threats close within `35m`.
* **Smart Anti-Armor Target, Fire & Evade Discipline (`PreventSmallArmsFireAtTanks`)**:
  * Redirects small arms at **exposed vehicle crew (`Seats.showUnitSet`)** and **un-flattened rubber tires (`VehicleDamageableWheelPart`)**, aims AT rifles at **unbroken tank tracks (`VehicleDamageableTrackPart`)** and penetrable plates, throws normal & AT grenades at close range, and stops useless rifle/SMG fire against heavy tank hulls—while infantry caught out in the open (`<= 85m`) with no way to hurt an approaching tank **proactively scramble behind walls/hard cover or bound diagonally out of the tank's sightline**.

---

### 6. Ballistics, Plane Audio De-Throbber & Battlefield Immersion ([`BallisticsAndImmersionModule.cs`](src/EasyRed2AI/Modules/BallisticsAndImmersionModule.cs))

* **Client-Side Plane Engine Loop De-Throbber (`EnablePlaneAudioSmoothing`)**:
  * Desynchronizes aircraft motor loop playback offsets whenever an engine starts or transitions between `engine_move` (near) and `engine_move_far`, clamps Unity Doppler pitch warble (`<= 0.22`), and low-pass smooths pitch changes with a subtle per-plane harmonic detune—eliminating repetitive flyby throbbing and multi-plane phase flanging.
* **Material-Aware Thin-Cover Bullet Penetration (`EnableThinCoverBulletPenetration`)**:
  * Full-powered rifle and MG rounds (`mmPenetration >= 6`, `impactSpeed >= 220m/s`) penetrate thin wooden fences, doors, shutters, and planks (`<= 0.35m`) once via a fast single-collider back-face raycast (`col.Raycast`) with reduced exit velocity (`42%–64%`) and slight deflection, while heavy high-caliber rounds (`12.7mm+`) can punch through up to `0.48m` of wood, generic props, or light sheet metal. Solid terrain, stone/concrete buildings, and armor still stop rounds completely.
* **Parachute Wind Drift & Slower Descent (`EnableParachuteWindDrift`)**:
  * Softens paratrooper vertical descent speed by **~25%** and applies smooth horizontal canopy wind drift while high above ground (`> 18m` AGL), tapering off cleanly before landing.
* **Spectator / Free-Camera HUD Retention (`EnableSpectatorHudRetention`)**:
  * Keeps the tactical HUD and 3D unit markers visible when entering Free/Spectator Camera (`F`) until you explicitly press the Cinematic Camera Hide/Show GUI toggle.

---

### 7. Death Screen Kill Attribution, Quick-Tap Spotting & 64-Char Room Names ([`DeathScreenAndQoLModule.cs`](src/EasyRed2AI/Modules/DeathScreenAndQoLModule.cs))

* **Instant Full-Page Death Screen Obituary (`EnableDeathScreenAttribution`)**:
  * Bypasses vanilla's slow sequential `Animation` fade-in so `"KILLED IN ACTION"`, the soldier's name, **calculated Soldier Age (`1925 - 1944 (Age 19)`)**, **Killer Rank + Full Name** (`[FRIENDLY FIRE]` / `[SELF-INFLICTED]`), and **Weapon • Hit Location • Distance (`MP40 • Headshot (Helmet) • 42m`)** appear immediately at 100% opacity.
* **Quick-Tap `Tab` (`OrdersMenu`) Spotting (`EnableQuickTapSpot`)**:
  * Short-pressing your configured Squad Orders keybind (`Tab`) spots in your look direction (`PlayerController.TrySpotInLookDirection`) even as a Squad Leader or Radioman, while holding the key still opens the radial orders wheel.
* **64-Character Multiplayer Room Names (`EnableExtendedRoomNames`)**:
  * Lifts vanilla's 8-character multiplayer server name cap up to **64 characters** when hosting or searching for matches.

---

## Multiplayer Compatibility Matrix — Client-Side vs. Host/Synced Features

Whether you are **joining a 100% vanilla server as a modded client**, **hosting a server for vanilla friends**, or running the mod on **both Host and Clients**, here is exactly how every feature behaves over Photon (`PUN2`):

| Feature / System | Category | Joining a Vanilla Server (Client-Only Mod) | Hosting for Vanilla Friends (Host-Only Mod) | Host + Clients Both Modded |
| :--- | :--- | :--- | :--- | :--- |
| **Plane Engine Loop De-Throbber** (`EnablePlaneAudioSmoothing`) | **100% Client-Side** | **Works 100%** for you | Host hears smoothed plane audio; vanilla clients hear vanilla audio | **Everyone** hears smoothed plane audio |
| **Death Screen Kill Attribution + Soldier Age** (`EnableDeathScreenAttribution`) | **100% Client-Side** | **Works 100%** for you | Host sees full obituary; vanilla clients see vanilla death screen | **Everyone** sees full obituary |
| **Quick-Tap `Tab` Spotting** (`EnableQuickTapSpot`) | **100% Client-Side Input** | **Works 100%** for you (triggers native `TrySpotInLookDirection`) | Works for Host | **Everyone** can quick-tap `Tab` to spot |
| **64-Character Multiplayer Room Names** (`EnableExtendedRoomNames`) | **Client UI / Host Room Name** | Can type 64-char search filters & see long server names | **Vanilla clients see your full 64-char server name** in the server browser! | **Works 100%** for everyone |
| **3D Veteran Overhead Badges & Squad Roster `[Vet]`/`[Role]` Tags** | **100% Client-Side UI** | **Works 100%** on your HUD | Host sees tags/badges; vanilla clients see **3D uniform shoulder rank decals** (`UpdateRanksDecal`) synced by Host | **Everyone** sees HUD tags, 3D badges & uniform decals |
| **Keep HUD in Free/Spectator Camera (`F`)** & **`F6`/`F8`/`F9`/`F10` Menu** | **100% Client-Side UI** | **Works 100%** for you | Host only | **Everyone** |
| **Macro Objective Garrison (`~20%`) & Counterattack Director** | **Host-Driven (Synced to All)** | Inactive (Host controls autonomous squads) | **Vanilla clients experience 100%!** Host assigns squad tasks and Photon syncs all squad movements to vanilla players. | **Works 100%** for everyone |
| **Proactive Static Gun Staffing & Smart APC Dismounts** | **Host-Driven (Synced to All)** | Inactive for host-owned bots | **Vanilla clients experience 100%!** Emplacement boarding and APC dismounts sync to all vanilla players via Photon. | **Works 100%** for everyone |
| **Tank Standoff, Reverse Discipline & Parachute Wind Drift** | **Per-Vehicle Owner (Host by default)** | Applies to vehicles you drive/own | **Vanilla clients experience 100%** for all AI-driven tanks and paratroopers! | **Works 100%** for everyone |
| **All Autonomous Enemy & Friendly AI Squads (Vision, Cover, Indoor Crouch, Bounding, Bayonet/Melee, Anti-Armor)** | **Host-Driven (Synced to All)** | Inactive for host-owned bots | **Vanilla clients experience 100%!** Even a vanilla client's **own on-foot AI squadmates** remain owned and simulated by the Host (`0x180A71EE2`)! | **Works 100%** for everyone |
| **AI Crewmates Inside a Client-Driven Tank/Vehicle** | **Client-Owned Vehicle Crew** | **Works 100%** for your vehicle's AI gunners/repair crew (`0x180A7230A`)! | Vanilla client's own tank crew uses vanilla AI while that client is driving | **Works 100%** for everyone |
| **Thin-Cover Wood Bullet Penetration** (`EnableThinCoverBulletPenetration`) | **Per-Shooter Simulation** | Works for bullets fired by you and your locally owned units | Works for bullets fired by the Host and **all Host-owned AI bots** (which can hit vanilla players behind thin fences!) | **Works 100%** for all players and bots |

---

## In-Game Feature Bundle Menu (`F6`), Hotkeys & `.cfg` File

Every feature in this mod is organized into **6 cohesive Feature Bundles** with one-click **`[ALL]` / `[OFF]`** bundle header buttons in the **in-game `F6` GUI menu** (and saved in `BepInEx\config\com.antigravity.easyred2.humanai.cfg`):

* **`F6`**: Open/Close the **Interactive Feature Bundle Menu** (toggle individual features or entire bundles ON/OFF live in-game).
* **`F8`**: Master Toggle Mod **ENABLED / DISABLED** live in-game.
* **`F9`**: **Hot-Reload** `com.antigravity.easyred2.humanai.cfg` from disk without restarting.
* **`F10`**: Display **Live Tactical Telemetry Toast** (Active Veterans, Promotions, Survival Drops, Indoor Crouch Saves, Callouts, Suppressive Bursts, Bounding Holds, Objective Garrisons, Static Guns Staffed, Smart APC Dismounts, Tank Standoff Holds, Wood Penetrations, Plane Audio Loops Desynced, and Friendly Fire Saved).

---

## Building From Source

Run [`build.ps1`](build.ps1) from PowerShell (auto-discovers your Steam `Easy Red 2` installation and `.NET SDK` Roslyn compiler, compiles all modules under `src\EasyRed2AI\`, and checks that `Easy Red 2.exe` is not running before deploying):

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Deploy
```
