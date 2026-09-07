@echo off
chcp 65001 >nul
setlocal EnableExtensions EnableDelayedExpansion

set "ROOT=%~dp0.."
for %%I in ("%ROOT%") do set "ROOT=%%~fI"
set "PROJECT=%ROOT%\src\CropPipViewer\CropPipViewer.csproj"
set "ARTIFACTS=%ROOT%\artifacts"
set "PUBLISH=%ARTIFACTS%\_singlefile_publish"
set "OUT=%ARTIFACTS%\CropPipViewer_v0.9.0-beta_win-x64"
set "ZIP=%ARTIFACTS%\CropPipViewer_v0.9.0-beta_win-x64.zip"
set "HASH=%ZIP%.sha256.txt"
set "LOG=%ROOT%\BUILD_PUBLIC_RELEASE.log"

>"%LOG%" echo Maple PiP Manager release build log
>>"%LOG%" echo Started: %DATE% %TIME%
>>"%LOG%" echo ROOT=%ROOT%
>>"%LOG%" echo PROJECT=%PROJECT%

echo ============================================================
echo Maple PiP Manager v0.9.0-beta - Single EXE Builder
echo ============================================================
echo.
echo This window will stay open even if the build fails.
echo A detailed log will be saved here:
echo %LOG%
echo.

call :MAIN
set "RC=%ERRORLEVEL%"

echo.
if "%RC%"=="0" (
    echo ============================================================
    echo [DONE] Build completed successfully.
    echo ZIP:
    echo %ZIP%
    echo.
    echo Extract the ZIP and run CropPipViewer.exe.
    echo ============================================================
) else (
    echo ============================================================
    echo [FAILED] Build stopped. Error code: %RC%
    echo.
    echo Log file:
    echo %LOG%
    echo.
    echo --- Last log lines ---
    powershell -NoProfile -Command "if (Test-Path -LiteralPath '%LOG%') { Get-Content -LiteralPath '%LOG%' -Tail 35 }"
    echo ============================================================
)

echo.
echo Press any key to close this window.
pause >nul
exit /b %RC%

:MAIN
if not exist "%PROJECT%" (
    echo [FAIL] Project file was not found.
    >>"%LOG%" echo [FAIL] Project file was not found: %PROJECT%
    exit /b 20
)

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [FAIL] .NET SDK was not found on this BUILD PC.
    echo Install .NET 8 SDK only on the PC used to build the release.
    echo End users do not need .NET installed.
    >>"%LOG%" echo [FAIL] dotnet command was not found.
    exit /b 10
)

>>"%LOG%" echo.
>>"%LOG%" echo --- dotnet --info ---
dotnet --info >>"%LOG%" 2>&1

dotnet --list-sdks | findstr /r /b "8\." >nul
if errorlevel 1 (
    echo [FAIL] .NET 8 SDK was not found on this BUILD PC.
    >>"%LOG%" echo [FAIL] .NET 8 SDK was not listed by dotnet --list-sdks.
    exit /b 11
)

if not exist "%ARTIFACTS%" mkdir "%ARTIFACTS%" >>"%LOG%" 2>&1
if exist "%PUBLISH%" rmdir /s /q "%PUBLISH%" >>"%LOG%" 2>&1
if exist "%OUT%" rmdir /s /q "%OUT%" >>"%LOG%" 2>&1
if exist "%ZIP%" del /q "%ZIP%" >>"%LOG%" 2>&1
if exist "%HASH%" del /q "%HASH%" >>"%LOG%" 2>&1
mkdir "%PUBLISH%" >>"%LOG%" 2>&1
mkdir "%OUT%" >>"%LOG%" 2>&1

echo [1/6] Publishing self-contained single EXE...
>>"%LOG%" echo.
>>"%LOG%" echo --- dotnet publish ---
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -o "%PUBLISH%" >>"%LOG%" 2>&1
if errorlevel 1 (
    echo [FAIL] dotnet publish failed.
    >>"%LOG%" echo [FAIL] dotnet publish returned an error.
    exit /b 1
)

echo [2/6] Checking single-file output...
if not exist "%PUBLISH%\CropPipViewer.exe" (
    echo [FAIL] CropPipViewer.exe was not produced.
    >>"%LOG%" echo [FAIL] Missing %PUBLISH%\CropPipViewer.exe
    exit /b 2
)

for %%F in ("%PUBLISH%\*.dll") do (
    if exist "%%~fF" (
        echo [FAIL] A DLL remains outside the EXE: %%~nxF
        >>"%LOG%" echo [FAIL] DLL remains outside EXE: %%~fF
        exit /b 3
    )
)

if exist "%PUBLISH%\CropPipViewer.deps.json" (
    echo [FAIL] deps.json remains outside the EXE.
    >>"%LOG%" echo [FAIL] deps.json remains outside EXE.
    exit /b 4
)
if exist "%PUBLISH%\CropPipViewer.runtimeconfig.json" (
    echo [FAIL] runtimeconfig.json remains outside the EXE.
    >>"%LOG%" echo [FAIL] runtimeconfig.json remains outside EXE.
    exit /b 5
)

echo [3/6] Creating clean end-user folder...
copy /y "%PUBLISH%\CropPipViewer.exe" "%OUT%\CropPipViewer.exe" >>"%LOG%" 2>&1
if errorlevel 1 exit /b 30
mkdir "%OUT%\docs" >>"%LOG%" 2>&1
copy /y "%ROOT%\README.md" "%OUT%\docs\README.md" >>"%LOG%" 2>&1
copy /y "%ROOT%\PRIVACY.md" "%OUT%\docs\PRIVACY.md" >>"%LOG%" 2>&1
copy /y "%ROOT%\docs\USER_GUIDE.md" "%OUT%\docs\USER_GUIDE.md" >>"%LOG%" 2>&1
copy /y "%ROOT%\docs\KNOWN_ISSUES.md" "%OUT%\docs\KNOWN_ISSUES.md" >>"%LOG%" 2>&1
copy /y "%ROOT%\docs\RELEASE_README.txt" "%OUT%\docs\FIRST_RUN.txt" >>"%LOG%" 2>&1
if exist "%ROOT%\docs\images\ui-overview.png" (
    mkdir "%OUT%\docs\images" >>"%LOG%" 2>&1
    copy /y "%ROOT%\docs\images\ui-overview.png" "%OUT%\docs\images\ui-overview.png" >>"%LOG%" 2>&1
)

echo [4/6] Checking clean top-level layout...
set /a ROOTFILECOUNT=0
for /f "delims=" %%F in ('dir /b /a-d "%OUT%" 2^>nul') do set /a ROOTFILECOUNT+=1
if not "!ROOTFILECOUNT!"=="1" (
    echo [FAIL] The extracted package must have exactly one top-level file.
    >>"%LOG%" echo [FAIL] Unexpected top-level file count: !ROOTFILECOUNT!
    dir /b "%OUT%" >>"%LOG%" 2>&1
    exit /b 6
)
if not exist "%OUT%\docs\" (
    echo [FAIL] docs folder was not created.
    >>"%LOG%" echo [FAIL] docs folder missing.
    exit /b 7
)

echo [5/6] Creating ZIP...
powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path '%OUT%\*' -DestinationPath '%ZIP%' -CompressionLevel Optimal -Force" >>"%LOG%" 2>&1
if errorlevel 1 (
    echo [FAIL] ZIP creation failed.
    >>"%LOG%" echo [FAIL] Compress-Archive returned an error.
    exit /b 8
)

echo [6/6] Creating SHA-256...
powershell -NoProfile -ExecutionPolicy Bypass -Command "(Get-FileHash -LiteralPath '%ZIP%' -Algorithm SHA256).Hash | Set-Content -Encoding ASCII -LiteralPath '%HASH%'" >>"%LOG%" 2>&1
if errorlevel 1 (
    echo [FAIL] SHA-256 creation failed.
    >>"%LOG%" echo [FAIL] Get-FileHash returned an error.
    exit /b 9
)

rmdir /s /q "%PUBLISH%" >>"%LOG%" 2>&1
>>"%LOG%" echo.
>>"%LOG%" echo [DONE] ZIP=%ZIP%
>>"%LOG%" echo Finished: %DATE% %TIME%
exit /b 0
