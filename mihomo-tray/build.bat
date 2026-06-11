@echo off
chcp 65001 >nul
setlocal

echo ========================================
echo   Mihomo Tray Controller - Build Script
echo ========================================
echo.

set "CSC=%SystemRoot%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    set "CSC=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

if not exist "%CSC%" (
    echo [ERROR] csc.exe not found. Please install .NET Framework 4.x.
    if not defined CI pause
    exit /b 1
)

echo Using compiler: %CSC%
echo.

set "OUTPUT=MihomoTray.exe"
set "SOURCE=MihomoTray.cs EmbeddedIcons.cs AssemblyInfo.cs"
set "REFS=/reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.dll /reference:System.IO.Compression.FileSystem.dll"

echo Compiling...
"%CSC%" /target:winexe /win32icon:logo.ico /win32manifest:app.manifest /out:"%OUTPUT%" /nologo %REFS% %SOURCE%

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo [ERROR] Compilation failed!
    if not defined CI pause
    exit /b 1
)

if defined MIHOMO_SIGN_SHA1 (
    where signtool.exe >nul 2>nul
    if %ERRORLEVEL% EQU 0 (
        echo Signing with certificate thumbprint: %MIHOMO_SIGN_SHA1%
        signtool.exe sign /fd SHA256 /sha1 "%MIHOMO_SIGN_SHA1%" /tr http://timestamp.digicert.com /td SHA256 "%OUTPUT%"
        if %ERRORLEVEL% NEQ 0 (
            echo.
            echo [ERROR] Signing failed!
            if not defined CI pause
            exit /b 1
        )
    ) else (
        echo [WARN] MIHOMO_SIGN_SHA1 is set, but signtool.exe was not found.
    )
) else (
    echo [INFO] Build is unsigned. Set MIHOMO_SIGN_SHA1 to enable Authenticode signing.
)

echo.
echo ========================================
echo   Build successful!
echo   Output: %CD%\%OUTPUT%
echo ========================================
echo.
echo Run MihomoTray.exe to start the controller.
echo.
if not defined CI pause
