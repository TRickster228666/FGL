@echo off
echo Building FrameGenLive from source...
echo.

dotnet build src\FGMEngine\FGMEngine.csproj -c Release
if %ERRORLEVEL% NEQ 0 (
    echo Error building FGMEngine!
    pause
    exit /b %ERRORLEVEL%
)

dotnet build src\FGLApp\FGLApp.csproj -c Release
if %ERRORLEVEL% NEQ 0 (
    echo Error building FGLApp!
    pause
    exit /b %ERRORLEVEL%
)

echo.
echo Build succeeded!
pause
