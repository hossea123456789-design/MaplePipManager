@echo off
setlocal
pushd "%~dp0"
echo [BUILD] CropPipViewer v33c Release
where dotnet >nul 2>nul
if errorlevel 1 (
  echo dotnet SDK not found. Install .NET 8 SDK.
  pause
  exit /b 1
)
dotnet build -c Release
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
echo Build complete.
pause
popd
