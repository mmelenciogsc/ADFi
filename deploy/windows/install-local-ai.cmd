@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0\..\.."

set "ADFI_RUNTIME=%LOCALAPPDATA%\ADFi\runtime"
set "ADFI_PYTHON=%ADFI_RUNTIME%\python"
if not exist "%ADFI_RUNTIME%" mkdir "%ADFI_RUNTIME%" || exit /b 1

where py >nul 2>nul || (
  echo Python launcher was not found. Install a supported 64-bit Python release first.
  exit /b 1
)
if not exist "%ADFI_PYTHON%\Scripts\python.exe" py -3 -m venv "%ADFI_PYTHON%" || exit /b 1
"%ADFI_PYTHON%\Scripts\python.exe" -m pip install --upgrade pip || exit /b 1
"%ADFI_PYTHON%\Scripts\python.exe" -m pip install --requirement deploy\windows\requirements-local-ai.txt || exit /b 1

where ffmpeg >nul 2>nul || echo WARNING: FFmpeg is not on PATH.
where ffprobe >nul 2>nul || echo WARNING: ffprobe is not on PATH.
where ollama >nul 2>nul || echo WARNING: Ollama is not on PATH.
where tesseract >nul 2>nul || echo WARNING: Tesseract is not on PATH.

echo Local Python runtime installed at %ADFI_PYTHON%
echo Model and voice files are installed separately and verified by release manifest.
endlocal
