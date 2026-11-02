param(
    [switch]$Deploy,
    [string]$GameDir = "D:\SteamLibrary\steamapps\common\Easy Red 2"
)

$ErrorActionPreference = "Stop"
$workspace = $PSScriptRoot

# Use local extracted toolchain if present, otherwise fall back to the game's BepInEx directory
$bepInExRoot = if (Test-Path "$workspace\tools\bepinex\extracted\BepInEx\core") {
    "$workspace\tools\bepinex\extracted"
} else {
    $GameDir
}

$dotnetDir = "$bepInExRoot\dotnet"
$coreDir = "$bepInExRoot\BepInEx\core"
$interopDir = "$bepInExRoot\BepInEx\interop"
$pluginsDir = "$bepInExRoot\BepInEx\plugins"
$buildDir = "$workspace\build"
$csc = "$workspace\tools\roslyn\extracted\tasks\netcore\bincore\csc.dll"

New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
if (Test-Path "$workspace\tools\bepinex\extracted") {
    New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
}

Write-Host "[1/2] Compiling EasyRed2HumanAI.dll..." -ForegroundColor Cyan
dotnet $csc /nologo /target:library /optimize+ /nostdlib `
    /r:"$dotnetDir\System.Private.CoreLib.dll" `
    /r:"$dotnetDir\System.Runtime.dll" `
    /r:"$dotnetDir\System.Collections.dll" `
    /r:"$dotnetDir\netstandard.dll" `
    /r:"$coreDir\BepInEx.Core.dll" `
    /r:"$coreDir\BepInEx.Unity.Common.dll" `
    /r:"$coreDir\BepInEx.Unity.IL2CPP.dll" `
    /r:"$coreDir\0Harmony.dll" `
    /r:"$coreDir\Il2CppInterop.Runtime.dll" `
    /r:"$interopDir\Il2Cppmscorlib.dll" `
    /r:"$interopDir\UnityEngine.CoreModule.dll" `
    /r:"$interopDir\UnityEngine.PhysicsModule.dll" `
    /r:"$interopDir\UnityEngine.IMGUIModule.dll" `
    /r:"$interopDir\UnityEngine.InputLegacyModule.dll" `
    /r:"$interopDir\UnityEngine.TextRenderingModule.dll" `
    /r:"$interopDir\UnityEngine.UI.dll" `
    /r:"$interopDir\Assembly-CSharp.dll" `
    /out:"$buildDir\EasyRed2HumanAI.dll" `
    "$workspace\src\EasyRed2AI\Plugin.cs" `
    "$workspace\src\EasyRed2AI\VisionConfig.cs" `
    "$workspace\src\EasyRed2AI\VisionPatches.cs" `
    "$workspace\src\EasyRed2AI\VeteranAndTacticsPatches.cs"

if ($LASTEXITCODE -ne 0) {
    throw "Compilation failed with exit code $LASTEXITCODE"
}

if (Test-Path "$workspace\tools\bepinex\extracted") {
    Copy-Item "$buildDir\EasyRed2HumanAI.dll" "$pluginsDir\EasyRed2HumanAI.dll" -Force
}
Write-Host "  -> Built $buildDir\EasyRed2HumanAI.dll" -ForegroundColor Green

if ($Deploy) {
    $proc = Get-Process "Easy Red 2" -ErrorAction SilentlyContinue
    if ($proc) {
        Write-Warning "Easy Red 2 is currently running (PID $($proc.Id)). Close the game before deploying."
        exit 1
    }

    Write-Host "[2/2] Deploying EasyRed2HumanAI to $GameDir..." -ForegroundColor Cyan
    if (-not (Test-Path "$GameDir\winhttp.dll") -and (Test-Path "$workspace\tools\bepinex\extracted")) {
        Copy-Item -Path "$workspace\tools\bepinex\extracted\*" -Destination $GameDir -Recurse -Force
        Write-Host "  -> Installed BepInEx 6 BE (build 791), dotnet runtime, unity-libs, and pre-built interop assemblies." -ForegroundColor Green
    } else {
        New-Item -ItemType Directory -Force -Path "$GameDir\BepInEx\plugins" | Out-Null
        Copy-Item "$buildDir\EasyRed2HumanAI.dll" "$GameDir\BepInEx\plugins\EasyRed2HumanAI.dll" -Force
        Write-Host "  -> Updated $GameDir\BepInEx\plugins\EasyRed2HumanAI.dll" -ForegroundColor Green
    }
}
