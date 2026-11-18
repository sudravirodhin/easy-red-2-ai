@echo off
setlocal
title Easy Red 2: Battle Hardened - Auto-Updating Launcher
set "BH_BAT_DIR=%~dp0"
set "BH_BAT_FILE=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$c = [IO.File]::ReadAllText($env:BH_BAT_FILE, [Text.Encoding]::UTF8); $m = '#' + '__PS_START__'; $i = $c.IndexOf($m); if ($i -lt 0) { throw 'Launcher script block missing.' }; & ([scriptblock]::Create($c.Substring($i)))"
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" (
    echo.
    pause
)
exit /b %RC%

#__PS_START__
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Resolve-EasyRed2Dir {
    param([string]$ScriptDir)

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($ScriptDir)) {
        $trimmed = $ScriptDir.TrimEnd('\')
        $candidates.Add($trimmed)
        $parent1 = Split-Path $trimmed -Parent
        if ($parent1) {
            $candidates.Add($parent1)
            $parent2 = Split-Path $parent1 -Parent
            if ($parent2) { $candidates.Add($parent2) }
        }
    }
    if (-not [string]::IsNullOrWhiteSpace($env:EASY_RED_2_DIR)) {
        $candidates.Add($env:EASY_RED_2_DIR.TrimEnd('\'))
    }

    $steamPaths = @()
    foreach ($reg in @(
        @{ Path = 'HKCU:\Software\Valve\Steam'; Name = 'SteamPath' },
        @{ Path = 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam'; Name = 'InstallPath' },
        @{ Path = 'HKLM:\SOFTWARE\Valve\Steam'; Name = 'InstallPath' }
    )) {
        try {
            $prop = Get-ItemProperty -Path $reg.Path -Name $reg.Name -ErrorAction SilentlyContinue
            $val = if ($prop) { $prop.($reg.Name) } else { $null }
            if (-not [string]::IsNullOrWhiteSpace($val)) {
                $steamPaths += ($val -replace '/', '\')
            }
        } catch {}
    }

    foreach ($sp in ($steamPaths | Select-Object -Unique)) {
        $candidates.Add((Join-Path $sp 'steamapps\common\Easy Red 2'))
        $vdf = Join-Path $sp 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            try {
                $vdfText = Get-Content $vdf -Raw -ErrorAction SilentlyContinue
                foreach ($m in [regex]::Matches($vdfText, '"path"\s+"([^"]+)"')) {
                    $lib = $m.Groups[1].Value -replace '\\\\', '\'
                    $candidates.Add((Join-Path $lib 'steamapps\common\Easy Red 2'))
                }
            } catch {}
        }
    }

    foreach ($drive in (Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue)) {
        $root = $drive.Root
        $candidates.Add((Join-Path $root 'SteamLibrary\steamapps\common\Easy Red 2'))
        $candidates.Add((Join-Path $root 'Steam\steamapps\common\Easy Red 2'))
        $candidates.Add((Join-Path $root 'Program Files (x86)\Steam\steamapps\common\Easy Red 2'))
    }

    foreach ($cand in ($candidates | Select-Object -Unique)) {
        if (-not [string]::IsNullOrWhiteSpace($cand) -and (Test-Path (Join-Path $cand 'Easy Red 2.exe'))) {
            return $cand
        }
    }

    return $null
}

function Invoke-DownloadFile {
    param(
        [string]$Url,
        [string]$DestPath
    )
    $headers = @{ 'User-Agent' = 'BattleHardened-Launcher' }
    Invoke-WebRequest -Uri $Url -OutFile $DestPath -Headers $headers -MaximumRedirection 10 -UseBasicParsing -TimeoutSec 60
}

function Start-EasyRed2Game {
    param([string]$GameDir)

    Write-Host ''
    Write-Host '[LAUNCH] Starting Easy Red 2: Battle Hardened...' -ForegroundColor Green
    $launchedViaSteam = $false
    try {
        $steamReg = Get-ItemProperty -Path 'HKCU:\Software\Valve\Steam' -Name 'SteamExe' -ErrorAction SilentlyContinue
        if ($steamReg -and $steamReg.SteamExe) {
            Start-Process 'steam://rungameid/1324780'
            $launchedViaSteam = $true
        }
    } catch {}

    if (-not $launchedViaSteam) {
        $exePath = Join-Path $GameDir 'Easy Red 2.exe'
        Start-Process -FilePath $exePath -WorkingDirectory $GameDir
    }

    Start-Sleep -Milliseconds 1500
    exit 0
}

Write-Host '============================================================================' -ForegroundColor Cyan
Write-Host '  Easy Red 2: Battle Hardened - Auto-Updating Launcher' -ForegroundColor Cyan
Write-Host '  Checks GitHub for updates, then launches Easy Red 2 automatically' -ForegroundColor DarkCyan
Write-Host '============================================================================' -ForegroundColor Cyan
Write-Host ''

$gameDir = Resolve-EasyRed2Dir -ScriptDir $env:BH_BAT_DIR
if (-not $gameDir) {
    Write-Host '[!] Could not auto-detect your Easy Red 2 folder.' -ForegroundColor Yellow
    $manual = Read-Host 'Paste your Easy Red 2 folder path (where Easy Red 2.exe is) or press Enter to exit'
    if (-not [string]::IsNullOrWhiteSpace($manual)) {
        $manual = $manual.Trim().Trim('"')
        if (Test-Path (Join-Path $manual 'Easy Red 2.exe')) {
            $gameDir = $manual
        }
    }
}

if (-not $gameDir) {
    Write-Host '[ERROR] Easy Red 2.exe not found. Place this .bat inside your Easy Red 2 folder and run it again.' -ForegroundColor Red
    exit 1
}

Write-Host "  Game Folder : $gameDir"

# Keep a copy of Launch-BattleHardened.bat inside the Easy Red 2 directory for convenience
try {
    $targetLauncher = Join-Path $gameDir 'Launch-BattleHardened.bat'
    if ($env:BH_BAT_FILE -and (Test-Path $env:BH_BAT_FILE) -and ($env:BH_BAT_FILE.ToLowerInvariant() -ne $targetLauncher.ToLowerInvariant())) {
        Copy-Item -Path $env:BH_BAT_FILE -Destination $targetLauncher -Force -ErrorAction SilentlyContinue
    }
} catch {}

$running = Get-Process -Name 'Easy Red 2' -ErrorAction SilentlyContinue
if ($running) {
    Write-Host ''
    Write-Host "[!] Easy Red 2 is already running (PID $($running[0].Id))." -ForegroundColor Yellow
    Start-Sleep -Seconds 2
    exit 0
}

if (Test-Path (Join-Path $gameDir '_BepInEx_Disabled_Backup\winhttp.dll')) {
    Write-Host ''
    Write-Host '[ERROR] BepInEx is currently disabled inside _BepInEx_Disabled_Backup\.' -ForegroundColor Red
    Write-Host '        Restore it with Uninstall-BepInEx-Mod.bat option [2] first.' -ForegroundColor Yellow
    exit 1
}

$pluginsDir = Join-Path $gameDir 'BepInEx\plugins'
$dllPath = Join-Path $pluginsDir 'EasyRed2HumanAI.dll'
$hasBepInEx = (Test-Path (Join-Path $gameDir 'winhttp.dll')) -and (Test-Path (Join-Path $gameDir 'BepInEx\core'))
$installedVer = $null
$installedLen = 0
$installedSha256 = $null

if (Test-Path $dllPath) {
    $curItem = Get-Item $dllPath
    $installedLen = $curItem.Length
    $fvi = [Diagnostics.FileVersionInfo]::GetVersionInfo($dllPath)
    $installedVer = if (-not [string]::IsNullOrWhiteSpace($fvi.ProductVersion)) { $fvi.ProductVersion.Trim() } elseif (-not [string]::IsNullOrWhiteSpace($fvi.FileVersion)) { $fvi.FileVersion.Trim() } else { $null }
    try {
        $installedSha256 = (Get-FileHash -Path $dllPath -Algorithm SHA256).Hash.ToLowerInvariant()
    } catch {}
    $verDisplay = if ($installedVer) { "v$($installedVer.TrimStart('v'))" } else { $curItem.LastWriteTime.ToString('yyyy-MM-dd HH:mm') }
    Write-Host "  Installed   : $verDisplay ($installedLen bytes)"
} else {
    Write-Host '  Installed   : (not yet installed)'
}

$latestTag = $null
$dllUrl = 'https://github.com/sudravirodhin/easy-red-2-ai/releases/latest/download/EasyRed2HumanAI.dll'
$dllExpectedSize = 0
$dllExpectedSha256 = $null
$fullZipUrl = $null

Write-Host '[1/2] Checking GitHub for updates...' -ForegroundColor Cyan
try {
    $rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/sudravirodhin/easy-red-2-ai/releases/latest' -Headers @{ 'User-Agent' = 'BattleHardened-Launcher' } -TimeoutSec 8
    if ($rel -and $rel.tag_name) {
        $latestTag = [string]$rel.tag_name
        foreach ($asset in $rel.assets) {
            if ($asset.name -eq 'EasyRed2HumanAI.dll') {
                $dllUrl = [string]$asset.browser_download_url
                if ($asset.size) { $dllExpectedSize = [long]$asset.size }
                if ($asset.digest -and ([string]$asset.digest).StartsWith('sha256:')) {
                    $dllExpectedSha256 = ([string]$asset.digest).Substring(7).ToLowerInvariant()
                }
            } elseif ($asset.name -like '*-with-BepInEx.zip') {
                $fullZipUrl = [string]$asset.browser_download_url
            }
        }
        Write-Host "  Latest      : $latestTag" -ForegroundColor Green
    }
} catch {
    if ($hasBepInEx -and (Test-Path $dllPath)) {
        Write-Host '  [Offline/Rate-Limited] Could not reach GitHub API; launching installed version.' -ForegroundColor Yellow
        Start-EasyRed2Game -GameDir $gameDir
    }
    Write-Host '  [Warn] GitHub API check failed; falling back to direct latest release download.' -ForegroundColor Yellow
}

if (-not $hasBepInEx) {
    if (-not $fullZipUrl) {
        Write-Host ''
        Write-Host '[ERROR] BepInEx is not installed and the full *-with-BepInEx.zip release asset URL could not be resolved.' -ForegroundColor Red
        exit 1
    }

    $tmpZip = Join-Path $env:TEMP ("BattleHardened_Full_" + [Guid]::NewGuid().ToString('N') + '.zip')
    try {
        Write-Host "[2/2] First-time setup: downloading BepInEx + Battle Hardened ($latestTag)..." -ForegroundColor Cyan
        Invoke-DownloadFile -Url $fullZipUrl -DestPath $tmpZip

        $zipInfo = Get-Item $tmpZip
        $hdr = New-Object byte[] 2
        $fs = [IO.File]::OpenRead($tmpZip)
        try { $null = $fs.Read($hdr, 0, 2) } finally { $fs.Dispose() }
        if ($zipInfo.Length -lt 1048576 -or $hdr[0] -ne 0x50 -or $hdr[1] -ne 0x4B) {
            throw 'Downloaded ZIP archive failed validation.'
        }

        Write-Host '      Extracting into Easy Red 2 folder...' -ForegroundColor Cyan
        Expand-Archive -Path $tmpZip -DestinationPath $gameDir -Force
        Write-Host "[OK] Installed Battle Hardened ($latestTag) with BepInEx!" -ForegroundColor Green
    } finally {
        Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
    }

    Start-EasyRed2Game -GameDir $gameDir
}

# Check if the installed DLL is already up to date
$isUpToDate = $false
if ((Test-Path $dllPath) -and $latestTag) {
    $cleanLatestVer = $latestTag.TrimStart('v').Trim()
    if ($dllExpectedSha256 -and $installedSha256 -and ($installedSha256 -eq $dllExpectedSha256)) {
        $isUpToDate = $true
    } elseif ($installedVer -and ($installedVer.TrimStart('v') -eq $cleanLatestVer) -and ($dllExpectedSize -le 0 -or $installedLen -eq $dllExpectedSize)) {
        $isUpToDate = $true
    }
}

if ($isUpToDate) {
    Write-Host "[2/2] Battle Hardened ($latestTag) is already up to date!" -ForegroundColor Green
    Start-EasyRed2Game -GameDir $gameDir
}

$tmpDll = Join-Path $env:TEMP ("EasyRed2HumanAI_" + [Guid]::NewGuid().ToString('N') + '.dll.tmp')
try {
    $tagLabel = if ($latestTag) { $latestTag } else { 'latest' }
    Write-Host "[2/2] Downloading update ($tagLabel)..." -ForegroundColor Cyan
    Invoke-DownloadFile -Url $dllUrl -DestPath $tmpDll

    $dlInfo = Get-Item $tmpDll
    $mz = New-Object byte[] 2
    $fs = [IO.File]::OpenRead($tmpDll)
    try { $null = $fs.Read($mz, 0, 2) } finally { $fs.Dispose() }
    if ($dlInfo.Length -lt 102400 -or $mz[0] -ne 0x4D -or $mz[1] -ne 0x5A) {
        throw "Downloaded DLL failed integrity verification (len=$($dlInfo.Length))."
    }

    if (-not (Test-Path $pluginsDir)) {
        New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
    }
    if (Test-Path $dllPath) {
        $bakPath = Join-Path $env:TEMP 'EasyRed2HumanAI.dll.bak'
        Copy-Item $dllPath $bakPath -Force
    }
    Copy-Item $tmpDll $dllPath -Force

    Write-Host "[OK] Updated EasyRed2HumanAI.dll to $tagLabel ($($dlInfo.Length) bytes)!" -ForegroundColor Green
} catch {
    if (Test-Path $dllPath) {
        Write-Host "[WARN] Update download failed ($($_.Exception.Message)). Launching existing installation..." -ForegroundColor Yellow
    } else {
        Write-Host "[ERROR] Update failed and no existing EasyRed2HumanAI.dll is installed: $($_.Exception.Message)" -ForegroundColor Red
        exit 1
    }
} finally {
    Remove-Item $tmpDll -Force -ErrorAction SilentlyContinue
}

Start-EasyRed2Game -GameDir $gameDir
