@echo off
chcp 65001 >nul
setlocal
cd /d "%~dp0"

echo ================================================
echo   Release build: PortableStreamer
echo ================================================
echo.

if not exist "PortableStreamer.csproj" (
    echo [ERROR] Script must be placed next to PortableStreamer.csproj
    pause
    exit /b 1
)

if not exist "tools\mediamtx.exe" (
    echo [ERROR] tools\mediamtx.exe not found
    pause
    exit /b 1
)

set "OUT=%~dp0publish"
if exist "%OUT%" rmdir /s /q "%OUT%"

echo [1/3] Trying AOT build...
dotnet publish PortableStreamer.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=true -o "%OUT%" -v m --nologo
if errorlevel 1 (
    echo.
    echo AOT is not available: install "Desktop development with C++" in Visual Studio.
    echo Falling back to a regular build without AOT...
    echo.
    dotnet publish PortableStreamer.csproj -c Release -r win-x64 --self-contained true -p:PublishAot=false -o "%OUT%" -v m --nologo
    if errorlevel 1 (
        echo.
        echo [ERROR] Build failed. See messages above.
        pause
        exit /b 1
    )
)

echo.
echo [2/3] Checking tools folder...
if not exist "%OUT%\tools\mediamtx.exe" (
    echo Copying tools again...
    robocopy "tools" "%OUT%\tools" /E /NFL /NDL /NJH /NJS /NP >nul
)

echo.
echo [3/3] Result:
if exist "%OUT%\PortableStreamer.exe" (for %%F in ("%OUT%\PortableStreamer.exe") do echo   PortableStreamer.exe - %%~zF bytes) else (echo   [ERROR] PortableStreamer.exe was not created)
if exist "%OUT%\tools\mediamtx.exe" (echo   tools\mediamtx.exe - OK) else (echo   [ERROR] tools\mediamtx.exe missing)
if exist "%OUT%\tools\gstreamer\bin\gst-launch-1.0.exe" (echo   gst-launch-1.0.exe - OK) else (echo   [ERROR] gst-launch-1.0.exe missing)

echo.
echo Done. Release folder:
echo   %OUT%
echo Copy the whole folder to another PC and run PortableStreamer.exe
pause
