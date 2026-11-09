@echo off
if exist "%~dp0bin\FrameGenLiveApp.exe" (
    start "" "%~dp0bin\FrameGenLiveApp.exe"
) else (
    echo Executable not found in bin\.
    echo Starting build first...
    call "%~dp0build.bat"
)
