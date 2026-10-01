@echo off
setlocal EnableExtensions

if "%~1"=="" (
  echo Usage: trust-development-certificate.cmd ^<certificate-file^> [health-url]
  echo This adds one development certificate to the current Windows user's Trusted Root store.
  exit /b 2
)

set "ADFI_CERTIFICATE=%~f1"
set "ADFI_HEALTH_URL=%~2"

if not exist "%ADFI_CERTIFICATE%" (
  echo Certificate not found: %ADFI_CERTIFICATE%
  exit /b 3
)

echo Certificate fingerprint:
certutil -hashfile "%ADFI_CERTIFICATE%" SHA256
if errorlevel 1 exit /b 4

echo Adding this certificate to the current user's Trusted Root store.
certutil -user -f -addstore Root "%ADFI_CERTIFICATE%"
if errorlevel 1 exit /b 5

if not "%ADFI_HEALTH_URL%"=="" (
  echo Verifying HTTPS without a certificate-validation bypass.
  curl.exe --fail --silent --show-error "%ADFI_HEALTH_URL%"
  if errorlevel 1 exit /b 6
  echo.
)

echo Development certificate trust completed.
exit /b 0
