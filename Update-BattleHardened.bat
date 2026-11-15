@echo off
setlocal
title Easy Red 2: Battle Hardened - Auto-Updater
set "BH_BAT_DIR=%~dp0"
set "BH_BAT_FILE=%~f0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$c = [IO.File]::ReadAllText($env:BH_BAT_FILE, [Text.Encoding]::UTF8); $i = $c.IndexOf('#__PS_START__'); if ($i -lt 0) { throw 'Updater script block missing.' }; & ([scriptblock]::Create($c.Substring($i)))"
set "RC=%ERRORLEVEL%"
echo.
pause
exit /b %RC%

#__PS_START__
$ErrorActionPreference = 'Stop'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

function Resolve-EasyRed2Dir {
    param([string]$ScriptDir)

    $candidates = [System.Collections.Generic.List[string]]::new()
    if (-not [string]::IsNullOrWhiteSpace($ScriptDir)) {
        $candidates.Add($ScriptDir.TrimEnd('\'))
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
    $headers = @{ 'User-Agent' = 'BattleHardened-Updater' }
    Invoke-WebRequest -Uri $Url -OutFile $DestPath -Headers $headers -MaximumRedirection 10 -UseBasicParsing -TimeoutSec 60
}

Write-Host '============================================================================' -ForegroundColor Cyan
Write-Host '  Easy Red 2: Battle Hardened - 1-Click Auto-Updater' -ForegroundColor Cyan
Write-Host '  Source: https://github.com/sudravirodhin/easy-red-2-ai/releases/latest' -ForegroundColor DarkCyan
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

while ($true) {
    $running = Get-Process -Name 'Easy Red 2' -ErrorAction SilentlyContinue
    if (-not $running) { break }
    Write-Host ''
    Write-Host "[!] Easy Red 2 is currently running (PID $($running[0].Id))." -ForegroundColor Yellow
    Write-Host '    Please close Easy Red 2 completely so files are not locked.' -ForegroundColor Yellow
    Read-Host '    Press ENTER to check again once the game is closed (or Ctrl+C to cancel)'
}

$pluginsDir = Join-Path $gameDir 'BepInEx\plugins'
$dllPath = Join-Path $pluginsDir 'EasyRed2HumanAI.dll'
if (Test-Path $dllPath) {
    $curItem = Get-Item $dllPath
    $fvi = [Diagnostics.FileVersionInfo]::GetVersionInfo($dllPath)
    $verStr = if (-not [string]::IsNullOrWhiteSpace($fvi.ProductVersion)) { $fvi.ProductVersion } elseif (-not [string]::IsNullOrWhiteSpace($fvi.FileVersion)) { $fvi.FileVersion } else { $curItem.LastWriteTime.ToString('yyyy-MM-dd HH:mm') }
    Write-Host "  Installed   : $verStr ($($curItem.Length) bytes)"
} else {
    Write-Host '  Installed   : (not yet installed)'
}

$latestTag = 'latest'
$dllUrl = 'https://github.com/sudravirodhin/easy-red-2-ai/releases/latest/download/EasyRed2HumanAI.dll'
$fullZipUrl = $null

Write-Host ''
Write-Host '[1/3] Checking GitHub latest release metadata...' -ForegroundColor Cyan
try {
    $rel = Invoke-RestMethod -Uri 'https://api.github.com/repos/sudravirodhin/easy-red-2-ai/releases/latest' -Headers @{ 'User-Agent' = 'BattleHardened-Updater' } -TimeoutSec 20
    if ($rel -and $rel.tag_name) {
        $latestTag = [string]$rel.tag_name
        foreach ($asset in $rel.assets) {
            if ($asset.name -eq 'EasyRed2HumanAI.dll') {
                $dllUrl = [string]$asset.browser_download_url
            } elseif ($asset.name -like '*-with-BepInEx.zip') {
                $fullZipUrl = [string]$asset.browser_download_url
            }
        }
        Write-Host "  Latest Tag  : $latestTag" -ForegroundColor Green
    }
} catch {
    Write-Host '  [Warn] GitHub API check failed/rate-limited; using direct /releases/latest/download/ endpoint.' -ForegroundColor Yellow
}

$hasBepInEx = (Test-Path (Join-Path $gameDir 'winhttp.dll')) -and (Test-Path (Join-Path $gameDir 'BepInEx\core'))
if (-not $hasBepInEx) {
    if (Test-Path (Join-Path $gameDir '_BepInEx_Disabled_Backup\winhttp.dll')) {
        Write-Host ''
        Write-Host '[ERROR] BepInEx is currently disabled inside _BepInEx_Disabled_Backup\.' -ForegroundColor Red
        Write-Host '        Restore it with Uninstall-BepInEx-Mod.bat option [2] first so your settings are preserved, then re-run this updater.' -ForegroundColor Yellow
        exit 1
    }

    if (-not $fullZipUrl) {
        Write-Host ''
        Write-Host '[ERROR] BepInEx is not installed and the full *-with-BepInEx.zip release asset URL could not be resolved.' -ForegroundColor Red
        Write-Host '        Download EasyRed2HumanAI-*-with-BepInEx.zip manually from GitHub Releases first.' -ForegroundColor Yellow
        exit 1
    }

    $tmpZip = Join-Path $env:TEMP ("BattleHardened_Full_" + [Guid]::NewGuid().ToString('N') + '.zip')
    try {
        Write-Host ''
        Write-Host "[2/3] BepInEx not found — downloading full package ($latestTag)..." -ForegroundColor Cyan
        Invoke-DownloadFile -Url $fullZipUrl -DestPath $tmpZip

        $zipInfo = Get-Item $tmpZip
        $hdr = New-Object byte[] 2
        $fs = [IO.File]::OpenRead($tmpZip)
        try { $null = $fs.Read($hdr, 0, 2) } finally { $fs.Dispose() }
        if ($zipInfo.Length -lt 1048576 -or $hdr[0] -ne 0x50 -or $hdr[1] -ne 0x4B) {
            throw 'Downloaded ZIP archive failed validation.'
        }

        Write-Host "[3/3] Extracting BepInEx + Battle Hardened ($latestTag) into Easy Red 2..." -ForegroundColor Cyan
        Expand-Archive -Path $tmpZip -DestinationPath $gameDir -Force

        $installed = Get-Item $dllPath
        Write-Host ''
        Write-Host "[SUCCESS] Installed Easy Red 2: Battle Hardened ($latestTag) with BepInEx!" -ForegroundColor Green
        Write-Host "          DLL: $dllPath ($($installed.Length) bytes)"
        exit 0
    } finally {
        Remove-Item $tmpZip -Force -ErrorAction SilentlyContinue
    }
}

$tmpDll = Join-Path $env:TEMP ("EasyRed2HumanAI_" + [Guid]::NewGuid().ToString('N') + '.dll.tmp')
try {
    Write-Host ''
    Write-Host "[2/3] Downloading latest EasyRed2HumanAI.dll ($latestTag)..." -ForegroundColor Cyan
    Invoke-DownloadFile -Url $dllUrl -DestPath $tmpDll

    $dlInfo = Get-Item $tmpDll
    $mz = New-Object byte[] 2
    $fs = [IO.File]::OpenRead($tmpDll)
    try { $null = $fs.Read($mz, 0, 2) } finally { $fs.Dispose() }
    if ($dlInfo.Length -lt 102400 -or $mz[0] -ne 0x4D -or $mz[1] -ne 0x5A) {
        throw "Downloaded DLL failed integrity verification (len=$($dlInfo.Length), magic=0x$($mz[0].ToString('X2'))$($mz[1].ToString('X2')))."
    }

    Write-Host "[3/3] Updating $dllPath..." -ForegroundColor Cyan
    if (-not (Test-Path $pluginsDir)) {
        New-Item -ItemType Directory -Force -Path $pluginsDir | Out-Null
    }
    if (Test-Path $dllPath) {
        $bakPath = Join-Path $env:TEMP 'EasyRed2HumanAI.dll.bak'
        Copy-Item $dllPath $bakPath -Force
    }
    Copy-Item $tmpDll $dllPath -Force

    $finalItem = Get-Item $dllPath
    Write-Host ''
    Write-Host "[SUCCESS] Updated to Easy Red 2: Battle Hardened ($latestTag)!" -ForegroundColor Green
    Write-Host "          Path: $dllPath ($($finalItem.Length) bytes)"
} catch {
    Write-Host ''
    Write-Host "[ERROR] Update failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
} finally {
    Remove-Item $tmpDll -Force -ErrorAction SilentlyContinue
}
