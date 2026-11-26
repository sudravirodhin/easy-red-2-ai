param(
    [switch]$Deploy,
    [switch]$Test,
    [switch]$VerboseTests,
    [switch]$RebuildCache,
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

function Sync-Er2CacheIfNeeded {
    param(
        [string]$WorkspaceRoot,
        [string]$ResolvedGameDir,
        [string]$CscPath,
        [string]$DotnetDir,
        [bool]$ForceRebuild
    )

    if (-not $ResolvedGameDir -or -not (Test-Path (Join-Path $ResolvedGameDir "GameAssembly.dll"))) {
        if ($ForceRebuild) {
            throw "Cannot rebuild ER2 cache: GameAssembly.dll was not found. Pass -GameDir '<path to Easy Red 2>'."
        }
        return
    }

    $cacheDir = Join-Path $WorkspaceRoot "tools\er2_cache"
    $manifestPath = Join-Path $cacheDir "manifest.json"
    $gameAsmItem = Get-Item (Join-Path $ResolvedGameDir "GameAssembly.dll")
    $curBytes = $gameAsmItem.Length
    $curWriteUtc = $gameAsmItem.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ")

    $needsRebuild = $ForceRebuild -or (-not (Test-Path $manifestPath))
    if (-not $needsRebuild) {
        try {
            $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
            if ([int64]$manifest.gameAssemblyBytes -ne $curBytes -or
                [string]$manifest.gameAssemblyLastWriteUtc -ne $curWriteUtc) {
                $needsRebuild = $true
            }
        } catch {
            $needsRebuild = $true
        }
    }

    $giDir = Join-Path $WorkspaceRoot "tools\gen_interop"
    $inspectSrc = Join-Path $giDir "Inspect.cs"
    $inspectDll = Join-Path $giDir "InspectInterop.dll"
    if ((Test-Path $inspectSrc) -and (-not (Test-Path $inspectDll) -or ((Get-Item $inspectSrc).LastWriteTimeUtc -gt (Get-Item $inspectDll).LastWriteTimeUtc))) {
        Write-Host "  -> Recompiling InspectInterop.dll..." -ForegroundColor Cyan
        & dotnet $CscPath /nologo /target:exe /optimize+ /nostdlib `
            "/r:$DotnetDir\System.Private.CoreLib.dll" `
            "/r:$DotnetDir\System.Runtime.dll" `
            "/r:$DotnetDir\System.Console.dll" `
            "/r:$DotnetDir\System.Collections.dll" `
            "/r:$DotnetDir\System.Linq.dll" `
            "/r:$DotnetDir\System.IO.FileSystem.dll" `
            "/r:$DotnetDir\System.Security.Cryptography.Algorithms.dll" `
            "/r:$DotnetDir\System.Security.Cryptography.Primitives.dll" `
            "/r:$DotnetDir\System.Text.RegularExpressions.dll" `
            "/r:$DotnetDir\netstandard.dll" `
            "/r:$giDir\AsmResolver.dll" `
            "/r:$giDir\AsmResolver.PE.dll" `
            "/r:$giDir\AsmResolver.PE.File.dll" `
            "/r:$giDir\AsmResolver.DotNet.dll" `
            "/r:$giDir\AssetRipper.Primitives.dll" `
            "/r:$giDir\Iced.dll" `
            "/r:$giDir\LibCpp2IL.dll" `
            "/out:$inspectDll" `
            $inspectSrc
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to compile InspectInterop.dll with exit code $LASTEXITCODE"
        }
    }

    if (-not $needsRebuild) {
        return
    }

    Write-Host "[0/2] GameAssembly change or -RebuildCache detected: re-ripping ER2 disassembly, Lua scripts & interop cache..." -ForegroundColor Yellow

    $gameInterop = Join-Path $ResolvedGameDir "BepInEx\interop"
    $localInterop = Join-Path $WorkspaceRoot "tools\bepinex\extracted\BepInEx\interop"
    if ((Test-Path (Join-Path $gameInterop "Assembly-CSharp.dll")) -and (Test-Path $localInterop)) {
        $srcVer = (Get-Item (Join-Path $gameInterop "Assembly-CSharp.dll")).LastWriteTimeUtc
        $dstDll = Join-Path $localInterop "Assembly-CSharp.dll"
        if (-not (Test-Path $dstDll) -or $srcVer -gt (Get-Item $dstDll).LastWriteTimeUtc) {
            Write-Host "  -> Syncing updated BepInEx\interop\*.dll into tools\bepinex\extracted\BepInEx\interop..." -ForegroundColor Cyan
            Copy-Item -Path (Join-Path $gameInterop "*.dll") -Destination $localInterop -Force
        }
    }

    if (Test-Path $inspectDll) {
        & dotnet $inspectDll --build-cache $cacheDir $ResolvedGameDir
        if ($LASTEXITCODE -ne 0) {
            throw "InspectInterop.dll --build-cache failed with exit code $LASTEXITCODE"
        }
    }
}

Sync-Er2CacheIfNeeded -WorkspaceRoot $workspace -ResolvedGameDir $resolvedGameDir -CscPath $csc -DotnetDir $dotnetDir -ForceRebuild:$RebuildCache.IsPresent

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
    "/r:$dotnetDir\System.IO.FileSystem.dll",
    "/r:$dotnetDir\System.Threading.dll",
    "/r:$dotnetDir\System.Threading.Thread.dll",
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
    "/r:$interopDir\UnityEngine.ParticleSystemModule.dll",
    "/r:$interopDir\UnityEngine.PhysicsModule.dll",
    "/r:$interopDir\UnityEngine.TerrainPhysicsModule.dll",
    "/r:$interopDir\UnityEngine.IMGUIModule.dll",
    "/r:$interopDir\UnityEngine.InputLegacyModule.dll",
    "/r:$interopDir\UnityEngine.TextRenderingModule.dll",
    "/r:$interopDir\UnityEngine.UI.dll",
    "/r:$interopDir\UnityEngine.UIModule.dll",
    "/r:$interopDir\Photon3Unity3D.dll",
    "/r:$interopDir\PhotonRealtime.dll",
    "/r:$interopDir\PhotonUnityNetworking.dll",
    "/r:$interopDir\Assembly-CSharp.dll",
    "/out:$buildDir\EasyRed2HumanAI.dll"
) + $sourceFiles

& dotnet $cscArgs

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

$verifySrc = Join-Path $workspace "tools\verify_patches\VerifyPatches.cs"
if (Test-Path $verifySrc) {
    $verifyDll = Join-Path $buildDir "VerifyPatches.dll"
    $cscRuntimeConfig = Join-Path (Split-Path $csc -Parent) "csc.runtimeconfig.json"
    if (-not (Test-Path $verifyDll) -or ((Get-Item $verifySrc).LastWriteTimeUtc -gt (Get-Item $verifyDll).LastWriteTimeUtc)) {
        & dotnet $csc /nologo /target:exe /optimize+ /nostdlib `
            "/r:$dotnetDir\System.Private.CoreLib.dll" `
            "/r:$dotnetDir\System.Runtime.dll" `
            "/r:$dotnetDir\System.Console.dll" `
            "/r:$dotnetDir\System.Collections.dll" `
            "/r:$dotnetDir\System.Linq.dll" `
            "/r:$dotnetDir\System.Reflection.dll" `
            "/r:$dotnetDir\System.IO.FileSystem.dll" `
            "/r:$dotnetDir\netstandard.dll" `
            "/out:$verifyDll" `
            $verifySrc
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to compile HarmonyPatch signature verifier"
        }
    }
    & dotnet exec --runtimeconfig $cscRuntimeConfig $verifyDll "$buildDir\EasyRed2HumanAI.dll" $interopDir $coreDir $dotnetDir
    if ($LASTEXITCODE -ne 0) {
        throw "HarmonyPatch signature verification failed! Fix parameter types/indices before deploying."
    }
}

if (Test-Path (Join-Path $workspace "tools\bepinex\extracted")) {
    Copy-Item "$buildDir\EasyRed2HumanAI.dll" "$pluginsDir\EasyRed2HumanAI.dll" -Force
    foreach ($batName in @("Launch-BattleHardened.bat", "Update-BattleHardened.bat", "Uninstall-BepInEx-Mod.bat")) {
        $batSrc = Join-Path $workspace $batName
        if (Test-Path $batSrc) {
            Copy-Item $batSrc (Join-Path $workspace "tools\bepinex\extracted\$batName") -Force
        }
    }
}
Write-Host "  -> Built $buildDir\EasyRed2HumanAI.dll" -ForegroundColor Green

if ($Test) {
    Write-Host "[Test 1/4] Running Emergent 3-Tier Decision Engine Suite..." -ForegroundColor Cyan
    $emergentScript = Join-Path $workspace "tools\verify_patches\VerifyEmergentEngine.ps1"
    if ($VerboseTests) {
        & $emergentScript
        if ($LASTEXITCODE -ne 0) {
            throw "VerifyEmergentEngine.ps1 failed!"
        }
    } else {
        $emergentLines = & $emergentScript 2>&1
        $emergentExit = $LASTEXITCODE
        foreach ($line in $emergentLines) {
            $s = "$line"
            if ($s -match '^\s*\[PASS\]' -or $s -match '^\s*\[Group \d+\]') {
                continue
            }
            Write-Host $s
        }
        if ($emergentExit -ne 0) {
            throw "VerifyEmergentEngine.ps1 failed!"
        }
    }

    Write-Host "[Test 2/4] Running Acoustic Ring, Ground Probe & CQB Physics Suite..." -ForegroundColor Cyan
    & (Join-Path $workspace "tools\verify_patches\VerifyPhysicsAndAcoustics.ps1")
    if ($LASTEXITCODE -ne 0) {
        throw "VerifyPhysicsAndAcoustics.ps1 failed!"
    }

    Write-Host "[Test 3/4] Running Deterministic Battle Loss & Emergence Model..." -ForegroundColor Cyan
    & (Join-Path $workspace "tools\verify_patches\Run-BattleLossModel.ps1") -GameDir $resolvedGameDir
    if ($LASTEXITCODE -ne 0) {
        throw "Run-BattleLossModel.ps1 failed!"
    }

    Write-Host "[Test 4/4] Running Deterministic Emergent/GOAP Path & Contract Audit..." -ForegroundColor Cyan
    & (Join-Path $workspace "tools\verify_patches\Run-EmergentPathAudit.ps1") -NoTrace -Gate
    if ($LASTEXITCODE -ne 0) {
        throw "Run-EmergentPathAudit.ps1 -NoTrace -Gate failed!"
    }
}

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
    foreach ($batName in @("Launch-BattleHardened.bat", "Update-BattleHardened.bat", "Uninstall-BepInEx-Mod.bat")) {
        $batSrc = Join-Path $workspace $batName
        if (Test-Path $batSrc) {
            Copy-Item $batSrc (Join-Path $resolvedGameDir $batName) -Force
        }
    }
}
