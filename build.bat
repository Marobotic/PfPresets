@echo off
setlocal

set "SCRIPT_DIR=%~dp0"
set "PROJECT=%SCRIPT_DIR%PfPresets.csproj"
set "CONFIG=Release"
set "OUTPUT_DIR=%SCRIPT_DIR%bin\%CONFIG%"
set "DEV_DIR=%APPDATA%\XIVLauncher\devPlugins\PfPresets"

if not exist "%PROJECT%" (
  echo [ERROR] Project not found: %PROJECT%
  exit /b 1
)

echo [1/2] Building %PROJECT%...
rem --no-incremental forces a full recompile so the DLL is always rewritten with a fresh
rem timestamp. Dalamud only reloads a dev plugin when the DLL changes on disk, so an
rem "up-to-date" incremental build would leave the in-game plugin stale.
dotnet build "%PROJECT%" -c %CONFIG% --no-incremental
if errorlevel 1 (
  echo [ERROR] Build failed.
  exit /b 1
)

if not exist "%OUTPUT_DIR%\PfPresets.dll" (
  echo [ERROR] Build output not found: %OUTPUT_DIR%\PfPresets.dll
  exit /b 1
)

echo [2/2] Deploying to %DEV_DIR%...
if not exist "%DEV_DIR%" (
  mkdir "%DEV_DIR%"
)

rem Clear stale artifacts first, including the DalamudPackager "PfPresets" subfolder and
rem latest.zip that a previous recursive copy left behind. A dev plugin only needs the
rem loose DLL, manifest, deps and (optionally) the pdb.
if exist "%DEV_DIR%\PfPresets" rmdir /S /Q "%DEV_DIR%\PfPresets"
del /Q "%DEV_DIR%\PfPresets.dll" "%DEV_DIR%\PfPresets.pdb" "%DEV_DIR%\PfPresets.json" "%DEV_DIR%\PfPresets.deps.json" 2>nul

copy /Y "%OUTPUT_DIR%\PfPresets.dll" "%DEV_DIR%\" >nul
if errorlevel 1 goto copyfail
copy /Y "%OUTPUT_DIR%\PfPresets.json" "%DEV_DIR%\" >nul
if errorlevel 1 goto copyfail
copy /Y "%OUTPUT_DIR%\PfPresets.deps.json" "%DEV_DIR%\" >nul
if errorlevel 1 goto copyfail
if exist "%OUTPUT_DIR%\PfPresets.pdb" copy /Y "%OUTPUT_DIR%\PfPresets.pdb" "%DEV_DIR%\" >nul

echo [OK] Build and deploy complete.
echo      Scan Dev Plugins in-game to reload.
exit /b 0

:copyfail
echo [ERROR] Failed to copy build output to devPlugins.
echo         If the plugin is loaded in-game and locking a file, disable it and retry.
exit /b 1
