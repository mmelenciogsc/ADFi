@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0\..\.."

set "ADFI_VERSION=%~1"
if "%ADFI_VERSION%"=="" set "ADFI_VERSION=0.1.0-dev"
set "ADFI_PUBLISH=%CD%\artifacts\windows\ADFi-%ADFI_VERSION%-win-x64"
set "ADFI_ZIP=%CD%\artifacts\windows\ADFi-%ADFI_VERSION%-win-x64.zip"

if exist "%ADFI_PUBLISH%" rmdir /s /q "%ADFI_PUBLISH%" || exit /b 1
if exist "%ADFI_ZIP%" del /q "%ADFI_ZIP%" || exit /b 1

dotnet publish src\ADFi.Desktop\ADFi.Desktop.csproj ^
  --configuration Release ^
  --runtime win-x64 ^
  --self-contained true ^
  --output "%ADFI_PUBLISH%" ^
  -p:Version="%ADFI_VERSION%" ^
  -p:PublishReadyToRun=true || exit /b 1

copy /y deploy\windows\requirements-local-ai.txt "%ADFI_PUBLISH%\requirements-local-ai.txt" >nul || exit /b 1
copy /y README.md "%ADFI_PUBLISH%\README.md" >nul || exit /b 1
powershell.exe -NoProfile -NonInteractive -Command ^
  "Compress-Archive -LiteralPath '%ADFI_PUBLISH%' -DestinationPath '%ADFI_ZIP%' -CompressionLevel Optimal; (Get-FileHash -Algorithm SHA256 -LiteralPath '%ADFI_ZIP%').Hash + '  ' + (Split-Path -Leaf '%ADFI_ZIP%') | Set-Content -Encoding ascii -LiteralPath ('%ADFI_ZIP%' + '.sha256')" || exit /b 1

echo Created %ADFI_ZIP%
echo Created %ADFI_ZIP%.sha256
endlocal
