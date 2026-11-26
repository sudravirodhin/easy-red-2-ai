param(
    [switch]$Gate,
    [string]$TracePath = "",
    [switch]$NoTrace,
    [switch]$Verbose
)

$ErrorActionPreference = "Stop"
$workspace    = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$goapFile     = Join-Path $workspace "src\EasyRed2AI\Modules\Goap\GoapDefinitionsAndPlanner.cs"
$persFile     = Join-Path $workspace "src\EasyRed2AI\Modules\ImmersionAndQoL\VeterancyAndPersonalities.cs"
$emergentFile = Join-Path $workspace "src\EasyRed2AI\Modules\Emergent\EmergentDecisionCore.cs"
$coreFile     = Join-Path $workspace "tools\verify_patches\EmergentPathAuditorCore.cs"
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

$resolvedTrace = ""
if (-not $NoTrace) {
    if (-not [string]::IsNullOrWhiteSpace($TracePath)) {
        if (-not (Test-Path $TracePath)) {
            throw "TracePath not found: $TracePath"
        }
        $resolvedTrace = (Resolve-Path $TracePath).Path
    } else {
        foreach ($cand in @(
            "D:\SteamLibrary\steamapps\common\Easy Red 2\BepInEx\BotActionTrace.jsonl",
            "C:\Program Files (x86)\Steam\steamapps\common\Easy Red 2\BepInEx\BotActionTrace.jsonl",
            "E:\SteamLibrary\steamapps\common\Easy Red 2\BepInEx\BotActionTrace.jsonl"
        )) {
            if (Test-Path $cand) {
                $resolvedTrace = $cand
                break
            }
        }
    }
}

$goapLines = Get-Content $goapFile
$partA = Slice-LinesBetween $goapLines '^\s*internal enum SquadNetDirective' 'public Vector3 MoveOverrideDest;' 1
$partB = Slice-LinesBetween $goapLines '^\s*internal struct BotMicroVariance' '_factionProfileCache\[faction\] = p;' 2
$persLines = Get-Content $persFile
$partPers = Slice-LinesBetween $persLines '^\s*public enum SoldierPersonality : byte' 'Pilot_Disciplined = 30' 1

$stubCs = @"
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
}
"@

$tmpCs  = Join-Path $buildDir "EmergentPathAuditor_Stubs.gen.cs"
$tmpDll = Join-Path $buildDir "EmergentPathAuditor.dll"
Set-Content -Path $tmpCs -Value $stubCs -Encoding UTF8

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

$exitCode = 0
try {
    & dotnet $csc /nologo /target:exe /optimize+ /nostdlib `
        "/r:$dotnetDir\System.Private.CoreLib.dll" `
        "/r:$dotnetDir\System.Runtime.dll" `
        "/r:$dotnetDir\System.Console.dll" `
        "/r:$dotnetDir\System.Collections.dll" `
        "/r:$dotnetDir\System.Linq.dll" `
        "/r:$dotnetDir\System.IO.FileSystem.dll" `
        "/r:$dotnetDir\System.Text.RegularExpressions.dll" `
        "/r:$dotnetDir\netstandard.dll" `
        "/out:$tmpDll" `
        $tmpCs $emergentFile $coreFile
    if ($LASTEXITCODE -ne 0) { throw "Failed to compile EmergentPathAuditor.dll" }

    $runArgs = @("exec", "--runtimeconfig", $cscRuntimeConfig, $tmpDll, $workspace)
    if (-not [string]::IsNullOrWhiteSpace($resolvedTrace)) {
        $runArgs += @("--trace", $resolvedTrace)
    }
    if ($Gate) { $runArgs += "--gate" }
    if ($Verbose) { $runArgs += "--verbose" }

    & dotnet @runArgs
    $exitCode = $LASTEXITCODE
}
finally {
    Remove-Item $tmpCs, $tmpDll -Force -ErrorAction SilentlyContinue
}

exit $exitCode
