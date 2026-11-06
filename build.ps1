param(
    [switch]$Deploy,
    [string]$GameDir = $env:EASY_RED_2_DIR
)

$ErrorActionPreference = "Stop"
$workspace = $PSScriptRoot

function Resolve-EasyRed2GameDir {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath) -and (Test-Path $ExplicitPath)) {
        return (Resolve-Path $ExplicitPath).Path
    }

    $candidates = [System.Collections.Generic.List[string]]::new()

    # 1. Check Steam Registry keys and parse libraryfolders.vdf
    $steamPaths = @()
    foreach ($reg in @(
        @{ Path = "HKCU:\Software\Valve\Steam"; Name = "SteamPath" },
        @{ Path = "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam"; Name = "InstallPath" },
        @{ Path = "HKLM:\SOFTWARE\Valve\Steam"; Name = "InstallPath" }
    )) {
        try {
            $val = (Get-ItemProperty -Path $reg.Path -Name $reg.Name -ErrorAction SilentlyContinue).$($reg.Name)
            if (-not [string]::IsNullOrWhiteSpace($val)) {
                $steamPaths += ($val -replace '/', '\')
            }
        } catch {}
    }

    foreach ($sp in ($steamPaths | Select-Object -Unique)) {
        $candidates.Add((Join-Path $sp "steamapps\common\Easy Red 2"))
        $vdf = Join-Path $sp "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            $content = Get-Content $vdf -Raw -ErrorAction SilentlyContinue
            foreach ($m in [regex]::Matches($content, '"path"\s+"([^"]+)"')) {
                $libPath = $m.Groups[1].Value -replace '\\\\', '\'
                $candidates.Add((Join-Path $libPath "steamapps\common\Easy Red 2"))
            }
        }
    }

    # 2. Check mounted drives for common SteamLibrary locations
    foreach ($drive in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        $root = $drive.Root
        $candidates.Add((Join-Path $root "SteamLibrary\steamapps\common\Easy Red 2"))
        $candidates.Add((Join-Path $root "Steam\steamapps\common\Easy Red 2"))
        $candidates.Add((Join-Path $root "Program Files (x86)\Steam\steamapps\common\Easy Red 2"))
    }

    foreach ($cand in ($candidates | Select-Object -Unique)) {
        if (Test-Path (Join-Path $cand "Easy Red 2.exe")) {
            return $cand
        }
    }

    return $null
}

function Resolve-CscPath {
    param([string]$WorkspaceRoot)

    # 1. Check installed .NET SDKs first
    try {
        $sdkLines = & dotnet --list-sdks 2>$null
        if ($sdkLines) {
            foreach ($line in ($sdkLines | Sort-Object -Descending)) {
                if ($line -match '^(\S+)\s+\[(.+)\]$') {
                    $sdkVer = $Matches[1]
                    $sdkBase = $Matches[2]
                    $sdkCsc = Join-Path $sdkBase "$sdkVer\Roslyn\bincore\csc.dll"
                    if (Test-Path $sdkCsc) {
                        return $sdkCsc
                    }
                }
            }
        }
    } catch {}

    # 2. Fallback to local workspace tools if present
    $localCsc = Join-Path $WorkspaceRoot "tools\roslyn\extracted\tasks\netcore\bincore\csc.dll"
    if (Test-Path $localCsc) {
        return $localCsc
    }

    throw "Could not locate Roslyn csc.dll. Install the .NET SDK (https://dotnet.microsoft.com/download) or place Roslyn under tools\roslyn."
}

$resolvedGameDir = Resolve-EasyRed2GameDir -ExplicitPath $GameDir

# Use local extracted toolchain if present, otherwise use the resolved game directory's BepInEx installation
$bepInExRoot = if (Test-Path (Join-Path $workspace "tools\bepinex\extracted\BepInEx\core")) {
    Join-Path $workspace "tools\bepinex\extracted"
} elseif ($resolvedGameDir -and (Test-Path (Join-Path $resolvedGameDir "BepInEx\core"))) {
    $resolvedGameDir
} else {
    throw "Could not find BepInEx\core and BepInEx\interop assemblies. Pass -GameDir '<path to Easy Red 2>' or set EASY_RED_2_DIR."
}

$dotnetDir = Join-Path $bepInExRoot "dotnet"
$coreDir = Join-Path $bepInExRoot "BepInEx\core"
$interopDir = Join-Path $bepInExRoot "BepInEx\interop"
$pluginsDir = Join-Path $bepInExRoot "BepInEx\plugins"
$buildDir = Join-Path $workspace "build"
$csc = Resolve-CscPath -WorkspaceRoot $workspace

New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
if (Test-Path (Join-Path $workspace "tools\bepinex\extracted")) {
    New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
}

Write-Host "[1/2] Compiling EasyRed2HumanAI.dll..." -ForegroundColor Cyan
$sourceFiles = Get-ChildItem -Path "$workspace\src\EasyRed2AI" -Filter "*.cs" -Recurse | Select-Object -ExpandProperty FullName
$cscArgs = @(
    $csc,
    "/nologo",
    "/target:library",
    "/optimize+",
    "/nostdlib",
    "/r:$dotnetDir\System.Private.CoreLib.dll",
    "/r:$dotnetDir\System.Runtime.dll",
    "/r:$dotnetDir\System.Collections.dll",
    "/r:$dotnetDir\netstandard.dll",
    "/r:$coreDir\BepInEx.Core.dll",
    "/r:$coreDir\BepInEx.Unity.Common.dll",
    "/r:$coreDir\BepInEx.Unity.IL2CPP.dll",
    "/r:$coreDir\0Harmony.dll",
    "/r:$coreDir\Il2CppInterop.Runtime.dll",
    "/r:$interopDir\Il2Cppmscorlib.dll",
    "/r:$interopDir\UnityEngine.CoreModule.dll",
    "/r:$interopDir\UnityEngine.AudioModule.dll",
    "/r:$interopDir\UnityEngine.AnimationModule.dll",
    "/r:$interopDir\UnityEngine.PhysicsModule.dll",
    "/r:$interopDir\UnityEngine.TerrainPhysicsModule.dll",
    "/r:$interopDir\UnityEngine.IMGUIModule.dll",
    "/r:$interopDir\UnityEngine.InputLegacyModule.dll",
    "/r:$interopDir\UnityEngine.TextRenderingModule.dll",
    "/r:$interopDir\UnityEngine.UI.dll",
    "/r:$interopDir\UnityEngine.UIModule.dll",
    "/r:$interopDir\PhotonUnityNetworking.dll",
    "/r:$interopDir\Assembly-CSharp.dll",
    "/out:$buildDir\EasyRed2HumanAI.dll"
) + $sourceFiles

& dotnet $cscArgs

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

if (Test-Path (Join-Path $workspace "tools\bepinex\extracted")) {
    Copy-Item "$buildDir\EasyRed2HumanAI.dll" "$pluginsDir\EasyRed2HumanAI.dll" -Force
}
Write-Host "  -> Built $buildDir\EasyRed2HumanAI.dll" -ForegroundColor Green

if ($Deploy) {
    if (-not $resolvedGameDir) {
        throw "Could not auto-detect Easy Red 2 installation directory. Pass -GameDir '<path to Easy Red 2>' or set EASY_RED_2_DIR."
    }

    $proc = Get-Process "Easy Red 2" -ErrorAction SilentlyContinue
    if ($proc) {
        Write-Warning "Easy Red 2 is currently running (PID $($proc.Id)). Close the game before deploying."
        exit 1
    }

    Write-Host "[2/2] Deploying EasyRed2HumanAI to $resolvedGameDir..." -ForegroundColor Cyan
    if (-not (Test-Path "$resolvedGameDir\winhttp.dll") -and (Test-Path (Join-Path $workspace "tools\bepinex\extracted"))) {
        Copy-Item -Path "$workspace\tools\bepinex\extracted\*" -Destination $resolvedGameDir -Recurse -Force
        Write-Host "  -> Installed BepInEx 6 BE (build 791), dotnet runtime, unity-libs, and pre-built interop assemblies." -ForegroundColor Green
    } else {
        New-Item -ItemType Directory -Force -Path "$resolvedGameDir\BepInEx\plugins" | Out-Null
        Copy-Item "$buildDir\EasyRed2HumanAI.dll" "$resolvedGameDir\BepInEx\plugins\EasyRed2HumanAI.dll" -Force
        Write-Host "  -> Updated $resolvedGameDir\BepInEx\plugins\EasyRed2HumanAI.dll" -ForegroundColor Green
    }
}
