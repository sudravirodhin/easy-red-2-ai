param(
    [int]$Seeds = 3,
    [switch]$Quick,
    [string]$Campaign = "",
    [string]$GameDir = $env:EASY_RED_2_DIR,
    [string]$JsonOut = "",
    [int]$OpTickets = 0,
    [double]$MaxDuration = 0,
    [string]$Engine = "emergent",
    [switch]$NoGate
)

$ErrorActionPreference = "Stop"
$workspace    = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$goapFile     = Join-Path $workspace "src\EasyRed2AI\Modules\Goap\GoapDefinitionsAndPlanner.cs"
$wqFile       = Join-Path $workspace "src\EasyRed2AI\Core\AiCore.WorldQuery.cs"
$emergentFile = Join-Path $workspace "src\EasyRed2AI\Modules\Emergent\EmergentDecisionCore.cs"
$coreFile     = Join-Path $workspace "tools\verify_patches\BattleLossModelCore.cs"
$buildDir  = Join-Path $workspace "build"
New-Item -ItemType Directory -Force -Path $buildDir | Out-Null

# Auto-discover Easy Red 2 directory if not passed (strictly read-only usage)
if ([string]::IsNullOrWhiteSpace($GameDir) -or -not (Test-Path $GameDir)) {
    foreach ($cand in @(
        "D:\SteamLibrary\steamapps\common\Easy Red 2",
        "C:\Program Files (x86)\Steam\steamapps\common\Easy Red 2",
        "C:\SteamLibrary\steamapps\common\Easy Red 2",
        "E:\SteamLibrary\steamapps\common\Easy Red 2"
    )) {
        if (Test-Path (Join-Path $cand "Easy Red 2_Data\StreamingAssets\Missions")) {
            $GameDir = $cand
            break
        }
    }
}

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

# Make _factionProfileCache and _lastFactionProfileTheaterGen [ThreadStatic] in the slice so Parallel.For is lock-free!
$partB = $partB -replace '(internal|private) static readonly Dictionary<string, FactionCommandProfile>\s+_factionProfileCache\s*=\s*new Dictionary<string, FactionCommandProfile>\(32\);',
    '[ThreadStatic] internal static Dictionary<string, FactionCommandProfile> _factionProfileCache;'
$partB = $partB -replace 'private static int _lastFactionProfileTheaterGen\s*=\s*-1;',
    '[ThreadStatic] private static int _lastFactionProfileTheaterGen;'
$partB = $partB -replace 'if \(_factionProfileCache\.TryGetValue\(faction, out FactionCommandProfile cached\)\)',
    "if (_factionProfileCache == null) _factionProfileCache = new Dictionary<string, FactionCommandProfile>(32);`n            if (_factionProfileCache.TryGetValue(faction, out FactionCommandProfile cached))"
$partB = $partB -replace 'if \(AiCore\.BattleTheaterGeneration != _lastFactionProfileTheaterGen\)',
    'if (_factionProfileCache == null) _factionProfileCache = new Dictionary<string, FactionCommandProfile>(32); if (AiCore.BattleTheaterGeneration != _lastFactionProfileTheaterGen)'

$wqLines = Get-Content $wqFile
$wqSnapProbe = Slice-LinesBetween $wqLines '^\s*public static bool TrySnapWalkableGroundProbe\(' 'snappedGroundPos = gHit\.point;\s*$' 2

$sliceCs = @"
#pragma warning disable 0649, 0414
using System;
using System.Collections.Generic;

namespace EasyRed2HumanAI
{
    using UnityEngine;

    public static partial class AiCore
    {
$wqSnapProbe
    }

    public static partial class VeteranAndTacticsPatches
    {
$partA
$partB
    }
}
"@

$tmpSlice = Join-Path $buildDir "BattleLossModel_Slice.gen.cs"
$tmpDll   = Join-Path $buildDir "BattleLossModel.dll"
Set-Content -Path $tmpSlice -Value $sliceCs -Encoding UTF8

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
    $GameDir
}
$dotnetDir = Join-Path $bepRoot "dotnet"

try {
    & dotnet $csc /nologo /target:exe /optimize+ /nostdlib `
        "/r:$dotnetDir\System.Private.CoreLib.dll" `
        "/r:$dotnetDir\System.Runtime.dll" `
        "/r:$dotnetDir\System.Runtime.Extensions.dll" `
        "/r:$dotnetDir\System.Console.dll" `
        "/r:$dotnetDir\System.Collections.dll" `
        "/r:$dotnetDir\System.Collections.Concurrent.dll" `
        "/r:$dotnetDir\System.Linq.dll" `
        "/r:$dotnetDir\System.IO.FileSystem.dll" `
        "/r:$dotnetDir\System.Text.RegularExpressions.dll" `
        "/r:$dotnetDir\System.Threading.dll" `
        "/r:$dotnetDir\System.Threading.Tasks.dll" `
        "/r:$dotnetDir\System.Threading.Tasks.Parallel.dll" `
        "/r:$dotnetDir\netstandard.dll" `
        "/out:$tmpDll" `
        $coreFile $tmpSlice $emergentFile
    if ($LASTEXITCODE -ne 0) { throw "Failed to compile BattleLossModel.dll" }

    $runArgs = @("exec", "--runtimeconfig", $cscRuntimeConfig, $tmpDll, "--seeds", $Seeds.ToString(), "--engine", $Engine)
    if ($GameDir)  { $runArgs += @("--game-dir", $GameDir) }
    if ($Quick)    { $runArgs += "--quick" }
    if ($Campaign) { $runArgs += @("--campaign", $Campaign) }
    if ($JsonOut)  { $runArgs += @("--json", $JsonOut) }
    if ($OpTickets -gt 0) { $runArgs += @("--op-tickets", $OpTickets.ToString()) }
    if ($MaxDuration -gt 0) { $runArgs += @("--max-duration", $MaxDuration.ToString([System.Globalization.CultureInfo]::InvariantCulture)) }
    if ($NoGate)   { $runArgs += "--no-gate" }

    & dotnet $runArgs
    if ($LASTEXITCODE -ne 0) { throw "BattleLossModel failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item $tmpSlice, $tmpDll -Force -ErrorAction SilentlyContinue
}
