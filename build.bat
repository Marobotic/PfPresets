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
dotnet build "%PROJECT%" -c %CONFIG%
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
xcopy "%OUTPUT_DIR%\*" "%DEV_DIR%\" /E /I /Y >nul
if errorlevel 1 (
  echo [ERROR] Failed to copy build output to devPlugins.
  exit /b 1
)

echo [OK] Build and deploy complete.
echo      Scan Dev Plugins in-game to reload.
exit /b 0
