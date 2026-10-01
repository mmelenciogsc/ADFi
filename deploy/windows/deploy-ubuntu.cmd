@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0\..\.."
if "%ADFI_DEPLOY_HOST%"=="" set "ADFI_DEPLOY_HOST=BLACKOUT"
if "%ADFI_DEPLOY_USER%"=="" set "ADFI_DEPLOY_USER=marx"
set "PUBLISH_DIR=%CD%\artifacts\ubuntu-api"

dotnet publish src\ADFi.Api\ADFi.Api.csproj --configuration Release --runtime linux-x64 --self-contained false --output "%PUBLISH_DIR%" || exit /b 1
ssh "%ADFI_DEPLOY_USER%@%ADFI_DEPLOY_HOST%" "mkdir -p /tmp/adfi-deploy" || exit /b 1
scp -r "%PUBLISH_DIR%\*" "%ADFI_DEPLOY_USER%@%ADFI_DEPLOY_HOST%:/tmp/adfi-deploy/" || exit /b 1
scp deploy\ubuntu\install-release.sh "%ADFI_DEPLOY_USER%@%ADFI_DEPLOY_HOST%:/tmp/adfi-install-release.sh" || exit /b 1
ssh -tt "%ADFI_DEPLOY_USER%@%ADFI_DEPLOY_HOST%" "sudo bash /tmp/adfi-install-release.sh /tmp/adfi-deploy" || exit /b 1
endlocal

