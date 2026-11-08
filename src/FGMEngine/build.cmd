@echo off
cd /d "%~dp0"
dotnet build FrameGenLive.csproj -c Release
pause
