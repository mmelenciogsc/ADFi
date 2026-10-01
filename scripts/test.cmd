@echo off
setlocal
cd /d "%~dp0.."
dotnet restore ADFi.slnx || exit /b 1
dotnet test ADFi.slnx --configuration Release --no-restore || exit /b 1
endlocal

