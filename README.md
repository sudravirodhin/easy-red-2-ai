# Easy Red 2 — Humanized AI & Veteran Tactics (`v0.3.3`)

> [!IMPORTANT]
> **Disclosure — Vibecoded Project:**
> This mod was **vibecoded** (designed, reverse-engineered, and written collaboratively with an AI coding agent). If you are averse to AI-assisted or AI-generated software/mods, please be aware of this upfront before downloading or using this repository.

A comprehensive **BepInEx 6 (IL2CPP)** AI and fairness overhaul for **Easy Red 2** designed to reduce frustrating *"how did they see me?"* and *"what just killed me?"* moments. It replaces vanilla's robotic X-ray spotting, erratic stance oscillation, suicidal open-field bandaging/repairs, standing in front of walls, and wasteful small-arms fire against tanks with human-like perception, self-preservation, cover intelligence, a 15-minute linear Veteran progression system, **30 Role-Based AI Personalities (3 per Infantry, Vehicle & Aircraft role)**, an **in-game `F6` Feature Toggle Menu**, and full Death Screen Kill Attribution.

---

## Compatibility & Tested Version

| Property | Details |
| :--- | :--- |
| **Target Branch** | Steam `public` (Default Branch) |
| **Confirmed Game Version** | **Easy Red 2 — Game Build `921`** (Steam Build ID `26375879`, Unity `2022.3.62f3` IL2CPP x64) |
| **Mod Loader** | **[BepInEx 6 Bleeding Edge (`BepInEx-Unity.IL2CPP-win-x64`, build `#791`+)](https://builds.bepinex.dev/projects/bepinex_be)** |
| **Last Confirmed Test Date** | **November 2, 2026** (`2026-11-02`) |

---

## Installation (Two Release Options)

Download your preferred package from the **[Releases](https://github.com/sudravirodhin/easy-red-2-ai/releases)** page:

### Option A: With BepInEx Included (`EasyRed2HumanAI-v0.3.3-with-BepInEx.zip`) — *Recommended for Fresh Installs*
Includes **[BepInEx 6.0.0-be.791 (Unity.IL2CPP-win-x64)](https://builds.bepinex.dev/projects/bepinex_be)**, the `.NET 6` runtime, Unity `2022.3.62` base libraries, pre-generated IL2CPP `interop` assemblies for Easy Red 2 Build `921`, and `EasyRed2HumanAI.dll` (with no other third-party mods).
1. Open your **Easy Red 2** installation folder (e.g. `Steam\steamapps\common\Easy Red 2`, where `Easy Red 2.exe` is located).
2. Extract all contents of `EasyRed2HumanAI-v0.3.3-with-BepInEx.zip` directly into the `Easy Red 2` folder.
3. Launch the game!

### Option B: Without BepInEx / Plugin Only (`EasyRed2HumanAI-v0.3.3-PluginOnly.zip` or `EasyRed2HumanAI.dll`)
For users who already have **[BepInEx 6 Bleeding Edge (`BepInEx-Unity.IL2CPP-win-x64`)](https://builds.bepinex.dev/projects/bepinex_be)** installed:
1. Download `EasyRed2HumanAI-v0.3.3-PluginOnly.zip` (or `EasyRed2HumanAI.dll` directly).
2. Place `EasyRed2HumanAI.dll` into:
   ```text
   Easy Red 2\BepInEx\plugins\EasyRed2HumanAI.dll
   ```

---

## Key Improvements Over Vanilla

### 1. Humanized Vision, Stance Stealth & FOV Cones ([`VisionPatches.cs`](src/EasyRed2AI/VisionPatches.cs))

| Feature | Vanilla Behavior | Humanized AI (`v0.3.3`) |
| :--- | :--- | :--- |
| **Stance Sight Ranges** | `220m` Standing, `50m` Crouching, `37m` Prone when unspotted — but jumps **all stances to a flat `300m`** the instant `IsEnemyAwareOfThisSquad` is true. | **150m Standing**, **80m Crouching**, **40m Prone**. Squad combat awareness applies only a **1.20x multiplier** (`48m` prone, `96m` crouched) so taking cover in grass/craters actually conceals you during firefights. |
| **Individual Firing Reveal** | One soldier firing alerts the enemy to the entire squad's crouched/prone members out to `300m`. | Only the **specific soldier who fired** gets a temporary **+85m visibility penalty for 4.0 seconds** (muzzle flash, smoke, and gunshot sound). Silent squadmates stay hidden. |
| **Human FOV Cones** | Alerted or suppressed bots bypass the FOV check and gain **360-degree omnidirectional vision** out to `300m`. | Enforces realistic human vision cones even in combat: **20m** 360° close hearing/awareness, **80° half-angle** (160° cone) mid-range out to **75m**, and **45° half-angle** (90° cone) beyond **75m**. |
| **Threat-Sector Gaze & Flanking** | Stationary defenders never rotate once `StopMove` is called (staring at whatever random angle their last step pointed) and suffer a **2.5s freeze** (`alertStartTime`) before locking onto spotted targets `> 30m`. | Stationary defenders orient toward **Known Enemy Contact Sectors** (spotted/loud/aware squads within `175m`), spot targets in their central `28°` overwatch cone **1.20x further** and **4x faster (`0.15s–0.55s`)**, and leave **quiet, unspotted flanks unwatched** (`60°` peripheral FOV & `0.72x` peripheral range) so flanking maneuvers are rewarded. |
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
| **8. Tank / Armor Crew** | **`[Brawler Crew]`**: **20% faster turret target switching** (`TurretAI.RefreshTargets`) for close-quarters brawling. | **`[Sniper Crew]`**: **+5° wider long-range turret optic cone** for long-range armor duels. | **`[Support Crew]`**: Prioritizes infantry support (`supportedSquad`) and faster response to friendly radio requests. |
| **9. Light Vehicle / AA** | **`[Outrider]`**: Fast-reacting mobile gunner/driver (`20%` faster turret target refresh). | **`[Escort]`**: Cautious stand-off support and higher self-preservation when repairing. | **`[Flak Watch]`**: **+12° wider scan sector** in `TurretAI.CanSee` for tracking aircraft and elevated threats. |
| **10. Aircraft Pilot** | **`[Ace]`**: Extended dogfight pursuit leash (`fighterDogfightExtraLeash = 450m`) when chasing enemy planes. | **`[Daredevil]`**: Commits closer on CAS dive-bombing runs (`0.90x diveAlignDistance`). | **`[Disciplined]`**: Pulls out of dives earlier (`1.08x diveAlignDistance`) for safer high-altitude bomb releases. |

---

### 4. Self-Preservation, Cover Intelligence & Stance Discipline ([`VeteranAndTacticsPatches.cs`](src/EasyRed2AI/VeteranAndTacticsPatches.cs))

* **Stance Redirection & Anti-Bobbing**:
  * **Out of Combat / Moving**: Bots move at full standing speed (`Idle`) unless stealth-following a crouched squad leader or locked in combat crouch.
  * **Engaging Enemies**: Bots never fire standing in the open—they drop to **Crouch** (or **Prone** for Machine Gunners / suppression above their personality threshold) *only* after verifying a clear line-of-fire from that lower stance, and maintain a combat crouch lock so brief LOS flickers behind ridges or bushes never cause `Stand <-> Crouch` bobbing.
  * **Stationary Defenders**: Soldiers occupying `CombatCover`, holding inside an objective capture zone (`PositionIsInsideValidConquerZone`), sheltered behind front walls, or in alerted squads stay **Crouched** rather than standing tall or randomly lying prone in tall grass (`DestinationWithoutCover` 50% prone override fixed).
* **Stand BEHIND Walls, Never IN FRONT (`EnableSmartWallSideCover`)**:
  * Computes the true enemy threat direction (visible enemies → squad targets → attacker/defender objective frontline) and raycasts both in front (`+threatDir`) and behind (`-threatDir`) candidate positions.
  * Rejects cover nodes with a wall at the soldier's back and open air toward the enemy, and automatically repositions exposed defenders (`TryFindShelteredSideOfWall`) to the **sheltered leeward side** of the wall.
* **Seek Cover First When Bleeding (`BandageOnlyAfterFindingCover`)**:
  * Wounded soldiers sprint to nearby cover first and drop to `Crouch`/`Prone` before bandaging.
* **Sheltered Tank & Vehicle Crew Repairs (`EnableSmartTankCrewRepair`)**:
  * Routes repairing crewmen around the hull to the **sheltered leeward/rear side** opposite the enemy threat vector and keeps them **Crouched** behind the tank's armor while repairing.

---

### 5. Muzzle Discipline, Bipods, Anti-Armor & Mobility ([`VeteranAndTacticsPatches.cs`](src/EasyRed2AI/VeteranAndTacticsPatches.cs))

* **Smart Anti-Armor Target & Fire Discipline (`PreventSmallArmsFireAtTanks`)**:
  * **Targets Exposed Crew (`Seats.showUnitSet`)**: Redirects small-arms fire directly at visible exposed crew members or passengers (open-topped TDs, halftracks, jeeps, pintle MG gunners) instead of the steel hull.
  * **Targets Vulnerable Weak Spots (Tires, Tracks & Light Armor)**: Aims small arms at **un-flattened rubber tires** (`VehicleDamageableWheelPart`) within `60m` and aims AT rifles at penetrable armor plates or **unbroken tank tracks** (`VehicleDamageableTrackPart`).
  * **Throws Normal & AT Grenades at Close Range**: Infantry within throwing range (`7.5m` to `GetGrenadeThrowRange`) throw **both normal grenades (`VirtualGrenade`) and anti-tank grenades (`VirtualATGrenade`)** at enemy tanks and vehicles.
  * **Holds Useless Small-Arms Fire & Re-Targets Foot Soldiers**: If a soldier's weapon cannot harm the tank and no exposed crewman is visible, they hold fire, stay crouched in cover, and re-target enemy foot soldiers.
* **Wall-Shooting Prevention & Auto-Peek (`EnableWallShootingPrevention` & `EnableAutoPeekOverCover`)**:
  * Raycasts `2.2m` ahead of the weapon muzzle before pulling the trigger and steps up one stance (`Prone → Crouch` or `Crouch → Stand`) to peek over low obstacles when clear.
* **Machine Gunner Bipod Wall & Ground Mounting (`EnableGunnerBipodWallMounting`)**:
  * Gunners actively seek nearby walls, sandbags, and windowsills and deploy their bipods (`BipodCheck(true)`).
* **Friendly Fire Avoidance, Anti-Stacking, Wall Vaulting & Comrade Liberation**:
  * Holds fire and sidesteps when a friendly is within a `5.0°–6.5°` cone or `0.65m` corridor, maintains `2.4m+` spacing, vaults low walls/fences (`CanVault` → `Vault`), and frees surrendered comrades (`UnSurrenderSynched`).

---

### 6. Death Screen Kill Attribution (`EnableDeathScreenAttribution`)

Displays a detailed obituary on both the native `DeathPanel` and a centered HUD banner (compatible with `ragdoll_overhaul`'s fade-to-black death screen):
* **Who Killed You**: Attacker's rank (`Pvt.`, `Cpl.`, `Sgt.`, `1st Sgt.`, `Lt.`), full name, and `[Vet I–III ★ • Personality]` badge.
* **Friendly Fire & Self-Inflicted Tags**: Highlights `[FRIENDLY FIRE]` in coral red or `[SELF-INFLICTED]` in amber.
* **What Was Used, Hit Location & Distance**: Shows the exact weapon, grenade, vehicle, or artillery barrage, hit zone (`Headshot`, `Headshot (Helmet)`, `Torso`, `Arm`, `Leg`, `Shrapnel`, `Explosion`), instant kill vs. bleed-out (`Killed by...` vs. `Bled out from wounds by...`), and distance in meters.

---

## In-Game Feature Toggle Menu (`F6`), Hotkeys & `.cfg` File

Every feature in this mod can be toggled individually either via the **in-game `F6` GUI menu** or by editing `BepInEx\config\com.antigravity.easyred2.humanai.cfg`:

* **`F6`**: Open/Close the **Interactive Feature Toggle Menu** (click any feature to toggle it ON/OFF live in-game and save directly to `.cfg`).
* **`F8`**: Master Toggle Mod **ENABLED / DISABLED** live in-game.
* **`F9`**: **Hot-Reload** `com.antigravity.easyred2.humanai.cfg` from disk without restarting.
* **`F10`**: Display **Live Tactical Telemetry Toast** (Active Veterans, Field Promotions, Survival Drops, Bipods Deployed, Wall Blocks/Peeks, Wall-Side Repositions, Useless Tank Fire Blocked / Exposed Crew Targeted, Friendly Fire Saved, and Sheltered Vehicle Repairs).

---

## Building From Source

Run [`build.ps1`](build.ps1) from PowerShell (auto-discovers your Steam `Easy Red 2` installation and `.NET SDK` Roslyn compiler, and checks that `Easy Red 2.exe` is not running before deploying):

```powershell
powershell -ExecutionPolicy Bypass -File .\build.ps1 -Deploy
```
