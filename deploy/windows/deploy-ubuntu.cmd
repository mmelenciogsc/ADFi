@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0\..\.."
if "%ADFI_DEPLOY_HOST%"=="" set "ADFI_DEPLOY_HOST=BLACKOUT"
if "%ADFI_DEPLOY_USER%"=="" set "ADFI_DEPLOY_USER=marx"
set "PUBLISH_DIR=%CD%\artifacts\ubuntu-api"
set "REMOTE_STAGE=/tmp/adfi-deploy-%RANDOM%-%RANDOM%"

dotnet publish src\ADFi.Api\ADFi.Api.csproj --configuration Release --runtime linux-x64 --self-contained false --output "%PUBLISH_DIR%" || exit /b 1
ssh "%ADFI_DEPLOY_USER%@%ADFI_DEPLOY_HOST%" "install -d -m 0700 '%REMOTE_STAGE%'" || exit /b 1
scp -r "%PUBLISH_DIR%\*" "%ADFI_DEPLOY_USER%@%ADFI_DEPLOY_HOST%:%REMOTE_STAGE%/" || exit /b 1
scp deploy\ubuntu\install-release.sh "%ADFI_DEPLOY_USER%@%ADFI_DEPLOY_HOST%:%REMOTE_STAGE%/install-release.sh" || exit /b 1
scp deploy\ubuntu\adfi-api.service "%ADFI_DEPLOY_USER%@%ADFI_DEPLOY_HOST%:%REMOTE_STAGE%/adfi-api.service" || exit /b 1
ssh -tt "%ADFI_DEPLOY_USER%@%ADFI_DEPLOY_HOST%" "sudo bash '%REMOTE_STAGE%/install-release.sh' '%REMOTE_STAGE%' '%REMOTE_STAGE%/adfi-api.service'; result=$?; rm -rf '%REMOTE_STAGE%'; exit $result" || exit /b 1
endlocal
