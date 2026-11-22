$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$visFile  = Join-Path $workspace "src\EasyRed2AI\Modules\Vision\VisionRangeAndCamo.cs"
$wqFile   = Join-Path $workspace "src\EasyRed2AI\Core\AiCore.WorldQuery.cs"
$goapFile = Join-Path $workspace "src\EasyRed2AI\Modules\Goap\GoapDefinitionsAndPlanner.cs"
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

$visLines = Get-Content $visFile
$visPartA = Slice-LinesBetween $visLines '^\s*private struct RecentGunfireEntry' '_explosionSeqCounter = 1;' 0
$visPartB = Slice-LinesBetween $visLines '^\s*private static void RecordAcousticEventInternal' 'public static bool HasFiredRecently' -2

$wqLines  = Get-Content $wqFile
$wqSnapProbe = Slice-LinesBetween $wqLines '^\s*public static bool TrySnapWalkableGroundProbe\(' 'snappedGroundPos = gHit\.point;\s*$' 2

$goapLines = Get-Content $goapFile
$goapPartA = Slice-LinesBetween $goapLines '^\s*internal enum SquadNetDirective' 'public Vector3 MoveOverrideDest;' 1
$goapPartB = Slice-LinesBetween $goapLines '^\s*internal struct BotMicroVariance' '_factionProfileCache\[faction\] = p;' 2

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
$cqbPatchedCapsule    = Extract-CqbFloat $cqbText '__instance\.mountedBlade\s*==\s*null[\s\S]*?float minReach\s*=\s*([0-9]+\.[0-9]+)f' 'patchedAiCapsuleDist'
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
        public static ConfigBool EnableBattleAtmosphereDirectives = new ConfigBool { Value = true };
    }

    public static class AiCore
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

        public struct ScavengeStub { public bool IsAtWeaponPickup; public bool IsUrgentPrimaryPickup; }
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
            public string LocationLower;
            public bool IsNightStart;
            public BattleAtmosphereArchetype Atmosphere;
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
$goapPartA
$goapPartB
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
            {
                // 3. CQB Weapon-Reach & Standoff Physics Invariants (parsed live from CloseCombatAndSupport.cs)
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
            {
                // 4. 9 Battle Atmosphere Archetype Profile Modifiers (GetFactionCommandProfile)
                int gen = 100;
                AiCore.CurrentTheater = new AiCore.BattleTheaterSnapshot { BattleDataPtr = new IntPtr(gen), Map = Gamemap.Stalingrad, InvadersFactionId = "Germany_axis", DefendersFactionId = "Ussr_allies", Atmosphere = AiCore.BattleAtmosphereArchetype.None };
                AiCore.BattleTheaterGeneration = ++gen;
                var ussrBase = VeteranAndTacticsPatches.GetFactionCommandProfile("Ussr_allies");
                var gerBase  = VeteranAndTacticsPatches.GetFactionCommandProfile("Germany_axis");

                // Order No. 227 boosts USSR SL bravery aura & surrender grit
                AiCore.CurrentTheater.Atmosphere = AiCore.BattleAtmosphereArchetype.Order227NotOneStepBack;
                AiCore.BattleTheaterGeneration = ++gen;
                var ussr227 = VeteranAndTacticsPatches.GetFactionCommandProfile("Ussr_allies");
                Assert(ussr227.LeaderAuraMult > ussrBase.LeaderAuraMult + 0.25f && ussr227.SurrenderSuppDelta >= 14f,
                    $"Order No. 227 failed to boost USSR LeaderAuraMult ({ussr227.LeaderAuraMult}) / SurrenderSuppDelta ({ussr227.SurrenderSuppDelta})!");

                // Kessel Pocket boosts encircled defender scavenge & garrison hold
                AiCore.CurrentTheater = new AiCore.BattleTheaterSnapshot { BattleDataPtr = new IntPtr(++gen), Map = Gamemap.Stalingrad, Year = 1943, InvadersFactionId = "Ussr_allies", DefendersFactionId = "Germany_axis", Atmosphere = AiCore.BattleAtmosphereArchetype.KesselEncircledPocket };
                AiCore.BattleTheaterGeneration = gen;
                var gerKessel = VeteranAndTacticsPatches.GetFactionCommandProfile("Germany_axis");
                Assert(gerKessel.ScavengeAndResupplyMult < gerBase.ScavengeAndResupplyMult && gerKessel.GarrisonSquadRatio > gerBase.GarrisonSquadRatio,
                    $"Kessel Pocket failed to boost defender scavenge ({gerKessel.ScavengeAndResupplyMult} vs {gerBase.ScavengeAndResupplyMult}) / garrison ({gerKessel.GarrisonSquadRatio} vs {gerBase.GarrisonSquadRatio})!");

                // Amphibious Beachhead lowers invader smoke cost & boosts defender emplacement staffing
                AiCore.CurrentTheater = new AiCore.BattleTheaterSnapshot { BattleDataPtr = new IntPtr(gen), Map = Gamemap.Normandy, InvadersFactionId = "UnitedStates_allies", DefendersFactionId = "Germany_axis", IsAmphibiousBeachMap = false, Atmosphere = AiCore.BattleAtmosphereArchetype.None };
                AiCore.BattleTheaterGeneration = ++gen;
                var usBase = VeteranAndTacticsPatches.GetFactionCommandProfile("UnitedStates_allies");
                AiCore.CurrentTheater.Atmosphere = AiCore.BattleAtmosphereArchetype.AmphibiousBeachhead;
                AiCore.BattleTheaterGeneration = ++gen;
                var usBeach = VeteranAndTacticsPatches.GetFactionCommandProfile("UnitedStates_allies");
                var gerBeach = VeteranAndTacticsPatches.GetFactionCommandProfile("Germany_axis");
                Assert(usBeach.SmokeCostMult < usBase.SmokeCostMult && gerBeach.EmplacementSearchMult > gerBase.EmplacementSearchMult,
                    $"Amphibious Beachhead failed: usSmoke={usBeach.SmokeCostMult} vs {usBase.SmokeCostMult}, gerEmp={gerBeach.EmplacementSearchMult}!");

                // Siege of Bastogne boosts defender aid/drag, garrison, and officer aura
                AiCore.CurrentTheater = new AiCore.BattleTheaterSnapshot { BattleDataPtr = new IntPtr(gen), Map = Gamemap.Ardennes, InvadersFactionId = "Germany_axis", DefendersFactionId = "UnitedStates_allies", Atmosphere = AiCore.BattleAtmosphereArchetype.SiegeOfBastogne };
                AiCore.BattleTheaterGeneration = ++gen;
                var usBastogne = VeteranAndTacticsPatches.GetFactionCommandProfile("UnitedStates_allies");
                Assert(usBastogne.ComradeAidAndDragMult < usBase.ComradeAidAndDragMult && usBastogne.LeaderAuraMult > 1.15f && usBastogne.GarrisonSquadRatio > usBase.GarrisonSquadRatio,
                    $"Siege of Bastogne failed: aid={usBastogne.ComradeAidAndDragMult}, aura={usBastogne.LeaderAuraMult}, gar={usBastogne.GarrisonSquadRatio}!");

                // Urban Fortress, Pacific Bunker, Mountain Ridge, Blitzkrieg & Night Airborne checks
                AiCore.CurrentTheater = new AiCore.BattleTheaterSnapshot { BattleDataPtr = new IntPtr(gen), Map = Gamemap.Budapest, InvadersFactionId = "Ussr_allies", DefendersFactionId = "Hungary_axis", Atmosphere = AiCore.BattleAtmosphereArchetype.UrbanFortress };
                AiCore.BattleTheaterGeneration = ++gen;
                var hunUrban = VeteranAndTacticsPatches.GetFactionCommandProfile("Hungary_axis");
                Assert(hunUrban.FragFlushCostMult < 0.80f && hunUrban.GarrisonSquadRatio >= 0.30f,
                    $"Urban Fortress failed: frag={hunUrban.FragFlushCostMult}, gar={hunUrban.GarrisonSquadRatio}!");

                AiCore.CurrentTheater = new AiCore.BattleTheaterSnapshot { BattleDataPtr = new IntPtr(gen), Map = Gamemap.Kwajalein, InvadersFactionId = "UnitedStates_allies", DefendersFactionId = "Japan_axis", Atmosphere = AiCore.BattleAtmosphereArchetype.PacificBunkerAndNightGyokusai };
                AiCore.BattleTheaterGeneration = ++gen;
                var jpnPacific = VeteranAndTacticsPatches.GetFactionCommandProfile("Japan_axis");
                Assert(jpnPacific.AllowGyokusaiDesperation && jpnPacific.EmplacementSearchMult > 1.15f && jpnPacific.SurrenderSuppDelta >= 12f,
                    $"Pacific Bunker failed: emp={jpnPacific.EmplacementSearchMult}, sur={jpnPacific.SurrenderSuppDelta}!");

                AiCore.CurrentTheater = new AiCore.BattleTheaterSnapshot { BattleDataPtr = new IntPtr(gen), Map = Gamemap.Cassino, InvadersFactionId = "England_allies", DefendersFactionId = "Germany_axis", Atmosphere = AiCore.BattleAtmosphereArchetype.MountainRidgeGrind };
                AiCore.BattleTheaterGeneration = ++gen;
                var ukBaseCheck = VeteranAndTacticsPatches.GetFactionCommandProfile("England_allies");
                Assert(ukBaseCheck.SmokeCostMult < 0.70f && ukBaseCheck.SuppressCostMult < 0.75f,
                    $"Mountain Ridge failed: smoke={ukBaseCheck.SmokeCostMult}, supp={ukBaseCheck.SuppressCostMult}!");

                AiCore.CurrentTheater = new AiCore.BattleTheaterSnapshot { BattleDataPtr = new IntPtr(gen), Map = Gamemap.Tunisia, InvadersFactionId = "Germany_axis", DefendersFactionId = "UnitedStates_allies", Atmosphere = AiCore.BattleAtmosphereArchetype.BlitzkriegSchwerpunkt };
                AiCore.BattleTheaterGeneration = ++gen;
                var gerBlitz = VeteranAndTacticsPatches.GetFactionCommandProfile("Germany_axis");
                Assert(gerBlitz.AttritionPivotThreshold < gerBase.AttritionPivotThreshold && gerBlitz.FlankMoveCostMult < gerBase.FlankMoveCostMult,
                    $"Blitzkrieg Schwerpunkt failed: pivot={gerBlitz.AttritionPivotThreshold}, flank={gerBlitz.FlankMoveCostMult}!");

                AiCore.CurrentTheater = new AiCore.BattleTheaterSnapshot { BattleDataPtr = new IntPtr(gen), Map = Gamemap.Normandy, InvadersFactionId = "England_allies", DefendersFactionId = "Germany_axis", Atmosphere = AiCore.BattleAtmosphereArchetype.NightAirborneCoupDeMain };
                AiCore.BattleTheaterGeneration = ++gen;
                var ukAirborne = VeteranAndTacticsPatches.GetFactionCommandProfile("England_allies");
                Assert(ukAirborne.AllowDisconnectedLocalFlank && ukAirborne.PreferAmbushWhenAlone && ukAirborne.FlankMoveCostMult < 0.90f,
                    $"Night Airborne failed: localFlank={ukAirborne.AllowDisconnectedLocalFlank}, ambush={ukAirborne.PreferAmbushWhenAlone}!");
            }
            Console.WriteLine($"  -> Verified Acoustic Ring, Ground Probe, CQB & Battle Atmosphere Suite: {_passed} assertions passed (0 failures)");
            return 0;
        }
    }
}
"@

$tmpCs  = Join-Path $buildDir "VerifyPhysicsAndAcoustics_Gen.cs"
$tmpDll = Join-Path $buildDir "VerifyPhysicsAndAcoustics_Gen.dll"
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
    if ($LASTEXITCODE -ne 0) { throw "Failed to compile VerifyPhysicsAndAcoustics_Gen.cs" }

    & dotnet exec --runtimeconfig $cscRuntimeConfig $tmpDll
    if ($LASTEXITCODE -ne 0) { throw "VerifyPhysicsAndAcoustics_Gen failed!" }
}
finally {
    Remove-Item $tmpCs, $tmpDll -Force -ErrorAction SilentlyContinue
}
