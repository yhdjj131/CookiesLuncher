@echo off
rem ============================================================
rem  build.bat - build a FULLY SELF-CONTAINED CookieLauncher
rem
rem  Steps:
rem    0. check python / pyinstaller
rem    1. make sure Thorium is present under external_browsers/
rem       (this project does NOT use Playwright's Chrome-for-Testing)
rem    2. clean old output
rem    3. run PyInstaller (--add-data embeds external_browsers)
rem    4. copy VC++ runtime DLLs next to the binaries that may need them
rem    5. verify Thorium really ended up inside dist
rem    6. write config template + run verify_selfcontained.py
rem
rem  ASCII only on purpose: cmd.exe mis-parses .bat files that contain
rem  multi-byte characters while code page 65001 is active.
rem ============================================================
setlocal enabledelayedexpansion
cd /d "%~dp0"
chcp 65001 >nul

set DIST=dist\CookieLauncher
set INTERNAL=%DIST%\_internal
set THORIUM_SRC=external_browsers\thorium-win64\thorium.exe
set THORIUM_DST=%INTERNAL%\external_browsers\thorium-win64\thorium.exe

echo ========================================
echo   Build CookieLauncher (self-contained)
echo ========================================
echo.

rem ---------- Step 0: environment ----------
echo [0/6] Checking environment ...
python --version >nul 2>&1
if errorlevel 1 (
  echo [ERROR] Python not found in PATH.
  echo.
  pause
  exit /b 1
)
pyinstaller --version >nul 2>&1
if errorlevel 1 (
  echo [ERROR] PyInstaller not installed. Run: pip install -r requirements.txt
  echo.
  pause
  exit /b 1
)

rem ---------- Step 1: Thorium present? ----------
echo [1/6] Checking Thorium under external_browsers ...
if not exist "%THORIUM_SRC%" (
  echo [ERROR] Thorium not found at %THORIUM_SRC%.
  echo         Place a Thorium win64 build into external_browsers\thorium-win64\.
  echo         It must contain thorium.exe plus its version resource folder.
  echo         See prepare_browsers.bat and README.md for details.
  echo.
  pause
  exit /b 1
)
echo       Thorium found.

rem ---------- Step 2: clean ----------
echo [2/6] Cleaning old build ...
if exist build rmdir /s /q build
if exist dist rmdir /s /q dist
if exist CookieLauncher.spec del /q CookieLauncher.spec

rem ---------- Step 3: PyInstaller ----------
echo [3/6] Running PyInstaller ...
pyinstaller --noconfirm --clean --onedir ^
  --name "CookieLauncher" ^
  --console ^
  --collect-all playwright ^
  --collect-submodules argon2 ^
  --hidden-import argon2 ^
  --hidden-import _argon2_cffi_bindings ^
  --hidden-import cryptography ^
  --hidden-import cryptography.fernet ^
  --add-data "external_browsers;external_browsers" ^
  main.py
if errorlevel 1 (
  echo [ERROR] PyInstaller failed. See the output above.
  echo.
  pause
  exit /b 1
)

rem ---------- Step 4: VC++ runtime DLLs ----------
echo [4/6] Copying VC++ runtime DLLs ...
if not exist "%INTERNAL%" mkdir "%INTERNAL%"
set DLL_MISSING=0
for %%d in (msvcp140.dll vcruntime140.dll vcruntime140_1.dll) do (
  if exist "%SystemRoot%\System32\%%d" (
    copy /y "%SystemRoot%\System32\%%d" "%INTERNAL%\%%d" >nul
    echo       Copied to _internal: %%d
  ) else (
    set DLL_MISSING=1
    echo       [WARN] %%d not found in System32
  )
)
rem Thorium and the Playwright driver are started as separate processes, so
rem the DLLs are also placed right next to those binaries (that is the only
rem location Windows searches reliably for a child process).
rem NOTE: "where /r" only lists files that really exist. Do NOT use
rem "for /r ... in (thorium.exe)": with a literal file name that loop runs
rem once per DIRECTORY (the name is not checked for existence) and would
rem scatter copies all over the tree.
for /f "delims=" %%f in ('where /r "%DIST%" thorium.exe 2^>nul') do (
  for %%d in (msvcp140.dll vcruntime140.dll vcruntime140_1.dll) do (
    if exist "%SystemRoot%\System32\%%d" copy /y "%SystemRoot%\System32\%%d" "%%~dpf%%d" >nul
  )
)
for /f "delims=" %%f in ('where /r "%DIST%" node.exe 2^>nul') do (
  for %%d in (msvcp140.dll vcruntime140.dll vcruntime140_1.dll) do (
    if exist "%SystemRoot%\System32\%%d" copy /y "%SystemRoot%\System32\%%d" "%%~dpf%%d" >nul
  )
)
if "!DLL_MISSING!"=="1" (
  echo       [WARN] Some VC++ DLLs were missing on this machine.
  echo              Target machines may need the Visual C++ Redistributable.
)

rem ---------- Step 5: verify embedded Thorium ----------
echo [5/6] Verifying embedded Thorium inside dist ...
if not exist "%THORIUM_DST%" (
  echo [ERROR] Thorium NOT found at %THORIUM_DST%.
  echo         The bundle is NOT self-contained. Check that --add-data
  echo         embedded external_browsers correctly.
  echo.
  pause
  exit /b 1
)
echo       Found: %THORIUM_DST%

rem ---------- Step 6: config template + self-check ----------
echo [6/6] Writing config template and running self-check ...
if exist config.json (
  copy /y config.json "%DIST%\config.json" >nul
  echo       Copied config.json from the project root.
  echo       NOTE: if it contains a real password_hash, clear it before shipping.
) else (
  python -c "import json,pathlib;d={'password_hash':'','encryption_salt':'','log_level':'INFO','log_retention_days':30,'browser_backend':'thorium','browser':{'headless':False,'window_size':'maximized'}};pathlib.Path(r'dist/CookieLauncher/config.json').write_text(json.dumps(d,ensure_ascii=False,indent=2),encoding='utf-8');print('      Default config.json template written.')"
)
if exist verify_selfcontained.py (
  python verify_selfcontained.py
  if errorlevel 1 (
    echo [ERROR] Self-check FAILED. The bundle is not fully self-contained.
    echo.
    pause
    exit /b 1
  )
) else (
  echo       verify_selfcontained.py not found, skipped.
)

echo.
echo ========================================
echo   Build complete: %DIST%\
echo   The folder is fully self-contained:
echo   Thorium, Python and all DLLs are inside.
echo   Run: %DIST%\CookieLauncher.exe
echo ========================================
pause
exit /b 0
