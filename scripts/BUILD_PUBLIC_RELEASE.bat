@echo off
chcp 65001 >nul
cd /d "%~dp0.."
echo Opening persistent build console...
%ComSpec% /k call scripts\BUILD_PUBLIC_RELEASE_CORE.bat
