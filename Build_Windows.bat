@echo off
cd /d "%~dp0"
where dotnet >nul 2>nul || (echo Install .NET 10 SDK first. & pause & exit /b 1)
dotnet publish Source\NagoyaPlanetConsole.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "%~dp0Release"
if errorlevel 1 (echo Build failed. & pause & exit /b 1)
echo Release\NagoyaPlanetConsole.exe
pause
