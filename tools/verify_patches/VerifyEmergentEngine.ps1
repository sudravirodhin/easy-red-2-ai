$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$goapFile     = Join-Path $workspace "src\EasyRed2AI\Modules\Goap\GoapDefinitionsAndPlanner.cs"
$persFile     = Join-Path $workspace "src\EasyRed2AI\Modules\ImmersionAndQoL\VeterancyAndPersonalities.cs"
$emergentFile = Join-Path $workspace "src\EasyRed2AI\Modules\Emergent\EmergentDecisionCore.cs"
$buildDir     = Join-Path $workspace "build"
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

function Slice-LinesBetween {
    param([string[]]$Lines, [string]$StartPattern, [string]$EndPattern, [int]$ExtraAfterEnd = 0)
    $startIdx = -1
    for ($i = 0; $i -lt $Lines.Length; $i++) {
        if ($Lines[$i] -match $StartPattern) { $startIdx = $i; break }
    }
    if ($startIdx -lt 0) { throw "Start pattern not found: $StartPattern" }
    $endIdx = -1
    for ($i = $startIdx; $i -lt $Lines.Length; $i++) {
        if ($Lines[$i] -match $EndPattern) { $endIdx = $i + $ExtraAfterEnd; break }
    }
    if ($endIdx -lt 0) { throw "End pattern not found: $EndPattern" }
    return ($Lines[$startIdx..$endIdx] -join "`n")
}

$goapLines = Get-Content $goapFile
$partA = Slice-LinesBetween $goapLines '^\s*internal enum SquadNetDirective' 'public Vector3 MoveOverrideDest;' 1
$partB = Slice-LinesBetween $goapLines '^\s*internal struct BotMicroVariance' '_factionProfileCache\[faction\] = p;' 2
$persLines = Get-Content $persFile
$partPers = Slice-LinesBetween $persLines '^\s*public enum SoldierPersonality : byte' 'Pilot_Disciplined = 30' 1

$harnessCs = @"
#pragma warning disable 0649, 0414
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 down => new Vector3(0f, -1f, 0f);
        public static Vector3 forward => new Vector3(0f, 0f, 1f);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public Vector3 normalized
        {
            get
            {
                float m = MathF.Sqrt(sqrMagnitude);
                return m > 1e-6f ? new Vector3(x / m, y / m, z / m) : zero;
            }
        }
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator *(Vector3 a, float s) => new Vector3(a.x * s, a.y * s, a.z * s);
    }

    public static class Mathf
    {
        public static float Abs(float v) => v < 0f ? -v : v;
        public static float Max(float a, float b) => a > b ? a : b;
        public static float Min(float a, float b) => a < b ? a : b;
        public static int Max(int a, int b) => a > b ? a : b;
        public static int Min(int a, int b) => a < b ? a : b;
        public static float Sqrt(float v) => MathF.Sqrt(v < 0f ? 0f : v);
        public static float Clamp(float v, float mn, float mx) => v < mn ? mn : (v > mx ? mx : v);
        public static int Clamp(int v, int mn, int mx) => v < mn ? mn : (v > mx ? mx : v);
        public static float Clamp01(float v) => Clamp(v, 0f, 1f);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
        public static float Exp(float v) => MathF.Exp(v);
        public static float Cos(float v) => MathF.Cos(v);
        public static float Sin(float v) => MathF.Sin(v);
    }
}

public enum Gamemap
{
    Unknown = 0, Stalingrad = 1, Normandy = 2, Anzio = 3, Kos = 4, Kwajalein = 5,
    Tunisia = 6, Cassino = 7, Ardennes = 8, Shanghai = 9, Nanking = 10, Makin = 11,
    RoiNamur = 12, KhalkhinGol = 13, Budapest = 14, Balaton = 15
}

public class Spottable { }
public class CombatCover { public IntPtr Pointer; }
public class Squad { public IntPtr Pointer; public bool isChargeSquad; }
public class Soldier { public IntPtr Pointer; }
public enum SoldierPose : byte { Idle = 0, Crouch = 1, Prone = 2 }

namespace EasyRed2HumanAI
{
    using UnityEngine;

    public class ConfigBool { public bool Value = true; }
    public static class VisionConfig
    {
        public static ConfigBool EnableFactionNetDoctrines = new ConfigBool { Value = true };
        public static ConfigBool EnableBattleAtmosphereDirectives = new ConfigBool { Value = true };
    }

    public static partial class AiCore
    {
        public enum BattleAtmosphereArchetype : byte
        {
            None = 0,
            Order227NotOneStepBack = 1,
            KesselEncircledPocket = 2,
            UrbanFortress = 3,
            AmphibiousBeachhead = 4,
            SiegeOfBastogne = 5,
            PacificBunkerAndNightGyokusai = 6,
            MountainRidgeGrind = 7,
            BlitzkriegSchwerpunkt = 8,
            NightAirborneCoupDeMain = 9
        }

        public struct BattleTheaterSnapshot
        {
            public IntPtr BattleDataPtr;
            public Gamemap Map;
            public int Year;
            public string InvadersFactionId;
            public string DefendersFactionId;
            public string InvadersBattalionLower;
            public string DefendersBattalionLower;
            public string LocationLower;
            public bool IsNightStart;
            public BattleAtmosphereArchetype Atmosphere;
            public bool IsAmphibiousBeachMap;
            public bool IsOpenDesertOrSteppeMap;
            public bool IsDenseUrbanMap;
            public bool IsJungleOrAtollMap;

            public string GetBattalionLowerForFaction(string faction)
            {
                if (string.IsNullOrEmpty(faction)) return string.Empty;
                if (!string.IsNullOrEmpty(InvadersFactionId) && AreSameSideFactions(faction, InvadersFactionId))
                    return InvadersBattalionLower ?? string.Empty;
                if (!string.IsNullOrEmpty(DefendersFactionId) && AreSameSideFactions(faction, DefendersFactionId))
                    return DefendersBattalionLower ?? string.Empty;
                return string.Empty;
            }
        }

        public static BattleTheaterSnapshot CurrentTheater;
        public static float NightDarkness01;
        public static int BattleTheaterGeneration;
        public static BattleTheaterSnapshot GetBattleTheaterSnapshot() => CurrentTheater;

        public static int GetDeterministicHash(IntPtr ptr, int salt)
        {
            unchecked
            {
                uint k = (uint)ptr.ToInt64() ^ ((uint)salt * 0x9E3779B9u);
                k ^= k >> 16;
                k *= 0x85EBCA6Bu;
                k ^= k >> 13;
                k *= 0xC2B2AE35u;
                k ^= k >> 16;
                return (int)(k & 0x7FFFFFFFu);
            }
        }

        public static bool AreSameSideFactions(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            if (a.EndsWith("_allies", StringComparison.OrdinalIgnoreCase) && b.EndsWith("_allies", StringComparison.OrdinalIgnoreCase)) return true;
            return a.EndsWith("_axis", StringComparison.OrdinalIgnoreCase) && b.EndsWith("_axis", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static partial class VeteranAndTacticsPatches
    {
$partPers
$partA
$partB
    }

    public static class EmergentVerifyMain
    {
        private static int _passed = 0;
        private static int _failed = 0;

        private static void Check(string name, bool cond, string detail = "")
        {
            if (cond)
            {
                _passed++;
                Console.WriteLine($"  [PASS] {name}" + (string.IsNullOrEmpty(detail) ? "" : $" ({detail})"));
            }
            else
            {
                _failed++;
                Console.WriteLine($"  [FAIL] {name}" + (string.IsNullOrEmpty(detail) ? "" : $" :: {detail}"));
            }
        }

        public static int Main(string[] args)
        {
            var usProf = VeteranAndTacticsPatches.GetFactionCommandProfile("UnitedStates_allies");
            var gerProf = VeteranAndTacticsPatches.GetFactionCommandProfile("Germany_axis");
            var ussrProf = VeteranAndTacticsPatches.GetFactionCommandProfile("Ussr_allies");
            var finProf = VeteranAndTacticsPatches.GetFactionCommandProfile("Finland_axis");
            var jpnProf = VeteranAndTacticsPatches.GetFactionCommandProfile("Japan_axis");
            var wDefault = EmergentEngine.ComputeWeights(in usProf, VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.5f, 12345u);

            // 1. Doorway Conga-Line Reflex
            Console.WriteLine("[Group 1] Doorway Conga-Line Reflex");
            {
                var ring = new EmergentEngine.DangerStampRing();
                ring.StampIncomingFireLane(new Vector3(0f, 0f, 4f), new Vector3(0f, 0f, 25f), 101L, true, EmergentEngine.DangerWeaponClass.SmallArms, 0.55f, 9.5f);
                ring.EvaluateSpotProjectedHarm(Vector3.zero, Vector3.zero, 101L, true, false, false, EmergentEngine.EmergentTier.Individual, 10.0f, out _, out _, out bool laneCasualty);
                Check("Incoming fire lane alone does not set DoorwayFreshCasualty", !laneCasualty);

                ring.StampFriendlyHarm(new Vector3(0f, 0f, 4f), new Vector3(0f, 0f, 25f), 101L, true, EmergentEngine.DangerWeaponClass.SmallArms, 0.78f, 12f, 20f, 10f);
                float hProj = ring.EvaluateSpotProjectedHarm(Vector3.zero, Vector3.zero, 101L, true, false, false, EmergentEngine.EmergentTier.Individual, 11f, out int stamps, out _, out bool doorwayCas);
                Check("Doorway casual flag set from 4m friendly kill", doorwayCas && stamps >= 1 && hProj > 0.30f, $"hProj={hProj:F2}");

                var inp = new EmergentEngine.EmergentInput
                {
                    Now = 11f, MyPos = Vector3.zero, SquadId = 101L,
                    SmoothedHarmNow = 0.15f, ProjectedHarm = hProj, DoorwayFreshCasualtyAhead = doorwayCas,
                    InCover = false, CoverAvailable = true, HasSmokeGrenade = true, CanHarmTarget = 0.7f
                };
                bool r1 = EmergentEngine.EvaluateFastReflex(in inp, in wDefault, 11f, out var dec1);
                Check("Fast reflex halts conga-line -> SeekCover", r1 && dec1.Reflex == EmergentEngine.EmergentReflexKind.DoorwayFreshCasualty && dec1.Step0 == EmergentEngine.EmergentActionId.SeekCover, $"{dec1.Reflex}/{dec1.Step0}");

                inp.CurrentStep0 = EmergentEngine.EmergentActionId.SeekCover;
                inp.LastReflexKind = dec1.Reflex;
                inp.LastReflexTime = 11f;
                bool rRepeat = EmergentEngine.EvaluateFastReflex(in inp, in wDefault, 11.5f, out _);
                Check("Doorway reflex cooldown prevents double-firing while already seeking cover", !rRepeat);

                inp.CurrentStep0 = EmergentEngine.EmergentActionId.None;
                inp.LastReflexTime = 0f;
                inp.CoverAvailable = false;
                bool r2 = EmergentEngine.EvaluateFastReflex(in inp, in wDefault, 11f, out var dec2);
                Check("No cover + smoke -> PopSmoke reflex", r2 && dec2.Reflex == EmergentEngine.EmergentReflexKind.DoorwayFreshCasualty && dec2.Step0 == EmergentEngine.EmergentActionId.PopSmoke, $"{dec2.Reflex}/{dec2.Step0}");
            }

            // 2. Asymmetric Enemy Relief
            Console.WriteLine("[Group 2] Asymmetric Enemy Relief");
            {
                var ring = new EmergentEngine.DangerStampRing();
                Vector3 lanePos = new Vector3(0f, 0f, 10f);
                ring.StampIncomingFireLane(lanePos, new Vector3(0f, 0f, 40f), 101L, true, EmergentEngine.DangerWeaponClass.SmallArms, 0.55f, 10f);
                float hBefore = ring.EvaluateSpotProjectedHarm(lanePos, Vector3.zero, 101L, true, false, false, EmergentEngine.EmergentTier.Individual, 11f, out _, out float relBefore, out _);
                ring.StampEnemyHarmRelief(lanePos, 101L, true, 0.75f, 11f);
                float hAfter = ring.EvaluateSpotProjectedHarm(lanePos, Vector3.zero, 101L, true, false, false, EmergentEngine.EmergentTier.Individual, 11.5f, out _, out float relAfter, out _);
                Check("Enemy harm relief reduces lane H_proj", relBefore == 0f && relAfter > 0.35f && hAfter < hBefore, $"H:{hBefore:F2}->{hAfter:F2} Rel:{relAfter:F2}");

                var baseInp = new EmergentEngine.EmergentInput
                {
                    Now = 12f, HasObjectiveTask = true, ObjectiveDist = 30f, ObjectiveUrgency = 0.85f,
                    SmoothedHarmNow = 0.15f, ProjectedHarm = hBefore, EnemyRelief = 0f, InCover = true
                };
                var relInp = baseInp;
                relInp.ProjectedHarm = hAfter;
                relInp.EnemyRelief = relAfter;
                var decBase = EmergentEngine.EvaluateDeliberative(in baseInp, in wDefault, default, 12f);
                var decRel  = EmergentEngine.EvaluateDeliberative(in relInp, in wDefault, default, 12f);
                Check("Enemy relief triggers higher-scoring Advance surge", decRel.Step0 == EmergentEngine.EmergentActionId.AdvanceObjective && decRel.Reason == "surge_enemy_relief" && decRel.Score > decBase.Score, $"base={decBase.Score:F2} relief={decRel.Score:F2} ({decRel.Reason})");
            }

            // 3. Platform-Relative Armor Immunity
            Console.WriteLine("[Group 3] Platform-Relative Armor Immunity");
            {
                var ring = new EmergentEngine.DangerStampRing();
                ring.StampFriendlyHarm(new Vector3(0f, 0f, 2f), new Vector3(0f, 0f, 40f), 101L, true, EmergentEngine.DangerWeaponClass.SmallArms, 0.85f, 15f, 20f, 10f);
                float hInf   = ring.EvaluateSpotProjectedHarm(Vector3.zero, Vector3.zero, 101L, true, isHeavyArmor: false, isLightArmor: false, EmergentEngine.EmergentTier.Individual, 11f, out _, out _, out _);
                float hLight = ring.EvaluateSpotProjectedHarm(Vector3.zero, Vector3.zero, 101L, true, isHeavyArmor: false, isLightArmor: true,  EmergentEngine.EmergentTier.Individual, 11f, out _, out _, out _);
                float hHeavy = ring.EvaluateSpotProjectedHarm(Vector3.zero, Vector3.zero, 101L, true, isHeavyArmor: true,  isLightArmor: false, EmergentEngine.EmergentTier.Individual, 11f, out _, out _, out _);
                Check("Heavy armor immune to small-arms H_proj; light scales partial", hHeavy == 0f && hLight > 0.10f && hLight < hInf && hInf > 0.50f, $"inf={hInf:F2} light={hLight:F2} heavy={hHeavy:F2}");

                ring.StampFriendlyHarm(new Vector3(0f, 0f, 3f), new Vector3(0f, 0f, 55f), 101L, true, EmergentEngine.DangerWeaponClass.AntiTank, 0.88f, 18f, 25f, 11f);
                float hHeavyAt = ring.EvaluateSpotProjectedHarm(Vector3.zero, Vector3.zero, 101L, true, isHeavyArmor: true, isLightArmor: false, EmergentEngine.EmergentTier.Individual, 11.5f, out _, out _, out _);
                Check("Heavy armor registers AntiTank sister penetration H_proj", hHeavyAt > 0.50f, $"heavyAT={hHeavyAt:F2}");
            }

            // 4. Safe(x, T) Duration Gating
            Console.WriteLine("[Group 4] Safe(x, T) Duration Gating");
            {
                float riskT = 0.46f;
                bool ok15 = EmergentEngine.IsSafeForDuration(0.42f, 1.5f, riskT);
                bool ok50 = EmergentEngine.IsSafeForDuration(0.42f, 5.0f, riskT);
                bool ok10 = EmergentEngine.IsSafeForDuration(0.42f, 10.0f, riskT);
                bool low10 = EmergentEngine.IsSafeForDuration(0.20f, 10.0f, riskT);
                Check("1.5s resupply allowed while 5s bandage / 10s repair blocked at moderate harm; 10s safe at low harm", ok15 && !ok50 && !ok10 && low10, $"1.5s={ok15} 5s={ok50} 10s={ok10} low10s={low10}");
            }

            // 5. Two-Speed Fast Reflex vs Hysteresis
            Console.WriteLine("[Group 5] Two-Speed Fast Reflex vs Hysteresis");
            {
                var inp = new EmergentEngine.EmergentInput
                {
                    Now = 20f, HasVisibleEnemy = true, CanHarmTarget = 0.85f, VisibleEnemyDist = 35f,
                    InCover = true, HasObjectiveTask = true, ObjectiveDist = 45f, ObjectiveUrgency = 0.4f,
                    SmoothedHarmNow = 0.25f, ProjectedHarm = 0.05f
                };
                var d0 = EmergentEngine.EvaluateDeliberative(in inp, in wDefault, default, 20f);
                var inpPerturb = inp;
                inpPerturb.Now = 20.3f;
                inpPerturb.SmoothedHarmNow = 0.28f;
                inpPerturb.ExposedSideOfWall = false;
                var d1 = EmergentEngine.EvaluateDeliberative(in inpPerturb, in wDefault, in d0, 20.3f);
                Check("Commitment hysteresis holds action on small noise", d0.Step0 == EmergentEngine.EmergentActionId.HoldAndFight && d1.Step0 == d0.Step0, $"{d0.Step0}->{d1.Step0}");

                inpPerturb.LiveGrenadeClose = true;
                bool rGren = EmergentEngine.EvaluateFastReflex(in inpPerturb, in wDefault, 20.3f, out var decGren);
                Check("LiveGrenade reflex overrides commitment immediately", rGren && decGren.Reflex == EmergentEngine.EmergentReflexKind.LiveGrenade && decGren.Step0 == EmergentEngine.EmergentActionId.EvadeGrenade, $"{decGren.Reflex}->{decGren.Step0}");

                var vehInp = new EmergentEngine.EmergentInput { Now = 21f, IsVehicle = true, IsHeavyArmor = true, HullBurningOrDoomed = true, SmoothedHarmNow = 0.95f };
                bool rDoom = EmergentEngine.EvaluateFastReflex(in vehInp, in wDefault, 21f, out var decDoom);
                Check("HullBurningOrDoomed triggers VehicleBailout reflex", rDoom && decDoom.Reflex == EmergentEngine.EmergentReflexKind.HullCookoffDoom && decDoom.Step0 == EmergentEngine.EmergentActionId.VehicleBailout, $"{decDoom.Reflex}->{decDoom.Step0}");

                var holdCovInp = new EmergentEngine.EmergentInput
                {
                    Now = 22f, InCover = true, HasObjectiveTask = true, InsideObjectiveZone = false,
                    HasVisibleEnemy = false, SmoothedHarmNow = 0.70f, ProjectedHarm = 0.15f
                };
                var decHoldCov = EmergentEngine.EvaluateDeliberative(in holdCovInp, in wDefault, default, 22f);
                Check("Covered bot under high harm without visible target holds cover (never default_patrols out)", decHoldCov.Step0 == EmergentEngine.EmergentActionId.HoldAndFight && decHoldCov.Reason == "default_hold_cover", $"{decHoldCov.Step0} ({decHoldCov.Reason})");
            }

            // 6. 2-3 Ply Prerequisite Chaining + Cumulative Chain Harm Rejection
            Console.WriteLine("[Group 6] 2-3 Ply Prerequisite Chains & Cumulative Harm Reject");
            {
                var ammoInp = new EmergentEngine.EmergentInput
                {
                    Now = 30f, AmmoEmpty = true, NearbyAmmoSource = true, HasVisibleEnemy = true,
                    CanHarmTarget = 0.85f, InCover = true, SmoothedHarmNow = 0.25f, ProjectedHarm = 0.0f
                };
                var decAmmo = EmergentEngine.EvaluateDeliberative(in ammoInp, in wDefault, default, 30f);
                Check("AmmoEmpty + VisibleEnemy chains ResupplyAmmo -> HoldAndFight", decAmmo.Step0 == EmergentEngine.EmergentActionId.ResupplyAmmo && decAmmo.Step1 == EmergentEngine.EmergentActionId.HoldAndFight, $"{decAmmo.Step0}->{decAmmo.Step1}");

                var bleedInp = new EmergentEngine.EmergentInput
                {
                    Now = 31f, IsBleeding = true, HasBandage = true, InCover = false, CoverAvailable = true,
                    SmoothedHarmNow = 0.50f, ProjectedHarm = 0.0f
                };
                var decBleed = EmergentEngine.EvaluateDeliberative(in bleedInp, in wDefault, default, 31f);
                Check("Exposed bleeding chains SeekCover -> SelfBandage", decBleed.Step0 == EmergentEngine.EmergentActionId.SeekCover && decBleed.Step1 == EmergentEngine.EmergentActionId.SelfBandage, $"{decBleed.Step0}->{decBleed.Step1}");

                var repairInp = new EmergentEngine.EmergentInput
                {
                    Now = 32f, IsVehicle = true, IsHeavyArmor = true, NeedsTrackOrEngineRepair = true,
                    HasSmokeGrenade = false, SmoothedHarmNow = 0.65f, ProjectedHarm = 0.35f, CanHarmTarget = 0.80f, HasVisibleEnemy = true
                };
                var decRepair = EmergentEngine.EvaluateDeliberative(in repairInp, in wDefault, default, 32f);
                Check("Unsafe 10s tank repair rejected -> fights buttoned", decRepair.UnsafePrereqRejected && decRepair.RejectedPrereqAction == EmergentEngine.EmergentActionId.VehicleFieldRepair && decRepair.Step0 == EmergentEngine.EmergentActionId.VehicleFaceAndFight, $"rej={decRepair.RejectedPrereqAction} act={decRepair.Step0}");

                var crewUpgInp = new EmergentEngine.EmergentInput
                {
                    Now = 33f, NeedsPrimaryWeaponUpgrade = true, NearbyAmmoSource = true, AmmoEmpty = false,
                    HasObjectiveTask = true, InsideObjectiveZone = false, ObjectiveDist = 80f, ObjectiveUrgency = 0.55f,
                    HasVisibleEnemy = true, VisibleEnemyDist = 55f, CanHarmTarget = 0.55f, SmoothedHarmNow = 0.10f
                };
                var decCrewUpg = EmergentEngine.EvaluateDeliberative(in crewUpgInp, in wDefault, default, 33f);
                Check("Pistol-only joined crewman prioritizes primary weapon scavenge over advancing", decCrewUpg.Step0 == EmergentEngine.EmergentActionId.ResupplyAmmo && decCrewUpg.Reason == "scavenge_primary_weapon", $"{decCrewUpg.Step0} ({decCrewUpg.Reason})");

                var crewCqbInp = crewUpgInp;
                crewCqbInp.Now = 34f;
                crewCqbInp.VisibleEnemyDist = 10f;
                crewCqbInp.PointBlankThreat = false;
                crewCqbInp.CanHarmTarget = 0.85f;
                var decCrewCqb = EmergentEngine.EvaluateDeliberative(in crewCqbInp, in wDefault, default, 34f);
                Check("Pistol-only crewman with enemy at CQB range fights instead of scavenging", decCrewCqb.Step0 == EmergentEngine.EmergentActionId.HoldAndFight, $"{decCrewCqb.Step0} ({decCrewCqb.Reason})");
            }

            // 7. IntentBoard Two-Mode Sharing + Non-Mutating Queries
            Console.WriteLine("[Group 7] IntentBoard Exclusive & CanAid MaxHelpers");
            {
                var board = new EmergentEngine.IntentBoard();
                bool q0 = board.IsClaimableExclusive(EmergentEngine.IntentKind.ExclusiveMedicPatient, 900L, 1L, 10f);
                Check("IsClaimableExclusive non-mutating", q0 && board.ActiveSlotCount == 0, $"slots={board.ActiveSlotCount}");
                bool c1 = board.TryClaimExclusive(EmergentEngine.IntentKind.ExclusiveMedicPatient, 900L, 1L, Vector3.zero, 5f, 10f);
                bool c2 = board.TryClaimExclusive(EmergentEngine.IntentKind.ExclusiveMedicPatient, 900L, 2L, Vector3.zero, 5f, 11f);
                board.ReleaseExclusive(EmergentEngine.IntentKind.ExclusiveMedicPatient, 900L, 1L);
                bool c3 = board.TryClaimExclusive(EmergentEngine.IntentKind.ExclusiveMedicPatient, 900L, 2L, Vector3.zero, 5f, 11.5f);
                Check("Exclusive mutex grants owner1, blocks owner2 until release", c1 && !c2 && c3);

                board.PostCanAid(EmergentEngine.IntentKind.CanAidOverwatchMedic, 777L, 50L, new Vector3(10f, 0f, 0f), 2, 8f, 10f);
                bool openCheck = board.HasOpenCanAid(EmergentEngine.IntentKind.CanAidOverwatchMedic, Vector3.zero, 30f, 51L, 10.5f);
                bool j1 = board.TryJoinCanAid(EmergentEngine.IntentKind.CanAidOverwatchMedic, Vector3.zero, 30f, 51L, 10.5f, out _, out _);
                bool j2 = board.TryJoinCanAid(EmergentEngine.IntentKind.CanAidOverwatchMedic, Vector3.zero, 30f, 52L, 10.5f, out _, out _);
                bool anyOverflow = false;
                for (long id = 53L; id <= 60L; id++)
                {
                    if (board.TryJoinCanAid(EmergentEngine.IntentKind.CanAidOverwatchMedic, Vector3.zero, 30f, id, 10.5f, out _, out _))
                        anyOverflow = true;
                }
                bool fullCheck = board.HasOpenCanAid(EmergentEngine.IntentKind.CanAidOverwatchMedic, Vector3.zero, 30f, 61L, 10.5f);
                Check("CanAid accepts 2 helpers, non-mutating check preserves capacity, blocks #3..10", openCheck && j1 && j2 && !anyOverflow && !fullCheck);
            }

            // 8. Radio Fog-of-War Honesty + MIA Timeout
            Console.WriteLine("[Group 8] Radio Fog-of-War Honesty + MIA Timeout");
            {
                var ring = new EmergentEngine.DangerStampRing();
                Vector3 pos = new Vector3(20f, 0f, 20f);
                ring.StampFriendlyHarm(pos, new Vector3(20f, 0f, 45f), 50L, reportedToNet: false, EmergentEngine.DangerWeaponClass.SmallArms, 0.72f, 15f, 20f, 10f);
                float netBlind = ring.EvaluateSpotProjectedHarm(pos, Vector3.zero, 99L, true, false, false, EmergentEngine.EmergentTier.TeamNet, 11f, out _, out _, out _);
                float t1Local  = ring.EvaluateSpotProjectedHarm(pos, pos + new Vector3(4f, 0f, 0f), 50L, false, false, false, EmergentEngine.EmergentTier.Individual, 11f, out _, out _, out _);
                Check("Unreported stamp invisible to TeamNet yet felt by local squad", netBlind == 0f && t1Local > 0.35f, $"net={netBlind:F2} local={t1Local:F2}");

                ring.StampMiaCommsTimeout(pos, 50L, 0.78f, 20f);
                float netMia = ring.EvaluateSpotProjectedHarm(pos, Vector3.zero, 99L, true, false, false, EmergentEngine.EmergentTier.TeamNet, 21f, out _, out _, out _);
                var t3Inp = new EmergentEngine.Tier3NetInput
                {
                    Now = 21f, ActiveSquads = 4, MiaSilentTimeouts = 1, LaneHarmHeat = netMia, TicketRatio01 = 0.75f,
                    SmokeAvailable = true, ArtilleryAvailable = true, AlternativeFlankOpen = true
                };
                var t3Dec = EmergentEngine.EvaluateTier3Net(in t3Inp, in usProf);
                bool validMiaLever = t3Dec.PrimaryLever == EmergentEngine.Tier3NetLever.SmokeBarrage ||
                                     t3Dec.PrimaryLever == EmergentEngine.Tier3NetLever.ArtilleryHE ||
                                     t3Dec.PrimaryLever == EmergentEngine.Tier3NetLever.ShiftAxisFlank;
                Check("MIA comms timeout surfaces in TeamNet & triggers counter-lever", netMia > 0.35f && validMiaLever, $"netMia={netMia:F2} lever={t3Dec.PrimaryLever}");
            }

            // 9. Tier-Inverted Priority + Sector-Scoped DesperationBias
            Console.WriteLine("[Group 9] Tier-Inverted Priority + Sector DesperationBias");
            {
                var baseW = EmergentEngine.ComputeWeights(in gerProf, VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.4f, 555u, desperationBias: 0f);
                var despW = EmergentEngine.ComputeWeights(in gerProf, VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.4f, 555u, desperationBias: 0.45f);
                var t3Crisis = new EmergentEngine.Tier3NetInput
                {
                    Now = 50f, ActiveSquads = 3, BalkingSquads = 1, LaneHarmHeat = 0.60f,
                    TicketRatio01 = 0.18f, CriticalObjectiveThreat01 = 0.80f,
                    SmokeAvailable = false, ArtilleryAvailable = false, TanksAvailable = false, CasAvailable = false
                };
                var t3Dec = EmergentEngine.EvaluateTier3Net(in t3Crisis, in gerProf);
                Check("Baseline T1 WSurvive > WWin; T3 exhaustion emits SectorDesperationBias shifting T1 WWin/Risk",
                    baseW.WSurvive > baseW.WWin &&
                    t3Dec.PrimaryLever == EmergentEngine.Tier3NetLever.SectorDesperationBias &&
                    t3Dec.SectorDesperationBias >= 0.30f &&
                    despW.WWin > baseW.WWin && despW.RiskThreshold > baseW.RiskThreshold && despW.WSurvive < baseW.WSurvive,
                    $"base(S={baseW.WSurvive:F2},W={baseW.WWin:F2},R={baseW.RiskThreshold:F2}) -> desp(S={despW.WSurvive:F2},W={despW.WWin:F2},R={despW.RiskThreshold:F2}) T3={t3Dec.PrimaryLever}/{t3Dec.SectorDesperationBias:F2}");

                var auraW = EmergentEngine.ComputeWeights(in usProf, VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.4f, 555u, leaderMoraleDelta: 0.14f);
                var plainW = EmergentEngine.ComputeWeights(in usProf, VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.4f, 555u, leaderMoraleDelta: 0f);
                var shockW = EmergentEngine.ComputeWeights(in usProf, VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.4f, 555u, leaderMoraleDelta: -0.15f);
                Check("Alive SL bravery aura boosts RiskThreshold/WWin & lowers WSurvive",
                    auraW.RiskThreshold > plainW.RiskThreshold && auraW.WWin > plainW.WWin && auraW.WSurvive < plainW.WSurvive,
                    $"plain(S={plainW.WSurvive:F2},W={plainW.WWin:F2},R={plainW.RiskThreshold:F2}) -> aura(S={auraW.WSurvive:F2},W={auraW.WWin:F2},R={auraW.RiskThreshold:F2})");

                var borderInp = new EmergentEngine.EmergentInput
                {
                    Now = 52f, InCover = false, CoverAvailable = true, HasObjectiveTask = true,
                    InsideObjectiveZone = false, ObjectiveDist = 45f, ObjectiveUrgency = 0.55f,
                    SmoothedHarmNow = 0.36f, ProjectedHarm = 0.05f
                };
                var decAuraBorder = EmergentEngine.EvaluateDeliberative(in borderInp, in auraW, default, 52f);
                var decShockBorder = EmergentEngine.EvaluateDeliberative(in borderInp, in shockW, default, 52f);
                Check("SL-death morale shock shifts borderline exposed advancing soldier into SeekCover",
                    shockW.RiskThreshold < plainW.RiskThreshold && shockW.WSurvive > plainW.WSurvive &&
                    decAuraBorder.Step0 == EmergentEngine.EmergentActionId.AdvanceObjective &&
                    decShockBorder.Step0 == EmergentEngine.EmergentActionId.SeekCover,
                    $"aura={decAuraBorder.Step0} shock={decShockBorder.Step0} shockR={shockW.RiskThreshold:F2}");
            }

            // 10. Doctrine x Personality x Individualism Matrix
            Console.WriteLine("[Group 10] Doctrine x Personality x Individualism");
            {
                var wUS   = EmergentEngine.ComputeWeights(in usProf,   VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.4f, 777u);
                var wGer  = EmergentEngine.ComputeWeights(in gerProf,  VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.4f, 777u);
                var wUssr = EmergentEngine.ComputeWeights(in ussrProf, VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.4f, 777u);
                var wFin  = EmergentEngine.ComputeWeights(in finProf,  VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.4f, 777u);
                var wJpn  = EmergentEngine.ComputeWeights(in jpnProf,  VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.4f, 777u);
                Check("5 doctrines mutate biases & individualism distinctly",
                    wUS.BiasSmoke > wUssr.BiasSmoke &&
                    wGer.BiasSuppress > wUssr.BiasSuppress &&
                    wFin.Individualism > wUssr.Individualism &&
                    wFin.BiasFlank > wUS.BiasFlank &&
                    wJpn.BiasBayonet > wUS.BiasBayonet &&
                    wUS.BiasAid > wJpn.BiasAid,
                    $"US.Smk={wUS.BiasSmoke:F2} GER.Sup={wGer.BiasSuppress:F2} FIN.Ind={wFin.Individualism:F2} JPN.Bay={wJpn.BiasBayonet:F2}");

                var wHeroic = EmergentEngine.ComputeWeights(in usProf, VeteranAndTacticsPatches.SoldierPersonality.Medic_Heroic, 0.4f, 888u);
                var wTriage = EmergentEngine.ComputeWeights(in usProf, VeteranAndTacticsPatches.SoldierPersonality.Medic_Triage, 0.4f, 888u);
                var wStalk  = EmergentEngine.ComputeWeights(in finProf, VeteranAndTacticsPatches.SoldierPersonality.Marksman_Stalker, 0.4f, 888u);
                var wAnchor = EmergentEngine.ComputeWeights(in gerProf, VeteranAndTacticsPatches.SoldierPersonality.Gunner_Anchor, 0.4f, 888u);
                Check("Personalities shift Risk/Aid/Individualism cleanly",
                    wHeroic.RiskThreshold > wTriage.RiskThreshold &&
                    wHeroic.WAid > wTriage.WAid &&
                    wStalk.Individualism > wAnchor.Individualism,
                    $"Hero(R={wHeroic.RiskThreshold:F2},A={wHeroic.WAid:F2}) > Triage(R={wTriage.RiskThreshold:F2},A={wTriage.WAid:F2}); StalkInd={wStalk.Individualism:F2} > AncInd={wAnchor.Individualism:F2}");
            }

            // 11. Compact Stimulus Flag Packing
            Console.WriteLine("[Group 11] Compact Stimulus Flag Packing");
            {
                var flagInp = new EmergentEngine.EmergentInput
                {
                    HasVisibleEnemy = true,
                    IsBleeding = true,
                    HasSmokeGrenade = true,
                    IsAtCapable = true,
                    InsideObjectiveZone = true,
                    DoorwayFreshCasualtyAhead = true,
                    LiveGrenadeClose = true,
                    NearbyAmmoSource = true,
                    PointBlankThreat = true
                };
                uint packed = EmergentEngine.PackInputFlags(in flagInp, unsafePrereqRejected: true);
                uint expected = (1u << 0) | (1u << 3) | (1u << 5) | (1u << 8) | (1u << 13) |
                                (1u << 19) | (1u << 21) | (1u << 24) | (1u << 25) | (1u << 27);
                Check("PackInputFlags packs exact 28-bit stimulus mask including UnsafePrereqRejected",
                    packed == expected && (packed & (1u << 1)) == 0u && (packed & (1u << 26)) == 0u,
                    $"packed=0x{packed:X} expected=0x{expected:X}");
            }

            // 12. CQB Immediate Combat Lock (5m enemy beats Flank/Advance/PopSmoke & guards PenetratingFire reflex)
            Console.WriteLine("[Group 12] CQB Immediate Combat Lock");
            {
                var cqbInp = new EmergentEngine.EmergentInput
                {
                    Now = 60f, HasVisibleEnemy = true, CanHarmTarget = 0.90f, VisibleEnemyDist = 5.0f,
                    InCover = false, HasSmokeGrenade = true, HasObjectiveTask = true, ObjectiveDist = 50f,
                    ObjectiveUrgency = 0.85f, FlankCoverAvailable = true, AllyPinningThreat = true,
                    SmoothedHarmNow = 0.82f, ProjectedHarm = 0.45f
                };
                bool reflexFired = EmergentEngine.EvaluateFastReflex(in cqbInp, in wDefault, 60f, out var rDec);
                var prevFlank = new EmergentEngine.EmergentDecision { Step0 = EmergentEngine.EmergentActionId.FlankManeuver, CommitUntil = 62f };
                var cqbDelib = EmergentEngine.EvaluateDeliberative(in cqbInp, in wDefault, in prevFlank, 60.5f);
                Check("CQB 5m armed enemy suppresses SuddenPenetratingFire turn-away & breaks Flank into Engage",
                    !reflexFired && cqbDelib.Step0 == EmergentEngine.EmergentActionId.HoldAndFight && cqbDelib.Reason == "cqb_snap_engage",
                    $"reflex={reflexFired} ({rDec.Reflex}) delib={cqbDelib.Step0} ({cqbDelib.Reason}) vs {cqbDelib.RunnerUp}");
            }

            // 13. Emergent Air Mission (Strike Bombers Run CAS vs Distant Enemy Air, Evade When Jumped, Cleaners Intercept)
            Console.WriteLine("[Group 13] Emergent Air Mission Evaluator");
            {
                var pilotW = EmergentEngine.ComputeWeights(in gerProf, VeteranAndTacticsPatches.SoldierPersonality.Pilot_Ace, 0.5f, 321u, 0f, isLeader: true);
                var distantFighterCas = new EmergentEngine.AirMissionInput
                {
                    IsBomber = false, HasBombs = true, HasPilotGunAmmo = true, HasAntiArmorCannons = false,
                    HasEnemyAir = true, NearestEnemyAirDist = 2400f, EnemyAirIsBomber = false,
                    HasGroundTarget = true, BestGroundTargetScore01 = 0.72f, GroundTargetIsArmorOrCluster = true
                };
                var casDec = EmergentEngine.EvaluateAirMission(in distantFighterCas, in pilotW);

                var jumpedCas = distantFighterCas;
                jumpedCas.UnderFireByAir = true;
                jumpedCas.NearestEnemyAirDist = 520f;
                var jumpDec = EmergentEngine.EvaluateAirMission(in jumpedCas, in pilotW);

                var heavyBomberUnderFighter = new EmergentEngine.AirMissionInput
                {
                    IsBomber = true, HasBombs = true, HasPilotGunAmmo = true,
                    HasEnemyAir = true, NearestEnemyAirDist = 700f, UnderFireByAir = false,
                    HasGroundTarget = true, BestGroundTargetScore01 = 0.75f, GroundTargetIsArmorOrCluster = true
                };
                var bmbDec = EmergentEngine.EvaluateAirMission(in heavyBomberUnderFighter, in pilotW);

                var cleanFighterIntercept = new EmergentEngine.AirMissionInput
                {
                    IsBomber = false, HasBombs = false, HasPilotGunAmmo = true,
                    HasEnemyAir = true, NearestEnemyAirDist = 800f, EnemyAirIsBomber = true,
                    HasGroundTarget = true, BestGroundTargetScore01 = 0.45f, FriendlyGroundUnderThreat = true
                };
                var intDec = EmergentEngine.EvaluateAirMission(in cleanFighterIntercept, in pilotW);

                Check("Strike runs CAS past 2.4km air, defends when jumped, heavy bomber bombs, clean fighter intercepts bomber",
                    casDec.Action == EmergentEngine.AirActionId.AttackGround &&
                    jumpDec.Action == EmergentEngine.AirActionId.InterceptAir &&
                    bmbDec.Action != EmergentEngine.AirActionId.InterceptAir &&
                    intDec.Action == EmergentEngine.AirActionId.InterceptAir,
                    $"cas={casDec.Action} jumped={jumpDec.Action} bmb={bmbDec.Action} clean={intDec.Action}");
            }

            // 14. Squad Affinity Cohesiveness
            Console.WriteLine("[Group 14] Squad Affinity Cohesiveness");
            {
                var medBase = new EmergentEngine.EmergentInput
                {
                    Now = 70f, IsMedic = true, HasInjuredAlly = true, InjuredAllySameSquad = false,
                    SmoothedHarmNow = 0.15f, ProjectedHarm = 0.05f
                };
                var medSquad = medBase;
                medSquad.InjuredAllySameSquad = true;
                var decOther = EmergentEngine.EvaluateDeliberative(in medBase, in wDefault, default, 70f);
                var decSquad = EmergentEngine.EvaluateDeliberative(in medSquad, in wDefault, default, 70f);

                var board = new EmergentEngine.IntentBoard();
                board.PostCanAid(EmergentEngine.IntentKind.CanAidOverwatchMedic, 1001L, 10L, new Vector3(12f, 0f, 0f), 2, 10f, 70f, originSquadId: 999L);
                board.PostCanAid(EmergentEngine.IntentKind.CanAidOverwatchMedic, 1002L, 20L, new Vector3(16f, 0f, 0f), 2, 10f, 70f, originSquadId: 50L);
                bool joinedOwnSq = board.TryJoinCanAid(EmergentEngine.IntentKind.CanAidOverwatchMedic, Vector3.zero, 42f, 77L, 50L, 70.5f, out _, out long joinedKey);
                Check("MedicHeal scores higher for own squad & IntentBoard prefers own-squad CanAid over closer external squad",
                    decSquad.Score > decOther.Score && joinedOwnSq && joinedKey == 1002L,
                    $"ownMed={decSquad.Score:F2} > otherMed={decOther.Score:F2}, joinedKey={joinedKey}");
            }

            // 15. Clustered-Enemy FragFlush & Exposed Drag Guard
            Console.WriteLine("[Group 15] Clustered-Enemy FragFlush & Exposed Drag Guard");
            {
                var clusterInp = new EmergentEngine.EmergentInput
                {
                    Now = 75f, HasFragGrenade = true, HasVisibleEnemy = true, VisibleEnemyDist = 18.0f,
                    CanHarmTarget = 0.70f, TargetBehindCover = false, ClusteredEnemiesAtThreat = 3,
                    InCover = true, SmoothedHarmNow = 0.25f, ProjectedHarm = 0.10f
                };
                var fragDec = EmergentEngine.EvaluateDeliberative(in clusterInp, in wDefault, default, 75f);

                var exposedDragInp = new EmergentEngine.EmergentInput
                {
                    Now = 76f, InCover = false, HasVisibleEnemy = true, VisibleEnemyDist = 16.0f,
                    CanHarmTarget = 0.85f, HasDownedAllyToDrag = true, SmoothedHarmNow = 0.20f, ProjectedHarm = 0.05f
                };
                var dragDec = EmergentEngine.EvaluateDeliberative(in exposedDragInp, in wDefault, default, 76f);

                Check("Visible clustered enemies trigger FragFlush & exposed <=24m enemy blocks open DragCasualty",
                    fragDec.Step0 == EmergentEngine.EmergentActionId.FragFlush &&
                    fragDec.Reason == "frag_cluster" &&
                    dragDec.Step0 != EmergentEngine.EmergentActionId.DragCasualty,
                    $"frag={fragDec.Step0}({fragDec.Reason}) exposedDrag={dragDec.Step0}");
            }

            // 16. Objective Ticking Contrary to Win Condition -> Sweep Contesters
            Console.WriteLine("[Group 16] Objective Ticking Contrary to Win Condition -> Sweep Contesters");
            {
                var sweepInp = new EmergentEngine.EmergentInput
                {
                    Now = 80f, HasObjectiveTask = true, InsideObjectiveZone = true, InCover = true,
                    HasVisibleEnemy = false, ObjectiveTickingAgainstUs = true, ObjectiveUrgency = 0.75f,
                    SmoothedHarmNow = 0.15f, ProjectedHarm = 0.05f
                };
                var sweepDec = EmergentEngine.EvaluateDeliberative(in sweepInp, in wDefault, default, 80f);
                Check("Objective ticking against win condition while inside cap sweeps out contesters instead of passive hold",
                    sweepDec.Step0 == EmergentEngine.EmergentActionId.AdvanceObjective &&
                    sweepDec.Reason == "sweep_cap_contesters",
                    $"sweep={sweepDec.Step0} ({sweepDec.Reason}) score={sweepDec.Score:F2}");
            }

            // 17. H.A.R.M. Pass 4: Radioman Weights & Guards, Blind Long-Range SuppressPin Gate, Safe 0-Sup Bandage
            Console.WriteLine("[Group 17] Radioman Preservation, Blind SuppressPin Range Gate & Safe 0-Sup Bandage");
            {
                var wRifle = EmergentEngine.ComputeWeights(in gerProf, VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.5f, 901u, isLeader: false, isRadioman: false);
                var wRadio = EmergentEngine.ComputeWeights(in gerProf, VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.5f, 901u, isLeader: false, isRadioman: true);
                Check("Radioman weights boost survival and tighten risk threshold",
                    wRadio.WSurvive > wRifle.WSurvive &&
                    wRadio.RiskThreshold < wRifle.RiskThreshold &&
                    wRadio.Individualism < wRifle.Individualism,
                    $"WSurv={wRadio.WSurvive:F2}>{wRifle.WSurvive:F2} Risk={wRadio.RiskThreshold:F2}<{wRifle.RiskThreshold:F2} Ind={wRadio.Individualism:F2}<{wRifle.Individualism:F2}");

                var radInp = new EmergentEngine.EmergentInput
                {
                    Now = 82f, IsRadioman = true, RadioLinkDrifting = true,
                    InCover = true, FlankCoverAvailable = true, AllyPinningThreat = true,
                    HasSurrenderedPrisoner = true, HasVisibleEnemy = false,
                    SmoothedHarmNow = 0.05f, ProjectedHarm = 0.02f
                };
                var radDec = EmergentEngine.EvaluateDeliberative(in radInp, in wRadio, default, 82f);
                Check("Radioman blocks FlankManeuver/LiberatePrisoner and prioritizes MaintainRadioLink",
                    radDec.Step0 != EmergentEngine.EmergentActionId.FlankManeuver &&
                    radDec.Step0 != EmergentEngine.EmergentActionId.LiberatePrisoner &&
                    radDec.Step0 == EmergentEngine.EmergentActionId.MaintainRadioLink,
                    $"radAct={radDec.Step0} ({radDec.Reason})");

                var blindFarSup = new EmergentEngine.EmergentInput
                {
                    Now = 84f, IsMachineGunnerOrBipod = true, InCover = true,
                    HasVisibleEnemy = false, HasLastKnownThreat = true, LastKnownThreatDist = 240f,
                    CanHarmTarget = 0.55f, SquadDirective = EmergentEngine.Tier2SquadDirective.AnvilPin,
                    SmoothedHarmNow = 0.10f, ProjectedHarm = 0.02f
                };
                var blindFarDec = EmergentEngine.EvaluateDeliberative(in blindFarSup, in wDefault, default, 84f);
                Check("Blind long-range (240m) SuppressPin blocked",
                    blindFarDec.Step0 != EmergentEngine.EmergentActionId.SuppressPin,
                    $"blind240={blindFarDec.Step0} ({blindFarDec.Reason})");

                var unharmArmorSup = new EmergentEngine.EmergentInput
                {
                    Now = 85f, IsMachineGunnerOrBipod = true, InCover = true,
                    HasVisibleEnemy = true, VisibleEnemyDist = 60f, CanHarmTarget = 0.0f,
                    SquadDirective = EmergentEngine.Tier2SquadDirective.AnvilPin,
                    SmoothedHarmNow = 0.10f, ProjectedHarm = 0.02f
                };
                var unharmDec = EmergentEngine.EvaluateDeliberative(in unharmArmorSup, in wDefault, default, 85f);
                Check("Unharmable target (CanHarm=0) blocks SuppressPin",
                    unharmDec.Step0 != EmergentEngine.EmergentActionId.SuppressPin,
                    $"armorSup={unharmDec.Step0} ({unharmDec.Reason})");

                var blindNearSup = new EmergentEngine.EmergentInput
                {
                    Now = 86f, IsMachineGunnerOrBipod = true, InCover = true,
                    HasVisibleEnemy = false, HasLastKnownThreat = true, LastKnownThreatDist = 90f,
                    CanHarmTarget = 0.55f, SquadDirective = EmergentEngine.Tier2SquadDirective.AnvilPin,
                    SmoothedHarmNow = 0.10f, ProjectedHarm = 0.02f
                };
                var blindNearDec = EmergentEngine.EvaluateDeliberative(in blindNearSup, in wDefault, default, 86f);
                Check("Blind in-range (90m) MG SuppressPin still allowed",
                    blindNearDec.Step0 == EmergentEngine.EmergentActionId.SuppressPin,
                    $"blind90={blindNearDec.Step0} ({blindNearDec.Reason})");

                var safeBandageInp = new EmergentEngine.EmergentInput
                {
                    Now = 88f, IsBleeding = true, HasBandage = true, InCover = true,
                    SmoothedHarmNow = 0.0f, ProjectedHarm = 0.0f
                };
                var bandageDec = EmergentEngine.EvaluateDeliberative(in safeBandageInp, in wDefault, default, 88f);
                Check("Bleeding in cover at 0 suppression chooses SelfBandage",
                    bandageDec.Step0 == EmergentEngine.EmergentActionId.SelfBandage,
                    $"bandage={bandageDec.Step0} ({bandageDec.Reason})");
            }

            // 18. H.A.R.M. Single-Authority Consolidation Checks
            Console.WriteLine("[Group 18] H.A.R.M. Single-Authority Prereqs, Armor & Sudden-Fire Reflex");
            {
                var expBleedInp = new EmergentEngine.EmergentInput
                {
                    Now = 90f, IsBleeding = true, HasBandage = true, InCover = false, CoverAvailable = true,
                    SmoothedHarmNow = 0.52f, ProjectedHarm = 0.05f
                };
                var expBleedDec = EmergentEngine.EvaluateDeliberative(in expBleedInp, in wDefault, default, 90f);
                Check("Exposed bleeding bot chains SeekCover -> SelfBandage without legacy SequentialUpdate hijack",
                    expBleedDec.Step0 == EmergentEngine.EmergentActionId.SeekCover &&
                    expBleedDec.Step1 == EmergentEngine.EmergentActionId.SelfBandage,
                    $"expBleed={expBleedDec.Step0}->{expBleedDec.Step1}");

                var armorEvadeInp = new EmergentEngine.EmergentInput
                {
                    Now = 92f, HasVisibleEnemy = true, VisibleEnemyDist = 30f, CanHarmTarget = 0.0f,
                    UncounterableArmorClose = true, InCover = false, CoverAvailable = true, HasSmokeGrenade = true,
                    SmoothedHarmNow = 0.65f, ProjectedHarm = 0.30f
                };
                var armorDec = EmergentEngine.EvaluateDeliberative(in armorEvadeInp, in wDefault, default, 92f);
                Check("Rifleman facing uncounterable armor evades/smokes instead of fighting",
                    armorDec.Step0 == EmergentEngine.EmergentActionId.SeekCover ||
                    armorDec.Step0 == EmergentEngine.EmergentActionId.PopSmoke ||
                    armorDec.Step0 == EmergentEngine.EmergentActionId.FallbackRegroup,
                    $"armor={armorDec.Step0} ({armorDec.Reason})");

                var spikeInp = new EmergentEngine.EmergentInput
                {
                    Now = 94f, SmoothedHarmNow = 0.85f, ProjectedHarm = 0.15f,
                    InCover = false, CoverAvailable = true, HasVisibleEnemy = true, VisibleEnemyDist = 35f
                };
                bool gotSpike = EmergentEngine.EvaluateFastReflex(in spikeInp, in wDefault, 94f, out var spikeDec);
                Check("Sudden penetrating fire reflex triggers SeekCover without OnSuppressed legacy halt",
                    gotSpike &&
                    spikeDec.Reflex == EmergentEngine.EmergentReflexKind.SuddenPenetratingFire &&
                    spikeDec.Step0 == EmergentEngine.EmergentActionId.SeekCover,
                    $"got={gotSpike} {spikeDec.Reflex}/{spikeDec.Step0}");

                var bleedCqbInp = new EmergentEngine.EmergentInput
                {
                    Now = 95f, IsBleeding = true, HasBandage = true, InCover = false, CoverAvailable = true,
                    HasVisibleEnemy = true, AmmoEmpty = false, CanHarmTarget = 0.85f, VisibleEnemyDist = 5f,
                    SmoothedHarmNow = 0.70f, ProjectedHarm = 0.15f
                };
                bool gotBleedCqb = EmergentEngine.EvaluateFastReflex(in bleedCqbInp, in wDefault, 95f, out var bleedCqbDec);
                Check("Bleeding bot in <9m CQB does not turn away via reflex_bleed_seek_cover",
                    !gotBleedCqb,
                    $"got={gotBleedCqb} {bleedCqbDec.Reflex}/{bleedCqbDec.Step0}");

                var bleedFarInp = bleedCqbInp;
                bleedFarInp.VisibleEnemyDist = 35f;
                bool gotBleedFar = EmergentEngine.EvaluateFastReflex(in bleedFarInp, in wDefault, 95f, out var bleedFarDec);
                Check("Bleeding bot outside CQB still triggers LethalBleedExposed SeekCover reflex",
                    gotBleedFar &&
                    bleedFarDec.Reflex == EmergentEngine.EmergentReflexKind.LethalBleedExposed &&
                    bleedFarDec.Step0 == EmergentEngine.EmergentActionId.SeekCover,
                    $"got={gotBleedFar} {bleedFarDec.Reflex}/{bleedFarDec.Step0}");

                var acoustAdvInp = new EmergentEngine.EmergentInput
                {
                    Now = 95.5f, CloseAcousticThreat = true, HasVisibleEnemy = false, AmmoEmpty = false,
                    CurrentStep0 = EmergentEngine.EmergentActionId.AdvanceObjective
                };
                bool gotAcoust = EmergentEngine.EvaluateFastReflex(in acoustAdvInp, in wDefault, 95.5f, out var acoustDec);
                var acoustCovInp = acoustAdvInp;
                acoustCovInp.CurrentStep0 = EmergentEngine.EmergentActionId.SeekCover;
                bool gotAcoustInCov = EmergentEngine.EvaluateFastReflex(in acoustCovInp, in wDefault, 95.5f, out _);
                Check("CloseAcousticThreat triggers CloseAcousticAlert HoldAndFight while advancing, but not over SeekCover",
                    gotAcoust && acoustDec.Reflex == EmergentEngine.EmergentReflexKind.CloseAcousticAlert &&
                    acoustDec.Step0 == EmergentEngine.EmergentActionId.HoldAndFight && !gotAcoustInCov,
                    $"got={gotAcoust} {acoustDec.Reflex}/{acoustDec.Step0} inCov={gotAcoustInCov}");

                var covBleedHighHarmInp = new EmergentEngine.EmergentInput
                {
                    Now = 96f, InCover = true, IsBleeding = true, HasBandage = true,
                    SmoothedHarmNow = 0.85f, ProjectedHarm = 0.25f,
                    HasVisibleEnemy = true, VisibleEnemyDist = 35f, CanHarmTarget = 0.80f
                };
                var decCovBleed = EmergentEngine.EvaluateDeliberative(in covBleedHighHarmInp, in wDefault, default, 96f);
                Check("Covered bleeding soldier under high harm bandages instead of peeking",
                    decCovBleed.Step0 == EmergentEngine.EmergentActionId.SelfBandage,
                    $"{decCovBleed.Step0} ({decCovBleed.Reason})");

                var expHeavyFireInp = new EmergentEngine.EmergentInput
                {
                    Now = 97f, InCover = false, CoverAvailable = true,
                    SmoothedHarmNow = 0.66f, AllyPinningThreat = true,
                    HasObjectiveTask = true, ObjectiveDist = 55f, ObjectiveUrgency = 0.60f
                };
                var decExpHeavy = EmergentEngine.EvaluateDeliberative(in expHeavyFireInp, in wDefault, default, 97f);
                Check("Exposed soldier under heavy direct fire seeks cover instead of advancing",
                    decExpHeavy.Step0 == EmergentEngine.EmergentActionId.SeekCover,
                    $"{decExpHeavy.Step0} ({decExpHeavy.Reason})");
            }

            // 19. Battlefield Scavenging: Bandages, AT Weapons, Grenades & Lull Priority
            Console.WriteLine("[Group 19] Battlefield Scavenging: Bandages, AT Weapons, Grenades & Lull Priority");
            {
                var bleedScavInp = new EmergentEngine.EmergentInput
                {
                    Now = 96f, IsBleeding = true, HasBandage = false, NearbyAmmoSource = true,
                    SmoothedHarmNow = 0.22f, ProjectedHarm = 0.0f
                };
                var bleedScavDec = EmergentEngine.EvaluateDeliberative(in bleedScavInp, in wDefault, default, 96f);
                Check("Bleeding soldier without bandage + NearbyAmmoSource chains ResupplyAmmo -> SelfBandage",
                    bleedScavDec.Step0 == EmergentEngine.EmergentActionId.ResupplyAmmo &&
                    bleedScavDec.Step1 == EmergentEngine.EmergentActionId.SelfBandage &&
                    bleedScavDec.Reason == "prereq_bandage_scavenge",
                    $"{bleedScavDec.Step0}->{bleedScavDec.Step1} ({bleedScavDec.Reason})");

                var atScavInp = new EmergentEngine.EmergentInput
                {
                    Now = 98f, HasVisibleEnemy = true, VisibleEnemyDist = 35f, CanHarmTarget = 0.0f,
                    UncounterableArmorClose = true, InCover = true, CoverAvailable = true,
                    NeedsAtWeaponScavenge = true, NearbyAmmoSource = true,
                    SmoothedHarmNow = 0.20f, ProjectedHarm = 0.0f
                };
                var atScavDec = EmergentEngine.EvaluateDeliberative(in atScavInp, in wDefault, default, 98f);
                Check("Covered rifleman facing heavy armor scavenges nearby AT weapon",
                    atScavDec.Step0 == EmergentEngine.EmergentActionId.ResupplyAmmo &&
                    atScavDec.Step1 == EmergentEngine.EmergentActionId.HoldAndFight &&
                    atScavDec.Reason == "scavenge_at_weapon",
                    $"{atScavDec.Step0}->{atScavDec.Step1} ({atScavDec.Reason})");

                var lullInp = new EmergentEngine.EmergentInput
                {
                    Now = 100f, HasVisibleEnemy = false, SmoothedHarmNow = 0.04f, ProjectedHarm = 0.0f,
                    HasObjectiveTask = true, InsideObjectiveZone = false, ObjectiveDist = 60f, ObjectiveUrgency = 0.45f,
                    ObjectiveTickingAgainstUs = false, NearbyAmmoSource = true
                };
                var lullDec = EmergentEngine.EvaluateDeliberative(in lullInp, in wDefault, default, 100f);
                Check("Calm-lull soldier picks scavenge_gear over routine marching",
                    lullDec.Step0 == EmergentEngine.EmergentActionId.ResupplyAmmo &&
                    lullDec.Reason == "scavenge_gear",
                    $"{lullDec.Step0} ({lullDec.Reason})");

                var urgentCapInp = lullInp;
                urgentCapInp.Now = 102f;
                urgentCapInp.ObjectiveTickingAgainstUs = true;
                var urgentCapDec = EmergentEngine.EvaluateDeliberative(in urgentCapInp, in wDefault, default, 102f);
                Check("Urgent ObjectiveTickingAgainstUs skips non-urgent scavenge_gear for AdvanceObjective",
                    urgentCapDec.Step0 == EmergentEngine.EmergentActionId.AdvanceObjective,
                    $"{urgentCapDec.Step0} ({urgentCapDec.Reason})");
            }

            // 20. Qualitative Behavioral Intentions (/grill-me & Live Playtest Invariants)
            Console.WriteLine("[Group 20] Qualitative Behavioral Intentions");
            {
                var defSymInp = new EmergentEngine.EmergentInput
                {
                    Now = 110f, InCover = true, InsideObjectiveZone = true, HasLastKnownThreat = true, LastKnownThreatDist = 35f,
                    ObjectiveTickingAgainstUs = false, IsHomeGroundDefender = false, SmoothedHarmNow = 0.05f
                };
                var defHomeInp = defSymInp;
                defHomeInp.IsHomeGroundDefender = true;
                var decSym = EmergentEngine.EvaluateDeliberative(in defSymInp, in wDefault, default, 110f);
                var decHome = EmergentEngine.EvaluateDeliberative(in defHomeInp, in wDefault, default, 110f);
                Check("Home-ground defender digs in on hold_angle_overwatch and outscores Conquest defender",
                    decHome.Step0 == EmergentEngine.EmergentActionId.HoldAndFight &&
                    decHome.Reason == "hold_angle_overwatch" &&
                    decHome.Score > decSym.Score,
                    $"home={decHome.Step0}({decHome.Reason},{decHome.Score:F2}) sym={decSym.Score:F2}");

                var doorFragInp = new EmergentEngine.EmergentInput
                {
                    Now = 111f, InCover = true, DoorwayFreshCasualtyAhead = true, HasFragGrenade = true,
                    HasLastKnownThreat = true, LastKnownThreatDist = 7.5f, SmoothedHarmNow = 0.25f
                };
                var decDoorFrag = EmergentEngine.EvaluateDeliberative(in doorFragInp, in wDefault, default, 111f);
                Check("Covered bot at hot doorway clears room with FragFlush (frag_doorway_clear)",
                    decDoorFrag.Step0 == EmergentEngine.EmergentActionId.FragFlush &&
                    decDoorFrag.Reason == "frag_doorway_clear",
                    $"{decDoorFrag.Step0} ({decDoorFrag.Reason})");

                var bot172Inp = new EmergentEngine.EmergentInput
                {
                    Now = 112f, InCover = false, SmoothedHarmNow = 0f, HasVisibleEnemy = true, VisibleEnemyDist = 22f,
                    CanHarmTarget = 0.90f, HasObjectiveTask = true, ObjectiveDist = 45f, ObjectiveUrgency = 0.45f
                };
                var dec172 = EmergentEngine.EvaluateDeliberative(in bot172Inp, in wDefault, default, 112f);
                var bot172SmkInp = bot172Inp;
                bot172SmkInp.SmokeActive = true;
                var dec172Smk = EmergentEngine.EvaluateDeliberative(in bot172SmkInp, in wDefault, default, 112f);
                Check("Exposed bot fights unpinned 22m enemy instead of blind-sprinting, but surges under smoke",
                    dec172.Step0 == EmergentEngine.EmergentActionId.HoldAndFight &&
                    dec172Smk.Step0 == EmergentEngine.EmergentActionId.AdvanceObjective,
                    $"unsmoked={dec172.Step0} smoked={dec172Smk.Step0}");

                var radCqbInp = new EmergentEngine.EmergentInput
                {
                    Now = 113f, IsRadioman = true, RadioLinkDrifting = true,
                    HasVisibleEnemy = true, VisibleEnemyDist = 13f, CanHarmTarget = 0.90f
                };
                var decRadCqb = EmergentEngine.EvaluateDeliberative(in radCqbInp, in wDefault, default, 113f);
                Check("Drifting Radioman defends himself against 13m visible enemy instead of jogging back to SL",
                    decRadCqb.Step0 == EmergentEngine.EmergentActionId.HoldAndFight,
                    $"{decRadCqb.Step0} ({decRadCqb.Reason})");

                var aheadRing = new EmergentEngine.DangerStampRing();
                var myP = new Vector3(0f, 0f, 0f);
                var doorP = new Vector3(0f, 0f, 7.0f);
                aheadRing.StampFriendlyHarm(doorP, new Vector3(0f, 0f, 15f), 1L, true, EmergentEngine.DangerWeaponClass.SmallArms, 0.75f, 8.0f, 10.0f, 114f);
                float hereH = aheadRing.EvaluateSpotProjectedHarm(myP, myP, 2L, true, false, false, EmergentEngine.EmergentTier.Individual, 114.2f, out _, out _, out bool hereDoor);
                float aheadH = aheadRing.EvaluateSpotProjectedHarm(new Vector3(0f, 0f, 5.5f), myP, 2L, true, false, false, EmergentEngine.EmergentTier.Individual, 114.2f, out _, out _, out bool aheadDoor);
                Check("5.5m path-ahead probe detects doorway casualty before bot steps into threshold",
                    aheadDoor && aheadH > hereH,
                    $"here=(H={hereH:F2},door={hereDoor}) ahead=(H={aheadH:F2},door={aheadDoor})");

                var safeMedDragInp = new EmergentEngine.EmergentInput
                {
                    Now = 115f, IsMedic = true, InCover = true, HasInjuredAlly = true, InjuredAllyInOpen = true,
                    InjuredAllyHarm = 0.55f, SmoothedHarmNow = 0.12f, HasSmokeGrenade = false, HasDownedAllyToDrag = true
                };
                var decSafeMedDrag = EmergentEngine.EvaluateDeliberative(in safeMedDragInp, in wDefault, default, 115f);
                Check("Safe medic with exposed casualty and no smoke chains DragCasualty -> MedicHeal",
                    decSafeMedDrag.Step0 == EmergentEngine.EmergentActionId.DragCasualty &&
                    decSafeMedDrag.Step1 == EmergentEngine.EmergentActionId.MedicHeal &&
                    decSafeMedDrag.Reason == "prereq_drag_medic",
                    $"{decSafeMedDrag.Step0}->{decSafeMedDrag.Step1} ({decSafeMedDrag.Reason})");

                var riskyMedDragInp = safeMedDragInp;
                riskyMedDragInp.Now = 116f;
                riskyMedDragInp.SmoothedHarmNow = 0.45f;
                var decRiskyMedDrag = EmergentEngine.EvaluateDeliberative(in riskyMedDragInp, in wDefault, default, 116f);
                Check("Medic at immediate personal risk (SmoothedHarmNow=0.45) aborts prereq_drag_medic",
                    decRiskyMedDrag.Step0 != EmergentEngine.EmergentActionId.DragCasualty &&
                    decRiskyMedDrag.UnsafePrereqRejected &&
                    decRiskyMedDrag.RejectedPrereqAction == EmergentEngine.EmergentActionId.MedicHeal,
                    $"{decRiskyMedDrag.Step0} rej={decRiskyMedDrag.RejectedPrereqAction}");
            }

            Console.WriteLine("============================================================");
            Console.WriteLine($"VerifyEmergentEngine: {_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }
    }
}
"@

$tmpCs  = Join-Path $buildDir "VerifyEmergent_Runner.gen.cs"
$tmpDll = Join-Path $buildDir "VerifyEmergentEngine.dll"
Set-Content -Path $tmpCs -Value $harnessCs -Encoding UTF8

$sdkLine = (& dotnet --list-sdks | Sort-Object -Descending | Select-Object -First 1)
if ($sdkLine -match '^(\S+)\s+\[(.+)\]$') {
    $csc = Join-Path $Matches[2] "$($Matches[1])\Roslyn\bincore\csc.dll"
} else {
    $csc = Join-Path $workspace "tools\roslyn\extracted\tasks\netcore\bincore\csc.dll"
}
$cscRuntimeConfig = Join-Path (Split-Path $csc -Parent) "csc.runtimeconfig.json"

$bepRoot = Join-Path $workspace "tools\bepinex\extracted"
if (-not (Test-Path (Join-Path $bepRoot "dotnet"))) {
    foreach ($cand in @("D:\SteamLibrary\steamapps\common\Easy Red 2", "C:\Program Files (x86)\Steam\steamapps\common\Easy Red 2")) {
        if (Test-Path (Join-Path $cand "dotnet")) { $bepRoot = $cand; break }
    }
}
$dotnetDir = Join-Path $bepRoot "dotnet"

try {
    & dotnet $csc /nologo /target:exe /optimize+ /nostdlib `
        "/r:$dotnetDir\System.Private.CoreLib.dll" `
        "/r:$dotnetDir\System.Runtime.dll" `
        "/r:$dotnetDir\System.Console.dll" `
        "/r:$dotnetDir\System.Collections.dll" `
        "/r:$dotnetDir\netstandard.dll" `
        "/out:$tmpDll" `
        $tmpCs $emergentFile
    if ($LASTEXITCODE -ne 0) { throw "Failed to compile VerifyEmergentEngine.dll" }

    & dotnet exec --runtimeconfig $cscRuntimeConfig $tmpDll
    if ($LASTEXITCODE -ne 0) { throw "VerifyEmergentEngine failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item $tmpCs, $tmpDll -Force -ErrorAction SilentlyContinue
}
