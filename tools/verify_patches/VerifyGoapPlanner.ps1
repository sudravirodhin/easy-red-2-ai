$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$goapFile = Join-Path $workspace "src\EasyRed2AI\Modules\Goap\GoapDefinitionsAndPlanner.cs"
$visFile  = Join-Path $workspace "src\EasyRed2AI\Modules\Vision\VisionRangeAndCamo.cs"
$wqFile   = Join-Path $workspace "src\EasyRed2AI\Core\AiCore.WorldQuery.cs"
$buildDir = Join-Path $workspace "build"
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
$partA = Slice-LinesBetween $goapLines '^\s*\[Flags\]' 'public Vector3 MoveOverrideDest;' 1
$partB = Slice-LinesBetween $goapLines '^\s*internal struct BotMicroVariance' '_factionProfileCache\[faction\] = p;' 2
$partC = Slice-LinesBetween $goapLines '^\s*internal static BotGoapPlan PlanBestGoapActionChainCore' 'Mathf\.Max\(0\.1f, cost - vet01 \* 0\.15f\);' 1

$visLines = Get-Content $visFile
$visPartA = Slice-LinesBetween $visLines '^\s*private struct RecentGunfireEntry' '_explosionSeqCounter = 1;' 0
$visPartB = Slice-LinesBetween $visLines '^\s*private static void RecordAcousticEventInternal' 'public static bool HasFiredRecently' -2

$wqLines  = Get-Content $wqFile
$wqSnapProbe = Slice-LinesBetween $wqLines '^\s*public static bool TrySnapWalkableGroundProbe\(' 'snappedGroundPos = gHit\.point;\s*$' 2

$cqbPath = Join-Path $workspace "src\EasyRed2AI\Modules\Infantry\CloseCombatAndSupport.cs"
$cqbText = Get-Content $cqbPath -Raw
function Extract-CqbFloat {
    param([string]$Text, [string]$Pattern, [string]$Label)
    $m = [regex]::Match($Text, $Pattern)
    if (-not $m.Success) { throw "Could not extract CQB invariant '$Label' using pattern: $Pattern" }
    return $m.Groups[1].Value
}
$cqbBayonetStandoff   = Extract-CqbFloat $cqbText 'HasBayonetReady\)\s*\{\s*standoffDist\s*=\s*([0-9]+\.[0-9]+)f;' 'bayonetStandoff'
$cqbKnifeStandoff     = Extract-CqbFloat $cqbText 'HoldsMeleeWeapon\)\s*\{\s*standoffDist\s*=\s*([0-9]+\.[0-9]+)f;' 'knifeStandoff'
$cqbUnbayStandoff     = Extract-CqbFloat $cqbText 'standoffDist\s*=\s*([0-9]+\.[0-9]+)f;\s*minPersonalSpace\s*=\s*0\.68f;' 'unbayonetedStandoff'
$cqbUnbayStrike       = Extract-CqbFloat $cqbText 'const float unbayonetedStrikeRange\s*=\s*([0-9]+\.[0-9]+)f;' 'unbayonetedStrikeRange'
$cqbKnifeStrike       = Extract-CqbFloat $cqbText 'const float meleeWeaponStrikeRange\s*=\s*([0-9]+\.[0-9]+)f;' 'meleeWeaponStrikeRange'
$cqbPistolCeaseFire   = Extract-CqbFloat $cqbText 'isPistol && !gunEmptyOrReloading && !hasBayonet\)\s*\{[^}]*?ceaseFireRange\s*=\s*([0-9]+\.[0-9]+)f;' 'pistolCeaseFireRange'
$cqbPatchedCapsule    = Extract-CqbFloat $cqbText '__instance\.mountedBlade\s*==\s*null\s*&&\s*__instance\.dist\s*<\s*([0-9]+\.[0-9]+)f' 'patchedAiCapsuleDist'
$cqbBayonetStrike     = Extract-CqbFloat $cqbText 'strikeRange\s*=\s*hasBayonet\s*\?\s*([0-9]+\.[0-9]+)f\s*:\s*unbayonetedStrikeRange;' 'bayonetStrikeRange'

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
        public float sqrMagnitude => x * x + y * y + z * z;
        public Vector3 normalized
        {
            get
            {
                float m = MathF.Sqrt(sqrMagnitude);
                return m > 1e-6f ? new Vector3(x / m, y / m, z / m) : zero;
            }
        }
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
        public static float Sqrt(float v) => MathF.Sqrt(v);
        public static float Clamp(float v, float mn, float mx) => v < mn ? mn : (v > mx ? mx : v);
        public static float Clamp01(float v) => Clamp(v, 0f, 1f);
        public static float Lerp(float a, float b, float t) => a + (b - a) * Clamp01(t);
    }

    public enum QueryTriggerInteraction { UseGlobal = 0, Ignore = 1, Collide = 2 }

    public class Collider
    {
        public bool IsTerrain;
        public bool IsVehicle;
    }

    public struct RaycastHit
    {
        public Vector3 point;
        public Vector3 normal;
        internal Collider _collider;
        public static int ColliderAccessCount;
        public Collider collider
        {
            get { ColliderAccessCount++; return _collider; }
        }
    }

    public static class Physics
    {
        public static bool MockRayHitValid;
        public static Vector3 MockRayPoint;
        public static Vector3 MockRayNormal;
        public static Collider MockRayCollider;
        public static bool MockSubFloorValid;
        public static Vector3 MockSubFloorPoint;
        public static Vector3 MockSubFloorNormal;
        public static Collider MockSubFloorCollider;
        public static bool MockLinecastBlocked;
        public static Collider MockLinecastCollider;
        public static Vector3 MockLinecastNormal;
        public static bool MockForceCapsuleBlocked;
        public static int MockObstacleLayerBit;
        public static int LastRayMask;
        public static int LastCapsuleMask;
        public static Vector3 LastRayOrigin;
        public static float LastRayMaxDist;
        public static Vector3 LastActiveGroundPoint;
        public static Vector3 LastActiveGroundNormal;

        public static bool Raycast(Vector3 origin, Vector3 dir, out RaycastHit hit, float maxDist, int mask, QueryTriggerInteraction qti)
        {
            LastRayOrigin = origin;
            LastRayMaxDist = maxDist;
            LastRayMask = mask;
            hit = default;
            if (MockRayHitValid)
            {
                float distDown = origin.y - MockRayPoint.y;
                if (distDown >= 0f && distDown <= maxDist)
                {
                    hit.point = MockRayPoint;
                    hit.normal = MockRayNormal;
                    hit._collider = MockRayCollider;
                    LastActiveGroundPoint = MockRayPoint;
                    LastActiveGroundNormal = MockRayNormal;
                    return true;
                }
            }
            if (MockSubFloorValid)
            {
                float subDistDown = origin.y - MockSubFloorPoint.y;
                if (subDistDown >= 0f && subDistDown <= maxDist)
                {
                    hit.point = MockSubFloorPoint;
                    hit.normal = MockSubFloorNormal;
                    hit._collider = MockSubFloorCollider;
                    LastActiveGroundPoint = MockSubFloorPoint;
                    LastActiveGroundNormal = MockSubFloorNormal;
                    return true;
                }
            }
            return false;
        }

        public static bool Linecast(Vector3 start, Vector3 end, out RaycastHit hit, int mask, QueryTriggerInteraction qti)
        {
            hit = default;
            if (!MockLinecastBlocked)
                return false;
            hit.point = (start + end) * 0.5f;
            hit.normal = MockLinecastNormal;
            hit._collider = MockLinecastCollider;
            return true;
        }

        public static bool CheckCapsule(Vector3 start, Vector3 end, float radius, int mask, QueryTriggerInteraction qti)
        {
            LastCapsuleMask = mask;
            if (MockForceCapsuleBlocked && (mask & MockObstacleLayerBit) != 0)
                return true;
            // Exact geometric check against the active ground plane to catch slope self-intersections!
            if ((MockRayHitValid || MockSubFloorValid) && (mask & 1) != 0)
            {
                Vector3 n = LastActiveGroundNormal.normalized;
                float d0 = Vector3.Dot(start - LastActiveGroundPoint, n);
                float d1 = Vector3.Dot(end - LastActiveGroundPoint, n);
                if (d0 <= radius || d1 <= radius)
                    return true;
            }
            return false;
        }
    }

    public class Transform
    {
        public Vector3 position;
    }

    public static class Time
    {
        public static float time = 100f;
    }
}

public enum Gamemap
{
    Unknown = 0,
    Stalingrad = 1,
    Normandy = 2,
    Anzio = 3,
    Kos = 4,
    Kwajalein = 5,
    Tunisia = 6,
    Cassino = 7,
    Ardennes = 8,
    Shanghai = 9,
    Nanking = 10,
    Makin = 11,
    RoiNamur = 12,
    KhalkhinGol = 13,
    Budapest = 14,
    Balaton = 15
}

public class Spottable { }
public class CombatCover { }
public class Squad { public bool isChargeSquad; }
public class Soldier
{
    public bool TowardWall;
    public bool IsTowardWall() => TowardWall;
}
public enum SoldierPose : byte { Idle = 0, Crouch = 1, Prone = 2 }

namespace EasyRed2HumanAI
{
    using UnityEngine;

    public class ConfigBool { public bool Value = true; }
    public static class VisionConfig
    {
        public static ConfigBool EnableFactionNetDoctrines = new ConfigBool { Value = true };
    }

    public static class AiCore
    {
        public struct ScavengeStub { public bool IsAtWeaponPickup; }
        public struct GoapStub { public ScavengeStub Scavenge; }
        public class SoldierFastHandles
        {
            public Soldier Ref;
            public Transform Tr;
            public GoapStub Goap;
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
            public bool IsAmphibiousBeachMap;
            public bool IsOpenDesertOrSteppeMap;
            public bool IsDenseUrbanMap;
            public bool IsJungleOrAtollMap;

            public string GetBattalionLowerForFaction(string faction)
            {
                if (string.IsNullOrEmpty(faction))
                    return string.Empty;
                if (!string.IsNullOrEmpty(InvadersFactionId) && AreSameSideFactions(faction, InvadersFactionId))
                    return InvadersBattalionLower ?? string.Empty;
                if (!string.IsNullOrEmpty(DefendersFactionId) && AreSameSideFactions(faction, DefendersFactionId))
                    return DefendersBattalionLower ?? string.Empty;
                return string.Empty;
            }
        }

        public static Dictionary<IntPtr, SoldierFastHandles> MockHandles = new Dictionary<IntPtr, SoldierFastHandles>();
        public static float NightDarkness01 = 0f;
        public static int BattleTheaterGeneration = 1;
        public static BattleTheaterSnapshot CurrentTheater;
        public static BattleTheaterSnapshot GetBattleTheaterSnapshot() => CurrentTheater;

        public static bool TryGetSoldierHandles(IntPtr ptr, out SoldierFastHandles h) => MockHandles.TryGetValue(ptr, out h);

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
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
                return true;
            bool aAllies = a.EndsWith("_allies", StringComparison.OrdinalIgnoreCase);
            bool bAllies = b.EndsWith("_allies", StringComparison.OrdinalIgnoreCase);
            if (aAllies && bAllies) return true;
            bool aAxis = a.EndsWith("_axis", StringComparison.OrdinalIgnoreCase);
            bool bAxis = b.EndsWith("_axis", StringComparison.OrdinalIgnoreCase);
            return aAxis && bAxis;
        }

        public static bool AreEnemyFactions(string a, string b) =>
            !string.IsNullOrEmpty(a) && !string.IsNullOrEmpty(b) && !AreSameSideFactions(a, b);

        public static string InternFaction(string s) => s;

        public static int GetSolidEnvironmentMask() => 1;
        public static int GetMovementObstacleMask() => 3;
        public static bool IsTerrainCollider(Collider col) => col != null && col.IsTerrain;
        public static bool IsVehicleCollider(Collider col) => col != null && col.IsVehicle;

$wqSnapProbe
    }

    public static partial class VisionPatches
    {
$visPartA
$visPartB

        public static void ClearRing()
        {
            Array.Clear(_gunfireRing, 0, _gunfireRing.Length);
            _gunfireRingHead = 0;
        }
        public static void InjectGunfire(IntPtr ptr, Vector3 pos, string fac, float t, float rad) =>
            RecordAcousticEventInternal(ptr, pos, fac, t, rad, false);
        public static void InjectExplosion(IntPtr ptr, Vector3 pos, string fac, float t, float rad) =>
            RecordAcousticEventInternal(ptr, pos, fac, t, rad, true);
    }

    public static partial class VeteranAndTacticsPatches
    {
        public static int IndividualSelfPreservationBalksCount;

        public enum SoldierPersonality : byte
        {
            None = 0,
            Leader_Spearhead = 1, Leader_Tactician = 2, Leader_OldGuard = 3,
            Assaulter_Shock = 4, Assaulter_Breacher = 5, Assaulter_Skirmisher = 6,
            Rifleman_Regular = 7, Rifleman_Cautious = 8, Rifleman_Grenadier = 9,
            Gunner_Anchor = 10, Gunner_AssaultMG = 11, Gunner_Overwatch = 12,
            Marksman_Deadeye = 13, Marksman_Stalker = 14, Marksman_Spotter = 15,
            AT_TankHunter = 16, AT_Ambusher = 17, AT_Demolitionist = 18,
            Medic_Heroic = 19, Medic_Combat = 20, Medic_Triage = 21,
            TankCrew_Brawler = 22, TankCrew_Sniper = 23, TankCrew_Support = 24,
            LightCrew_Outrider = 25, LightCrew_Escort = 26, LightCrew_FlakWatch = 27,
            Pilot_Ace = 28, Pilot_Daredevil = 29, Pilot_Disciplined = 30
        }

        private static bool IsMovementGoapAction(GoapActionId id)
        {
            switch (id)
            {
                case GoapActionId.SeekEmergencyCover:
                case GoapActionId.RepositionBehindWall:
                case GoapActionId.MaintainRadioLinkWithLeader:
                case GoapActionId.MoveToFlankCover:
                case GoapActionId.BoundWithOverwatch:
                case GoapActionId.PointBlankBayonetBreach:
                case GoapActionId.RescueSurrenderedComrade:
                case GoapActionId.LoneWolfLinkUpOrAmbush:
                case GoapActionId.ScavengeSupplyFromCorpse:
                case GoapActionId.TacticalFallbackRetreat:
                    return true;
                default:
                    return false;
            }
        }

$partA
$partB
$partC
    }

    public static class Program
    {
        private static int _passed;
        private static void Assert(bool cond, string msg)
        {
            if (!cond) throw new Exception("ASSERTION FAILED: " + msg);
            _passed++;
        }

        public static int Main()
        {
            var factions = new[] { "Ussr_allies", "Hungary_axis", "Germany", "UnitedStates", "England", "Japan", "Finland", "Italy" };
            var directives = new[]
            {
                VeteranAndTacticsPatches.SquadNetDirective.Disconnected_Autonomous,
                VeteranAndTacticsPatches.SquadNetDirective.Connected_StandardObjective,
                VeteranAndTacticsPatches.SquadNetDirective.Anvil_PinAndCallSupport,
                VeteranAndTacticsPatches.SquadNetDirective.Hammer_WideFlankEnvelopment,
                VeteranAndTacticsPatches.SquadNetDirective.Regroup_SectorWithdrawal
            };
            var personalities = (VeteranAndTacticsPatches.SoldierPersonality[])Enum.GetValues(typeof(VeteranAndTacticsPatches.SoldierPersonality));

            int sweepCount = 0;
            int holdOverwatchWins = 0;
            foreach (var fac in factions)
            foreach (var dir in directives)
            foreach (var pers in personalities)
            foreach (bool canOverwatch in new[] { false, true })
            foreach (bool canBlindSuppress in new[] { false, true })
            foreach (bool flankAvail in new[] { false, true })
            {
                var board = new VeteranAndTacticsPatches.SquadBlackboard
                {
                    AliveMemberCount = 6,
                    HasActiveThreat = true,
                    ThreatPosition = new Vector3(0f, 0f, 45f),
                    NetDirective = dir
                };
                var ws = VeteranAndTacticsPatches.GoapStateBits.SquadHasActiveThreat | VeteranAndTacticsPatches.GoapStateBits.InCover;
                if (canOverwatch) ws |= VeteranAndTacticsPatches.GoapStateBits.CanProvideOverwatch | VeteranAndTacticsPatches.GoapStateBits.CanFireAtVisibleEnemy | VeteranAndTacticsPatches.GoapStateBits.HasVisibleEnemy;
                else if (canBlindSuppress) ws |= VeteranAndTacticsPatches.GoapStateBits.CanBlindSuppressThreat;
                if (flankAvail) ws |= VeteranAndTacticsPatches.GoapStateBits.FlankCoverAvailable;
                bool isMedic = pers == VeteranAndTacticsPatches.SoldierPersonality.Medic_Heroic ||
                               pers == VeteranAndTacticsPatches.SoldierPersonality.Medic_Combat ||
                               pers == VeteranAndTacticsPatches.SoldierPersonality.Medic_Triage;

                var plan = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(ws, new IntPtr(1001 + sweepCount), fac, true, isMedic, false, ref board, pers, 0.5f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                sweepCount++;

                // 1. Self-Pinning Invariant: 0 self-pinning 2-step chains!
                bool badSelfPin = (plan.Step0 == VeteranAndTacticsPatches.GoapActionId.SuppressThreatPosition ||
                                   plan.Step0 == VeteranAndTacticsPatches.GoapActionId.HoldOverwatchFire) &&
                                  (plan.Step1 == VeteranAndTacticsPatches.GoapActionId.BoundWithOverwatch ||
                                   plan.Step1 == VeteranAndTacticsPatches.GoapActionId.MoveToFlankCover);
                Assert(!badSelfPin, $"Self-pinning chain detected for {fac}/{dir}/{pers}: {plan.Step0} -> {plan.Step1}");

                // 2. Role Hijack Invariant: SupportSquad MUST NEVER select BoundWithOverwatch!
                if (plan.LastGoalBit == VeteranAndTacticsPatches.GoapStateBits.SquadSupported)
                {
                    Assert(plan.Step0 != VeteranAndTacticsPatches.GoapActionId.BoundWithOverwatch &&
                           plan.Step1 != VeteranAndTacticsPatches.GoapActionId.BoundWithOverwatch,
                           $"SupportSquad hijacked into BoundWithOverwatch for {fac}/{dir}/{pers}");
                }

                // 2b. Blind Suppression Precondition: SuppressThreatPosition MUST NEVER be chosen when CanBlindSuppressThreat == 0!
                if (!canBlindSuppress || canOverwatch)
                {
                    Assert(plan.Step0 != VeteranAndTacticsPatches.GoapActionId.SuppressThreatPosition &&
                           plan.Step1 != VeteranAndTacticsPatches.GoapActionId.SuppressThreatPosition,
                           $"SuppressThreatPosition chosen without CanBlindSuppressThreat for {fac}/{dir}/{pers} (canOw={canOverwatch}, canBlind={canBlindSuppress})");
                }

                // 3. Overwatch Tie-Break Invariant: Whenever goal == TargetPinned and CanProvideOverwatch == 1,
                //    HoldOverwatchFire MUST win over blind SuppressThreatPosition 100% of the time (even on Germany/AnvilPin/Gunner_Anchor)!
                if (canOverwatch && plan.LastGoalBit == VeteranAndTacticsPatches.GoapStateBits.TargetPinned)
                {
                    Assert(plan.Step0 == VeteranAndTacticsPatches.GoapActionId.HoldOverwatchFire,
                        $"TargetPinned with CanProvideOverwatch picked {plan.Step0} instead of HoldOverwatchFire for {fac}/{dir}/{pers}!");
                    holdOverwatchWins++;
                }
            }
            Assert(holdOverwatchWins > 500, $"Expected >500 HoldOverwatchFire wins across sweep, got {holdOverwatchWins}");

            // 3b. v0.9.3 Log-Fix Regressions (Unsafe Bleed Crisis & Gurkha vs IJA Gyokusai):
            {
                var bleedBoard = new VeteranAndTacticsPatches.SquadBlackboard { AliveMemberCount = 5, HasActiveThreat = true };
                var wsUnsafeBleed = VeteranAndTacticsPatches.GoapStateBits.IsBleeding |
                                    VeteranAndTacticsPatches.GoapStateBits.BandageUnsafeUnderFire |
                                    VeteranAndTacticsPatches.GoapStateBits.HasBandage |
                                    VeteranAndTacticsPatches.GoapStateBits.SquadHasActiveThreat;
                var bleedPlan = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    wsUnsafeBleed, new IntPtr(4901), "Hungary_axis", true, false, false, ref bleedBoard,
                    VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.2f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                Assert(bleedPlan.Step0 == VeteranAndTacticsPatches.GoapActionId.SeekEmergencyCover,
                    $"Bleeding+BandageUnsafeUnderFire expected SeekEmergencyCover, got {bleedPlan.Step0} -> {bleedPlan.Step1}");

                var gurkhaProf = VeteranAndTacticsPatches.GetFactionCommandProfile("Gurkha_allies");
                Assert(!gurkhaProf.AllowGyokusaiDesperation, "Gurkha_allies wrongly enabled AllowGyokusaiDesperation!");

                var ijaProf = VeteranAndTacticsPatches.GetFactionCommandProfile("Japan_axis");
                Assert(ijaProf.AllowGyokusaiDesperation, "Japan_axis did not enable AllowGyokusaiDesperation!");

                var sqNormal = new Squad { isChargeSquad = false };
                var ijaDayBoard = new VeteranAndTacticsPatches.SquadBlackboard { AliveMemberCount = 6, TicketRatio01 = 0.85f, IsInvaderFaction = true };
                AiCore.NightDarkness01 = 0f;
                Assert(!VeteranAndTacticsPatches.IsTrueGyokusaiOrBanzaiCondition(in ijaProf, sqNormal, in ijaDayBoard),
                    "Daytime full-ticket IJA squad falsely triggered Gyokusai!");
                AiCore.NightDarkness01 = 0.75f;
                Assert(VeteranAndTacticsPatches.IsTrueGyokusaiOrBanzaiCondition(in ijaProf, sqNormal, in ijaDayBoard),
                    "Nighttime IJA squad failed to unlock Gyokusai!");
                AiCore.NightDarkness01 = 0f;
                ijaDayBoard.TicketRatio01 = 0.15f;
                Assert(VeteranAndTacticsPatches.IsTrueGyokusaiOrBanzaiCondition(in ijaProf, sqNormal, in ijaDayBoard),
                    "Low-ticket IJA last stand failed to unlock Gyokusai!");
            }

            // 4. Negative-Cost FlankCover -> FlushFrag Hijack Elimination:
            {
                var board = new VeteranAndTacticsPatches.SquadBlackboard
                {
                    AliveMemberCount = 6,
                    HasActiveThreat = true,
                    ThreatPosition = new Vector3(0f, 0f, 22f),
                    NetDirective = VeteranAndTacticsPatches.SquadNetDirective.Connected_StandardObjective
                };
                var ws = VeteranAndTacticsPatches.GoapStateBits.SquadHasActiveThreat |
                         VeteranAndTacticsPatches.GoapStateBits.AllyPinningThreat |
                         VeteranAndTacticsPatches.GoapStateBits.FlankCoverAvailable |
                         VeteranAndTacticsPatches.GoapStateBits.HasFragGrenade |
                         VeteranAndTacticsPatches.GoapStateBits.InGrenadeRange |
                         VeteranAndTacticsPatches.GoapStateBits.TargetLostBehindCover;
                var plan = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    ws, new IntPtr(5001), "Ussr_allies", true, false, false, ref board,
                    VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.3f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                Assert(!(plan.Step0 == VeteranAndTacticsPatches.GoapActionId.MoveToFlankCover && plan.Step1 == VeteranAndTacticsPatches.GoapActionId.FlushThreatWithFrag),
                    "Regular rifleman on Connected_StandardObjective still got hijacked into FlankCover -> FlushFrag!");

                board.NetDirective = VeteranAndTacticsPatches.SquadNetDirective.Hammer_WideFlankEnvelopment;
                var wsGren = VeteranAndTacticsPatches.GoapStateBits.SquadHasActiveThreat |
                             VeteranAndTacticsPatches.GoapStateBits.AllyPinningThreat |
                             VeteranAndTacticsPatches.GoapStateBits.FlankCoverAvailable |
                             VeteranAndTacticsPatches.GoapStateBits.HasFragGrenade |
                             VeteranAndTacticsPatches.GoapStateBits.TargetLostBehindCover;
                var planGren = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    wsGren, new IntPtr(5002), "Ussr_allies", true, false, false, ref board,
                    VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Grenadier, 0.3f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                Assert(planGren.Step0 == VeteranAndTacticsPatches.GoapActionId.MoveToFlankCover &&
                       planGren.Step1 == VeteranAndTacticsPatches.GoapActionId.FlushThreatWithFrag,
                    $"Grenadier on Hammer flank failed intentional 2-step FlankCover -> FlushFrag (got {planGren.Step0} -> {planGren.Step1})");
            }

            // 5. Bleeding Out of Cover -> 2-step SeekEmergencyCover -> BandageInCover:
            {
                var board = new VeteranAndTacticsPatches.SquadBlackboard { AliveMemberCount = 5 };
                var ws = VeteranAndTacticsPatches.GoapStateBits.IsBleeding | VeteranAndTacticsPatches.GoapStateBits.HasBandage;
                var plan = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    ws, new IntPtr(6001), "Hungary_axis", true, false, false, ref board,
                    VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.2f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                Assert(plan.Step0 == VeteranAndTacticsPatches.GoapActionId.SeekEmergencyCover &&
                       plan.Step1 == VeteranAndTacticsPatches.GoapActionId.BandageInCover,
                    $"Bleeding soldier out of cover expected SeekCover -> BandageInCover, got {plan.Step0} -> {plan.Step1}");
            }

            // 6. PointBlankBayonetBreach Selection:
            {
                var board = new VeteranAndTacticsPatches.SquadBlackboard { AliveMemberCount = 5, HasActiveThreat = true };
                var ws = VeteranAndTacticsPatches.GoapStateBits.InPointBlankRange | VeteranAndTacticsPatches.GoapStateBits.HasVisibleEnemy;
                var plan = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    ws, new IntPtr(7001), "Ussr_allies", true, false, false, ref board,
                    VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.2f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                Assert(plan.Step0 == VeteranAndTacticsPatches.GoapActionId.PointBlankBayonetBreach,
                    $"PointBlankRange expected PointBlankBayonetBreach, got {plan.Step0}");
            }

            // 7. RescueSurrenderedComrade Selection:
            {
                var board = new VeteranAndTacticsPatches.SquadBlackboard { AliveMemberCount = 5 };
                var ws = VeteranAndTacticsPatches.GoapStateBits.ComradeNeedsLiberation;
                var plan = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    ws, new IntPtr(8001), "UnitedStates", true, false, false, ref board,
                    VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.2f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                Assert(plan.Step0 == VeteranAndTacticsPatches.GoapActionId.RescueSurrenderedComrade,
                    $"ComradeNeedsLiberation expected RescueSurrenderedComrade, got {plan.Step0}");
            }

            // 8. Acoustic Ring Buffer Regression Checks:
            VisionPatches.ClearRing();
            Time.time = 100f;
            VisionPatches.InjectExplosion(new IntPtr(-1001), new Vector3(0f, 0f, -20f), "Hungary_axis", 99.8f, 260f);
            Assert(!VisionPatches.TryGetNearestEnemyGunfire(Vector3.zero, "Ussr_allies", 50f, 5f, out _),
                "Explosion impact falsely triggered TryGetNearestEnemyGunfire!");
            Assert(!VisionPatches.TryGetDominantEnemyGunfireDirection(Vector3.zero, "Ussr_allies", 150f, 5f, out _, out _),
                "Overshot explosion behind wall falsely triggered TryGetDominantEnemyGunfireDirection (would invert leeward wall cover)!");
            Assert(!VisionPatches.TryGetAudibleBattleDirection(Vector3.zero, "Ussr_allies", 9f, 11f, out _, out _),
                "Close 20m overshot crater behind trench should not spin sentry 180 deg backward!");

            VisionPatches.InjectExplosion(new IntPtr(-1002), new Vector3(0f, 0f, 140f), "Hungary_axis", 99.5f, 260f);
            Assert(VisionPatches.TryGetAudibleBattleDirection(Vector3.zero, "Ussr_allies", 9f, 11f, out Vector3 audVec, out _) && audVec.z > 100f,
                "Distant 140m explosion failed to orient TryGetAudibleBattleDirection!");

            VisionPatches.ClearRing();
            VisionPatches.InjectExplosion(new IntPtr(-1003), new Vector3(0f, 0f, 95f), "Hungary_axis", 99.8f, 260f);
            VisionPatches.InjectGunfire(new IntPtr(9001), new Vector3(95f, 0f, 0f), "Hungary_axis", 99.8f, 210f);
            Assert(VisionPatches.TryGetAudibleBattleDirection(Vector3.zero, "Ussr_allies", 9f, 11f, out Vector3 winVec, out _) && winVec.x > 90f,
                "95m shell crater wrongly outranked 95m live enemy turret/shooter!");

            // 9. TrySnapWalkableGroundProbe Rooftop, Steep TerrainCollider & Capsule Clearance Checks:
            {
                var nonTerrainCol = new Collider { IsTerrain = false };
                var terrainCol    = new Collider { IsTerrain = true };
                Vector3 myFeet = new Vector3(10f, 5.0f, 10f);

                // 9a. Urban shed/bunker/building rooftops (+2.02m and +3.6m on non-terrain) MUST be rejected
                Physics.MockRayHitValid = true;
                Physics.MockForceCapsuleBlocked = false;
                Physics.MockRayCollider = nonTerrainCol;
                Physics.MockRayNormal = Vector3.up;
                Physics.MockRayPoint = new Vector3(14.5f, 7.02f, 10f);
                Assert(!AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(14.5f, 5.0f, 10f), 4.2f, 9.0f, 0.50f, 2.0f, false, 0f, out _),
                    "Non-terrain +2.02m shed rooftop should be rejected in Pass 2 unstick!");

                Physics.MockRayPoint = new Vector3(20.0f, 8.60f, 10f);
                Assert(!AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(20.0f, 5.0f, 10f), 6.5f, 13.5f, 0.55f, 2.0f, false, 0f, out _),
                    "Non-terrain +3.60m building rooftop should be rejected in open-field bound!");

                // 9b. Urban deep cellar / canal drop (-2.05m on non-terrain) MUST be rejected
                Physics.MockRayPoint = new Vector3(14.5f, 2.95f, 10f);
                Assert(!AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(14.5f, 5.0f, 10f), 4.2f, 9.0f, 0.50f, 2.0f, false, 0f, out _),
                    "Non-terrain -2.05m cellar/canal drop should be rejected!");

                // 9c. Gentle non-terrain street slope (+1.85m <= 2.0m) MUST pass with single cached Collider wrapper access
                RaycastHit.ColliderAccessCount = 0;
                Physics.MockRayPoint = new Vector3(20.0f, 6.85f, 10f);
                Physics.MockRayNormal = new Vector3(-0.17f, 0.985f, 0f).normalized;
                Assert(AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(20.0f, 5.0f, 10f), 6.5f, 13.5f, 0.55f, 2.0f, false, 0f, out Vector3 streetSnap) &&
                       Mathf.Abs(streetSnap.y - 6.85f) < 1e-4f,
                    "Gentle +1.85m urban street slope was wrongly rejected!");
                Assert(RaycastHit.ColliderAccessCount == 1,
                    $"Expected 1 cached RaycastHit.collider access on gentle street slope, got {RaycastHit.ColliderAccessCount}!");

                // 9d. Steep TerrainCollider hillside climbs & descents across FULL call-site envelopes:
                //     - Pass 2 (4.5m @ 60 deg => +7.79m / -7.79m, liftY=8.0f, maxRayDist=16.0f, minNormalY=0.50f)
                //     - BoundForward (12.44m @ 56.6 deg => +18.88m / -18.88m, liftY=19.0f, maxRayDist=38.0f, minNormalY=0.55f)
                //     AND verify single Collider wrapper access (ColliderAccessCount == 1) on steep uphill TerrainCollider!
                Physics.MockRayCollider = terrainCol;
                RaycastHit.ColliderAccessCount = 0;
                Physics.MockRayPoint = new Vector3(14.5f, 12.75f, 10f); // +7.75m climb at 60.0 deg!
                Physics.MockRayNormal = new Vector3(-0.8660254f, 0.50f, 0f);
                Assert(AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(14.5f, 5.0f, 10f), 8.0f, 16.0f, 0.50f, 2.0f, false, 0f, out Vector3 hillPass2) &&
                       Mathf.Abs(hillPass2.y - 12.75f) < 1e-4f,
                    "Steep 60 deg (+7.75m) TerrainCollider climb failed or capsule self-intersected slope in Pass 2!");
                Assert(RaycastHit.ColliderAccessCount == 1,
                    $"Expected exactly 1 RaycastHit.collider access on steep uphill TerrainCollider, got {RaycastHit.ColliderAccessCount}!");

                Physics.MockRayPoint = new Vector3(22.4f, 23.60f, 10f); // +18.60m climb at 56.6 deg over 12.4m bound!
                Physics.MockRayNormal = new Vector3(-0.8351646f, 0.55f, 0f);
                Assert(AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(22.4f, 5.0f, 10f), 19.0f, 38.0f, 0.55f, 2.0f, false, 0f, out Vector3 hillBound) &&
                       Mathf.Abs(hillBound.y - 23.60f) < 1e-4f,
                    "Steep 56.6 deg (+18.60m) TerrainCollider climb failed or capsule self-intersected slope in Bound!");

                Physics.MockRayPoint = new Vector3(14.5f, -2.60f, 10f); // -7.60m downhill at 59.4 deg on TerrainCollider in Pass 2!
                Physics.MockRayNormal = new Vector3(0.860f, 0.51f, 0f).normalized;
                Assert(AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(14.5f, 5.0f, 10f), 8.0f, 16.0f, 0.50f, 2.0f, false, 0f, out _),
                    "Downhill -7.60m TerrainCollider descent was wrongly rejected in Pass 2!");

                Physics.MockRayPoint = new Vector3(22.4f, -13.20f, 10f); // -18.20m downhill at 55.8 deg on TerrainCollider in Bound!
                Physics.MockRayNormal = new Vector3(0.827f, 0.562f, 0f).normalized;
                Assert(AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(22.4f, 5.0f, 10f), 19.0f, 38.0f, 0.55f, 2.0f, false, 0f, out _),
                    "Downhill -18.20m TerrainCollider descent was wrongly rejected in Bound!");

                // 9e. Unwalkable >60 deg cliff (normal.y = 0.49 < 0.50) and submerged water MUST be rejected
                Physics.MockRayPoint = new Vector3(14.5f, 7.50f, 10f);
                Physics.MockRayNormal = new Vector3(-0.87178f, 0.49f, 0f);
                Assert(!AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(14.5f, 5.0f, 10f), 8.0f, 16.0f, 0.50f, 2.0f, false, 0f, out _),
                    "Unwalkable normal.y=0.49 cliff should be rejected!");

                Physics.MockRayNormal = Vector3.up;
                Physics.MockRayPoint = new Vector3(14.5f, 5.10f, 10f);
                Assert(!AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(14.5f, 5.0f, 10f), 8.0f, 16.0f, 0.50f, 2.0f, true, 5.0f, out _),
                    "Probe below waterLevel + 0.15f should be rejected!");

                // 9f. Solid BoxCollider interior (bit 1) AND InvisibleTrepassableWall box (bit 2) MUST be rejected even on TerrainCollider at dy=0
                Physics.MockForceCapsuleBlocked = true;
                Physics.MockObstacleLayerBit = 1;
                Physics.MockRayPoint = new Vector3(14.5f, 5.0f, 10f);
                Assert(!AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(14.5f, 5.0f, 10f), 8.0f, 16.0f, 0.50f, 2.0f, false, 0f, out _),
                    "Solid BoxCollider interior over TerrainCollider was not rejected by CheckCapsule!");

                Physics.MockObstacleLayerBit = 2; // InvisibleTrepassableWall bit inside GetMovementObstacleMask()
                Assert(!AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(14.5f, 5.0f, 10f), 8.0f, 16.0f, 0.50f, 2.0f, false, 0f, out _),
                    "InvisibleTrepassableWall sandbag/fence interior was not rejected by CheckCapsule!");

                // 9g. Null collider with |dy| > 2.0f and Raycast miss MUST be rejected
                Physics.MockForceCapsuleBlocked = false;
                Physics.MockRayCollider = null;
                Physics.MockRayPoint = new Vector3(14.5f, 7.5f, 10f);
                Assert(!AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(14.5f, 5.0f, 10f), 8.0f, 16.0f, 0.50f, 2.0f, false, 0f, out _),
                    "Null collider with |dy| > 2.0f should be rejected!");

                Physics.MockRayHitValid = false;
                Assert(!AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(14.5f, 5.0f, 10f), 8.0f, 16.0f, 0.50f, 2.0f, false, 0f, out _),
                    "Raycast miss should be rejected!");

                // 9h. Overhead 2nd-floor / pitched-roof / bridge-deck sub-slab fallback & sealed-building exterior-wall rejection!
                Physics.MockRayHitValid = true;
                Physics.MockRayCollider = nonTerrainCol;
                Physics.MockRayPoint = new Vector3(14.5f, 8.60f, 10f); // +3.60m overhead pitched roof / 2nd floor
                Physics.MockRayNormal = new Vector3(-0.90f, 0.435f, 0f).normalized; // steep 64 deg gable roof above 1st-floor hall!
                Physics.MockSubFloorValid = true;
                Physics.MockSubFloorCollider = nonTerrainCol;
                Physics.MockSubFloorPoint = new Vector3(14.5f, 5.05f, 10f); // walkable 1st floor underneath
                Physics.MockSubFloorNormal = Vector3.up;
                Physics.MockLinecastBlocked = false;
                Assert(AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(14.5f, 5.0f, 10f), 8.0f, 16.0f, 0.50f, 2.0f, false, 0f, out Vector3 indoorSnap) &&
                       Mathf.Abs(indoorSnap.y - 5.05f) < 1e-4f,
                    "Indoor 1st-floor / under-bridge ground beneath +3.60m pitched roof failed to snap!");

                // Sealed 3-story hollow building (+11.5m roof, TerrainCollider floor inside at y=5.0m, exterior wall blocking +0.60m chord) MUST be rejected!
                Physics.MockRayPoint = new Vector3(20.5f, 16.50f, 10f); // +11.5m 3-story roof caught by liftY=19.0f!
                Physics.MockRayNormal = Vector3.up;
                Physics.MockSubFloorCollider = terrainCol;
                Physics.MockSubFloorPoint = new Vector3(20.5f, 5.0f, 10f);
                Physics.MockLinecastBlocked = true;
                Physics.MockLinecastCollider = nonTerrainCol;
                Physics.MockLinecastNormal = new Vector3(-1f, 0f, 0f);
                Assert(!AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(20.5f, 5.0f, 10f), 19.0f, 38.0f, 0.55f, 2.0f, false, 0f, out _),
                    "Sealed 3-story hollow building (+11.5m roof, exterior wall at +0.60m) was not rejected!");

                // Low crawlspace (< 1.90m headroom beneath overhead slab) MUST be rejected even with clear Linecast!
                Physics.MockLinecastBlocked = false;
                Physics.MockRayPoint = new Vector3(14.5f, 7.10f, 10f); // +2.10m slab
                Physics.MockSubFloorPoint = new Vector3(14.5f, 5.45f, 10f); // only 1.65m headroom!
                Assert(!AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(14.5f, 5.0f, 10f), 8.0f, 16.0f, 0.50f, 2.0f, false, 0f, out _),
                    "Low 1.65m crawlspace beneath overhead slab should be rejected!");
                Physics.MockSubFloorValid = false;

                // 9i. Vehicle hull/fender colliders (+1.15m within |dy| <= 2.0m) MUST be rejected without sub-floor >= 1.90m!
                var vehicleCol = new Collider { IsTerrain = false, IsVehicle = true };
                Physics.MockRayHitValid = true;
                Physics.MockRayCollider = vehicleCol;
                Physics.MockRayNormal = Vector3.up;
                Physics.MockRayPoint = new Vector3(14.5f, 6.15f, 10f); // +1.15m tank engine deck
                Physics.MockSubFloorValid = false;
                Assert(!AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(14.5f, 5.0f, 10f), 8.0f, 16.0f, 0.50f, 2.0f, false, 0f, out _),
                    "Tank/vehicle hull deck (+1.15m) with no sub-floor should be rejected!");

                // Vehicle deck +1.15m over y=5.0m ground (only 1.15m clearance < 1.90m) MUST be rejected!
                Physics.MockSubFloorValid = true;
                Physics.MockSubFloorCollider = terrainCol;
                Physics.MockSubFloorNormal = Vector3.up;
                Physics.MockSubFloorPoint = new Vector3(14.5f, 5.0f, 10f);
                Assert(!AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(14.5f, 5.0f, 10f), 8.0f, 16.0f, 0.50f, 2.0f, false, 0f, out _),
                    "Vehicle chassis overhang with only 1.15m clearance (< 1.90m) should be rejected!");

                // High vehicle deck >= 1.90m above non-vehicle ground (e.g. tank crossing high trestle) snaps cleanly below!
                Physics.MockRayPoint = new Vector3(14.5f, 7.50f, 10f); // 2.50m above y=5.0m non-vehicle deck
                Physics.MockSubFloorCollider = nonTerrainCol;
                Physics.MockSubFloorPoint = new Vector3(14.5f, 5.05f, 10f);
                Assert(AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(14.5f, 5.0f, 10f), 8.0f, 16.0f, 0.50f, 2.0f, false, 0f, out Vector3 underVehSnap) &&
                       Mathf.Abs(underVehSnap.y - 5.05f) < 1e-4f,
                    "Walkable ground with 2.45m clearance beneath high vehicle overhang should snap cleanly!");

                // Sub-floor that is ALSO a vehicle collider MUST still be rejected!
                Physics.MockSubFloorCollider = vehicleCol;
                Assert(!AiCore.TrySnapWalkableGroundProbe(myFeet, new Vector3(14.5f, 5.0f, 10f), 8.0f, 16.0f, 0.50f, 2.0f, false, 0f, out _),
                    "Secondary vehicle collider beneath primary vehicle collider should be rejected!");
                Physics.MockSubFloorValid = false;
            }

            // 10. BoundWithOverwatch LOF & Unpinned-Suppression Parity Regressions:
            {
                var bndBoard = new VeteranAndTacticsPatches.SquadBlackboard
                {
                    AliveMemberCount = 5,
                    HasActiveThreat = true,
                    ThreatPosition = new Vector3(0f, 0f, 45f),
                    NetDirective = VeteranAndTacticsPatches.SquadNetDirective.Hammer_WideFlankEnvelopment,
                    HasActiveObjectiveTask = true
                };

                // 10a. Visible clear shot (CanFireAtVisibleEnemy) must NEVER select BoundWithOverwatch (which executor rejects!)
                var wsClearShot = VeteranAndTacticsPatches.GoapStateBits.SquadHasActiveThreat |
                                  VeteranAndTacticsPatches.GoapStateBits.AllyPinningThreat |
                                  VeteranAndTacticsPatches.GoapStateBits.CanFireAtVisibleEnemy |
                                  VeteranAndTacticsPatches.GoapStateBits.HasVisibleEnemy |
                                  VeteranAndTacticsPatches.GoapStateBits.InCover;
                var planClearShot = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    wsClearShot, new IntPtr(9501), "Germany_axis", true, false, false, ref bndBoard,
                    VeteranAndTacticsPatches.SoldierPersonality.Assaulter_Shock, 0.4f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                Assert(planClearShot.Step0 != VeteranAndTacticsPatches.GoapActionId.BoundWithOverwatch,
                    $"Clear visible LOF shock trooper must not pick BoundWithOverwatch (got {planClearShot.Step0} -> {planClearShot.Step1})!");

                // 10b. Suppressed in cover with unpinned threat + blind-suppress weapon must SuppressThreatPosition (TargetPinned) first
                var wsSuppressedPin = VeteranAndTacticsPatches.GoapStateBits.SquadHasActiveThreat |
                                      VeteranAndTacticsPatches.GoapStateBits.IsSuppressed |
                                      VeteranAndTacticsPatches.GoapStateBits.InCover |
                                      VeteranAndTacticsPatches.GoapStateBits.CanBlindSuppressThreat;
                var planSuppressedPin = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    wsSuppressedPin, new IntPtr(9502), "UnitedStates_allies", true, false, false, ref bndBoard,
                    VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.3f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                Assert(planSuppressedPin.Step0 == VeteranAndTacticsPatches.GoapActionId.SuppressThreatPosition &&
                       planSuppressedPin.LastGoalBit == VeteranAndTacticsPatches.GoapStateBits.TargetPinned,
                    $"Suppressed unpinned soldier with CanBlindSuppressThreat expected SuppressThreatPosition/TargetPinned (got {planSuppressedPin.Step0} / {planSuppressedPin.LastGoalBit})!");

                // 10c. Suppressed in cover with smoke grenade: 5-man rifleman chains ThrowSmokeScreen -> BoundWithOverwatch,
                //      5-man medic throws 1-step ThrowSmokeScreen toward SquadSupported, and 2-man skirmisher chains ThrowSmokeScreen -> BoundWithOverwatch
                var wsSmokePush = VeteranAndTacticsPatches.GoapStateBits.SquadHasActiveThreat |
                                  VeteranAndTacticsPatches.GoapStateBits.IsSuppressed |
                                  VeteranAndTacticsPatches.GoapStateBits.InCover |
                                  VeteranAndTacticsPatches.GoapStateBits.HasSmokeGrenade;
                var planSmoke5 = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    wsSmokePush, new IntPtr(9503), "England_allies", true, false, false, ref bndBoard,
                    VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.3f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                Assert(planSmoke5.Step0 == VeteranAndTacticsPatches.GoapActionId.ThrowSmokeScreen &&
                       planSmoke5.Step1 == VeteranAndTacticsPatches.GoapActionId.BoundWithOverwatch,
                    $"Suppressed unpinned 5-man soldier with Smoke expected ThrowSmokeScreen -> BoundWithOverwatch (got {planSmoke5.Step0} -> {planSmoke5.Step1})!");

                var planSmokeMedic = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    wsSmokePush, new IntPtr(9505), "England_allies", true, true, false, ref bndBoard,
                    VeteranAndTacticsPatches.SoldierPersonality.Medic_Combat, 0.3f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                Assert(planSmokeMedic.Step0 == VeteranAndTacticsPatches.GoapActionId.ThrowSmokeScreen &&
                       planSmokeMedic.LastGoalBit == VeteranAndTacticsPatches.GoapStateBits.SquadSupported,
                    $"Suppressed unpinned 5-man Medic with Smoke expected ThrowSmokeScreen/SquadSupported (got {planSmokeMedic.Step0} / {planSmokeMedic.LastGoalBit})!");

                bndBoard.AliveMemberCount = 2;
                var planSmoke2 = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    wsSmokePush, new IntPtr(9504), "England_allies", true, false, false, ref bndBoard,
                    VeteranAndTacticsPatches.SoldierPersonality.Assaulter_Skirmisher, 0.3f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                Assert(planSmoke2.Step0 == VeteranAndTacticsPatches.GoapActionId.ThrowSmokeScreen &&
                       planSmoke2.Step1 == VeteranAndTacticsPatches.GoapActionId.BoundWithOverwatch,
                    $"Suppressed 2-man skirmisher with Smoke expected ThrowSmokeScreen -> BoundWithOverwatch (got {planSmoke2.Step0} -> {planSmoke2.Step1})!");
            }

            // 11. Scavenging Survival Instincts & Instant Danger Preemption Regressions:
            {
                var scavBoard = new VeteranAndTacticsPatches.SquadBlackboard
                {
                    AliveMemberCount = 5,
                    HasActiveThreat = true,
                    ThreatPosition = new Vector3(0f, 0f, 40f),
                    NetDirective = VeteranAndTacticsPatches.SquadNetDirective.Connected_StandardObjective
                };

                // 11a. Suppressed or near fresh casualty out of cover with NeedAmmoOrMedScavenge MUST pick SeekEmergencyCover (NEVER ScavengeSupplyFromCorpse!)
                var wsSuppOpenScav = VeteranAndTacticsPatches.GoapStateBits.NeedAmmoOrMedScavenge |
                                     VeteranAndTacticsPatches.GoapStateBits.IsSuppressed |
                                     VeteranAndTacticsPatches.GoapStateBits.SquadHasActiveThreat;
                var planSuppOpen = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    wsSuppOpenScav, new IntPtr(9601), "Finland", true, false, false, ref scavBoard,
                    VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.5f, 100f, VeteranAndTacticsPatches.GoapActionId.ScavengeSupplyFromCorpse);
                Assert(planSuppOpen.Step0 == VeteranAndTacticsPatches.GoapActionId.SeekEmergencyCover &&
                       planSuppOpen.Step1 != VeteranAndTacticsPatches.GoapActionId.ScavengeSupplyFromCorpse,
                    $"Suppressed open soldier with NeedAmmoOrMedScavenge picked {planSuppOpen.Step0} -> {planSuppOpen.Step1} instead of SeekEmergencyCover!");

                var wsCasualtyOpenScav = VeteranAndTacticsPatches.GoapStateBits.NeedAmmoOrMedScavenge |
                                         VeteranAndTacticsPatches.GoapStateBits.NearFreshCasualty |
                                         VeteranAndTacticsPatches.GoapStateBits.SquadHasActiveThreat;
                var planCasOpen = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    wsCasualtyOpenScav, new IntPtr(9602), "Finland", true, false, false, ref scavBoard,
                    VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.5f, 100f, VeteranAndTacticsPatches.GoapActionId.ScavengeSupplyFromCorpse);
                Assert(planCasOpen.Step0 == VeteranAndTacticsPatches.GoapActionId.SeekEmergencyCover,
                    $"Soldier near fresh casualty with NeedAmmoOrMedScavenge picked {planCasOpen.Step0} instead of SeekEmergencyCover!");

                // 11b. Visible enemy / clear LOF with NeedAmmoOrMedScavenge MUST fight (DirectFire / HoldOverwatch), NEVER scavenge!
                var wsVisEnemyScav = VeteranAndTacticsPatches.GoapStateBits.NeedAmmoOrMedScavenge |
                                     VeteranAndTacticsPatches.GoapStateBits.HasVisibleEnemy |
                                     VeteranAndTacticsPatches.GoapStateBits.CanFireAtVisibleEnemy |
                                     VeteranAndTacticsPatches.GoapStateBits.InCover |
                                     VeteranAndTacticsPatches.GoapStateBits.SquadHasActiveThreat;
                var planVisEnemy = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    wsVisEnemyScav, new IntPtr(9603), "Germany", true, false, false, ref scavBoard,
                    VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.4f, 100f, VeteranAndTacticsPatches.GoapActionId.ScavengeSupplyFromCorpse);
                Assert(planVisEnemy.Step0 == VeteranAndTacticsPatches.GoapActionId.DirectFireEngagement,
                    $"Soldier with visible enemy + NeedAmmoOrMedScavenge picked {planVisEnemy.Step0} instead of DirectFireEngagement!");

                // 11c. Bleeding without bandage in the open MUST SeekEmergencyCover first (NEVER run to a corpse while bleeding in the open!)
                var wsBleedNoBandageOpen = VeteranAndTacticsPatches.GoapStateBits.IsBleeding |
                                           VeteranAndTacticsPatches.GoapStateBits.NeedAmmoOrMedScavenge;
                var planBleedOpen = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    wsBleedNoBandageOpen, new IntPtr(9604), "UnitedStates", true, false, false, ref scavBoard,
                    VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.3f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                Assert(planBleedOpen.Step0 == VeteranAndTacticsPatches.GoapActionId.SeekEmergencyCover,
                    $"Bleeding soldier without bandage in open picked {planBleedOpen.Step0} instead of SeekEmergencyCover!");

                // 11d. Bleeding without bandage already safe in cover chains ScavengeSupplyFromCorpse -> BandageInCover when calm
                var wsBleedNoBandageCover = VeteranAndTacticsPatches.GoapStateBits.IsBleeding |
                                            VeteranAndTacticsPatches.GoapStateBits.InCover |
                                            VeteranAndTacticsPatches.GoapStateBits.NeedAmmoOrMedScavenge;
                var planBleedCover = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    wsBleedNoBandageCover, new IntPtr(9605), "UnitedStates", true, false, false, ref scavBoard,
                    VeteranAndTacticsPatches.SoldierPersonality.Rifleman_Regular, 0.3f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                Assert(planBleedCover.Step0 == VeteranAndTacticsPatches.GoapActionId.ScavengeSupplyFromCorpse &&
                       planBleedCover.Step1 == VeteranAndTacticsPatches.GoapActionId.BandageInCover,
                    $"Safe bleeding soldier in cover without bandage expected ScavengeSupplyFromCorpse -> BandageInCover, got {planBleedCover.Step0} -> {planBleedCover.Step1}!");

                // 11e. IsGoapActionCurrentlyLegal immediately revokes ScavengeSupplyFromCorpse on any danger bit
                Assert(VeteranAndTacticsPatches.IsGoapActionCurrentlyLegal(
                    VeteranAndTacticsPatches.GoapActionId.ScavengeSupplyFromCorpse,
                    VeteranAndTacticsPatches.GoapStateBits.NeedAmmoOrMedScavenge | VeteranAndTacticsPatches.GoapStateBits.InCover,
                    new IntPtr(9606), ref scavBoard, 100f),
                    "Calm ScavengeSupplyFromCorpse should be legal!");
                Assert(!VeteranAndTacticsPatches.IsGoapActionCurrentlyLegal(
                    VeteranAndTacticsPatches.GoapActionId.ScavengeSupplyFromCorpse,
                    VeteranAndTacticsPatches.GoapStateBits.NeedAmmoOrMedScavenge | VeteranAndTacticsPatches.GoapStateBits.IsSuppressed,
                    new IntPtr(9606), ref scavBoard, 100f),
                    "Suppressed ScavengeSupplyFromCorpse MUST be illegal (instant preemption)!");
                Assert(!VeteranAndTacticsPatches.IsGoapActionCurrentlyLegal(
                    VeteranAndTacticsPatches.GoapActionId.ScavengeSupplyFromCorpse,
                    VeteranAndTacticsPatches.GoapStateBits.NeedAmmoOrMedScavenge | VeteranAndTacticsPatches.GoapStateBits.HasVisibleEnemy,
                    new IntPtr(9606), ref scavBoard, 100f),
                    "Visible-enemy ScavengeSupplyFromCorpse MUST be illegal (instant preemption)!");
            }

            // 12. Comrade Liberation Priority, Bailed-Crew Evacuation & CQB Weapon-Reach Invariants:
            {
                var all21Factions = new[]
                {
                    "Germany_axis", "Finland_axis", "UnitedStates_allies", "UnitedStates_Afrikan_allies",
                    "England_allies", "Canada_allies", "Australia_allies", "NewZealand_allies",
                    "India_allies", "Gurkha_allies", "France_allies", "Poland_allies",
                    "Greece_allies", "Ussr_allies", "Mongolia_allies", "China_allies",
                    "Japan_axis", "Manchukuo_axis", "Italy_axis", "Romania_axis", "Hungary_axis"
                };

                var libBoard = new VeteranAndTacticsPatches.SquadBlackboard
                {
                    AliveMemberCount = 5,
                    HasActiveThreat = true,
                    ThreatPosition = new Vector3(0f, 0f, 45f),
                    NetDirective = VeteranAndTacticsPatches.SquadNetDirective.Connected_StandardObjective
                };

                var wsAloneLib = VeteranAndTacticsPatches.GoapStateBits.ComradeNeedsLiberation |
                                 VeteranAndTacticsPatches.GoapStateBits.IsEntirelyAlone;
                var wsThreatLib = VeteranAndTacticsPatches.GoapStateBits.ComradeNeedsLiberation |
                                  VeteranAndTacticsPatches.GoapStateBits.SquadHasActiveThreat |
                                  VeteranAndTacticsPatches.GoapStateBits.InCover |
                                  VeteranAndTacticsPatches.GoapStateBits.CanBlindSuppressThreat |
                                  VeteranAndTacticsPatches.GoapStateBits.HasSmokeGrenade;

                int libSweepId = 0;
                foreach (var fac in all21Factions)
                foreach (var pers in personalities)
                {
                    if (pers == VeteranAndTacticsPatches.SoldierPersonality.None)
                        continue;

                    bool isMedic = pers == VeteranAndTacticsPatches.SoldierPersonality.Medic_Heroic ||
                                   pers == VeteranAndTacticsPatches.SoldierPersonality.Medic_Combat ||
                                   pers == VeteranAndTacticsPatches.SoldierPersonality.Medic_Triage;

                    foreach (bool isBailedCrew in new[] { false, true })
                    {
                        var pAlone = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                            wsAloneLib, new IntPtr(9700 + libSweepId++), fac, true, isMedic, isBailedCrew, ref libBoard, pers, 0.5f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                        Assert(pAlone.Step0 == VeteranAndTacticsPatches.GoapActionId.RescueSurrenderedComrade &&
                               pAlone.LastGoalBit == VeteranAndTacticsPatches.GoapStateBits.SquadSupported,
                            $"Lone comrade liberation failed for {fac}/{pers}/crew={isBailedCrew}: got {pAlone.Step0} ({pAlone.LastGoalBit})");
                    }

                    var pThreat = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                        wsThreatLib, new IntPtr(9700 + libSweepId++), fac, true, isMedic, false, ref libBoard, pers, 0.5f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                    Assert(pThreat.Step0 == VeteranAndTacticsPatches.GoapActionId.RescueSurrenderedComrade &&
                           pThreat.LastGoalBit == VeteranAndTacticsPatches.GoapStateBits.SquadSupported,
                        $"Unengaged comrade liberation with smoke/suppress available failed for {fac}/{pers}: got {pThreat.Step0} ({pThreat.LastGoalBit})");
                }

                // 12b. Liberation instant danger preemption across every bit of LiberateForbiddenMask
                Assert(VeteranAndTacticsPatches.IsGoapActionCurrentlyLegal(
                    VeteranAndTacticsPatches.GoapActionId.RescueSurrenderedComrade,
                    VeteranAndTacticsPatches.GoapStateBits.ComradeNeedsLiberation | VeteranAndTacticsPatches.GoapStateBits.InCover,
                    new IntPtr(9801), ref libBoard, 100f),
                    "Calm RescueSurrenderedComrade should be legal!");

                var libForbiddenBits = new[]
                {
                    VeteranAndTacticsPatches.GoapStateBits.IsBleeding,
                    VeteranAndTacticsPatches.GoapStateBits.IsSuppressed,
                    VeteranAndTacticsPatches.GoapStateBits.IsHeavilySuppressed,
                    VeteranAndTacticsPatches.GoapStateBits.NearFreshCasualty,
                    VeteranAndTacticsPatches.GoapStateBits.HasVisibleEnemy,
                    VeteranAndTacticsPatches.GoapStateBits.CanFireAtVisibleEnemy,
                    VeteranAndTacticsPatches.GoapStateBits.ExposedSideOfWall,
                    VeteranAndTacticsPatches.GoapStateBits.OverwhelmedNeedFallback,
                    VeteranAndTacticsPatches.GoapStateBits.InPointBlankRange
                };
                foreach (var forbidBit in libForbiddenBits)
                {
                    Assert(!VeteranAndTacticsPatches.IsGoapActionCurrentlyLegal(
                        VeteranAndTacticsPatches.GoapActionId.RescueSurrenderedComrade,
                        VeteranAndTacticsPatches.GoapStateBits.ComradeNeedsLiberation | forbidBit,
                        new IntPtr(9801), ref libBoard, 100f),
                        $"RescueSurrenderedComrade MUST be illegal when {forbidBit} is set!");
                }

                // 12c. Bailed-crew lone-wolf evacuation selects LoneWolfLinkUpOrAmbush
                var crewBoard = new VeteranAndTacticsPatches.SquadBlackboard
                {
                    AliveMemberCount = 2,
                    HasActiveThreat = true,
                    ThreatPosition = new Vector3(0f, 0f, 35f),
                    NetDirective = VeteranAndTacticsPatches.SquadNetDirective.Disconnected_Autonomous
                };
                var pCrewEvac = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                    VeteranAndTacticsPatches.GoapStateBits.IsEntirelyAlone | VeteranAndTacticsPatches.GoapStateBits.SquadHasActiveThreat | VeteranAndTacticsPatches.GoapStateBits.InCover,
                    new IntPtr(9802), "Germany_axis", true, false, true, ref crewBoard, VeteranAndTacticsPatches.SoldierPersonality.TankCrew_Brawler, 0.4f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                Assert(pCrewEvac.Step0 == VeteranAndTacticsPatches.GoapActionId.LoneWolfLinkUpOrAmbush &&
                       pCrewEvac.LastGoalBit == VeteranAndTacticsPatches.GoapStateBits.SquadSupported,
                    $"Bailed crew evac expected LoneWolfLinkUpOrAmbush, got {pCrewEvac.Step0} ({pCrewEvac.LastGoalBit})!");

                var wsCrewDistracted =
                    VeteranAndTacticsPatches.GoapStateBits.IsEntirelyAlone |
                    VeteranAndTacticsPatches.GoapStateBits.SquadHasActiveThreat |
                    VeteranAndTacticsPatches.GoapStateBits.InCover |
                    VeteranAndTacticsPatches.GoapStateBits.TargetLostBehindCover |
                    VeteranAndTacticsPatches.GoapStateBits.AllyPinningThreat |
                    VeteranAndTacticsPatches.GoapStateBits.HasFragGrenade |
                    VeteranAndTacticsPatches.GoapStateBits.HasSmokeGrenade |
                    VeteranAndTacticsPatches.GoapStateBits.FlankCoverAvailable |
                    VeteranAndTacticsPatches.GoapStateBits.IsMachineGunner |
                    VeteranAndTacticsPatches.GoapStateBits.CanBlindSuppressThreat;
                foreach (var crewPers in new[]
                {
                    VeteranAndTacticsPatches.SoldierPersonality.Gunner_Anchor,
                    VeteranAndTacticsPatches.SoldierPersonality.Assaulter_Breacher,
                    VeteranAndTacticsPatches.SoldierPersonality.AT_Demolitionist
                })
                {
                    var pCrewDistr = VeteranAndTacticsPatches.PlanBestGoapActionChainCore(
                        wsCrewDistracted, new IntPtr(9803), "Germany_axis", true, false, true, ref crewBoard, crewPers, 0.4f, 100f, VeteranAndTacticsPatches.GoapActionId.None);
                    Assert(pCrewDistr.Step0 == VeteranAndTacticsPatches.GoapActionId.LoneWolfLinkUpOrAmbush &&
                           pCrewDistr.LastGoalBit == VeteranAndTacticsPatches.GoapStateBits.SquadSupported,
                        $"Distracted bailed crew ({crewPers}) expected LoneWolfLinkUpOrAmbush, got {pCrewDistr.Step0} ({pCrewDistr.LastGoalBit})!");
                }

                // 12d. CQB Weapon-Reach & Standoff Physics Invariants (parsed live from CloseCombatAndSupport.cs)
                const float bayonetStandoff = ${cqbBayonetStandoff}f;
                const float knifeStandoff = ${cqbKnifeStandoff}f;
                const float unbayonetedStandoff = ${cqbUnbayStandoff}f;
                const float unbayonetedStrikeRange = ${cqbUnbayStrike}f;
                const float meleeWeaponStrikeRange = ${cqbKnifeStrike}f;
                const float pistolCeaseFireRange = ${cqbPistolCeaseFire}f;
                const float patchedAiCapsuleDist = ${cqbPatchedCapsule}f;
                const float bayonetStrikeRange = ${cqbBayonetStrike}f;

                Assert(pistolCeaseFireRange < unbayonetedStandoff,
                    $"Loaded pistol ceaseFireRange ({pistolCeaseFireRange}) must stay below unbayonetedStandoff ({unbayonetedStandoff})!");
                Assert(unbayonetedStandoff < unbayonetedStrikeRange && unbayonetedStrikeRange <= patchedAiCapsuleDist + 0.30f,
                    $"Unbayoneted/pistol-whip strike ({unbayonetedStrikeRange}) must sit between standoff ({unbayonetedStandoff}) and swing reach ({patchedAiCapsuleDist + 0.30f})!");
                Assert(knifeStandoff < meleeWeaponStrikeRange && meleeWeaponStrikeRange <= patchedAiCapsuleDist + 0.30f + 0.18f,
                    $"Knife strike ({meleeWeaponStrikeRange}) must sit between knife standoff ({knifeStandoff}) and extended reach ({patchedAiCapsuleDist + 0.48f})!");
                Assert(bayonetStandoff <= 1.65f && bayonetStandoff < bayonetStrikeRange,
                    $"Bayonet standoff ({bayonetStandoff}) must sit inside 1.65m native reach and below strike trigger ({bayonetStrikeRange})!");
            }

            Console.WriteLine($"  -> Verified GOAP Planner, Acoustic Ring & Ground Probe Suite: {_passed} assertions passed across {sweepCount} combinatoric states ({holdOverwatchWins} HoldOverwatch wins, 0 failures)");
            return 0;
        }
    }
}
"@

$tmpCs  = Join-Path $buildDir "VerifyGoapPlanner_Gen.cs"
$tmpDll = Join-Path $buildDir "VerifyGoapPlanner_Gen.dll"
Set-Content -Path $tmpCs -Value $harnessCs -Encoding UTF8

$sdkLine = (& dotnet --list-sdks | Sort-Object -Descending | Select-Object -First 1)
if ($sdkLine -match '^(\S+)\s+\[(.+)\]$') {
    $csc = Join-Path $Matches[2] "$($Matches[1])\Roslyn\bincore\csc.dll"
} else {
    $csc = Join-Path $workspace "tools\roslyn\extracted\tasks\netcore\bincore\csc.dll"
}
$cscRuntimeConfig = Join-Path (Split-Path $csc -Parent) "csc.runtimeconfig.json"

$bepRoot = if (Test-Path (Join-Path $workspace "tools\bepinex\extracted\dotnet")) {
    Join-Path $workspace "tools\bepinex\extracted"
} else {
    "D:\SteamLibrary\steamapps\common\Easy Red 2"
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
        $tmpCs
    if ($LASTEXITCODE -ne 0) { throw "Failed to compile VerifyGoapPlanner_Gen.cs" }

    & dotnet exec --runtimeconfig $cscRuntimeConfig $tmpDll
    if ($LASTEXITCODE -ne 0) { throw "VerifyGoapPlanner_Gen failed!" }
}
finally {
    Remove-Item $tmpCs, $tmpDll -Force -ErrorAction SilentlyContinue
}
