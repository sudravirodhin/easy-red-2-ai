$ErrorActionPreference = "Stop"
$workspace = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$aiCoreFile = Join-Path $workspace "src\EasyRed2AI\Core\AiCore.cs"
$visFile  = Join-Path $workspace "src\EasyRed2AI\Modules\Vision\VisionRangeAndCamo.cs"
$wqFile   = Join-Path $workspace "src\EasyRed2AI\Core\AiCore.WorldQuery.cs"
$goapFile = Join-Path $workspace "src\EasyRed2AI\Modules\Goap\GoapDefinitionsAndPlanner.cs"
$balFile  = Join-Path $workspace "src\EasyRed2AI\Modules\ImmersionAndQoL\BallisticsAndArmorRealism.cs"
$turFile  = Join-Path $workspace "src\EasyRed2AI\Modules\MacroAndVehicles\TurretAmmoAndBailout.cs"
$locFile  = Join-Path $workspace "src\EasyRed2AI\Modules\Infantry\LocomotionAndVaulting.cs"
$ffFile   = Join-Path $workspace "src\EasyRed2AI\Modules\Infantry\FriendlyFireAndYield.cs"
$stFile   = Join-Path $workspace "src\EasyRed2AI\Modules\Infantry\StanceAndCoverTactics.cs"
$thmFile  = Join-Path $workspace "src\EasyRed2AI\Modules\Vision\ThreatMemoryAndWatchSectors.cs"
$goapExecFile = Join-Path $workspace "src\EasyRed2AI\Modules\Goap\SoldierGoapExecution.cs"
$vetFile  = Join-Path $workspace "src\EasyRed2AI\Modules\ImmersionAndQoL\VeterancyAndPersonalities.cs"
$gvFile   = Join-Path $workspace "src\EasyRed2AI\Modules\MacroAndVehicles\GroundVehicleTactics.cs"
$caFile   = Join-Path $workspace "src\EasyRed2AI\Modules\MacroAndVehicles\CombinedArmsAndTurrets.cs"
$airFile  = Join-Path $workspace "src\EasyRed2AI\Modules\MacroAndVehicles\AircraftTactics.cs"
$tbFile   = Join-Path $workspace "src\EasyRed2AI\Modules\MacroAndVehicles\TurretAmmoAndBailout.cs"
$cfgHudFile = Join-Path $workspace "src\EasyRed2AI\UI\ConfigMenuAndHud.cs"
$tnFile   = Join-Path $workspace "src\EasyRed2AI\Modules\Goap\TeamNetAndSquadCommand.cs"
$emFile   = Join-Path $workspace "src\EasyRed2AI\Modules\Emergent\EmergentRuntimeBridge.cs"
$pluginFile = Join-Path $workspace "src\EasyRed2AI\Plugin.cs"
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

$aiLines = Get-Content $aiCoreFile
$aiFovPart = Slice-LinesBetween $aiLines '^\s*public static float EvaluateHumanSquintHalfFov\(' '^\s*return Mathf\.Lerp\(Mathf\.Min\(midHalfFov, 64\.0f\)' 1

$visLines = Get-Content $visFile
$visPartA = Slice-LinesBetween $visLines '^\s*private struct RecentGunfireEntry' '_explosionSeqCounter = 1;' 0
$visFootstepRadiusPart = Slice-LinesBetween $visLines '^\s*internal static float ComputeFootstepAudibleRadius\(' '^\s*private static void RecordSoldierFootstep\(' -2
$visPartB = Slice-LinesBetween $visLines '^\s*private static void RecordAcousticEventInternal' 'public static bool HasFiredRecently' -2
$visRangePart = Slice-LinesBetween $visLines '^\s*internal static float ComputeEffectiveSoldierSightRange\(' '^\s*return \(baseDist \+ firingBonus\) \* camoMultiplier' 1
$visThinWallPart = Slice-LinesBetween $visLines '^\s*internal static bool IsObserverBlockedByCloseThinWall\(' '^\s*\[HarmonyPatch\(typeof\(Creature\), nameof\(Creature\.CanSee\)\)\]' -2

$wqLines  = Get-Content $wqFile
$wqEnvPart = Slice-LinesBetween $wqLines '^\s*public static bool RaycastEnvironment\(' '^\s*public static bool HasLowCeilingOverhead\(' -2
$wqIgnoreColPart = Slice-LinesBetween $wqLines '^\s*private static ColliderEnvMeta GetColliderEnvMetaFromHit\(' '^\s*public static Il2CppSystem\.Collections\.Generic\.List' -4
$wqSnapProbe  = Slice-LinesBetween $wqLines '^\s*public static bool TrySnapWalkableGroundProbe\(' 'snappedGroundPos = gHit\.point;\s*$' 2
$wqBarrelPart = Slice-LinesBetween $wqLines '^\s*public static float GetBarrelHeightForPose\(' '^\s*return 1\.24f;' 2
$wqSmokeRiskPart = Slice-LinesBetween $wqLines '^\s*public static bool EvaluateBlindSmokeTraversalRisk\(' '^\s*public static bool IsBlindSmokePathHighRisk\(' -2

$goapLines = Get-Content $goapFile
$goapPartA = Slice-LinesBetween $goapLines '^\s*internal enum SquadNetDirective' 'public Vector3 MoveOverrideDest;' 1
$goapPartB = Slice-LinesBetween $goapLines '^\s*internal struct BotMicroVariance' '_factionProfileCache\[faction\] = p;' 2

$balLines = Get-Content $balFile
$balOverPenPart = Slice-LinesBetween $balLines '^\s*internal static bool TryComputeHumanOverPenetration\(' '^\s*deflectionRad\s*=\s*isHeavyCal' 2
$balArmorModPart = Slice-LinesBetween $balLines '^\s*internal static void EvaluateArmorImpactModifiers\(' '^\s*\[HarmonyPatch\(typeof\(VehicleDamagablePart\)' -2

$turLines = Get-Content $turFile
$turArmorScorePart = Slice-LinesBetween $turLines '^\s*internal static float ScoreTurretShellAgainstArmor\(' '^\s*return effPen;' 1

$gvLines = Get-Content $gvFile
$gvAtSelPart = Slice-LinesBetween $gvLines '^\s*internal enum AtWeaponSwitchAction' '^\s*// Vanilla AI Fix: Handheld AT' -3

$caLines = Get-Content $caFile
$caTurretWallPart = Slice-LinesBetween $caLines '^\s*internal static bool IsTurretDirectFireBlockedByWall\(' '^\s*internal static bool ShouldBlockTurretFriendlyFire\(' -2
$caTurretFfPart = Slice-LinesBetween $caLines '^\s*private static bool IsFriendlySoldierInTurretFireDanger\(' '^\s*\[HarmonyPatch\(typeof\(Squad\),' -2

$locLines = Get-Content $locFile
$locVaultArcPart = Slice-LinesBetween $locLines '^\s*private static bool HasClearVaultArcAndLanding\(' '^\s*private static bool GuardDirectMoveSegment\(' -2

$ffLines = Get-Content $ffFile
$ffWallPart = Slice-LinesBetween $ffLines '^\s*internal static bool IsBotGunClippedThroughWall\(' '^\s*private static bool IsFriendlyInLineOfFire\(' -2
$ffConePart = Slice-LinesBetween $ffLines '^\s*private static bool CheckSingleFriendlyInCone\(' '^\s*private static void TryNudgeFriendlyOutOfFireCone\(' -2

$stLines = Get-Content $stFile
$stStealthPart = Slice-LinesBetween $stLines '^\s*internal static bool EvaluateTacticalStealthCrouch\(' '^\s*internal static bool ShouldBotStealthCrouch\(' -2
$stProneSafetyPart = Slice-LinesBetween $stLines '^\s*internal static bool EvaluateProneSafetyVsWin\(' '^\s*internal static bool ShouldBotChooseOrStayProneForSafety\(' -2
$stLofPart = Slice-LinesBetween $stLines '^\s*private static bool HasClearLineOfFireAtHeight\(' '^\s*private static bool TrySidestepWallObstruction\(' -2

$tnLines = Get-Content $tnFile
$tnEnemyHealSafetyPart = Slice-LinesBetween $tnLines '^\s*internal static bool EvaluateMedicConfidentlySafeForEnemyHeal\(' '^\s*internal static bool IsMedicConfidentlySafeToHealEnemy\(' -2
$thmLines = Get-Content $thmFile
$thmGrenadeClearancePart = Slice-LinesBetween $thmLines '^\s*internal static bool EvaluateGrenadeFriendlyClearance\(' '^\s*private static bool IsUnitFacingTowardTarget\(' -2

# Static anti-regression source guards for over-wall shooting, auto-peek, turret/plane/crew raycast invariants & stealth-crouch
$ffRaw       = Get-Content $ffFile -Raw
$stRaw       = Get-Content $stFile -Raw
$thmRaw      = Get-Content $thmFile -Raw
$goapExecRaw = Get-Content $goapExecFile -Raw
$wqRaw       = Get-Content $wqFile -Raw
$vetRaw      = Get-Content $vetFile -Raw
$tbRaw  = Get-Content $tbFile -Raw
$airRaw = Get-Content $airFile -Raw
$visRaw = Get-Content $visFile -Raw
$aiCoreRaw = Get-Content $aiCoreFile -Raw
$locRaw    = Get-Content $locFile -Raw
$cfgHudRaw = Get-Content $cfgHudFile -Raw
if ($tbRaw -notmatch 'TurretGun_SetGunTriggerPressed_Patch[\s\S]*?IsAirVehicle\(\)') {
    throw "Regression: TurretGun_SetGunTriggerPressed_Patch in TurretAmmoAndBailout.cs must early-skip IsAirVehicle()!"
}
if ($airRaw -match 'Physics\.Raycast\(\s*pos,\s*velDir') {
    throw "Regression: AircraftTactics.cs dive pull-up must use AiCore.IsDirectionBlockedByEnvironment (never raw Physics.Raycast(pos, velDir))!"
}
if ($airRaw -notmatch 'bool rayHitObstacle = AiCore\.IsDirectionBlockedByEnvironment') {
    throw "Regression: AircraftTactics.cs dive pull-up must call AiCore.IsDirectionBlockedByEnvironment!"
}
if ($visRaw -notmatch 'Creature_CanSee_Patch[\s\S]*?IsObserverBlockedByCloseThinWall\(') {
    throw "Regression: Creature_CanSee_Patch in VisionRangeAndCamo.cs must call IsObserverBlockedByCloseThinWall!"
}
if ($stRaw -match 'soldier\.isTowardWallTps\s*&&\s*!soldier\.IsTowardWall\(\)') {
    throw "Regression: StanceAndCoverTactics.cs contains self-cancelling 'soldier.isTowardWallTps && !soldier.IsTowardWall()' check!"
}
if ($stRaw -match 'soldier\.m_pose\s*==\s*SoldierPose\.Crouch\s*&&\s*!crouchClear') {
    throw "Regression: StanceAndCoverTactics.cs contains 'Crouch && !crouchClear' Auto-Peek dead-zone!"
}
$muzzleBlockMatch = [regex]::Match($ffRaw, 'IsMuzzleBlockedByWall\([\s\S]*?^\s{8}\}', [System.Text.RegularExpressions.RegexOptions]::Multiline)
if (-not $muzzleBlockMatch.Success) { throw "Could not locate IsMuzzleBlockedByWall in FriendlyFireAndYield.cs!" }
if ($muzzleBlockMatch.Value -match 'shooter\.IsTowardWall\(\)') {
    throw "Regression: IsMuzzleBlockedByWall must not gate fire on noisy vanilla shooter.IsTowardWall()!"
}
if ($stRaw -notmatch 'angleToTarget\s*<=\s*35f') {
    throw "Regression: Auto-Peek in StanceAndCoverTactics.cs must match 35 deg wall-check cone (angleToTarget <= 35f)!"
}
$peekParamMatch = [regex]::Match($vetRaw, 'GetEffectivePeekParameters\([\s\S]*?^\s{8}\}', [System.Text.RegularExpressions.RegexOptions]::Multiline)
if (-not $peekParamMatch.Success) { throw "Could not locate GetEffectivePeekParameters in VeterancyAndPersonalities.cs!" }
$defSuppMatch = [regex]::Match($peekParamMatch.Value, 'maxPeekSuppression\s*=\s*([0-9]+(?:\.[0-9]+)?)f')
if (-not $defSuppMatch.Success -or [float]$defSuppMatch.Groups[1].Value -lt 40) {
    throw "Regression: GetEffectivePeekParameters default maxPeekSuppression must be >= 40f!"
}
if ($stRaw -notmatch 'internal static bool ShouldBotStealthCrouch\(' -or $stRaw -notmatch 'internal static bool EvaluateTacticalStealthCrouch\(') {
    throw "Regression: StanceAndCoverTactics.cs must define ShouldBotStealthCrouch and EvaluateTacticalStealthCrouch!"
}
if ($thmRaw -notmatch 'ShouldBotStealthCrouch\(' -or $thmRaw -notmatch 'activeStealthCrouch[\s\S]*?MoveOptimized\(false,\s*__0\)') {
    throw "Regression: ThreatMemoryAndWatchSectors.cs must call ShouldBotStealthCrouch and use MoveOptimized(false, __0) for quiet stealth movement!"
}
if ($goapExecRaw -notmatch 'TryExecuteCloseAcousticInvestigate[\s\S]*?StealthCrouchUntil' -or $goapExecRaw -notmatch 'ExecuteGoapMoveToFlankCover[\s\S]*?StealthCrouchUntil') {
    throw "Regression: SoldierGoapExecution.cs must stamp StealthCrouchUntil in both TryExecuteCloseAcousticInvestigate and ExecuteGoapMoveToFlankCover!"
}
if ($wqRaw -notmatch 'IsEnemyAircraftOverhead\([\s\S]*?WorldAirSquadCount\s*<=\s*0') {
    throw "Regression: AiCore.WorldQuery.cs must define IsEnemyAircraftOverhead with WorldAirSquadCount <= 0 fast-exit!"
}
if ($stRaw -notmatch 'internal static bool EvaluateProneSafetyVsWin\(' -or $stRaw -notmatch 'internal static bool ShouldBotChooseOrStayProneForSafety\(') {
    throw "Regression: StanceAndCoverTactics.cs must define EvaluateProneSafetyVsWin and ShouldBotChooseOrStayProneForSafety!"
}
if ($goapExecRaw -notmatch 'ExecuteGoapSeekEmergencyCover[\s\S]*?ShouldBotChooseOrStayProneForSafety\(') {
    throw "Regression: ExecuteGoapSeekEmergencyCover in SoldierGoapExecution.cs must call ShouldBotChooseOrStayProneForSafety!"
}
if ($aiCoreRaw -notmatch 'GetCachedSoldierSyncher\(' -or $aiCoreRaw -notmatch 'IsAnyHumanPlayerSoldier\([\s\S]*?GetCachedSoldierSyncher') {
    throw "Regression: AiCore.cs must define GetCachedSoldierSyncher and use it in IsAnyHumanPlayerSoldier!"
}
if ($locRaw -notmatch 'SoldierAI_UpdateMoveDirection_Patch[\s\S]*?LastUpdateMoveDirRenderFrame') {
    throw "Regression: LocomotionAndVaulting.cs SoldierAI_UpdateMoveDirection_Patch must guard on LastUpdateMoveDirRenderFrame!"
}
if ($cfgHudRaw -notmatch 'Squad_Join_Patch[\s\S]*?CachedSquad[\s\S]*?SyncBotOrdersOnSquadJoin\(') {
    throw "Regression: ConfigMenuAndHud.cs Squad_Join_Patch must refresh CachedSquad and call SyncBotOrdersOnSquadJoin!"
}
if ($goapExecRaw -notmatch 'SyncBotOrdersOnSquadJoin\([\s\S]*?Order\.follow[\s\S]*?GetDestinationInPosition\([\s\S]*?AssignBotCoverOrDestination\(') {
    throw "Regression: SoldierGoapExecution.cs SyncBotOrdersOnSquadJoin must handle Order.follow, GetDestinationInPosition, and AssignBotCoverOrDestination!"
}

$cqbPath = Join-Path $workspace "src\EasyRed2AI\Modules\Infantry\CloseCombatAndSupport.cs"
$cqbText = Get-Content $cqbPath -Raw
$tnRaw = Get-Content $tnFile -Raw
$pluginRaw = Get-Content $pluginFile -Raw
if ($tnRaw -notmatch 'EvaluateMedicConfidentlySafeForEnemyHeal\(' -or
    $tnRaw -notmatch 'IsMedicConfidentlySafeToHealEnemy\(' -or
    $tnRaw -notmatch 'IsActivelyHealingTeammate[\s\S]*?IsMedicConfidentlySafeToHealEnemy\(' -or
    $tnRaw -notmatch 'DriveMedicHealingApproach[\s\S]*?IsMedicConfidentlySafeToHealEnemy\(') {
    throw "Regression: TeamNetAndSquadCommand.cs must define EvaluateMedicConfidentlySafeForEnemyHeal/IsMedicConfidentlySafeToHealEnemy and call IsMedicConfidentlySafeToHealEnemy in both IsActivelyHealingTeammate and DriveMedicHealingApproach!"
}
if ($cqbText -notmatch 'Squad_MedicRoutine_Patch[\s\S]*?IsMedicConfidentlySafeToHealEnemy\(' -or
    $cqbText -notmatch 'MedicHealingDestination_OnCoverReached_Patch[\s\S]*?IsMedicConfidentlySafeToHealEnemy\(') {
    throw "Regression: CloseCombatAndSupport.cs must enforce IsMedicConfidentlySafeToHealEnemy in both Squad_MedicRoutine_Patch and MedicHealingDestination_OnCoverReached_Patch!"
}
if ($pluginRaw -notmatch 'SafePatch\(typeof\(VeteranAndTacticsPatches\.MedicHealingDestination_OnCoverReached_Patch\)\)') {
    throw "Regression: Plugin.cs must register SafePatch(typeof(VeteranAndTacticsPatches.MedicHealingDestination_OnCoverReached_Patch))!"
}
$emRaw = Get-Content $emFile -Raw
if ($wqRaw -notmatch 'IsSegmentCrossingSmoke\(' -or
    $wqRaw -notmatch 'HasFriendlyEyesBeyondSmoke\(' -or
    $wqRaw -notmatch 'EvaluateBlindSmokeTraversalRisk\(' -or
    $wqRaw -notmatch 'IsBlindSmokePathHighRisk\(') {
    throw "Regression: AiCore.WorldQuery.cs must define IsSegmentCrossingSmoke, HasFriendlyEyesBeyondSmoke, EvaluateBlindSmokeTraversalRisk, and IsBlindSmokePathHighRisk!"
}
if ($emRaw -notmatch 'IsBlindSmokePathHighRisk\(' -or
    $goapExecRaw -notmatch 'IsBlindSmokePathHighRisk\(' -or
    $tnRaw -notmatch 'IsBlindSmokePathHighRisk\(' -or
    $stRaw -notmatch 'IsBlindSmokePathHighRisk\(') {
    throw "Regression: EmergentRuntimeBridge.cs, SoldierGoapExecution.cs, TeamNetAndSquadCommand.cs, and StanceAndCoverTactics.cs must all enforce IsBlindSmokePathHighRisk!"
}
if ($tnRaw -match 'hasSmokeCover\s*=[\s\S]{0,260}Physics\.Linecast\([\s\S]{0,140}GetSmokeOcclusionMask\(') {
    throw "Regression: TeamNetAndSquadCommand.cs must not grant hasSmokeCover for charges from arbitrary Physics.Linecast smoke checks!"
}
$grenadeFnText = ([regex]::Match($thmRaw, 'internal static bool IsSafeToThrowGrenadeAt[\s\S]*?\r?\n        \}')).Value
if ($thmRaw -notmatch 'EvaluateGrenadeFriendlyClearance\(' -or
    [string]::IsNullOrEmpty($grenadeFnText) -or
    $grenadeFnText -notmatch 'AiCore\.WorldSoldiers' -or
    $grenadeFnText -notmatch 'AiCore\.WorldInjuredSoldiers' -or
    $grenadeFnText -notmatch 'AiCore\.AreSameSideFactions\(myFaction,\s*fSnap\.Faction\)' -or
    $grenadeFnText -match 'sqSnap\.Faction\s*!=\s*myFaction') {
    throw "Regression: ThreatMemoryAndWatchSectors.cs grenade FF must scan WorldSoldiers + WorldInjuredSoldiers via AreSameSideFactions and EvaluateGrenadeFriendlyClearance!"
}
if ($vetRaw -notmatch 'Soldier_Throw_Patch[\s\S]*?AiCore\.TryGetThrowablePrefabInfo\(') {
    throw "Regression: VeterancyAndPersonalities.cs Soldier_Throw_Patch must scale safety radius via AiCore.TryGetThrowablePrefabInfo!"
}
if ($wqRaw -match 'EnsureWorldSnapshot[\s\S]*?(?:IsPlayer\s*=\s*u\.IsPlayer\(\)|bool\s+isHuman\s*=\s*u\.IsPlayer\(\))') {
    throw "Regression: AiCore.WorldQuery.cs EnsureWorldSnapshot must use IsAnyHumanPlayerSoldier(...) instead of raw u.IsPlayer()!"
}
if ($visRaw -notmatch 'ComputeGunfireRainMaskMultiplier\(' -or
    $visRaw -notmatch 'ComputeVehicleEngineAudibleRadius\(' -or
    $visRaw -notmatch 'RecordSoldierFired[\s\S]*?200\.0f' -or
    $visRaw -notmatch 'RecordTurretFired[\s\S]*?gun\.audioSource') {
    throw "Regression: VisionRangeAndCamo.cs must define rain/engine acoustic helpers, 200m soldier gunfire cap, and TurretGun.audioSource lookup!"
}
if ($wqRaw -notmatch 'ActiveAudibleGroundVehicles' -or
    $wqRaw -notmatch 'ClearSpatialCaches[\s\S]*?ActiveAudibleGroundVehicles' -or
    $wqRaw -notmatch 'TryGetNearestAudibleEnemyVehicle\(') {
    throw "Regression: AiCore.WorldQuery.cs must track/reset ActiveAudibleGroundVehicles and expose TryGetNearestAudibleEnemyVehicle!"
}
if ($thmRaw -notmatch 'TryGetNearestAudibleEnemyVehicle\(' -or
    $thmRaw -notmatch 'TryGetNearestEnemyGunfirePosition\(' -or
    $thmRaw -notmatch 'NotifyProjectileImpactReaction[\s\S]*?ComputeGunfireRainMaskMultiplier\(') {
    throw "Regression: ThreatMemoryAndWatchSectors.cs must wire vehicle/gunfire hearing into watch orientation and apply rain masking to near-miss cracks!"
}
function Extract-CqbFloat {
    param([string]$Text, [string]$Pattern, [string]$Label)
    $m = [regex]::Match($Text, $Pattern)
    if (-not $m.Success) { throw "Could not extract CQB invariant '$Label' using pattern: $Pattern" }
    return $m.Groups[1].Value
}
$cqbBayonetStandoff   = Extract-CqbFloat $cqbText 'HasBayonetReady\)\s*\{\s*standoffDist\s*=\s*([0-9]+\.[0-9]+)f;' 'bayonetStandoff'
$cqbKnifeStandoff     = Extract-CqbFloat $cqbText 'HoldsMeleeWeapon\)\s*\{\s*standoffDist\s*=\s*([0-9]+\.[0-9]+)f;' 'knifeStandoff'
$cqbUnbayStandoff     = Extract-CqbFloat $cqbText 'standoffDist\s*=\s*([0-9]+\.[0-9]+)f;\s*minPersonalSpace\s*=\s*0\.58f;' 'unbayonetedStandoff'
$cqbUnbayStrike       = Extract-CqbFloat $cqbText 'const float unbayonetedStrikeRange\s*=\s*([0-9]+\.[0-9]+)f;' 'unbayonetedStrikeRange'
$cqbKnifeStrike       = Extract-CqbFloat $cqbText 'const float meleeWeaponStrikeRange\s*=\s*([0-9]+\.[0-9]+)f;' 'meleeWeaponStrikeRange'
$cqbPistolCeaseFire   = Extract-CqbFloat $cqbText 'isPistol && !gunEmptyOrReloading && !hasBayonet\)\s*\{[^}]*?ceaseFireRange\s*=\s*([0-9]+\.[0-9]+)f;' 'pistolCeaseFireRange'
$cqbPatchedCapsule    = Extract-CqbFloat $cqbText 'mountedBlade\s*==\s*null[\s\S]*?isEmpGunner\s*\?\s*[0-9]+\.[0-9]+f\s*:\s*([0-9]+\.[0-9]+)f' 'patchedAiCapsuleDist'
$cqbBayonetStrike     = Extract-CqbFloat $cqbText 'const float bayonetStrikeRange\s*=\s*([0-9]+\.[0-9]+)f;' 'bayonetStrikeRange'

$harnessCs = @"
#pragma warning disable 0649, 0414
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    public class Il2CppStructArray<T> where T : struct
    {
        private readonly T[] _arr;
        public Il2CppStructArray(int size) { _arr = new T[size]; }
        public int Length => _arr.Length;
        public T this[int i] { get => _arr[i]; set => _arr[i] = value; }
    }
}

namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public float magnitude => MathF.Sqrt(x * x + y * y);
    }

    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new Vector3(0f, 0f, 0f);
        public static Vector3 up => new Vector3(0f, 1f, 0f);
        public static Vector3 down => new Vector3(0f, -1f, 0f);
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
        public static float Dot(Vector3 a, Vector3 b) => a.x * b.x + a.y * b.y + a.z * b.z;
        public static Vector3 Cross(Vector3 a, Vector3 b) => new Vector3(a.y * b.z - a.z * b.y, a.z * b.x - a.x * b.z, a.x * b.y - a.y * b.x);
        public static float Angle(Vector3 from, Vector3 to)
        {
            float denom = MathF.Sqrt(from.sqrMagnitude * to.sqrMagnitude);
            if (denom < 1e-8f) return 0f;
            float d = Dot(from, to) / denom;
            if (d < -1f) d = -1f; else if (d > 1f) d = 1f;
            return MathF.Acos(d) * (180f / MathF.PI);
        }
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public static Vector3 Lerp(Vector3 a, Vector3 b, float t) => a + (b - a) * Mathf.Clamp01(t);
        public static Vector3 operator -(Vector3 a) => new Vector3(-a.x, -a.y, -a.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new Vector3(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new Vector3(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator *(Vector3 a, float s) => new Vector3(a.x * s, a.y * s, a.z * s);
        public static Vector3 operator /(Vector3 a, float s) => new Vector3(a.x / s, a.y / s, a.z / s);
        public static bool operator ==(Vector3 a, Vector3 b) => MathF.Abs(a.x - b.x) < 1e-6f && MathF.Abs(a.y - b.y) < 1e-6f && MathF.Abs(a.z - b.z) < 1e-6f;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
        public override bool Equals(object obj) => obj is Vector3 v && this == v;
        public override int GetHashCode() => HashCode.Combine(x, y, z);
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
        public Transform RootTransform;
    }

    public struct RaycastHit
    {
        public Vector3 point;
        public Vector3 normal;
        internal Collider _collider;
        public static int ColliderAccessCount;
        public int colliderInstanceID => _collider != null ? System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_collider) : 0;
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

        public struct WallBox
        {
            public float MinX, MaxX, MinY, MaxY, MinZ, MaxZ;
            public bool IsVehicle;
            public Transform RootTransform;
        }
        public static readonly List<WallBox> SceneWalls = new List<WallBox>();
        public static bool UseSceneWalls;

        private static bool RaycastWallBox(
            WallBox b,
            Vector3 origin,
            Vector3 dir,
            float maxDist,
            float inflateRadius,
            bool allowZeroOverlap,
            out float hitT,
            out Vector3 hitNormal)
        {
            hitT = float.MaxValue;
            hitNormal = Vector3.zero;
            float tmin = 0f;
            float tmax = maxDist;
            Vector3 n = Vector3.zero;

            if (!ClipSlab(origin.x, dir.x, b.MinX - inflateRadius, b.MaxX + inflateRadius, new Vector3(-1f, 0f, 0f), new Vector3(1f, 0f, 0f), ref tmin, ref tmax, ref n))
                return false;
            if (!ClipSlab(origin.y, dir.y, b.MinY - inflateRadius, b.MaxY + inflateRadius, new Vector3(0f, -1f, 0f), new Vector3(0f, 1f, 0f), ref tmin, ref tmax, ref n))
                return false;
            if (!ClipSlab(origin.z, dir.z, b.MinZ - inflateRadius, b.MaxZ + inflateRadius, new Vector3(0f, 0f, -1f), new Vector3(0f, 0f, 1f), ref tmin, ref tmax, ref n))
                return false;

            float minT = allowZeroOverlap ? 0f : 1e-5f;
            if (tmin >= minT && tmin <= tmax && tmin <= maxDist)
            {
                hitT = tmin;
                hitNormal = n;
                return true;
            }
            return false;
        }

        public static bool RaycastSceneWalls(
            Vector3 origin,
            Vector3 dir,
            float maxDist,
            out RaycastHit hit,
            float inflateRadius = 0f,
            bool allowZeroOverlap = false)
        {
            hit = default;
            float bestT = float.MaxValue;
            Vector3 bestNormal = Vector3.zero;
            WallBox bestBox = default;
            bool any = false;

            for (int i = 0; i < SceneWalls.Count; i++)
            {
                WallBox b = SceneWalls[i];
                if (RaycastWallBox(b, origin, dir, maxDist, inflateRadius, allowZeroOverlap, out float t, out Vector3 n) && t < bestT)
                {
                    bestT = t;
                    bestNormal = n;
                    bestBox = b;
                    any = true;
                }
            }

            if (any)
            {
                hit.point = origin + dir * bestT;
                hit.normal = bestNormal;
                hit._collider = new Collider { IsTerrain = false, IsVehicle = bestBox.IsVehicle, RootTransform = bestBox.RootTransform };
                return true;
            }
            return false;
        }

        public static int RaycastNonAlloc(
            Vector3 origin,
            Vector3 dir,
            Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RaycastHit> results,
            float maxDist,
            int mask,
            QueryTriggerInteraction qti)
        {
            if (!UseSceneWalls || results == null)
                return 0;

            int count = 0;
            // Iterate in reverse so PhysX-style unsorted order is genuinely exercised by callers
            for (int i = SceneWalls.Count - 1; i >= 0 && count < results.Length; i--)
            {
                WallBox b = SceneWalls[i];
                if (RaycastWallBox(b, origin, dir, maxDist, 0f, false, out float t, out Vector3 n))
                {
                    results[count++] = new RaycastHit
                    {
                        point = origin + dir * t,
                        normal = n,
                        _collider = new Collider { IsTerrain = false, IsVehicle = b.IsVehicle, RootTransform = b.RootTransform }
                    };
                }
            }
            return count;
        }

        private static bool ClipSlab(
            float orig,
            float d,
            float minB,
            float maxB,
            Vector3 nMin,
            Vector3 nMax,
            ref float tmin,
            ref float tmax,
            ref Vector3 hitN)
        {
            if (MathF.Abs(d) < 1e-6f)
                return orig >= minB && orig <= maxB;

            float invD = 1f / d;
            float t1 = (minB - orig) * invD;
            float t2 = (maxB - orig) * invD;
            Vector3 n1 = nMin;
            if (t1 > t2)
            {
                float tmp = t1;
                t1 = t2;
                t2 = tmp;
                n1 = nMax;
            }
            if (t1 > tmin)
            {
                tmin = t1;
                hitN = n1;
            }
            if (t2 < tmax)
                tmax = t2;
            return tmin <= tmax;
        }

        public static bool Raycast(Vector3 origin, Vector3 dir, out RaycastHit hit, float maxDist, int mask, QueryTriggerInteraction qti)
        {
            LastRayOrigin = origin;
            LastRayMaxDist = maxDist;
            LastRayMask = mask;
            hit = default;
            if (UseSceneWalls)
                return RaycastSceneWalls(origin, dir, maxDist, out hit);
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
            if (UseSceneWalls)
            {
                Vector3 d = end - start;
                float len = d.magnitude;
                if (len > 1e-5f)
                    return RaycastSceneWalls(start, d / len, len, out hit);
                return false;
            }
            if (!MockLinecastBlocked)
                return false;
            hit.point = (start + end) * 0.5f;
            hit.normal = MockLinecastNormal;
            hit._collider = MockLinecastCollider;
            return true;
        }

        public static bool Linecast(Vector3 start, Vector3 end, int mask, QueryTriggerInteraction qti)
        {
            return Linecast(start, end, out _, mask, qti);
        }

        public static bool SphereCast(Vector3 origin, float radius, Vector3 dir, out RaycastHit hit, float maxDist, int mask, QueryTriggerInteraction qti)
        {
            hit = default;
            if (UseSceneWalls)
                return RaycastSceneWalls(origin, dir, maxDist, out hit, radius, allowZeroOverlap: true);
            return false;
        }

        public static bool CheckCapsule(Vector3 start, Vector3 end, float radius, int mask, QueryTriggerInteraction qti)
        {
            LastCapsuleMask = mask;
            if (UseSceneWalls)
            {
                Vector3 d = end - start;
                float len = d.magnitude;
                if (len < 1e-5f)
                    return RaycastSceneWalls(start, Vector3.up, 0f, out _, radius, allowZeroOverlap: true);
                return RaycastSceneWalls(start, d / len, len, out _, radius, allowZeroOverlap: true);
            }
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
        private static long _nextPtr = 1000;
        public IntPtr Pointer { get; } = new IntPtr(System.Threading.Interlocked.Increment(ref _nextPtr));
        public Vector3 position;
        private Transform _root;
        public Transform root { get => _root ?? this; set => _root = value; }
        public Vector3 right = new Vector3(1f, 0f, 0f);
        public Vector3 forward = new Vector3(0f, 0f, 1f);
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

public enum ShellType { AP = 0, HE = 1, Rocket = 2, APHE = 3, Smoke = 4, HEAT = 5, Shotgun = 6 }
public class Vehicle
{
    public IntPtr Pointer;
    public bool IsAir;
    public bool IsAirVehicle() => IsAir;
}
public class GenericGun
{
    public Transform transform = new Transform();
    public Vector3 BulletGenPos;
    public Vector3 GetBulletGenerationPosition() => BulletGenPos;
}
public class Spottable
{
    public bool IsAir;
    public bool IsVeh;
    public Vector3 SpotPos;
    public bool IsAirVehicle() => IsAir;
    public bool IsWheeledVehicleOrTank() => IsVeh;
    public Vector3 SpotPosition() => SpotPos;
}
public class CombatCover { }
public class Squad { public bool isChargeSquad; }
public class Soldier
{
    private static long _nextSolPtr = 50000;
    public IntPtr Pointer { get; set; } = new IntPtr(System.Threading.Interlocked.Increment(ref _nextSolPtr));
    public bool IsAlive = true;
    public bool IsHumanPlayer;
    public Transform transform = new Transform();
    public SoldierPose m_pose = SoldierPose.Idle;
    public float LeanAngle;
    public bool isInBodyAnimation;
    public bool IsReloading;
    public bool pointingGun;
    public GenericGun HeldGun;
    public Vector3 GunPos;
    public Vector3 FacePos;
    public Vector3 CenterPos;
    public Vector3 FireDir = new Vector3(0f, 0f, 1f);
    public bool OnVehicle;
    public Vehicle CurrentVeh;
    public bool TowardWall;
    public bool IsTowardWall() => TowardWall;
    public GenericGun GetHeldGun() => HeldGun;
    public Vector3 GunPosition() => GunPos;
    public Vector3 FacePosition() => FacePos;
    public Vector3 GetCenterOfUnit() => CenterPos;
    public Vector3 GetFireDir() => FireDir;
    public bool IsOnVehicle() => OnVehicle;
    public Vehicle GetCurrentVehicle() => CurrentVeh;
}
public enum SoldierPose : byte { Idle = 0, Crouch = 1, Prone = 2 }

namespace EasyRed2HumanAI
{
    using UnityEngine;

    public class ConfigBool { public bool Value = true; }
    public class ConfigFloat { public float Value; }
    public static class VisionConfig
    {
        public static ConfigBool EnableFactionNetDoctrines = new ConfigBool { Value = true };
        public static ConfigBool EnableBattleAtmosphereDirectives = new ConfigBool { Value = true };
        public static ConfigFloat MuzzleObstructionCheckDist = new ConfigFloat { Value = 1.65f };
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
            public Transform RootTr;
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
        public static float RainIntensity01 = 0f;
        public static int BattleTheaterGeneration = 1;
        public static BattleTheaterSnapshot CurrentTheater;
        public static BattleTheaterSnapshot GetBattleTheaterSnapshot() => CurrentTheater;

        public static bool TryGetSoldierHandles(IntPtr ptr, out SoldierFastHandles h) => MockHandles.TryGetValue(ptr, out h);
        public static SoldierFastHandles GetOrCreateSoldierHandles(Soldier s) => s != null && MockHandles.TryGetValue(s.Pointer, out SoldierFastHandles h) ? h : null;

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

        internal struct ColliderEnvMeta
        {
            public bool IsVehicle;
            public IntPtr RootPtr;
        }
        private static readonly Dictionary<int, ColliderEnvMeta> _vehicleColliderCacheById = new Dictionary<int, ColliderEnvMeta>(64);
        private static Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<RaycastHit> _envRayScratch;
        public static int GetSolidEnvironmentMask() => 1;
        public static int GetMovementObstacleMask() => 3;
        public static bool IsTerrainCollider(Collider col) => col != null && col.IsTerrain;
        private static ColliderEnvMeta GetColliderEnvMeta(Collider col) =>
            col == null ? default : new ColliderEnvMeta
            {
                IsVehicle = col.IsVehicle,
                RootPtr = col.RootTransform != null ? col.RootTransform.Pointer : IntPtr.Zero
            };
        public static bool IsVehicleCollider(Collider col) => GetColliderEnvMeta(col).IsVehicle;
        public static bool IsAnyHumanPlayerSoldier(Soldier s) => s != null && s.IsHumanPlayer;
        public static bool IsSoldierInVehicleOrTransition(Soldier s) => false;
        public static bool MockHasWater;
        public static float MockWaterLevel;
        public static bool TryGetActiveWaterLevel(out float w) { w = MockWaterLevel; return MockHasWater; }

        public static float HorizontalAngleToDirection(Soldier s, Vector3 toDir)
        {
            if (s == null || s.transform == null)
                return 0f;
            Vector3 f = new Vector3(s.transform.forward.x, 0f, s.transform.forward.z).normalized;
            Vector3 d = new Vector3(toDir.x, 0f, toDir.z).normalized;
            if (f.sqrMagnitude < 1e-6f || d.sqrMagnitude < 1e-6f)
                return 0f;
            return MathF.Acos(Mathf.Clamp(Vector3.Dot(f, d), -1f, 1f)) * (180f / MathF.PI);
        }

$wqEnvPart
$wqIgnoreColPart

$aiFovPart
$wqBarrelPart
$wqSnapProbe
$wqSmokeRiskPart
    }

    public static partial class VisionPatches
    {
$visPartA
$visFootstepRadiusPart
$visPartB
$visRangePart
$visThinWallPart

        public static void ClearRing()
        {
            Array.Clear(_gunfireRing, 0, _gunfireRing.Length);
            _gunfireRingHead = 0;
        }
        public static void InjectGunfire(IntPtr ptr, Vector3 pos, string fac, float t, float rad) =>
            RecordAcousticEventInternal(ptr, pos, fac, t, rad, false);
        public static void InjectExplosion(IntPtr ptr, Vector3 pos, string fac, float t, float rad) =>
            RecordAcousticEventInternal(ptr, pos, fac, t, rad, true);
        public static bool TestIsObserverBlockedByCloseThinWall(bool observerOnVehicle, Vector3 observerPos, Vector3 toTarget, float dist, Transform observerRoot, bool targetIsVehicle) =>
            IsObserverBlockedByCloseThinWall(observerOnVehicle, observerPos, toTarget, dist, observerRoot, targetIsVehicle);
    }

    public static partial class VeteranAndTacticsPatches
    {
$goapPartA
$goapPartB
$balOverPenPart
$balArmorModPart
$turArmorScorePart
$gvAtSelPart
$caTurretWallPart
$caTurretFfPart
$ffWallPart
$ffConePart
$stStealthPart
$stProneSafetyPart
$tnEnemyHealSafetyPart
$thmGrenadeClearancePart
$stLofPart
$locVaultArcPart

        public static bool TestIsMuzzleBlockedByWall(Soldier shooter, Vector3 targetPos, float dist, bool targetIsVehicle = false) =>
            IsMuzzleBlockedByWall(shooter, targetPos, dist, targetIsVehicle);
        public static bool TestIsTargetFullyOccludedBySolidWall(Soldier shooter, Spottable target, Vector3 targetCenterPos, float dist) =>
            IsTargetFullyOccludedBySolidWall(shooter, target, targetCenterPos, dist);
        public static bool TestHasClearFromCrouch(Soldier soldier, Vector3 targetPos, float dist, bool targetIsVehicle = false) =>
            HasClearLineOfFireFromCrouch(soldier, targetPos, dist, targetIsVehicle);
        public static bool TestHasClearFromStanding(Soldier soldier, Vector3 targetPos, float dist, bool targetIsVehicle = false) =>
            HasClearLineOfFireFromStanding(soldier, targetPos, dist, targetIsVehicle);
        public static Vector3 TestGetOverWallPeekTarget(Spottable target, Vector3 torsoPos) =>
            GetOverWallPeekTarget(target, torsoPos);
        public static bool TestCheckSingleFriendlyInCone(Vector3 gunPos, Vector3 fireDir, float maxCheckDist, float allowedConeDeg, float corridorWidth, Soldier shooter, Soldier friend, out Vector3 sidestepDir) =>
            CheckSingleFriendlyInCone(gunPos, fireDir, maxCheckDist, allowedConeDeg, corridorWidth, shooter, friend, out sidestepDir);
        public static bool TestHasClearVaultArcAndLanding(Vector3 myPos, Vector3 vaultPos, int vaultMask, float crestHeight, out string rejectReason, float maxDropDeltaY = 1.68f) =>
            HasClearVaultArcAndLanding(myPos, vaultPos, vaultMask, crestHeight, out rejectReason, maxDropDeltaY);
        public static bool TestIsTurretDirectFireBlockedByWall(Vector3 pivotPos, Vector3 firePos, Vector3 fireDir, bool isAirVehicle, bool isIndirect, bool isExplosive, bool hasTargetPos, float targetDist, Transform ignoreRoot) =>
            IsTurretDirectFireBlockedByWall(pivotPos, firePos, fireDir, isAirVehicle, isIndirect, isExplosive, hasTargetPos, targetDist, ignoreRoot);
        public static bool TestIsFriendlySoldierInTurretFireDanger(Soldier friend, IntPtr gunnerPtr, IntPtr rootVehPtr, Vector3 firePos, Vector3 fireDir, float maxCheckDist, float baseConeDeg, bool isIndirect, bool isExplosive, bool hasTargetPos, Vector3 targetPos, float dangerCloseBlastRadius) =>
            IsFriendlySoldierInTurretFireDanger(friend, gunnerPtr, rootVehPtr, firePos, fireDir, maxCheckDist, baseConeDeg, isIndirect, isExplosive, hasTargetPos, targetPos, dangerCloseBlastRadius);
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
            {
                // 5. Realistic Human Bullet Over-Penetration Physics Checks
                Assert(!VeteranAndTacticsPatches.TryComputeHumanOverPenetration(380f, 124, 9.0f, 3, false, false, false, 0.26f, out _, out _),
                    "9mm SMG round (380 m/s) should NOT over-penetrate human torso!");
                Assert(!VeteranAndTacticsPatches.TryComputeHumanOverPenetration(380f, 124, 9.0f, 3, false, true, false, 0.14f, out _, out _),
                    "9mm pistol/SMG round (380 m/s) should NOT over-penetrate limb!");

                // .30 Carbine (110 gr, 600 m/s, ~1283 J): stops in torso, passes through slender limb
                Assert(!VeteranAndTacticsPatches.TryComputeHumanOverPenetration(600f, 110, 7.62f, 5, false, false, false, 0.26f, out _, out _),
                    ".30 Carbine (~1283 J) should be absorbed inside human torso (< 1800 J)!");
                Assert(VeteranAndTacticsPatches.TryComputeHumanOverPenetration(600f, 110, 7.62f, 5, false, true, false, 0.14f, out float carbineLimbOut, out _) &&
                       carbineLimbOut > 410f && carbineLimbOut < 445f,
                    $".30 Carbine limb exit speed unexpected: {carbineLimbOut}");

                // Full-power 7.92x57mm / 7.62x54R (154 gr, 800 m/s, 7.92mm): exits torso ~449.8 m/s with tumbling deflection
                Assert(VeteranAndTacticsPatches.TryComputeHumanOverPenetration(800f, 154, 7.92f, 9, false, false, false, 0.26f, out float rifleOut, out float rifleDeflect) &&
                       rifleOut > 440f && rifleOut < 475f && rifleDeflect > 0.04f,
                    $"Full-power rifle torso over-penetration unexpected: out={rifleOut}, defl={rifleDeflect}");
                // ER2 damage drops sharply along continuation path (sqrt(KE / 1500) * 100)
                float keIn = 0.5f * 800f * 800f * (154f * 6.48e-5f);
                float keOut = 0.5f * rifleOut * rifleOut * (154f * 6.48e-5f);
                Assert(keOut < keIn * 0.35f,
                    $"Rifle continuation energy should drop below 35% of entry (in={keIn:F0}J, out={keOut:F0}J)!");

                // Long-range spent rifle bullet (450 m/s) and lengthwise prone spine traversal (0.95m) stop in body
                Assert(!VeteranAndTacticsPatches.TryComputeHumanOverPenetration(450f, 154, 7.92f, 9, false, false, false, 0.26f, out _, out _),
                    "Long-range slow rifle bullet (450 m/s) should stop in torso!");
                Assert(!VeteranAndTacticsPatches.TryComputeHumanOverPenetration(800f, 154, 7.92f, 9, false, false, false, 0.95f, out _, out _),
                    "Lengthwise prone body traversal (0.95m) should stop standard rifle bullet!");
                // Rifle bullet stopped by steel helmet (normal rifle < heavy cal / 8000 J)
                Assert(!VeteranAndTacticsPatches.TryComputeHumanOverPenetration(800f, 154, 7.92f, 9, false, false, true, 0.22f, out _, out _),
                    "Standard rifle bullet on steel helmet must remain vanilla (no over-pen)!");

                // .50 BMG / HMG (660 gr, 890 m/s, 12.7mm): punches cleanly through torso AND helmet with > 620 m/s exit
                Assert(VeteranAndTacticsPatches.TryComputeHumanOverPenetration(890f, 660, 12.7f, 20, false, false, false, 0.26f, out float hmgTorsoOut, out _) &&
                       hmgTorsoOut > 700f && hmgTorsoOut < 740f,
                    $".50 BMG torso over-penetration unexpected: {hmgTorsoOut}");
                Assert(VeteranAndTacticsPatches.TryComputeHumanOverPenetration(890f, 660, 12.7f, 20, false, false, true, 0.22f, out float hmgHelmetOut, out float hmgDeflect) &&
                       hmgHelmetOut > 630f && hmgHelmetOut < 680f && hmgDeflect < 0.02f,
                    $".50 BMG helmet over-penetration unexpected: out={hmgHelmetOut}, defl={hmgDeflect}");
            }
            {
                // 6. 3D Wall-Firing & Auto-Peek Geometry Matrix
                Physics.UseSceneWalls = true;
                try
                {
                    // 6A. Lowered-gun & mid-raise immunity sweep behind waist-high walls (0.85m..1.18m)
                    float[] shooterWallHeights = new float[] { 0.85f, 0.95f, 1.05f, 1.15f, 1.18f };
                    float[] dists6a = new float[] { 10f, 25f };
                    for (int wi = 0; wi < shooterWallHeights.Length; wi++)
                    {
                        float swH = shooterWallHeights[wi];
                        for (int di = 0; di < dists6a.Length; di++)
                        {
                            float d = dists6a[di];
                            Physics.SceneWalls.Clear();
                            Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = swH, MinZ = 0.60f, MaxZ = 0.85f });

                            Vector3 rawTorso = new Vector3(0f, 0.95f, d);
                            var enemy = new Spottable { SpotPos = new Vector3(0f, 1.56f, d) };

                            for (int tw = 0; tw < 2; tw++)
                            {
                                bool towardWall = tw == 1;
                                for (int stage = 0; stage < 3; stage++)
                                {
                                    bool ptGun = stage > 0;
                                    float gunY = stage == 0 ? 0.92f : (stage == 1 ? 1.02f : 1.32f);
                                    var gun = new GenericGun { BulletGenPos = new Vector3(0f, gunY, 0.35f) };
                                    gun.transform.position = new Vector3(0f, gunY - 0.02f, 0.15f);
                                    var shooter = new Soldier
                                    {
                                        m_pose = SoldierPose.Idle,
                                        pointingGun = ptGun,
                                        TowardWall = towardWall,
                                        GunPos = new Vector3(0f, gunY, 0.25f),
                                        FacePos = new Vector3(0f, 1.56f, 0.02f),
                                        HeldGun = gun
                                    };
                                    bool rawBlocked = VeteranAndTacticsPatches.TestIsMuzzleBlockedByWall(shooter, rawTorso, d);
                                    bool canLift = !rawBlocked || VeteranAndTacticsPatches.TryResolveExposedOverWallAim(shooter, enemy, rawTorso, d, out _);
                                    Assert(canLift,
                                        $"6A deadlock: shooterWallH={swH}, dist={d}, towardWall={towardWall}, stage={stage}");
                                }
                            }
                        }
                    }

                    // 6B. Enemy behind own wall (1.15m): torso blocked -> TryResolveExposedOverWallAim lifts cleanly
                    float[] dists6b = new float[] { 6f, 12f, 25f, 50f };
                    for (int di = 0; di < dists6b.Length; di++)
                    {
                        float d = dists6b[di];
                        Physics.SceneWalls.Clear();
                        Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 1.15f, MinZ = d - 0.65f, MaxZ = d - 0.40f });

                        Vector3 rawTorso = new Vector3(0f, 0.95f, d);
                        var enemy = new Spottable { SpotPos = new Vector3(0f, 1.56f, d) };
                        var gun = new GenericGun { BulletGenPos = new Vector3(0f, 1.32f, 0.35f) };
                        gun.transform.position = new Vector3(0f, 1.30f, 0.15f);
                        var shooter = new Soldier
                        {
                            m_pose = SoldierPose.Idle,
                            pointingGun = true,
                            GunPos = new Vector3(0f, 1.32f, 0.25f),
                            FacePos = new Vector3(0f, 1.56f, 0.02f),
                            HeldGun = gun
                        };

                        bool lifted = VeteranAndTacticsPatches.TryResolveExposedOverWallAim(shooter, enemy, rawTorso, d, out Vector3 exposedPos);
                        Assert(lifted && exposedPos.y >= 1.40f && !VeteranAndTacticsPatches.TestIsTargetFullyOccludedBySolidWall(shooter, enemy, exposedPos, d),
                            $"6B enemy-wall lift failed at dist={d}: lifted={lifted}, exposedY={exposedPos.y}");
                    }

                    // 6C. Full 54-scenario multi-tick crouch -> stand over-wall transition loop
                    float[] crouchWalls = new float[] { 0.85f, 1.00f, 1.15f };
                    float[] enemyWalls = new float[] { 0.0f, 1.05f, 1.18f };
                    float[] dists6c = new float[] { 8f, 18f, 40f };
                    int scenarioCount = 0;
                    for (int si = 0; si < crouchWalls.Length; si++)
                    {
                        for (int ei = 0; ei < enemyWalls.Length; ei++)
                        {
                            for (int di = 0; di < dists6c.Length; di++)
                            {
                                float swH = crouchWalls[si];
                                float ewH = enemyWalls[ei];
                                float d = dists6c[di];

                                Physics.SceneWalls.Clear();
                                Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = swH, MinZ = 0.60f, MaxZ = 0.85f });
                                if (ewH > 0f)
                                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = ewH, MinZ = d - 0.65f, MaxZ = d - 0.40f });

                                Vector3 rawTorso = new Vector3(0f, 0.95f, d);
                                var enemy = new Spottable { SpotPos = new Vector3(0f, 1.56f, d) };
                                Vector3 upperPeek = VeteranAndTacticsPatches.TestGetOverWallPeekTarget(enemy, rawTorso);

                                for (int tw = 0; tw < 2; tw++)
                                {
                                    scenarioCount++;
                                    bool towardWall = tw == 1;

                                    // Tick 0: Crouched behind shooter wall -> must detect block and find standing peek LOF
                                    var crouchGun = new GenericGun { BulletGenPos = new Vector3(0f, 0.65f, 0.30f) };
                                    crouchGun.transform.position = new Vector3(0f, 0.63f, 0.12f);
                                    var crouched = new Soldier
                                    {
                                        m_pose = SoldierPose.Crouch,
                                        pointingGun = true,
                                        TowardWall = towardWall,
                                        GunPos = new Vector3(0f, 0.65f, 0.22f),
                                        FacePos = new Vector3(0f, 1.04f, 0.02f),
                                        HeldGun = crouchGun
                                    };
                                    Assert(VeteranAndTacticsPatches.TestIsMuzzleBlockedByWall(crouched, rawTorso, d) &&
                                           !VeteranAndTacticsPatches.TryResolveExposedOverWallAim(crouched, enemy, rawTorso, d, out _),
                                        $"6C Tick0 should be crouched-blocked (swH={swH}, ewH={ewH}, d={d})!");

                                    bool standClear = VeteranAndTacticsPatches.TestHasClearFromStanding(crouched, rawTorso, d) ||
                                                      (upperPeek != rawTorso && VeteranAndTacticsPatches.TestHasClearFromStanding(crouched, upperPeek, d));
                                    Assert(standClear,
                                        $"6C Tick0 failed standing Auto-Peek LOF check (swH={swH}, ewH={ewH}, d={d})!");

                                    // Ticks 1..3: Stood up (hip -> mid-raise -> shouldered) -> must remain shootable
                                    for (int tick = 1; tick <= 3; tick++)
                                    {
                                        bool ptGun = tick >= 2;
                                        float gunY = tick == 1 ? 0.92f : (tick == 2 ? 1.04f : 1.32f);
                                        var standGun = new GenericGun { BulletGenPos = new Vector3(0f, gunY, 0.35f) };
                                        standGun.transform.position = new Vector3(0f, gunY - 0.02f, 0.15f);
                                        var standing = new Soldier
                                        {
                                            m_pose = SoldierPose.Idle,
                                            pointingGun = ptGun,
                                            TowardWall = towardWall,
                                            GunPos = new Vector3(0f, gunY, 0.25f),
                                            FacePos = new Vector3(0f, 1.56f, 0.02f),
                                            HeldGun = standGun
                                        };

                                        bool blockedRaw = VeteranAndTacticsPatches.TestIsMuzzleBlockedByWall(standing, rawTorso, d);
                                        Vector3 aimPos = rawTorso;
                                        if (ewH > 0f || blockedRaw)
                                        {
                                            if (VeteranAndTacticsPatches.TryResolveExposedOverWallAim(standing, enemy, rawTorso, d, out Vector3 expPos))
                                            {
                                                aimPos = expPos;
                                                blockedRaw = VeteranAndTacticsPatches.TestIsMuzzleBlockedByWall(standing, aimPos, d);
                                            }
                                        }
                                        Assert(!blockedRaw && !VeteranAndTacticsPatches.TestIsTargetFullyOccludedBySolidWall(standing, enemy, aimPos, d),
                                            $"6C Tick{tick} stayed blocked after standing (swH={swH}, ewH={ewH}, d={d}, tw={towardWall})!");
                                    }
                                }
                            }
                        }
                    }
                    Assert(scenarioCount == 54, $"Expected 54 6C scenarios, ran {scenarioCount}");

                    // 6D. True Wall-Blocking, Low Window/Pillbox Lintel Header, and Clipped Rifle Barrel Guards
                    float dGuard = 15f;
                    Vector3 torsoGuard = new Vector3(0f, 0.95f, dGuard);
                    var enemyGuard = new Spottable { SpotPos = new Vector3(0f, 1.56f, dGuard) };

                    // 6D-1: Tall solid 2.2m wall
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 2.20f, MinZ = 0.60f, MaxZ = 0.85f });
                    var tallGun = new GenericGun { BulletGenPos = new Vector3(0f, 1.32f, 0.35f) };
                    tallGun.transform.position = new Vector3(0f, 1.30f, 0.15f);
                    var blockedShooter = new Soldier
                    {
                        m_pose = SoldierPose.Idle,
                        pointingGun = true,
                        GunPos = new Vector3(0f, 1.32f, 0.25f),
                        FacePos = new Vector3(0f, 1.56f, 0.02f),
                        HeldGun = tallGun
                    };
                    Assert(VeteranAndTacticsPatches.TestIsMuzzleBlockedByWall(blockedShooter, torsoGuard, dGuard) &&
                           !VeteranAndTacticsPatches.TryResolveExposedOverWallAim(blockedShooter, enemyGuard, torsoGuard, dGuard, out _) &&
                           !VeteranAndTacticsPatches.TestHasClearFromCrouch(blockedShooter, torsoGuard, dGuard) &&
                           !VeteranAndTacticsPatches.TestHasClearFromStanding(blockedShooter, torsoGuard, dGuard) &&
                           VeteranAndTacticsPatches.TestIsTargetFullyOccludedBySolidWall(blockedShooter, enemyGuard, torsoGuard, dGuard),
                        "6D-1 Tall 2.2m solid wall failed to block fire/peek!");

                    // 6D-2: Low window/pillbox lintel (y=[1.45, 2.40] at z=[0.35, 0.55]): blocks standing eye, clears crouched
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 1.45f, MaxY = 2.40f, MinZ = 0.35f, MaxZ = 0.55f });
                    Assert(VeteranAndTacticsPatches.TestIsMuzzleBlockedByWall(blockedShooter, torsoGuard, dGuard) &&
                           !VeteranAndTacticsPatches.TestHasClearFromStanding(blockedShooter, torsoGuard, dGuard),
                        "6D-2 Low lintel failed to block standing eye/peek!");

                    var crouchLintelGun = new GenericGun { BulletGenPos = new Vector3(0f, 0.68f, 0.30f) };
                    crouchLintelGun.transform.position = new Vector3(0f, 0.66f, 0.12f);
                    var crouchedUnderLintel = new Soldier
                    {
                        m_pose = SoldierPose.Crouch,
                        pointingGun = true,
                        GunPos = new Vector3(0f, 0.68f, 0.22f),
                        FacePos = new Vector3(0f, 1.04f, 0.02f),
                        HeldGun = crouchLintelGun
                    };
                    Assert(!VeteranAndTacticsPatches.TestIsMuzzleBlockedByWall(crouchedUnderLintel, torsoGuard, dGuard) &&
                           VeteranAndTacticsPatches.TestHasClearFromCrouch(crouchedUnderLintel, torsoGuard, dGuard),
                        "6D-2 Crouched shooter beneath 1.45m lintel was falsely blocked!");

                    // 6D-3: Clipped rifle barrel poked through thin wall (wall at z=[0.20, 0.35], muzzle at z=0.55)
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 2.20f, MinZ = 0.20f, MaxZ = 0.35f });
                    var clippedGun = new GenericGun { BulletGenPos = new Vector3(0f, 1.28f, 0.55f) };
                    clippedGun.transform.position = new Vector3(0f, 1.26f, 0.15f);
                    var clippedShooter = new Soldier
                    {
                        m_pose = SoldierPose.Idle,
                        pointingGun = false,
                        GunPos = new Vector3(0f, 1.28f, 0.55f),
                        FacePos = new Vector3(0f, 1.56f, 0.02f),
                        HeldGun = clippedGun
                    };
                    Assert(VeteranAndTacticsPatches.IsBotGunClippedThroughWall(clippedShooter) &&
                           VeteranAndTacticsPatches.TestIsMuzzleBlockedByWall(clippedShooter, torsoGuard, dGuard),
                        "6D-3 Clipped barrel through wall failed to block!");

                    // 6E: Vehicle hull colliders lie on layer Default (solid env mask); targeting an enemy tank
                    // point-blank or at medium range MUST NOT self-block AT rockets on the target vehicle's own hull,
                    // while a solid masonry wall in front of the tank MUST still block.
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -1.6f, MaxX = 1.6f, MinY = 0f, MaxY = 2.6f, MinZ = 0.40f, MaxZ = 6.50f, IsVehicle = true });
                    var atGunClose = new GenericGun { BulletGenPos = new Vector3(0f, 1.35f, 0.48f) };
                    atGunClose.transform.position = new Vector3(0f, 1.32f, 0.12f);
                    var atShooterClose = new Soldier
                    {
                        m_pose = SoldierPose.Idle,
                        pointingGun = true,
                        GunPos = new Vector3(0f, 1.35f, 0.30f),
                        FacePos = new Vector3(0f, 1.56f, 0.02f),
                        HeldGun = atGunClose
                    };
                    var tankTargetClose = new Spottable { IsVeh = true, SpotPos = new Vector3(0f, 1.80f, 3.50f) };
                    var tankPosClose = new Vector3(0f, 1.25f, 3.50f);
                    Assert(!VeteranAndTacticsPatches.IsBotGunClippedThroughWall(atShooterClose, targetIsVehicle: true) &&
                           !VeteranAndTacticsPatches.TestIsMuzzleBlockedByWall(atShooterClose, tankPosClose, 3.50f, targetIsVehicle: true) &&
                           !VeteranAndTacticsPatches.TestIsTargetFullyOccludedBySolidWall(atShooterClose, tankTargetClose, tankPosClose, 3.50f),
                        "6E-1 Point-blank 3.5m AT shot vs enemy tank was falsely blocked by target vehicle collider!");

                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -1.8f, MaxX = 1.8f, MinY = 0f, MaxY = 2.6f, MinZ = 21.5f, MaxZ = 28.5f, IsVehicle = true });
                    var atGunMid = new GenericGun { BulletGenPos = new Vector3(0f, 1.35f, 0.38f) };
                    atGunMid.transform.position = new Vector3(0f, 1.32f, 0.12f);
                    var atShooterMid = new Soldier
                    {
                        m_pose = SoldierPose.Idle,
                        pointingGun = true,
                        GunPos = new Vector3(0f, 1.35f, 0.28f),
                        FacePos = new Vector3(0f, 1.56f, 0.02f),
                        HeldGun = atGunMid
                    };
                    var tankTargetMid = new Spottable { IsVeh = true, SpotPos = new Vector3(0f, 1.80f, 25.0f) };
                    var tankPosMid = new Vector3(0f, 1.25f, 25.0f);
                    Assert(!VeteranAndTacticsPatches.TestIsMuzzleBlockedByWall(atShooterMid, tankPosMid, 25.0f, targetIsVehicle: true) &&
                           !VeteranAndTacticsPatches.TestIsTargetFullyOccludedBySolidWall(atShooterMid, tankTargetMid, tankPosMid, 25.0f),
                        "6E-2 Medium-range 25m AT shot vs enemy tank was falsely occluded by target vehicle hull!");

                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 2.4f, MinZ = 0.70f, MaxZ = 0.95f, IsVehicle = false });
                    Assert(VeteranAndTacticsPatches.TestIsMuzzleBlockedByWall(atShooterMid, tankPosMid, 25.0f, targetIsVehicle: true) &&
                           VeteranAndTacticsPatches.TestIsTargetFullyOccludedBySolidWall(atShooterMid, tankTargetMid, tankPosMid, 25.0f),
                        "6E-3 Solid stone wall in front of enemy tank failed to block AT rocket!");

                    // 6E-4: Multi-range AT/Infantry sweep vs enemy tank (1.5m..150m) including crouch/stand LOF + open point-blank infantry
                    float[] atSweepDists = new float[] { 1.5f, 2.5f, 3.5f, 8.0f, 15.0f, 25.0f, 55.0f, 90.0f, 150.0f };
                    for (int di = 0; di < atSweepDists.Length; di++)
                    {
                        float d = atSweepDists[di];
                        Physics.SceneWalls.Clear();
                        Physics.SceneWalls.Add(new Physics.WallBox { MinX = -1.8f, MaxX = 1.8f, MinY = 0f, MaxY = 2.6f, MinZ = MathF.Max(0.42f, d - 3.2f), MaxZ = d + 3.2f, IsVehicle = true });
                        var sweepGun = new GenericGun { BulletGenPos = new Vector3(0f, 1.35f, 0.36f) };
                        sweepGun.transform.position = new Vector3(0f, 1.32f, 0.12f);
                        var sweepShooter = new Soldier
                        {
                            m_pose = SoldierPose.Idle,
                            pointingGun = true,
                            GunPos = new Vector3(0f, 1.35f, 0.28f),
                            FacePos = new Vector3(0f, 1.56f, 0.02f),
                            HeldGun = sweepGun
                        };
                        var sweepTarget = new Spottable { IsVeh = true, SpotPos = new Vector3(0f, 1.80f, d) };
                        Vector3 sweepCenter = new Vector3(0f, 1.25f, d);
                        Assert(!VeteranAndTacticsPatches.TestIsMuzzleBlockedByWall(sweepShooter, sweepCenter, d, targetIsVehicle: true) &&
                               !VeteranAndTacticsPatches.TestIsTargetFullyOccludedBySolidWall(sweepShooter, sweepTarget, sweepCenter, d),
                            $"6E-4 AT vs enemy tank falsely blocked in open at dist={d}m!");
                        if (d > 1.6f)
                        {
                            Assert(VeteranAndTacticsPatches.TestHasClearFromCrouch(sweepShooter, sweepCenter, d, targetIsVehicle: true) &&
                                   VeteranAndTacticsPatches.TestHasClearFromStanding(sweepShooter, sweepCenter, d, targetIsVehicle: true),
                                $"6E-4 Crouch/Standing LOF vs enemy tank falsely blocked by target hull at dist={d}m!");
                        }

                        // With masonry wall inserted ahead of tank (from 3.5m+ so wall fits between muzzle & tank)
                        if (d >= 3.5f)
                        {
                            Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 2.4f, MinZ = 0.70f, MaxZ = 0.95f, IsVehicle = false });
                            Assert(VeteranAndTacticsPatches.TestIsMuzzleBlockedByWall(sweepShooter, sweepCenter, d, targetIsVehicle: true) &&
                                   VeteranAndTacticsPatches.TestIsTargetFullyOccludedBySolidWall(sweepShooter, sweepTarget, sweepCenter, d) &&
                                   !VeteranAndTacticsPatches.TestHasClearFromStanding(sweepShooter, sweepCenter, d, targetIsVehicle: true),
                                $"6E-4 Masonry wall ahead of tank failed to block at dist={d}m!");
                        }
                    }

                    // 6E-5: Crouched behind 1.0m low wall vs 15m enemy tank -> crouch blocked, standing LOF clear
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 1.0f, MinZ = 0.60f, MaxZ = 0.85f, IsVehicle = false });
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -1.8f, MaxX = 1.8f, MinY = 0f, MaxY = 2.6f, MinZ = 12.0f, MaxZ = 18.0f, IsVehicle = true });
                    var lowWallShooter = new Soldier { m_pose = SoldierPose.Crouch, pointingGun = true };
                    Vector3 tankCenter15 = new Vector3(0f, 1.25f, 15.0f);
                    Assert(!VeteranAndTacticsPatches.TestHasClearFromCrouch(lowWallShooter, tankCenter15, 15.0f, targetIsVehicle: true) &&
                           VeteranAndTacticsPatches.TestHasClearFromStanding(lowWallShooter, tankCenter15, 15.0f, targetIsVehicle: true),
                        "6E-5 1.0m wall vs 15m tank should block crouch LOF and clear standing LOF!");

                    // 6E-6: Unobstructed point-blank infantry vs infantry at 1.5m & 2.5m must not false-block
                    Physics.SceneWalls.Clear();
                    foreach (float pbDist in new float[] { 1.5f, 2.5f })
                    {
                        var pbGun = new GenericGun { BulletGenPos = new Vector3(0f, 1.32f, 0.35f) };
                        pbGun.transform.position = new Vector3(0f, 1.30f, 0.15f);
                        var pbShooter = new Soldier
                        {
                            m_pose = SoldierPose.Idle,
                            pointingGun = true,
                            GunPos = new Vector3(0f, 1.32f, 0.25f),
                            FacePos = new Vector3(0f, 1.56f, 0.02f),
                            HeldGun = pbGun
                        };
                        var pbEnemy = new Spottable { SpotPos = new Vector3(0f, 1.56f, pbDist) };
                        Vector3 pbTorso = new Vector3(0f, 0.95f, pbDist);
                        Assert(!VeteranAndTacticsPatches.TestIsMuzzleBlockedByWall(pbShooter, pbTorso, pbDist) &&
                               !VeteranAndTacticsPatches.TestIsTargetFullyOccludedBySolidWall(pbShooter, pbEnemy, pbTorso, pbDist),
                            $"6E-6 Open point-blank infantry at {pbDist}m falsely blocked!");
                    }
                }
                finally
                {
                    Physics.UseSceneWalls = false;
                    Physics.SceneWalls.Clear();
                }

                // 6F: AT Weapon Selection & enemyIsInRange State (SoldierAI.UseBestWearedWeaponCheck_Patch)
                {
                    var dCloseArmor = VeteranAndTacticsPatches.DecideAtWeaponSelection(
                        hasTarget: true, targetIsArmoredVehicle: true, dist: 6.0f,
                        g0IsAt: true, g0HasAmmo: true, g0DamageRange: 150.0f,
                        stowedAtSlot: -1, stowedAtDamageRange: 0f, stowedNonAtSlot: 1, stowedNonAtDamageRange: 150.0f);
                    Assert(dCloseArmor.Action == VeteranAndTacticsPatches.AtWeaponSwitchAction.HoldSlot0AndFinalize &&
                           dCloseArmor.ShouldRefillSlot0 &&
                           dCloseArmor.EnemyIsInRange,
                        "6F-1 Holding loaded AT at 6m vs armor must hold Slot 0, refill, and set enemyIsInRange=true!");

                    var dFarArmor = VeteranAndTacticsPatches.DecideAtWeaponSelection(
                        hasTarget: true, targetIsArmoredVehicle: true, dist: 80.0f,
                        g0IsAt: true, g0HasAmmo: true, g0DamageRange: 150.0f,
                        stowedAtSlot: -1, stowedAtDamageRange: 0f, stowedNonAtSlot: 1, stowedNonAtDamageRange: 150.0f);
                    Assert(dFarArmor.Action == VeteranAndTacticsPatches.AtWeaponSwitchAction.HoldSlot0AndFinalize &&
                           dFarArmor.ShouldRefillSlot0 &&
                           dFarArmor.EnemyIsInRange,
                        "6F-2 Holding loaded AT at 80m vs armor must hold Slot 0, refill, and set enemyIsInRange=true!");

                    var dEquipStowedAt = VeteranAndTacticsPatches.DecideAtWeaponSelection(
                        hasTarget: true, targetIsArmoredVehicle: true, dist: 40.0f,
                        g0IsAt: false, g0HasAmmo: true, g0DamageRange: 150.0f,
                        stowedAtSlot: 1, stowedAtDamageRange: 150.0f, stowedNonAtSlot: -1, stowedNonAtDamageRange: 0f);
                    Assert(dEquipStowedAt.Action == VeteranAndTacticsPatches.AtWeaponSwitchAction.SwitchToSlotAndFinalize &&
                           dEquipStowedAt.TargetSlot == 1 &&
                           dEquipStowedAt.EnemyIsInRange,
                        "6F-3 Rifle in Slot 0 + stowed AT in Slot 1 vs armor at 40m must switch to Slot 1!");

                    var dSwitchSidearmCqb = VeteranAndTacticsPatches.DecideAtWeaponSelection(
                        hasTarget: true, targetIsArmoredVehicle: false, dist: 4.0f,
                        g0IsAt: true, g0HasAmmo: true, g0DamageRange: 150.0f,
                        stowedAtSlot: -1, stowedAtDamageRange: 0f, stowedNonAtSlot: 2, stowedNonAtDamageRange: 50.0f);
                    Assert(dSwitchSidearmCqb.Action == VeteranAndTacticsPatches.AtWeaponSwitchAction.SwitchToSlotAndFinalize &&
                           dSwitchSidearmCqb.TargetSlot == 2 &&
                           dSwitchSidearmCqb.EnemyIsInRange,
                        "6F-4 AT in Slot 0 + loaded sidearm in Slot 2 vs foot enemy at 4m must switch to Slot 2!");

                    var dDryAltCqb = VeteranAndTacticsPatches.DecideAtWeaponSelection(
                        hasTarget: true, targetIsArmoredVehicle: false, dist: 4.0f,
                        g0IsAt: true, g0HasAmmo: true, g0DamageRange: 150.0f,
                        stowedAtSlot: -1, stowedAtDamageRange: 0f, stowedNonAtSlot: -1, stowedNonAtDamageRange: 0f);
                    Assert(dDryAltCqb.Action == VeteranAndTacticsPatches.AtWeaponSwitchAction.HoldSlot0AndFinalize &&
                           dDryAltCqb.ShouldRefillSlot0 &&
                           dDryAltCqb.EnemyIsInRange,
                        "6F-5 AT in Slot 0 with no usable non-AT alt vs 4m foot enemy must finalize Slot 0 (never fall through to dry Slot 1)!");

                    var dBeyondMaxArmorWindow = VeteranAndTacticsPatches.DecideAtWeaponSelection(
                        hasTarget: true, targetIsArmoredVehicle: true, dist: 180.0f,
                        g0IsAt: true, g0HasAmmo: true, g0DamageRange: 150.0f,
                        stowedAtSlot: -1, stowedAtDamageRange: 0f, stowedNonAtSlot: 1, stowedNonAtDamageRange: 150.0f);
                    Assert(dBeyondMaxArmorWindow.Action == VeteranAndTacticsPatches.AtWeaponSwitchAction.FallthroughToVanilla,
                        "6F-6 Target armor beyond 95m AT engage window must fall through cleanly!");

                    var dShortRangeLauncher = VeteranAndTacticsPatches.DecideAtWeaponSelection(
                        hasTarget: true, targetIsArmoredVehicle: true, dist: 90.0f,
                        g0IsAt: true, g0HasAmmo: true, g0DamageRange: 35.0f,
                        stowedAtSlot: -1, stowedAtDamageRange: 0f, stowedNonAtSlot: 1, stowedNonAtDamageRange: 150.0f);
                    Assert(dShortRangeLauncher.Action == VeteranAndTacticsPatches.AtWeaponSwitchAction.HoldSlot0AndFinalize &&
                           !dShortRangeLauncher.EnemyIsInRange,
                        "6F-7 Short-range 35m weapon vs 90m armor must keep enemyIsInRange=false!");
                }
            }
            {
                // 7. Vision & Camo Curve Matrix
                float[] testDists = new float[] { 1f, 5f, 11.9f, 12f, 15f, 25f, 34f, 50f, 65f, 100f, 150f, 210f, 300f };
                float[] omniRadii = new float[] { 4.5f, 8.0f, 12.0f };
                for (int oi = 0; oi < omniRadii.Length; oi++)
                {
                    float omni = omniRadii[oi];
                    float clampedOmni = Mathf.Clamp(omni, 6.0f, 14.0f);
                    float prevFov = 999f;
                    for (int di = 0; di < testDists.Length; di++)
                    {
                        float d = testDists[di];
                        float fov = AiCore.EvaluateHumanSquintHalfFov(d, omni, 65f, 52f, 30f);
                        Assert(fov >= 26f && fov <= 180f, $"Section 7 FOV out of bounds: dist={d}, omni={omni}, fov={fov}");
                        Assert(fov <= prevFov + 1e-4f, $"Section 7 FOV non-monotonic: dist={d}, prev={prevFov}, cur={fov}");
                        if (d <= clampedOmni)
                            Assert(fov >= 179.9f, $"Section 7 inside omni radius should be 180: dist={d}, omni={clampedOmni}, fov={fov}");
                        if (d <= 34.0f)
                            Assert(fov >= 89.9f, $"Section 7 <= 34m peripheral band should keep >= 90 deg: dist={d}, fov={fov}");
                        prevFov = fov;
                    }
                }

                float[] nights = new float[] { 0.0f, 0.5f, 1.0f };
                float[] optics = new float[] { 1.0f, 1.6f };
                for (int ni = 0; ni < nights.Length; ni++)
                {
                    float night = nights[ni];
                    for (int opti = 0; opti < optics.Length; opti++)
                    {
                        float opt = optics[opti];
                        float idleR = VisionPatches.ComputeEffectiveSoldierSightRange(SoldierPose.Idle, false, true, 165f, 110f, 55f, 1.0f, night, 0.4f, 5.0f, 0f, 1.0f, opt);
                        float crouchR = VisionPatches.ComputeEffectiveSoldierSightRange(SoldierPose.Crouch, false, true, 165f, 110f, 55f, 1.0f, night, 0.4f, 5.0f, 0f, 1.0f, opt);
                        float proneR = VisionPatches.ComputeEffectiveSoldierSightRange(SoldierPose.Prone, false, true, 165f, 110f, 55f, 1.0f, night, 0.4f, 5.0f, 0f, 1.0f, opt);
                        Assert(proneR < crouchR && crouchR < idleR,
                            $"Section 7 pose hierarchy broken at night={night}, opt={opt}: prone={proneR}, crouch={crouchR}, idle={idleR}");
                    }

                    float baseSilent = VisionPatches.ComputeEffectiveSoldierSightRange(SoldierPose.Idle, false, true, 165f, 110f, 55f, 1.0f, night, 0.4f, 5.0f, 0f, 1.0f, 1.0f);
                    float optSilent = VisionPatches.ComputeEffectiveSoldierSightRange(SoldierPose.Idle, false, true, 165f, 110f, 55f, 1.0f, night, 0.4f, 5.0f, 0f, 1.0f, 1.6f);
                    Assert(optSilent > baseSilent * 1.55f, $"Section 7 scope 1.6x failed at night={night}: {optSilent} vs {baseSilent}");
                }

                float dayIdle = VisionPatches.ComputeEffectiveSoldierSightRange(SoldierPose.Idle, false, true, 165f, 110f, 55f, 1.0f, 0.0f, 1.0f, 5.0f, 0f, 1.0f, 1.0f);
                float halfNightSilent = VisionPatches.ComputeEffectiveSoldierSightRange(SoldierPose.Idle, false, true, 165f, 110f, 55f, 1.0f, 0.5f, 0.7f, 5.0f, 0f, 1.0f, 1.0f);
                float fullNightSilent = VisionPatches.ComputeEffectiveSoldierSightRange(SoldierPose.Idle, false, true, 165f, 110f, 55f, 1.0f, 1.0f, 0.4f, 5.0f, 0f, 1.0f, 1.0f);
                float nightFlare0 = VisionPatches.ComputeEffectiveSoldierSightRange(SoldierPose.Idle, false, true, 165f, 110f, 55f, 1.0f, 1.0f, 0.4f, 0.05f, 0f, 1.0f, 1.0f);
                float nightFlareMid = VisionPatches.ComputeEffectiveSoldierSightRange(SoldierPose.Idle, false, true, 165f, 110f, 55f, 1.0f, 1.0f, 0.4f, 0.90f, 0f, 1.0f, 1.0f);
                float nightFlareEnd = VisionPatches.ComputeEffectiveSoldierSightRange(SoldierPose.Idle, false, true, 165f, 110f, 55f, 1.0f, 1.0f, 0.4f, 1.85f, 0f, 1.0f, 1.0f);
                Assert(dayIdle > halfNightSilent && halfNightSilent > fullNightSilent,
                    $"Section 7 silent night shrink non-monotonic: day={dayIdle}, half={halfNightSilent}, full={fullNightSilent}");
                Assert(nightFlare0 > fullNightSilent * 2.0f && nightFlare0 > nightFlareMid && nightFlareMid > nightFlareEnd && MathF.Abs(nightFlareEnd - fullNightSilent) < 1e-3f,
                    $"Section 7 night flare curve failed: flare0={nightFlare0}, mid={nightFlareMid}, end={nightFlareEnd}, silent={fullNightSilent}");
            }
            {
                // 8. Armor, Obliquity, Top-Attack & Smart-Ammo Matrix
                float penThresh = 90f * 0.85f;
                float sAphe = VeteranAndTacticsPatches.ScoreTurretShellAgainstArmor(ShellType.APHE, 110f, 75f, false, penThresh);
                float sHeat = VeteranAndTacticsPatches.ScoreTurretShellAgainstArmor(ShellType.HEAT, 110f, 75f, false, penThresh);
                float sAp = VeteranAndTacticsPatches.ScoreTurretShellAgainstArmor(ShellType.AP, 110f, 75f, false, penThresh);
                float sHe = VeteranAndTacticsPatches.ScoreTurretShellAgainstArmor(ShellType.HE, 15f, 75f, true, penThresh);
                float sMg = VeteranAndTacticsPatches.ScoreTurretShellAgainstArmor(ShellType.AP, 8f, 7.92f, false, penThresh);
                Assert(sAphe > sHeat && sHeat > sAp && sAp > sHe && sHe > sMg,
                    $"Section 8 heavy tank ammo ranking invalid: APHE={sAphe}, HEAT={sHeat}, AP={sAp}, HE={sHe}, MG={sMg}");

                float sPenHeat = VeteranAndTacticsPatches.ScoreTurretShellAgainstArmor(ShellType.HEAT, 115f, 75f, true, penThresh);
                float sBounceAp = VeteranAndTacticsPatches.ScoreTurretShellAgainstArmor(ShellType.AP, 65f, 75f, false, penThresh);
                Assert(sPenHeat > sBounceAp * 2f,
                    $"Section 8 penetrating HEAT should beat non-penetrating AP: HEAT={sPenHeat}, AP={sBounceAp}");

                float sHeavyHe = VeteranAndTacticsPatches.ScoreTurretShellAgainstArmor(ShellType.HE, 10f, 75f, true, penThresh);
                Assert(sHeavyHe > sMg, $"Section 8 75mm HE ({sHeavyHe}) should outrank MG ({sMg}) vs armor");

                float sHalftrackAphe = VeteranAndTacticsPatches.ScoreTurretShellAgainstArmor(ShellType.APHE, 45f, 37f, false, 12f * 0.85f);
                float sHalftrackMg = VeteranAndTacticsPatches.ScoreTurretShellAgainstArmor(ShellType.AP, 8f, 7.92f, false, 12f * 0.85f);
                Assert(sHalftrackAphe > sHalftrackMg, "Section 8 37mm APHE must beat MG vs 12mm halftrack");

                // Top-attack 20mm AP diving at 65 deg obliquity onto 40mm tank plate
                VeteranAndTacticsPatches.EvaluateArmorImpactModifiers(
                    false, false, false, 20f, 22, 40f, true, false, 65f,
                    out float topArm, out float topOb, out bool modArm, out bool modSpall, out float spallM, out float spallMax, out bool blkAir);
                Assert(modArm && MathF.Abs(topOb - 15f) < 1e-4f && MathF.Abs(topArm - 18f) < 1e-4f && modSpall && MathF.Abs(spallM - 0.40f) < 1e-4f && MathF.Abs(spallMax - 45f) < 1e-4f && !blkAir,
                    $"Section 8 top-attack 20mm AP failed: modArm={modArm}, arm={topArm}, ob={topOb}, spallM={spallM}");

                // 75mm APHE (isHeOrFlak=true) keeps 1.0x lethal burst inside tank
                VeteranAndTacticsPatches.EvaluateArmorImpactModifiers(
                    false, false, true, 75f, 110, 40f, false, false, 10f,
                    out _, out _, out _, out bool apheModSpall, out float apheSpallM, out _, out _);
                Assert(!apheModSpall && MathF.Abs(apheSpallM - 1.0f) < 1e-4f, "Section 8 75mm APHE should keep full 1.0x crew lethality");

                // Aircraft fuselage vs 7.92mm MG (blocks pilot sniping) vs .50 cal away/near cockpit vs 20mm HE
                VeteranAndTacticsPatches.EvaluateArmorImpactModifiers(true, false, false, 7.92f, 9, 4f, false, true, 0f,
                    out _, out _, out _, out bool rAirMod, out float rAirM, out _, out bool rAirBlk);
                Assert(rAirMod && rAirM == 0f && rAirBlk, "Section 8 7.92mm on intact plane must block pilot sniping");

                VeteranAndTacticsPatches.EvaluateArmorImpactModifiers(true, false, false, 12.7f, 20, 4f, false, false, 0f,
                    out _, out _, out _, out bool hmgFarMod, out float hmgFarM, out _, out bool hmgFarBlk);
                Assert(hmgFarMod && hmgFarM == 0f && hmgFarBlk, "Section 8 .50 cal away from cockpit must block crew spall");

                VeteranAndTacticsPatches.EvaluateArmorImpactModifiers(true, false, false, 12.7f, 20, 4f, false, true, 0f,
                    out _, out _, out _, out bool hmgNearMod, out float hmgNearM, out float hmgNearMax, out bool hmgNearBlk);
                Assert(hmgNearMod && MathF.Abs(hmgNearM - 0.28f) < 1e-4f && MathF.Abs(hmgNearMax - 34f) < 1e-4f && !hmgNearBlk,
                    "Section 8 .50 cal near cockpit must deal 0.28x capped spall");

                VeteranAndTacticsPatches.EvaluateArmorImpactModifiers(true, false, true, 20f, 5, 4f, false, true, 0f,
                    out _, out _, out _, out bool airHeMod, out _, out _, out bool airHeBlk);
                Assert(!airHeMod && !airHeBlk, "Section 8 20mm HE on plane must keep vanilla explosion damage");
            }
            {
                // 9. Friendly-Fire Cone & Corridor Geometry Matrix
                var shooterFf = new Soldier();
                shooterFf.transform.right = new Vector3(1f, 0f, 0f);
                var allyFf = new Soldier();
                Vector3 gunOrig = new Vector3(0f, 1.3f, 0f);
                Vector3 fDir = new Vector3(0f, 0f, 1f);

                float[] ffFwd = new float[] { -2.0f, 0.05f, 1.5f, 4.0f, 7.5f, 12.0f, 25.0f, 50.0f };
                float[] ffLat = new float[] { -4.5f, -2.5f, -0.85f, -0.35f, 0.0f, 0.35f, 0.85f, 2.5f, 4.5f };
                for (int fi = 0; fi < ffFwd.Length; fi++)
                {
                    float fwd = ffFwd[fi];
                    for (int li = 0; li < ffLat.Length; li++)
                    {
                        float lat = ffLat[li];
                        allyFf.CenterPos = new Vector3(lat, 1.3f, fwd);
                        bool blk = VeteranAndTacticsPatches.TestCheckSingleFriendlyInCone(
                            gunOrig, fDir, 40f, 7f, 0.90f, shooterFf, allyFf, out Vector3 sidestep);

                        if (fwd <= 0.1f || fwd >= 40f)
                        {
                            Assert(!blk, $"Section 9 invalid block behind/beyond target: fwd={fwd}, lat={lat}");
                        }
                        else if (fwd >= 1.5f && fwd <= 7.5f && MathF.Abs(lat) <= 0.85f)
                        {
                            Assert(blk, $"Section 9 close corridor failed to block: fwd={fwd}, lat={lat}");
                            if (lat > 0.1f) Assert(sidestep.x < -0.5f, $"Section 9 sidestep should point left when ally is right (lat={lat})");
                            if (lat < -0.1f) Assert(sidestep.x > 0.5f, $"Section 9 sidestep should point right when ally is left (lat={lat})");
                        }
                        else if (MathF.Abs(fwd - 4.0f) < 0.1f && MathF.Abs(MathF.Abs(lat) - 2.5f) < 0.1f)
                        {
                            Assert(!blk, $"Section 9 staggered line 2.5m lateral should NOT false-block: fwd={fwd}, lat={lat}");
                        }
                    }
                }

                // Ground vehicle widened corridor (11m fwd, 1.45m lateral)
                var tankA = new Vehicle { Pointer = new IntPtr(101), IsAir = false };
                var tankB = new Vehicle { Pointer = new IntPtr(202), IsAir = false };
                allyFf.CenterPos = new Vector3(1.45f, 1.3f, 11.0f);
                allyFf.OnVehicle = true;
                allyFf.CurrentVeh = tankA;
                shooterFf.OnVehicle = false;
                Assert(VeteranAndTacticsPatches.TestCheckSingleFriendlyInCone(gunOrig, fDir, 40f, 7f, 0.90f, shooterFf, allyFf, out _),
                    "Section 9 ally inside different ground tank at 11m / 1.45m should block");
                shooterFf.OnVehicle = true;
                shooterFf.CurrentVeh = tankA;
                Assert(!VeteranAndTacticsPatches.TestCheckSingleFriendlyInCone(gunOrig, fDir, 40f, 7f, 0.90f, shooterFf, allyFf, out _),
                    "Section 9 same-vehicle gunner at 1.45m lateral should NOT self-block");
                shooterFf.CurrentVeh = tankB;
                Assert(VeteranAndTacticsPatches.TestCheckSingleFriendlyInCone(gunOrig, fDir, 40f, 7f, 0.90f, shooterFf, allyFf, out _),
                    "Section 9 different-vehicle tank crew at 1.45m lateral should block");
                allyFf.OnVehicle = false;
                shooterFf.OnVehicle = false;
            }
            {
                // 10. 3D Vault & Cliff/Bridge Safety Matrix
                Physics.UseSceneWalls = true;
                AiCore.MockHasWater = false;
                try
                {
                    Vector3 myPos = new Vector3(0f, 0f, 0f);
                    Vector3 vaultPos = new Vector3(0f, 0f, 1.25f);
                    float[] crestHeights = new float[] { 0.60f, 0.90f, 1.25f, 1.50f };
                    for (int ci = 0; ci < crestHeights.Length; ci++)
                    {
                        float ch = crestHeights[ci];
                        Physics.SceneWalls.Clear();
                        Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = -0.25f, MaxY = 0f, MinZ = -2f, MaxZ = 4f });
                        Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = ch, MinZ = 0.50f, MaxZ = 0.75f });
                        bool ok = VeteranAndTacticsPatches.TestHasClearVaultArcAndLanding(myPos, vaultPos, 3, ch, out string rej);
                        Assert(ok, $"Section 10 valid crestHeight={ch} rejected: {rej}");
                    }

                    // Bridge/cliff drop (-5.0m floor) blocked normally, allowed when room/rooftop-trapped (maxDrop=7.5m)
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = -5.2f, MaxY = -5.0f, MinZ = 0.80f, MaxZ = 4.0f });
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 1.0f, MinZ = 0.50f, MaxZ = 0.75f });
                    Assert(!VeteranAndTacticsPatches.TestHasClearVaultArcAndLanding(myPos, vaultPos, 3, 1.0f, out string cliffRej, 1.68f) &&
                           cliffRej == "no_landing_floor",
                        $"Section 10 bridge/cliff drop (-5m) must reject with no_landing_floor, got '{cliffRej}'");
                    Assert(VeteranAndTacticsPatches.TestHasClearVaultArcAndLanding(myPos, vaultPos, 3, 1.0f, out string bailRej, 7.5f),
                        $"Section 10 escalated rooftop bailout (7.5m drop) should succeed, got '{bailRej}'");

                    // Deep water landing rejected
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = -0.25f, MaxY = 0.05f, MinZ = -2f, MaxZ = 4f });
                    AiCore.MockHasWater = true;
                    AiCore.MockWaterLevel = 0.0f;
                    Assert(!VeteranAndTacticsPatches.TestHasClearVaultArcAndLanding(myPos, vaultPos, 3, 0.90f, out string waterRej) &&
                           waterRej == "no_landing_floor",
                        $"Section 10 flooded landing must reject, got '{waterRej}'");
                    AiCore.MockHasWater = false;

                    // Blocked wall behind crest + low ceiling over landing
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = -0.25f, MaxY = 0f, MinZ = -2f, MaxZ = 4f });
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 1.0f, MinZ = 0.50f, MaxZ = 0.75f });
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 2.2f, MinZ = 1.10f, MaxZ = 1.40f });
                    Assert(!VeteranAndTacticsPatches.TestHasClearVaultArcAndLanding(myPos, new Vector3(0f, 0f, 1.65f), 3, 1.0f, out string behindRej) &&
                           behindRej == "blocked_behind_wall",
                        $"Section 10 wall behind crest must reject, got '{behindRej}'");

                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = -0.25f, MaxY = 0f, MinZ = -2f, MaxZ = 4f });
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 1.50f, MaxY = 2.2f, MinZ = 0.95f, MaxZ = 2.2f });
                    Assert(!VeteranAndTacticsPatches.TestHasClearVaultArcAndLanding(myPos, vaultPos, 3, 0.65f, out string ceilRej) &&
                           ceilRej == "landing_occupied",
                        $"Section 10 low ceiling above landing must reject, got '{ceilRej}'");

                    // Vehicle hull floor rejected
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = -0.25f, MaxY = 0.2f, MinZ = 0.80f, MaxZ = 3.0f, IsVehicle = true });
                    Assert(!VeteranAndTacticsPatches.TestHasClearVaultArcAndLanding(myPos, vaultPos, 3, 0.90f, out string vehRej) &&
                           vehRej == "vehicle_hull",
                        $"Section 10 vehicle hull landing must reject with vehicle_hull, got '{vehRej}'");

                    // Too-high +2.10m ledge rejected
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 1.85f, MaxY = 2.10f, MinZ = 0.80f, MaxZ = 3.0f });
                    Assert(!VeteranAndTacticsPatches.TestHasClearVaultArcAndLanding(myPos, vaultPos, 3, 1.50f, out string highRej) &&
                           highRej == "no_landing_floor",
                        $"Section 10 +2.10m ledge must reject, got '{highRej}'");
                }
                finally
                {
                    Physics.UseSceneWalls = false;
                    Physics.SceneWalls.Clear();
                    AiCore.MockHasWater = false;
                }
            }
            {
                // 11. Tank, Emplacement, Plane Turret Firing, CAS/Dive LOS & Vehicle Crew Sight Matrix
                Physics.UseSceneWalls = true;
                try
                {
                    Transform ownTankRoot = new Transform();
                    Transform enemyTankRoot = new Transform();
                    Transform empRoot = new Transform();
                    Transform planeRoot = new Transform();
                    Vector3 fwd = new Vector3(0f, 0f, 1f);

                    // 11A. AiCore.RaycastEnvironment core: ignoreRoot + ignoreVehicles + unsorted nearest-hit resolution
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -2f, MaxX = 2f, MinY = 0f, MaxY = 3f, MinZ = 0.5f, MaxZ = 1.5f, IsVehicle = true, RootTransform = ownTankRoot });
                    Assert(!AiCore.RaycastEnvironment(Vector3.zero, fwd, 20f, ownTankRoot, out _, ignoreVehicles: false),
                        "11A Own vehicle hull in front of origin must be ignored in open air!");

                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 5f, MinZ = 12f, MaxZ = 14f, IsVehicle = false });
                    Assert(AiCore.RaycastEnvironment(Vector3.zero, fwd, 20f, ownTankRoot, out RaycastHit mtnHit, ignoreVehicles: false) &&
                           mtnHit.point.z >= 11.9f && mtnHit.point.z <= 12.1f,
                        "11A Solid mountain behind own hull must still be detected!");

                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 5f, MinZ = 20f, MaxZ = 22f, IsVehicle = false });
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 5f, MinZ = 80f, MaxZ = 82f, IsVehicle = false });
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -2f, MaxX = 2f, MinY = 0f, MaxY = 3f, MinZ = 0.5f, MaxZ = 1.5f, IsVehicle = true, RootTransform = ownTankRoot });
                    Assert(AiCore.RaycastEnvironment(Vector3.zero, fwd, 100f, ownTankRoot, out RaycastHit nearWallHit, ignoreVehicles: false) &&
                           nearWallHit.point.z >= 19.9f && nearWallHit.point.z <= 20.1f,
                        $"11A Nearest-hit scan across unsorted walls failed: got z={nearWallHit.point.z}");

                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -2f, MaxX = 2f, MinY = 0f, MaxY = 3f, MinZ = 5f, MaxZ = 9f, IsVehicle = true, RootTransform = enemyTankRoot });
                    Assert(AiCore.RaycastEnvironment(Vector3.zero, fwd, 20f, ownTankRoot, out _, ignoreVehicles: false) &&
                           !AiCore.RaycastEnvironment(Vector3.zero, fwd, 20f, ownTankRoot, out _, ignoreVehicles: true),
                        "11A ignoreVehicles=false vs true failed against enemy vehicle hull!");

                    // 11B. IsTurretDirectFireBlockedByWall: tanks, static emplacements, indirect mortars, and planes
                    float[] tankDualDists = new float[] { 2.5f, 4.0f, 25.0f, 120.0f };
                    for (int di = 0; di < tankDualDists.Length; di++)
                    {
                        float d = tankDualDists[di];
                        Vector3 pivot = new Vector3(0f, 1.8f, 0f);
                        Vector3 muzzle = new Vector3(0f, 1.8f, 1.6f);
                        Physics.SceneWalls.Clear();
                        // Own mantlet/hull collider sitting between turret pivot & muzzle + enemy tank at target d
                        Physics.SceneWalls.Add(new Physics.WallBox { MinX = -1f, MaxX = 1f, MinY = 1.0f, MaxY = 2.4f, MinZ = 0.4f, MaxZ = 0.9f, IsVehicle = true, RootTransform = ownTankRoot });
                        Physics.SceneWalls.Add(new Physics.WallBox { MinX = -1.8f, MaxX = 1.8f, MinY = 0f, MaxY = 2.6f, MinZ = 1.6f + d - 2.0f, MaxZ = 1.6f + d + 2.5f, IsVehicle = true, RootTransform = enemyTankRoot });
                        Assert(!VeteranAndTacticsPatches.TestIsTurretDirectFireBlockedByWall(pivot, muzzle, fwd, false, false, true, true, d, ownTankRoot) &&
                               !VeteranAndTacticsPatches.TestIsTurretDirectFireBlockedByWall(pivot, muzzle, fwd, false, false, false, true, d, ownTankRoot),
                            $"11B Tank duel (75mm/coax) falsely blocked at dist={d}m!");
                    }

                    // Tank brawling enemy tank at 3.5m with building wall behind target at 5.0m -> must fire
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -1.8f, MaxX = 1.8f, MinY = 0f, MaxY = 2.6f, MinZ = 3.5f, MaxZ = 6.5f, IsVehicle = true, RootTransform = enemyTankRoot });
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 4.0f, MinZ = 6.6f, MaxZ = 7.2f, IsVehicle = false });
                    Assert(!VeteranAndTacticsPatches.TestIsTurretDirectFireBlockedByWall(
                            new Vector3(0f, 1.8f, 0f), new Vector3(0f, 1.8f, 1.6f), fwd, false, false, true, true, 3.5f, ownTankRoot),
                        "11B Building behind close 3.5m enemy tank should NOT block tank cannon!");

                    // Tank behind 1.8m stone wall targeting enemy at 25m -> MUST block
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 2.5f, MinZ = 3.2f, MaxZ = 3.6f, IsVehicle = false });
                    Assert(VeteranAndTacticsPatches.TestIsTurretDirectFireBlockedByWall(
                            new Vector3(0f, 1.8f, 0f), new Vector3(0f, 1.8f, 1.6f), fwd, false, false, true, true, 25f, ownTankRoot),
                        "11B Solid stone wall right in front of tank muzzle failed to block!");

                    // Tank barrel poked/clipped through masonry wall -> MUST block even for non-explosive MG
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 2.5f, MinZ = 0.6f, MaxZ = 0.9f, IsVehicle = false });
                    Assert(VeteranAndTacticsPatches.TestIsTurretDirectFireBlockedByWall(
                            new Vector3(0f, 1.8f, 0f), new Vector3(0f, 1.8f, 1.6f), fwd, false, false, false, true, 25f, ownTankRoot),
                        "11B Barrel clipped through masonry wall failed to block!");

                    // Static AT/MG emplacement with own gun shield between pivot & muzzle
                    foreach (float empD in new float[] { 15f, 80f, 180f })
                    {
                        Physics.SceneWalls.Clear();
                        Physics.SceneWalls.Add(new Physics.WallBox { MinX = -1f, MaxX = 1f, MinY = 0.4f, MaxY = 1.6f, MinZ = 0.35f, MaxZ = 0.55f, IsVehicle = true, RootTransform = empRoot });
                        Assert(!VeteranAndTacticsPatches.TestIsTurretDirectFireBlockedByWall(
                                new Vector3(0f, 1.0f, 0f), new Vector3(0f, 1.0f, 1.2f), fwd, false, false, true, true, empD, empRoot),
                            $"11B Shielded AT emplacement blocked by own gun shield at dist={empD}m!");
                        Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 2.5f, MinZ = 3.0f, MaxZ = 3.3f, IsVehicle = false });
                        Assert(VeteranAndTacticsPatches.TestIsTurretDirectFireBlockedByWall(
                                new Vector3(0f, 1.0f, 0f), new Vector3(0f, 1.0f, 1.2f), fwd, false, false, true, true, empD, empRoot),
                            $"11B Solid wall ahead of AT emplacement failed to block at dist={empD}m!");
                    }

                    // Indirect mortar behind courtyard wall -> must fire
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 2.5f, MinZ = 2.5f, MaxZ = 3.0f, IsVehicle = false });
                    Assert(!VeteranAndTacticsPatches.TestIsTurretDirectFireBlockedByWall(
                            new Vector3(0f, 0.8f, 0f), new Vector3(0f, 1.2f, 0.3f), fwd, false, isIndirect: true, isExplosive: true, true, 120f, empRoot),
                        "11B Indirect mortar behind courtyard wall should NOT be wall-blocked!");

                    // Plane with 20mm/37mm cannon + nose/wing collider ahead -> must never self-block
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -4f, MaxX = 4f, MinY = -1f, MaxY = 2f, MinZ = 1.0f, MaxZ = 3.0f, IsVehicle = false, RootTransform = planeRoot });
                    Assert(!VeteranAndTacticsPatches.TestIsTurretDirectFireBlockedByWall(
                            Vector3.zero, new Vector3(0f, 0f, 0.5f), fwd, isAirVehicle: true, isIndirect: false, isExplosive: true, true, 150f, planeRoot),
                        "11B Aircraft 20mm/37mm cannon should never be wall-blocked!");

                    // 11C. IsFriendlySoldierInTurretFireDanger: same-crew, close muzzle, corridor, 75mm blast, mortar impact
                    Physics.SceneWalls.Clear();
                    var sameVeh = new Vehicle { Pointer = new IntPtr(7001), IsAir = false };
                    var gunner = new Soldier { Pointer = new IntPtr(8001) };
                    var crewFriend = new Soldier { Pointer = new IntPtr(8002), OnVehicle = true, CurrentVeh = sameVeh, CenterPos = new Vector3(0f, 1.8f, 2.5f) };
                    crewFriend.transform.position = new Vector3(0f, 1.0f, 2.5f);
                    Assert(!VeteranAndTacticsPatches.TestIsFriendlySoldierInTurretFireDanger(
                            crewFriend, gunner.Pointer, sameVeh.Pointer, Vector3.zero, fwd, 120f, 6f, false, true, true, new Vector3(0f, 0f, 80f), 20f),
                        "11C Same-vehicle crew member should never block turret fire!");

                    var allyFront = new Soldier { Pointer = new IntPtr(8003), CenterPos = new Vector3(0.3f, 1.2f, 2.5f) };
                    allyFront.transform.position = new Vector3(0.3f, 0.3f, 2.5f);
                    Assert(VeteranAndTacticsPatches.TestIsFriendlySoldierInTurretFireDanger(
                            allyFront, gunner.Pointer, sameVeh.Pointer, new Vector3(0f, 1.2f, 0f), fwd, 120f, 6f, false, false, true, new Vector3(0f, 1.2f, 80f), 0f),
                        "11C Ally 2.5m directly in front of muzzle must block fire!");

                    var allyCorridor = new Soldier { Pointer = new IntPtr(8004), CenterPos = new Vector3(1.2f, 1.2f, 30f) };
                    allyCorridor.transform.position = new Vector3(1.2f, 0.3f, 30f);
                    var allySafeFlank = new Soldier { Pointer = new IntPtr(8005), CenterPos = new Vector3(10.0f, 1.2f, 30f) };
                    allySafeFlank.transform.position = new Vector3(10.0f, 0.3f, 30f);
                    Assert(VeteranAndTacticsPatches.TestIsFriendlySoldierInTurretFireDanger(
                            allyCorridor, gunner.Pointer, sameVeh.Pointer, new Vector3(0f, 1.2f, 0f), fwd, 120f, 6f, false, false, true, new Vector3(0f, 1.2f, 80f), 0f) &&
                           !VeteranAndTacticsPatches.TestIsFriendlySoldierInTurretFireDanger(
                            allySafeFlank, gunner.Pointer, sameVeh.Pointer, new Vector3(0f, 1.2f, 0f), fwd, 120f, 6f, false, false, true, new Vector3(0f, 1.2f, 80f), 0f),
                        "11C Downrange corridor check failed for 30m in-lane vs 10m lateral!");

                    var blastAiClose = new Soldier { Pointer = new IntPtr(8006), CenterPos = new Vector3(12f, 1f, 80f) };
                    blastAiClose.transform.position = new Vector3(12f, 0f, 80f);
                    var blastAiSafe = new Soldier { Pointer = new IntPtr(8007), CenterPos = new Vector3(25f, 1f, 80f) };
                    blastAiSafe.transform.position = new Vector3(25f, 0f, 80f);
                    var blastPlayer = new Soldier { Pointer = new IntPtr(8008), IsHumanPlayer = true, CenterPos = new Vector3(24f, 1f, 80f) };
                    blastPlayer.transform.position = new Vector3(24f, 0f, 80f);
                    Vector3 shellTarget = new Vector3(0f, 1f, 80f);
                    Assert(VeteranAndTacticsPatches.TestIsFriendlySoldierInTurretFireDanger(
                            blastAiClose, gunner.Pointer, sameVeh.Pointer, Vector3.zero, fwd, 120f, 6f, false, true, true, shellTarget, 20f) &&
                           !VeteranAndTacticsPatches.TestIsFriendlySoldierInTurretFireDanger(
                            blastAiSafe, gunner.Pointer, sameVeh.Pointer, Vector3.zero, fwd, 120f, 6f, false, true, true, shellTarget, 20f) &&
                           VeteranAndTacticsPatches.TestIsFriendlySoldierInTurretFireDanger(
                            blastPlayer, gunner.Pointer, sameVeh.Pointer, Vector3.zero, fwd, 120f, 6f, false, true, true, shellTarget, 20f),
                        "11C 75mm blast danger-close check failed for AI 12m/25m or player +6m margin!");

                    var mortarUnderArc = new Soldier { Pointer = new IntPtr(8009), CenterPos = new Vector3(0f, 1f, 45f) };
                    mortarUnderArc.transform.position = new Vector3(0f, 0f, 45f);
                    var mortarImpact = new Soldier { Pointer = new IntPtr(8010), CenterPos = new Vector3(25f, 1f, 140f) };
                    mortarImpact.transform.position = new Vector3(25f, 0f, 140f);
                    Assert(!VeteranAndTacticsPatches.TestIsFriendlySoldierInTurretFireDanger(
                            mortarUnderArc, gunner.Pointer, IntPtr.Zero, Vector3.zero, fwd, 160f, 6f, isIndirect: true, isExplosive: true, true, new Vector3(0f, 1f, 140f), 30f) &&
                           VeteranAndTacticsPatches.TestIsFriendlySoldierInTurretFireDanger(
                            mortarImpact, gunner.Pointer, IntPtr.Zero, Vector3.zero, fwd, 160f, 6f, isIndirect: true, isExplosive: true, true, new Vector3(0f, 1f, 140f), 30f),
                        "11C Indirect mortar must ignore allies beneath high arc and protect impact zone!");

                    // 11D. Aircraft CAS LOS, dive pull-up obstacle ray & runway/carrier probe
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -4f, MaxX = 4f, MinY = -2f, MaxY = 2f, MinZ = 1.5f, MaxZ = 3.5f, IsVehicle = true, RootTransform = planeRoot });
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -2f, MaxX = 2f, MinY = -1f, MaxY = 2f, MinZ = 117f, MaxZ = 123f, IsVehicle = true, RootTransform = enemyTankRoot });
                    Assert(!AiCore.RaycastEnvironment(Vector3.zero, fwd, 116f, planeRoot, out _, ignoreVehicles: true),
                        "11D Aircraft CAS LOS must ignore own nose/prop and target vehicle hull!");
                    Assert(!AiCore.IsDirectionBlockedByEnvironment(Vector3.zero, fwd, 110f, planeRoot, ignoreVehicles: true),
                        "11D Aircraft dive pull-up ray must ignore own nose/prop and dive target hull!");

                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -20f, MaxX = 20f, MinY = -5f, MaxY = 10f, MinZ = 75f, MaxZ = 80f, IsVehicle = false });
                    Assert(AiCore.IsDirectionBlockedByEnvironment(Vector3.zero, fwd, 110f, planeRoot, ignoreVehicles: true),
                        "11D Aircraft dive pull-up ray MUST trigger for terrain/hill at 75m!");

                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -4f, MaxX = 4f, MinY = -2.0f, MaxY = -1.2f, MinZ = -2f, MaxZ = 2f, IsVehicle = true, RootTransform = planeRoot });
                    Assert(!AiCore.RaycastEnvironment(Vector3.up * 1.8f, Vector3.down, 11f, planeRoot, out _, ignoreVehicles: false),
                        "11D Runway probe must ignore plane's own belly/landing gear!");
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -30f, MaxX = 30f, MinY = -6.5f, MaxY = -5.5f, MinZ = -30f, MaxZ = 30f, IsVehicle = true, RootTransform = new Transform() });
                    Assert(AiCore.RaycastEnvironment(Vector3.up * 1.8f, Vector3.down, 11f, planeRoot, out _, ignoreVehicles: false),
                        "11D Runway probe must still detect carrier deck / runway below plane!");

                    // 11E. VisionPatches.IsObserverBlockedByCloseThinWall: shielded gunner vs foot soldier vs point-blank tank
                    Transform obsRoot = new Transform();
                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -2f, MaxX = 2f, MinY = 0f, MaxY = 2f, MinZ = 0.20f, MaxZ = 0.30f, IsVehicle = true, RootTransform = empRoot });
                    Assert(!VisionPatches.TestIsObserverBlockedByCloseThinWall(true, new Vector3(0f, 1.2f, 0f), new Vector3(0f, 0f, 20f), 20f, obsRoot, false),
                        "11E Vehicle/emplacement gunner behind close gun shield must NOT lose sight!");
                    Assert(!VisionPatches.TestIsObserverBlockedByCloseThinWall(false, new Vector3(0f, 1.5f, 0f), new Vector3(0f, 0f, 0.60f), 0.60f, obsRoot, targetIsVehicle: true),
                        "11E Foot soldier 0.60m from enemy tank hull must NOT be sight-blocked by target tank!");

                    Physics.SceneWalls.Clear();
                    Physics.SceneWalls.Add(new Physics.WallBox { MinX = -5f, MaxX = 5f, MinY = 0f, MaxY = 2.5f, MinZ = 0.20f, MaxZ = 0.35f, IsVehicle = false });
                    Assert(VisionPatches.TestIsObserverBlockedByCloseThinWall(false, new Vector3(0f, 1.5f, 0f), new Vector3(0f, 0f, 20f), 20f, obsRoot, false),
                        "11E Foot soldier pressed against solid 0.25m brick wall MUST remain sight-blocked!");

                    // 11F. Tactical Stealth-Crouch triggers, emergency/CQB overrides & footstep acoustic scaling
                    Assert(Mathf.Abs(VisionPatches.ComputeFootstepAudibleRadius(SoldierPose.Prone, false) - 0f) < 1e-4f &&
                           Mathf.Abs(VisionPatches.ComputeFootstepAudibleRadius(SoldierPose.Prone, true) - 0f) < 1e-4f &&
                           Mathf.Abs(VisionPatches.ComputeFootstepAudibleRadius(SoldierPose.Crouch, false) - 4.5f) < 1e-4f &&
                           Mathf.Abs(VisionPatches.ComputeFootstepAudibleRadius(SoldierPose.Crouch, true) - 7.0f) < 1e-4f &&
                           Mathf.Abs(VisionPatches.ComputeFootstepAudibleRadius(SoldierPose.Idle, false) - 11.5f) < 1e-4f &&
                           Mathf.Abs(VisionPatches.ComputeFootstepAudibleRadius(SoldierPose.Idle, true) - 18.0f) < 1e-4f,
                        "11F ComputeFootstepAudibleRadius values mismatch (Prone=0, Crouch=4.5/7.0, Stand=11.5/18.0)!");

                    Assert(VeteranAndTacticsPatches.EvaluateTacticalStealthCrouch(true, false, false, false, false, false, false, false) &&
                           VeteranAndTacticsPatches.EvaluateTacticalStealthCrouch(false, true, false, false, false, false, false, false) &&
                           VeteranAndTacticsPatches.EvaluateTacticalStealthCrouch(false, false, true, false, false, false, false, false) &&
                           VeteranAndTacticsPatches.EvaluateTacticalStealthCrouch(false, false, false, true, false, false, false, false) &&
                           VeteranAndTacticsPatches.EvaluateTacticalStealthCrouch(false, false, false, false, true, false, false, false),
                        "11F Each of the 5 stealth triggers must enable tactical stealth-crouch when uncompromised!");

                    Assert(!VeteranAndTacticsPatches.EvaluateTacticalStealthCrouch(false, false, false, false, false, false, false, false),
                        "11F Open-field movement with no stealth trigger must stay upright!");

                    Assert(!VeteranAndTacticsPatches.EvaluateTacticalStealthCrouch(true, true, true, true, true, true, false, false) &&
                           !VeteranAndTacticsPatches.EvaluateTacticalStealthCrouch(true, true, true, true, true, false, true, false) &&
                           !VeteranAndTacticsPatches.EvaluateTacticalStealthCrouch(true, true, true, true, true, false, false, true),
                        "11F Emergency/charging, compromised/loud fire, or point-blank CQB must override ALL stealth triggers!");

                    // 11G. H.A.R.M. Prone-for-Safety vs Win-to-Rise utility trade-off
                    // 1) Exposed under fire with low win urgency -> drops prone for safety
                    Assert(VeteranAndTacticsPatches.EvaluateProneSafetyVsWin(
                            currentlyProne: false, inCoverOrShelteredWall: false, hasClearProneLof: true,
                            hasVisibleEnemy: true, enemyDist: 55f, suppression: 30f, smoothedHarm: 0.42f, totalHarm: 0.45f,
                            underFireRecently: true, underShelling: false, enemyPlaneOverhead: false, isOpenGunnerOrMarksman: false,
                            wSurv: 1.40f, wWin: 0.90f, winUrgency: 0.25f, canHarm: 0.65f, objectiveTickingAgainst: false, wantsToMoveOrWinPush: false),
                        "11G Exposed soldier under fire (TotalHarm=0.45) with low win urgency should choose Prone for safety!");

                    // 2) Already prone exposed during brief lull (hysteresis keeps them down at 0.26 harm)
                    Assert(VeteranAndTacticsPatches.EvaluateProneSafetyVsWin(
                            currentlyProne: true, inCoverOrShelteredWall: false, hasClearProneLof: false,
                            hasVisibleEnemy: false, enemyDist: 999f, suppression: 12f, smoothedHarm: 0.26f, totalHarm: 0.26f,
                            underFireRecently: false, underShelling: false, enemyPlaneOverhead: false, isOpenGunnerOrMarksman: false,
                            wSurv: 1.35f, wWin: 0.95f, winUrgency: 0.20f, canHarm: 0f, objectiveTickingAgainst: false, wantsToMoveOrWinPush: false),
                        "11G Already prone exposed soldier should stay Prone during brief lull (hysteresis)!");

                    // 3) High Win urgency + ticking objective + bold wWin -> gets them up!
                    Assert(!VeteranAndTacticsPatches.EvaluateProneSafetyVsWin(
                            currentlyProne: true, inCoverOrShelteredWall: false, hasClearProneLof: true,
                            hasVisibleEnemy: true, enemyDist: 45f, suppression: 28f, smoothedHarm: 0.35f, totalHarm: 0.38f,
                            underFireRecently: true, underShelling: false, enemyPlaneOverhead: false, isOpenGunnerOrMarksman: false,
                            wSurv: 1.15f, wWin: 1.35f, winUrgency: 0.85f, canHarm: 0.70f, objectiveTickingAgainst: true, wantsToMoveOrWinPush: false),
                        "11G High Win urgency + ticking objective must lift bot out of Prone!");

                    // 4) Exposed with blocked prone LOF & moderate threat -> rises to shoot, vs extreme fire+shelling -> stays flat!
                    Assert(!VeteranAndTacticsPatches.EvaluateProneSafetyVsWin(
                            currentlyProne: true, inCoverOrShelteredWall: false, hasClearProneLof: false,
                            hasVisibleEnemy: true, enemyDist: 42f, suppression: 22f, smoothedHarm: 0.34f, totalHarm: 0.34f,
                            underFireRecently: true, underShelling: false, enemyPlaneOverhead: false, isOpenGunnerOrMarksman: false,
                            wSurv: 1.25f, wWin: 1.05f, winUrgency: 0.45f, canHarm: 0.85f, objectiveTickingAgainst: false, wantsToMoveOrWinPush: false),
                        "11G Blocked prone LOF against visible enemy under moderate threat should rise to Crouch!");
                    Assert(VeteranAndTacticsPatches.EvaluateProneSafetyVsWin(
                            currentlyProne: true, inCoverOrShelteredWall: false, hasClearProneLof: false,
                            hasVisibleEnemy: true, enemyDist: 42f, suppression: 70f, smoothedHarm: 0.80f, totalHarm: 0.85f,
                            underFireRecently: true, underShelling: true, enemyPlaneOverhead: false, isOpenGunnerOrMarksman: false,
                            wSurv: 1.50f, wWin: 0.90f, winUrgency: 0.30f, canHarm: 0.70f, objectiveTickingAgainst: false, wantsToMoveOrWinPush: false),
                        "11G Extreme fire + shelling must keep pinned soldier Prone even without prone LOF!");

                    // 5) Intact cover under moderate fire -> stays crouched in cover; under shelling -> flattens in cover
                    Assert(!VeteranAndTacticsPatches.EvaluateProneSafetyVsWin(
                            currentlyProne: false, inCoverOrShelteredWall: true, hasClearProneLof: false,
                            hasVisibleEnemy: false, enemyDist: 999f, suppression: 30f, smoothedHarm: 0.35f, totalHarm: 0.35f,
                            underFireRecently: true, underShelling: false, enemyPlaneOverhead: false, isOpenGunnerOrMarksman: false,
                            wSurv: 1.35f, wWin: 0.95f, winUrgency: 0.30f, canHarm: 0.40f, objectiveTickingAgainst: false, wantsToMoveOrWinPush: false),
                        "11G Soldier in intact cover under moderate fire should hold Crouch rather than belly-flopping!");
                    Assert(VeteranAndTacticsPatches.EvaluateProneSafetyVsWin(
                            currentlyProne: false, inCoverOrShelteredWall: true, hasClearProneLof: false,
                            hasVisibleEnemy: false, enemyDist: 999f, suppression: 45f, smoothedHarm: 0.60f, totalHarm: 0.75f,
                            underFireRecently: true, underShelling: true, enemyPlaneOverhead: false, isOpenGunnerOrMarksman: false,
                            wSurv: 1.45f, wWin: 0.90f, winUrgency: 0.25f, canHarm: 0f, objectiveTickingAgainst: false, wantsToMoveOrWinPush: false),
                        "11G Soldier in cover under active artillery shelling should go Prone!");

                    // 6) Open-ground gunner/marksman (65m, clear prone LOF) -> Prone, unless objective ticks urgently
                    Assert(VeteranAndTacticsPatches.EvaluateProneSafetyVsWin(
                            currentlyProne: false, inCoverOrShelteredWall: false, hasClearProneLof: true,
                            hasVisibleEnemy: true, enemyDist: 65f, suppression: 0f, smoothedHarm: 0.05f, totalHarm: 0.05f,
                            underFireRecently: false, underShelling: false, enemyPlaneOverhead: false, isOpenGunnerOrMarksman: true,
                            wSurv: 1.35f, wWin: 0.95f, winUrgency: 0.20f, canHarm: 0.85f, objectiveTickingAgainst: false, wantsToMoveOrWinPush: false),
                        "11G Open-ground MG/Marksman at 65m with clear prone LOF should go Prone!");
                    Assert(!VeteranAndTacticsPatches.EvaluateProneSafetyVsWin(
                            currentlyProne: true, inCoverOrShelteredWall: false, hasClearProneLof: true,
                            hasVisibleEnemy: true, enemyDist: 65f, suppression: 0f, smoothedHarm: 0.05f, totalHarm: 0.05f,
                            underFireRecently: false, underShelling: false, enemyPlaneOverhead: false, isOpenGunnerOrMarksman: true,
                            wSurv: 1.10f, wWin: 1.35f, winUrgency: 0.90f, canHarm: 0.75f, objectiveTickingAgainst: true, wantsToMoveOrWinPush: false),
                        "11G Urgent ticking objective + high wWin should get open gunner/marksman up!");

                    // 7) Outdoor unmarked under enemy aircraft -> Prone
                    Assert(VeteranAndTacticsPatches.EvaluateProneSafetyVsWin(
                            currentlyProne: false, inCoverOrShelteredWall: false, hasClearProneLof: true,
                            hasVisibleEnemy: false, enemyDist: 999f, suppression: 0f, smoothedHarm: 0f, totalHarm: 0f,
                            underFireRecently: false, underShelling: false, enemyPlaneOverhead: true, isOpenGunnerOrMarksman: false,
                            wSurv: 1.35f, wWin: 0.95f, winUrgency: 0.25f, canHarm: 0f, objectiveTickingAgainst: false, wantsToMoveOrWinPush: false),
                        "11G Outdoor bot under enemy aircraft with low win urgency should go Prone!");

                    // 8) Danger cleared, close CQB (10m), or moving push -> never stay Prone
                    Assert(!VeteranAndTacticsPatches.EvaluateProneSafetyVsWin(
                            currentlyProne: true, inCoverOrShelteredWall: false, hasClearProneLof: true,
                            hasVisibleEnemy: false, enemyDist: 999f, suppression: 0f, smoothedHarm: 0.05f, totalHarm: 0.05f,
                            underFireRecently: false, underShelling: false, enemyPlaneOverhead: false, isOpenGunnerOrMarksman: false,
                            wSurv: 1.50f, wWin: 0.85f, winUrgency: 0.20f, canHarm: 0f, objectiveTickingAgainst: false, wantsToMoveOrWinPush: false) &&
                           !VeteranAndTacticsPatches.EvaluateProneSafetyVsWin(
                            currentlyProne: true, inCoverOrShelteredWall: false, hasClearProneLof: true,
                            hasVisibleEnemy: true, enemyDist: 10f, suppression: 50f, smoothedHarm: 0.70f, totalHarm: 0.80f,
                            underFireRecently: true, underShelling: false, enemyPlaneOverhead: false, isOpenGunnerOrMarksman: false,
                            wSurv: 1.60f, wWin: 0.80f, winUrgency: 0.20f, canHarm: 0.90f, objectiveTickingAgainst: false, wantsToMoveOrWinPush: false) &&
                           !VeteranAndTacticsPatches.EvaluateProneSafetyVsWin(
                            currentlyProne: true, inCoverOrShelteredWall: false, hasClearProneLof: true,
                            hasVisibleEnemy: false, enemyDist: 999f, suppression: 60f, smoothedHarm: 0.70f, totalHarm: 0.80f,
                            underFireRecently: true, underShelling: true, enemyPlaneOverhead: false, isOpenGunnerOrMarksman: false,
                            wSurv: 1.60f, wWin: 0.80f, winUrgency: 0.20f, canHarm: 0f, objectiveTickingAgainst: false, wantsToMoveOrWinPush: true),
                        "11G Safe area, close CQB (10m), or active move push must NEVER choose/stay Prone!");

                    // 11H. Medic enemy-heal safety gate (only when medic themselves is confidently safe)
                    Assert(VeteranAndTacticsPatches.EvaluateMedicConfidentlySafeForEnemyHeal(
                            medicAliveAndStanding: true, medicBleeding: false, medicInVehicleOrCarrying: false,
                            suppression: 5.9f, underFireRecently: false, hasVisibleEnemy: false, hasActiveCombatEnemyNearby: false,
                            medicHarm: 0.15f, patientSpotHarm: 0.19f, underShellingOrDangerClose: false, enemyPlaneOverhead: false,
                            hasWoundedFriendlyNearby: false),
                        "11H Calm post-fight lull with zero wounded friendlies should permit enemy POW syringe heal!");
                    Assert(!VeteranAndTacticsPatches.EvaluateMedicConfidentlySafeForEnemyHeal(
                            medicAliveAndStanding: true, medicBleeding: false, medicInVehicleOrCarrying: false,
                            suppression: 0f, underFireRecently: false, hasVisibleEnemy: false, hasActiveCombatEnemyNearby: false,
                            medicHarm: 0f, patientSpotHarm: 0f, underShellingOrDangerClose: false, enemyPlaneOverhead: false,
                            hasWoundedFriendlyNearby: true),
                        "11H Nearby wounded friendly must ALWAYS block enemy syringe heal!");
                    Assert(!VeteranAndTacticsPatches.EvaluateMedicConfidentlySafeForEnemyHeal(
                            medicAliveAndStanding: true, medicBleeding: false, medicInVehicleOrCarrying: false,
                            suppression: 6.0f, underFireRecently: false, hasVisibleEnemy: false, hasActiveCombatEnemyNearby: false,
                            medicHarm: 0f, patientSpotHarm: 0f, underShellingOrDangerClose: false, enemyPlaneOverhead: false,
                            hasWoundedFriendlyNearby: false) &&
                           !VeteranAndTacticsPatches.EvaluateMedicConfidentlySafeForEnemyHeal(
                            medicAliveAndStanding: true, medicBleeding: false, medicInVehicleOrCarrying: false,
                            suppression: 0f, underFireRecently: true, hasVisibleEnemy: false, hasActiveCombatEnemyNearby: false,
                            medicHarm: 0f, patientSpotHarm: 0f, underShellingOrDangerClose: false, enemyPlaneOverhead: false,
                            hasWoundedFriendlyNearby: false) &&
                           !VeteranAndTacticsPatches.EvaluateMedicConfidentlySafeForEnemyHeal(
                            medicAliveAndStanding: true, medicBleeding: false, medicInVehicleOrCarrying: false,
                            suppression: 0f, underFireRecently: false, hasVisibleEnemy: true, hasActiveCombatEnemyNearby: false,
                            medicHarm: 0f, patientSpotHarm: 0f, underShellingOrDangerClose: false, enemyPlaneOverhead: false,
                            hasWoundedFriendlyNearby: false) &&
                           !VeteranAndTacticsPatches.EvaluateMedicConfidentlySafeForEnemyHeal(
                            medicAliveAndStanding: true, medicBleeding: false, medicInVehicleOrCarrying: false,
                            suppression: 0f, underFireRecently: false, hasVisibleEnemy: false, hasActiveCombatEnemyNearby: true,
                            medicHarm: 0f, patientSpotHarm: 0f, underShellingOrDangerClose: false, enemyPlaneOverhead: false,
                            hasWoundedFriendlyNearby: false),
                        "11H Suppression >= 6, underFire, visible enemy, or nearby combat enemy out of LOS must block enemy heal!");
                    Assert(!VeteranAndTacticsPatches.EvaluateMedicConfidentlySafeForEnemyHeal(
                            medicAliveAndStanding: true, medicBleeding: false, medicInVehicleOrCarrying: false,
                            suppression: 0f, underFireRecently: false, hasVisibleEnemy: false, hasActiveCombatEnemyNearby: false,
                            medicHarm: 0.16f, patientSpotHarm: 0f, underShellingOrDangerClose: false, enemyPlaneOverhead: false,
                            hasWoundedFriendlyNearby: false) &&
                           !VeteranAndTacticsPatches.EvaluateMedicConfidentlySafeForEnemyHeal(
                            medicAliveAndStanding: true, medicBleeding: false, medicInVehicleOrCarrying: false,
                            suppression: 0f, underFireRecently: false, hasVisibleEnemy: false, hasActiveCombatEnemyNearby: false,
                            medicHarm: 0f, patientSpotHarm: 0.20f, underShellingOrDangerClose: false, enemyPlaneOverhead: false,
                            hasWoundedFriendlyNearby: false) &&
                           !VeteranAndTacticsPatches.EvaluateMedicConfidentlySafeForEnemyHeal(
                            medicAliveAndStanding: true, medicBleeding: false, medicInVehicleOrCarrying: false,
                            suppression: 0f, underFireRecently: false, hasVisibleEnemy: false, hasActiveCombatEnemyNearby: false,
                            medicHarm: 0f, patientSpotHarm: 0f, underShellingOrDangerClose: true, enemyPlaneOverhead: false,
                            hasWoundedFriendlyNearby: false) &&
                           !VeteranAndTacticsPatches.EvaluateMedicConfidentlySafeForEnemyHeal(
                            medicAliveAndStanding: true, medicBleeding: false, medicInVehicleOrCarrying: false,
                            suppression: 0f, underFireRecently: false, hasVisibleEnemy: false, hasActiveCombatEnemyNearby: false,
                            medicHarm: 0f, patientSpotHarm: 0f, underShellingOrDangerClose: false, enemyPlaneOverhead: true,
                            hasWoundedFriendlyNearby: false),
                        "11H Elevated HARM (>=0.16 / patient>=0.20), shelling, or enemy plane overhead must block enemy heal!");
                    Assert(!VeteranAndTacticsPatches.EvaluateMedicConfidentlySafeForEnemyHeal(
                            medicAliveAndStanding: true, medicBleeding: true, medicInVehicleOrCarrying: false,
                            suppression: 0f, underFireRecently: false, hasVisibleEnemy: false, hasActiveCombatEnemyNearby: false,
                            medicHarm: 0f, patientSpotHarm: 0f, underShellingOrDangerClose: false, enemyPlaneOverhead: false,
                            hasWoundedFriendlyNearby: false) &&
                           !VeteranAndTacticsPatches.EvaluateMedicConfidentlySafeForEnemyHeal(
                            medicAliveAndStanding: false, medicBleeding: false, medicInVehicleOrCarrying: false,
                            suppression: 0f, underFireRecently: false, hasVisibleEnemy: false, hasActiveCombatEnemyNearby: false,
                            medicHarm: 0f, patientSpotHarm: 0f, underShellingOrDangerClose: false, enemyPlaneOverhead: false,
                            hasWoundedFriendlyNearby: false) &&
                           !VeteranAndTacticsPatches.EvaluateMedicConfidentlySafeForEnemyHeal(
                            medicAliveAndStanding: true, medicBleeding: false, medicInVehicleOrCarrying: true,
                            suppression: 0f, underFireRecently: false, hasVisibleEnemy: false, hasActiveCombatEnemyNearby: false,
                            medicHarm: 0f, patientSpotHarm: 0f, underShellingOrDangerClose: false, enemyPlaneOverhead: false,
                            hasWoundedFriendlyNearby: false),
                        "11H Bleeding, incapacitated/charging, or in-vehicle/carrying medic must never heal enemies!");

                    // 11I. Blind-smoke traversal high-risk assessment (never charge blindly through smoke without far-side intel)
                    Assert(!AiCore.EvaluateBlindSmokeTraversalRisk(false, false, false, false, false, 0.12f, out float clearHarm) &&
                           Mathf.Abs(clearHarm - 0.12f) < 1e-4f,
                        "11I Clear path without smoke must preserve known Harm!");
                    Assert(AiCore.EvaluateBlindSmokeTraversalRisk(true, false, false, false, false, 0.12f, out float blindHarm) &&
                           blindHarm >= 0.68f,
                        "11I Blind smoke ahead with no friendly eyes beyond smoke MUST mark high risk (Harm >= 0.68)!");
                    Assert(!AiCore.EvaluateBlindSmokeTraversalRisk(true, false, true, false, false, 0.12f, out float spotHarm) &&
                           Mathf.Abs(spotHarm - 0.12f) < 1e-4f,
                        "11I Smoke with calm friendly eyes already beyond it must allow traversal without inflating Harm!");
                    Assert(!AiCore.EvaluateBlindSmokeTraversalRisk(true, true, false, false, false, 0.15f, out float insideHarm) &&
                           Mathf.Abs(insideHarm - 0.15f) < 1e-4f &&
                           !AiCore.EvaluateBlindSmokeTraversalRisk(true, false, false, true, false, 0.25f, out float escHarm) &&
                           Mathf.Abs(escHarm - 0.25f) < 1e-4f,
                        "11I Already-inside smoke or hazard-escape/retreat must never trap bots!");
                    Assert(!AiCore.EvaluateBlindSmokeTraversalRisk(true, false, false, false, true, 0.12f, out float despHarm) &&
                           despHarm >= 0.44f,
                        "11I Desperate overtime/Gyokusai may push through smoke if needed, raising caution floor to 0.44!");

                    // 11J. Large-group, shoulder-bounce, moving-assault & satchel grenade FF clearance
                    Vector3 gThrower = new Vector3(0f, 0f, 0f);
                    Vector3 gTarget = new Vector3(0f, 0f, 25f);
                    Assert(VeteranAndTacticsPatches.EvaluateGrenadeFriendlyClearance(
                            gThrower, gTarget, 25f, new Vector3(20f, 0f, 10f),
                            friendlyIsHumanPlayer: false, friendlyMovingTowardTarget: false,
                            baseAiSafeRadius: 11.5f, basePlayerSafeRadius: 15.0f),
                        "11J Clear throw (friendly 20m off lateral arc) must be allowed!");
                    Assert(!VeteranAndTacticsPatches.EvaluateGrenadeFriendlyClearance(
                            gThrower, gTarget, 25f, new Vector3(0f, 0f, 15f),
                            friendlyIsHumanPlayer: false, friendlyMovingTowardTarget: false,
                            baseAiSafeRadius: 11.5f, basePlayerSafeRadius: 15.0f),
                        "11J AI friendly within 10m of target must block frag throw!");
                    Assert(!VeteranAndTacticsPatches.EvaluateGrenadeFriendlyClearance(
                            gThrower, gTarget, 25f, new Vector3(13.5f, 0f, 25f),
                            friendlyIsHumanPlayer: true, friendlyMovingTowardTarget: false,
                            baseAiSafeRadius: 11.5f, basePlayerSafeRadius: 15.0f),
                        "11J Human player (local or remote Co-op) at 13.5m must be protected by 15.0m player envelope!");
                    Assert(!VeteranAndTacticsPatches.EvaluateGrenadeFriendlyClearance(
                            gThrower, gTarget, 25f, new Vector3(0.4f, 0f, 0.6f),
                            friendlyIsHumanPlayer: false, friendlyMovingTowardTarget: false,
                            baseAiSafeRadius: 11.5f, basePlayerSafeRadius: 15.0f),
                        "11J Teammate 0.6m ahead / 0.4m lateral (close crowd shoulder bounce) must block throw!");
                    Assert(VeteranAndTacticsPatches.EvaluateGrenadeFriendlyClearance(
                            gThrower, gTarget, 25f, new Vector3(0f, 0f, -0.8f),
                            friendlyIsHumanPlayer: false, friendlyMovingTowardTarget: false,
                            baseAiSafeRadius: 11.5f, basePlayerSafeRadius: 15.0f) &&
                           !VeteranAndTacticsPatches.EvaluateGrenadeFriendlyClearance(
                            gThrower, gTarget, 25f, new Vector3(0f, 0f, -0.8f),
                            friendlyIsHumanPlayer: true, friendlyMovingTowardTarget: false,
                            baseAiSafeRadius: 11.5f, basePlayerSafeRadius: 15.0f),
                        "11J Friendly 0.8m behind thrower should allow AI throw but block close human-player envelope!");
                    Assert(VeteranAndTacticsPatches.EvaluateGrenadeFriendlyClearance(
                            gThrower, gTarget, 25f, new Vector3(13.5f, 0f, 25f),
                            friendlyIsHumanPlayer: false, friendlyMovingTowardTarget: false,
                            baseAiSafeRadius: 11.5f, basePlayerSafeRadius: 15.0f) &&
                           !VeteranAndTacticsPatches.EvaluateGrenadeFriendlyClearance(
                            gThrower, gTarget, 25f, new Vector3(13.5f, 0f, 25f),
                            friendlyIsHumanPlayer: false, friendlyMovingTowardTarget: true,
                            baseAiSafeRadius: 11.5f, basePlayerSafeRadius: 15.0f),
                        "11J Advancing friendly at 13.5m heading toward target must trigger +3.0m lead buffer!");
                    Assert(!VeteranAndTacticsPatches.EvaluateGrenadeFriendlyClearance(
                            gThrower, gTarget, 25f, new Vector3(15.0f, 0f, 25f),
                            friendlyIsHumanPlayer: false, friendlyMovingTowardTarget: false,
                            baseAiSafeRadius: 16.0f, basePlayerSafeRadius: 16.0f) &&
                           VeteranAndTacticsPatches.EvaluateGrenadeFriendlyClearance(
                            gThrower, gTarget, 25f, new Vector3(18.0f, 0f, 25f),
                            friendlyIsHumanPlayer: false, friendlyMovingTowardTarget: false,
                            baseAiSafeRadius: 16.0f, basePlayerSafeRadius: 16.0f),
                        "11J Heavy satchel/demo scaled radius (16.0m) must block friendly at 15.0m and allow at 18.0m!");

                    // 11K. Player-parity gunfire/tank engine acoustics & rain/combat masking
                    Assert(Mathf.Abs(VisionPatches.ComputeGunfireRainMaskMultiplier(0f) - 1.0f) < 1e-4f &&
                           Mathf.Abs(VisionPatches.ComputeGunfireRainMaskMultiplier(0.5f) - 0.89f) < 1e-4f &&
                           Mathf.Abs(VisionPatches.ComputeGunfireRainMaskMultiplier(1.0f) - 0.78f) < 1e-4f,
                        "11K Gunfire rain mask multiplier must interpolate cleanly from 1.0x down to 0.78x in heavy rain!");

                    float mvTank = VisionPatches.ComputeVehicleEngineAudibleRadius(150f, isTank: true, isMoving: true, rain01: 0f, loudCombatNear: false);
                    float idleTank = VisionPatches.ComputeVehicleEngineAudibleRadius(150f, isTank: true, isMoving: false, rain01: 0f, loudCombatNear: false);
                    float mvTruck = VisionPatches.ComputeVehicleEngineAudibleRadius(90f, isTank: false, isMoving: true, rain01: 0f, loudCombatNear: false);
                    float idleTruck = VisionPatches.ComputeVehicleEngineAudibleRadius(90f, isTank: false, isMoving: false, rain01: 0f, loudCombatNear: false);
                    Assert(mvTank > 100f && mvTank < 150f &&
                           idleTank > 45f && idleTank <= 55f && idleTank < mvTank &&
                           Mathf.Abs(mvTruck - 58.5f) < 1e-3f && mvTruck < 90f &&
                           idleTruck > 20f && idleTruck <= 32f && idleTruck < mvTruck,
                        "11K Bots must hear moving/idling tanks & trucks proportionally while staying strictly below player MaxSoundDistance!");

                    float rainTank = VisionPatches.ComputeVehicleEngineAudibleRadius(150f, isTank: true, isMoving: true, rain01: 1.0f, loudCombatNear: false);
                    float combatTank = VisionPatches.ComputeVehicleEngineAudibleRadius(150f, isTank: true, isMoving: true, rain01: 0f, loudCombatNear: true);
                    float bothTank = VisionPatches.ComputeVehicleEngineAudibleRadius(150f, isTank: true, isMoving: true, rain01: 1.0f, loudCombatNear: true);
                    Assert(Mathf.Abs(rainTank - mvTank * 0.65f) < 0.05f &&
                           Mathf.Abs(combatTank - mvTank * 0.62f) < 0.05f &&
                           bothTank >= mvTank * 0.38f - 0.01f && bothTank < rainTank && bothTank < combatTank,
                        "11K Heavy rain (~0.65x), nearby explosions (~0.62x), and combined masking (>=0.38x) must dampen tank engine range!");

                    float extremeTank = VisionPatches.ComputeVehicleEngineAudibleRadius(300f, isTank: true, isMoving: true, rain01: 0f, loudCombatNear: false);
                    float extremeTruck = VisionPatches.ComputeVehicleEngineAudibleRadius(300f, isTank: false, isMoving: true, rain01: 0f, loudCombatNear: false);
                    Assert(extremeTank <= 155f + 1e-3f && extremeTruck <= 95f + 1e-3f,
                        "11K Moving tank/truck acoustic hearing must honor hard 155m / 95m ceiling!");

                    VisionPatches.ClearRing();
                    Time.time = 200f;
                    AiCore.RainIntensity01 = 0f;
                    VisionPatches.InjectGunfire(new IntPtr(9101), new Vector3(0f, 0f, 185f), "Hungary_axis", 199.5f, 200.0f);
                    Assert(VisionPatches.TryGetNearestEnemyGunfirePosition(Vector3.zero, "Ussr_allies", 250f, 5f, out _) &&
                           VisionPatches.TryGetDominantEnemyGunfireDirection(Vector3.zero, "Ussr_allies", 250f, 5f, out _, out _) &&
                           VisionPatches.TryGetAudibleBattleDirection(Vector3.zero, "Ussr_allies", 9f, 11f, out _, out _),
                        "11K Clear-weather 200m gunfire cap must detect enemy shot at 185m across all gunfire/watch queries!");
                    AiCore.RainIntensity01 = 1.0f;
                    Assert(!VisionPatches.TryGetNearestEnemyGunfirePosition(Vector3.zero, "Ussr_allies", 250f, 5f, out _) &&
                           !VisionPatches.TryGetDominantEnemyGunfireDirection(Vector3.zero, "Ussr_allies", 250f, 5f, out _, out _) &&
                           !VisionPatches.TryGetAudibleBattleDirection(Vector3.zero, "Ussr_allies", 9f, 11f, out _, out _),
                        "11K Heavy rain (RainIntensity01=1.0) must mask 200m rifle report down to 156m so 185m shot is no longer localized!");
                    AiCore.RainIntensity01 = 0f;
                }
                finally
                {
                    Physics.UseSceneWalls = false;
                    Physics.SceneWalls.Clear();
                    AiCore.MockHasWater = false;
                    AiCore.RainIntensity01 = 0f;
                }
            }
            Console.WriteLine($"  -> Verified 11-Suite Physics/Geometry/Vision/Armor Matrix: {_passed} assertions passed (0 failures)");
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
