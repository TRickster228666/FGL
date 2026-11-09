@echo off
title Building FrameGenLive
echo ==============================================
echo   Building FrameGenLive from source...
echo ==============================================
echo.

echo [1/2] Compiling FrameGenLive Engine...
dotnet build src\FGMEngine\FrameGenLive.csproj -c Release -o bin
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Failed to build FrameGenLive Engine!
    pause
    exit /b %ERRORLEVEL%
)

echo [2/2] Compiling FrameGenLiveApp UI...
dotnet build src\FGLApp\FrameGenLiveApp.csproj -c Release -o bin
if %ERRORLEVEL% NEQ 0 (
    echo [ERROR] Failed to build FrameGenLiveApp UI!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo ==============================================
echo   BUILD SUCCEEDED!
echo   Executables are located in: %~dp0bin
echo ==============================================
echo.
echo Starting FrameGenLiveApp...
start "" "%~dp0bin\FrameGenLiveApp.exe"
pause
