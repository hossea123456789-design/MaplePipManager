@echo off
setlocal
pushd "%~dp0"
set "TFM=net8.0-windows10.0.19041.0"
set "EXE=%~dp0bin\Release\%TFM%\CropPipViewer.exe"
echo [RUN] CropPipViewer v33c
if not exist "%EXE%" (
  echo Release executable not found:
  echo %EXE%
  echo.
  echo Run BUILD.BAT first.
  pause
  popd
  exit /b 1
)
start "" "%EXE%"
popd
