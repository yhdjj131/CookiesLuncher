@echo off
rem ============================================================
rem  prepare_browsers.bat
rem  Check that Thorium is present under external_browsers\.
rem
rem  This project does NOT use Playwright's built-in Chrome for
rem  Testing; the thorium backend ships its own community build
rem  (proprietary codecs, H.264/AAC) inside the bundle.
rem
rem  Run this once, BEFORE build.bat.
rem
rem  ASCII only on purpose: cmd.exe mis-parses .bat files that
rem  contain multi-byte characters while code page 65001 is active.
rem ============================================================
setlocal
cd /d "%~dp0"
chcp 65001 >nul

set THORIUM_SRC=external_browsers\thorium-win64\thorium.exe

echo ========================================
echo   Prepare Thorium Browser
echo ========================================
echo.

if exist "%THORIUM_SRC%" (
  echo [1/1] Thorium found at %THORIUM_SRC%.
  echo       Ready to build.
) else (
  echo [ERROR] Thorium NOT found at %THORIUM_SRC%.
  echo.
  echo         Place a Thorium win64 build into:
  echo           external_browsers\thorium-win64\
  echo         It must contain thorium.exe plus its version resource
  echo         folder, e.g. a folder like 154.0.8037.45 that holds
  echo         chrome.dll, Locales, resources, WidevineCdm and more.
  echo.
  echo         Source: github.com/gz83/thorium releases, e.g. the file
  echo         Thorium_SSE4_154.0.8037.45.zip. Unpack it; the content of
  echo         its BIN folder goes into thorium-win64\.
  echo.
  pause
  exit /b 1
)

echo.
echo ========================================
echo   Done. Next step: run build.bat
echo ========================================
pause
