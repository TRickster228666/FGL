@echo off
echo Building FrameGenLive from source...
echo.

dotnet build src\FGMEngine\FrameGenLive.csproj -c Release
if %ERRORLEVEL% NEQ 0 (
    echo Error building FrameGenLive Engine!
    pause
    exit /b %ERRORLEVEL%
)

dotnet build src\FGLApp\FrameGenLiveApp.csproj -c Release
if %ERRORLEVEL% NEQ 0 (
    echo Error building FrameGenLiveApp UI!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Build succeeded!
pause
