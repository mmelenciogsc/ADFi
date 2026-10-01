# Dependencies and licenses

Use current official distribution channels and pin exact production versions and hashes in a release manifest. Do not copy model binaries into Git.

| Component | Purpose | Official source | License/operational note |
| --- | --- | --- | --- |
| .NET 10 / ASP.NET Core / WPF | Desktop and API | <https://dotnet.microsoft.com/download/dotnet/10.0> | Microsoft product terms; SDK 10.0.401 is pinned in `global.json`. |
| Ollama | Default local model runtime | <https://ollama.com/download/windows> | Record installed runtime and model digest per run. |
| Qwen3-VL | Local visual reasoning | <https://ollama.com/library/qwen3-vl> | Balanced default is the verified `qwen3-vl:4b-instruct` tag; do not assume all model tiers have identical resource needs. |
| FFmpeg/ffprobe | Probe, extraction, mixing and rendering | <https://ffmpeg.org/download.html> | Record build configuration and comply with the license of the distributed build. |
| OpenCV / OpenCvSharp | Deterministic visual processing and face continuity | <https://opencv.org/releases/> and <https://github.com/shimat/opencvsharp> | OpenCV 4.13 wrapper/runtime is NuGet-pinned; YuNet/SFace model files require their own release manifest. |
| Whisper | Chunk transcription | <https://github.com/openai/whisper> | Official `openai-whisper` 20250625 is pinned for the bounded worker; models remain external, hashed assets selected by hardware policy. |
| Tesseract | Exact OCR fallback | <https://github.com/tesseract-ocr/tesseract> | Invoke only for evidence crops requiring exact literal text. |
| Piper | Segment narration | <https://github.com/OHF-Voice/piper1-gpl> | `piper-tts` 1.8.0 is pinned for Windows installation. Current engine is GPL-3.0; voice datasets/models are separately licensed. |
| Piper voices | Five preview choices | <https://huggingface.co/rhasspy/piper-voices> | Cori high, LJSpeech high, Bryce medium (public domain); Joe medium (CC0-1.0); Sam medium (Apache-2.0). Preserve applicable notices and verify hashes at install time. |
| PostgreSQL | Server persistence | <https://www.postgresql.org/download/linux/ubuntu/> | Use a supported release, local-only database listener where practical, and encrypted backups. |
| PayPal | Recurring subscriptions/webhooks/payouts | <https://developer.paypal.com/docs/subscriptions/> | Start in sandbox. Client secret and webhook ID remain server-only. |
| Google/YouTube | Creator-owned publication | <https://developers.google.com/youtube/v3/docs/videos/insert> | Installed-app OAuth; `youtube.upload` plus `youtube.readonly`; prepare compliance audit before public uploads. |
| Sign in with ChatGPT | User-owned ChatGPT cloud option | <https://developers.openai.com/siwc/token-sharing-open-source/sign-in> | Eligible ChatGPT accounts only; no API key/password request and no access to conversation history. Public Responses API calls use `store:false`. |

`en_US-hfc_*`, `en_US-ryan-*`, `en_GB-semaine-*`, and other noncommercial voice datasets are intentionally absent from the five-voice product catalog.
