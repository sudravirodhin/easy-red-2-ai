@echo off
setlocal EnableDelayedExpansion
title Easy Red 2 - BepInEx & Mod Manager (Disable / Restore / Uninstall)

cd /d "%~dp0"

if not exist "Easy Red 2.exe" (
    echo ============================================================================
    echo [ERROR] "Easy Red 2.exe" was not found in this folder:
    echo         %~dp0
    echo.
    echo Please place and run this script directly inside your Easy Red 2 game folder
    echo ^(e.g. Steam\steamapps\common\Easy Red 2^).
    echo ============================================================================
    pause
    exit /b 1
)

tasklist /FI "IMAGENAME eq Easy Red 2.exe" 2>NUL | find /I "Easy Red 2.exe" >NUL
if "%ERRORLEVEL%"=="0" (
    echo ============================================================================
    echo [ERROR] Easy Red 2 is currently running!
    echo Please close Easy Red 2 before modifying or uninstalling BepInEx files.
    echo ============================================================================
    pause
    exit /b 1
)

:MENU
cls
echo ============================================================================
echo   Easy Red 2 - BepInEx ^& Humanized AI Mod Manager
echo   Game Folder: %~dp0
echo ============================================================================
if exist "winhttp.dll" (
    echo   Current Status: [MODDED - BepInEx Active]
) else if exist "_BepInEx_Disabled_Backup\winhttp.dll" (
    echo   Current Status: [VANILLA - BepInEx Temporarily Disabled ^& Backed Up]
) else (
    echo   Current Status: [VANILLA - BepInEx Not Installed]
)
echo ============================================================================
echo.
echo   [1] Play Vanilla Temporarily (Backup ^& Disable BepInEx + Mods)
echo   [2] Restore Modded Game      (Re-enable Backed-Up BepInEx + Mods)
echo   [3] Uninstall Completely     (Permanently Remove BepInEx + Mods)
echo   [4] Exit
echo.
set /p CHOICE="Select an option [1-4]: "

if "%CHOICE%"=="1" goto DISABLE_MODS
if "%CHOICE%"=="2" goto RESTORE_MODS
if "%CHOICE%"=="3" goto UNINSTALL_MODS
if "%CHOICE%"=="4" exit /b 0
goto MENU

:DISABLE_MODS
echo.
if not exist "winhttp.dll" if not exist "BepInEx" (
    echo [INFO] BepInEx is already disabled or not installed.
    pause
    goto MENU
)
if not exist "_BepInEx_Disabled_Backup" mkdir "_BepInEx_Disabled_Backup"
for %%I in ("BepInEx" "dotnet" "winhttp.dll" "doorstop_config.ini" ".doorstop_version" "changelog.txt") do (
    if exist "%%~I" (
        move /Y "%%~I" "_BepInEx_Disabled_Backup\" >NUL
        echo   Moved %%~I -^> _BepInEx_Disabled_Backup\
    )
)
echo.
echo [SUCCESS] Easy Red 2 is now 100%% Vanilla!
echo           Run this file again and choose [2] whenever you want to restore your mods.
echo.
pause
exit /b 0

:RESTORE_MODS
echo.
if not exist "_BepInEx_Disabled_Backup" (
    echo [INFO] No backup folder ^(_BepInEx_Disabled_Backup^) found to restore.
    pause
    goto MENU
)
for %%I in ("BepInEx" "dotnet" "winhttp.dll" "doorstop_config.ini" ".doorstop_version" "changelog.txt") do (
    if exist "_BepInEx_Disabled_Backup\%%~I" (
        move /Y "_BepInEx_Disabled_Backup\%%~I" "." >NUL
        echo   Restored %%~I
    )
)
rd "_BepInEx_Disabled_Backup" 2>NUL
echo.
echo [SUCCESS] BepInEx and all your mods/configs have been restored!
echo.
pause
exit /b 0

:UNINSTALL_MODS
echo.
set /p CONFIRM="Are you sure you want to permanently delete BepInEx and all plugins? (Y/N): "
if /I not "%CONFIRM%"=="Y" goto MENU
if exist "BepInEx" rd /s /q "BepInEx"
if exist "dotnet" rd /s /q "dotnet"
if exist "_BepInEx_Disabled_Backup" rd /s /q "_BepInEx_Disabled_Backup"
for %%F in ("winhttp.dll" "doorstop_config.ini" ".doorstop_version" "changelog.txt") do (
    if exist "%%~F" del /f /q "%%~F"
)
echo.
echo [SUCCESS] BepInEx and all mods have been completely uninstalled.
echo.
pause
exit /b 0
