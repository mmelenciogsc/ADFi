@echo off
setlocal EnableExtensions DisableDelayedExpansion
cd /d "%~dp0\..\.."

set "ADFI_RUNTIME=%LOCALAPPDATA%\ADFi\runtime"
set "ADFI_PYTHON=%ADFI_RUNTIME%\python"
set "ADFI_PIPER_VOICES=%LOCALAPPDATA%\ADFi\models\piper"
if not exist "%ADFI_RUNTIME%" mkdir "%ADFI_RUNTIME%" || exit /b 1
if not exist "%ADFI_PIPER_VOICES%" mkdir "%ADFI_PIPER_VOICES%" || exit /b 1

where py >nul 2>nul || (
  echo Python launcher was not found. Install a supported 64-bit Python release first.
  exit /b 1
)
py -3.12 -V >nul 2>nul
if errorlevel 1 (
  echo Python 3.12 x64 is required for the pinned Whisper and Piper runtime.
  exit /b 1
)
if not exist "%ADFI_PYTHON%\Scripts\python.exe" py -3.12 -m venv "%ADFI_PYTHON%" || exit /b 1
"%ADFI_PYTHON%\Scripts\python.exe" -m pip install --upgrade pip || exit /b 1
"%ADFI_PYTHON%\Scripts\python.exe" -m pip install --requirement deploy\windows\requirements-local-ai.txt || exit /b 1
"%ADFI_PYTHON%\Scripts\python.exe" -m piper.download_voices ^
  --data-dir "%ADFI_PIPER_VOICES%" ^
  en_GB-cori-high en_US-ljspeech-high en_US-bryce-medium en_US-joe-medium en_US-sam-medium || exit /b 1

where ffmpeg >nul 2>nul || echo WARNING: FFmpeg is not on PATH.
where ffprobe >nul 2>nul || echo WARNING: ffprobe is not on PATH.
where ollama >nul 2>nul || echo WARNING: Ollama is not on PATH.
where tesseract >nul 2>nul || if not exist "%ProgramFiles%\Tesseract-OCR\tesseract.exe" echo WARNING: Tesseract was not found.

echo Local Python runtime installed at %ADFI_PYTHON%
echo Piper voice files installed at %ADFI_PIPER_VOICES%
echo Ollama and Whisper model files are installed separately and verified by release manifest.
endlocal
