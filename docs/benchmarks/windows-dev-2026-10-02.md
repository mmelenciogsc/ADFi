# Windows development-machine validation — 2026-10-02

This is measured development evidence, not a universal performance claim. Measurements were taken
on Windows 11 Home Single Language 10.0.26200 (build 26200), an Intel Core i7-12650H with 16 logical
processors, 16,870,072,320 bytes of installed RAM, an NVIDIA GeForce RTX 4060 Laptop GPU reporting
8,188 MiB total VRAM, and an SSD-backed `D:` workspace.

At the measurement snapshot, 3,719,053,312 bytes of system RAM and 7,956 MiB of VRAM were free. The
automatic policy therefore conservatively selects the constrained `qwen3-vl:2b-instruct` tier when
that low free-RAM condition persists. A separately bounded `qwen3-vl:4b-instruct` smoke benchmark
succeeded at 59.61 generated tokens per second for 7 evaluation tokens; wall time including model
load was 29.64 seconds. Its Ollama model ID was `ee4b975b58c1`. `keep_alive: 0` was used and both
`ollama ps` and NVIDIA compute-process telemetry were empty immediately afterward. The 60-frame,
640x360, libx264 `veryfast` startup-inclusive transcode measurement was 11.12 frames per second.

Installed local runtime evidence:

- Ollama 0.34.4, with Qwen3-VL 2B/4B/8B instruction variants present.
- Python 3.12.10 isolated under `%LOCALAPPDATA%\ADFi\runtime\python`.
- `openai-whisper` 20250625; CPU-only `tiny` smoke transcription completed at approximately 149
  analyzed frames per second on a 2.171-second generated narration sample.
- `piper-tts` 1.8.0 and all five catalog voices. A real Cori synthesis produced a valid 2.171-second
  WAV. Voice models remain external assets under `%LOCALAPPDATA%\ADFi\models\piper`.
- Tesseract 5.4.0.20240606 with Leptonica 1.84.1. A generated `ADFi OCR 2048` image was recovered
  exactly using page-segmentation mode 7.

The downloaded Piper assets were hashed after installation:

| Asset | SHA-256 |
| --- | --- |
| `en_GB-cori-high.onnx` | `470b4dd634c98f8a4850d7626ffc3dfc90774628eeef6605a6dd8f88f30a5903` |
| `en_GB-cori-high.onnx.json` | `9e7fb5b5671612c22f3c81cbe46c1ae87b031a4632bcb509e499dad6f1e2adec` |
| `en_US-ljspeech-high.onnx` | `e84352f695c445804406028d9e13a7bb92552ff8bfe88f3669c5cd2b3621c1b0` |
| `en_US-ljspeech-high.onnx.json` | `7e1f4634af596d83cca997fb7a931ba80b70f8a316a2655ee69c55365e0ace14` |
| `en_US-bryce-medium.onnx` | `dc9caa6c313199ffb5ac698b6e542fa6cba388aeaf2731e25262e33b9810aef1` |
| `en_US-bryce-medium.onnx.json` | `7ceb1bc4af6d4e41b6d1edbb86c67e91e01eaa71f66db4cd0ae92ac704d415be` |
| `en_US-joe-medium.onnx` | `58afce0321b8d9c46d7cdf9c16500cc55a793b4220212dba6b70fb788b3baf06` |
| `en_US-joe-medium.onnx.json` | `3d6d5410b3795cb1950595247ef8f06190719e6fdbfa3a2356d8ec368e1aad33` |
| `en_US-sam-medium.onnx` | `56417b3b4afe8ec6bb4cabf06e17d67261fdd5bf334592abcfc80052fba11163` |
| `en_US-sam-medium.onnx.json` | `8c7fb47f19683b0b81037c5564f9a5ad4699a9da685e0e5da0a72fd3c3f5c1c4` |

This smoke evidence does not replace the required 5-, 15-, and 60-minute 1080p end-to-end matrix,
JAWS/NVDA acceptance testing or per-voice listening review.

## Licensed three-minute end-to-end diagnostics

After the component smoke checks, two licensed real-film excerpts completed Phases 0–12 through the
same production services used by the desktop app. Sintel processed 23 transcript segments and 14
visual scenes; Tears of Steel processed 39 transcript segments and 13 visual scenes. Their respective
Phases 3–5 times were 5 minutes 33.181 seconds and 6 minutes 1.491 seconds on the automatically
selected constrained Qwen tier. Final rendering took 9.975 and 11.558 seconds, stream-copied both
H.264 video streams and produced exactly 180-second outputs that passed complete FFmpeg decode and
artifact-hash verification.

Neither excerpt contained a verified narration-safe gap of at least two seconds once dialogue and
meaningful source sound were protected, so both correctly produced zero AD cues. This proves the
pipeline fails conservatively rather than forcing narration, but it is not a narration-quality demo.
Inputs, commands, measurements, output paths, hashes and limitations are in
[Licensed real-media diagnostics](../REAL-MEDIA-DIAGNOSTICS.md).

## Full complementary-source diagnostics

Three complete ten-to-thirteen-minute works then exercised the same production Phases 0–12 across
the three source-audio conditions needed by the walkthrough:

| Source condition | Duration | Transcript segments | Scenes | AD segments | Final size |
| --- | ---: | ---: | ---: | ---: | ---: |
| Dialogue, music and effects — *Cosmos Laundromat* | 12:10.56 | 72 | 197 | 36 | 532,379,413 bytes |
| Music and effects, no intelligible dialogue — *Big Buck Bunny* | 10:34.62 | 5 | 183 | 42 | 273,450,190 bytes |
| Fully silent — *A Trip to the Moon* | 12:41.52 | 0; Whisper skipped | 70 | 57 | 43,651,467 bytes |

All three original hashes remained unchanged. All three final MP4 hashes match their finalization
manifests, and complete FFmpeg decode checks succeeded. These runs were intentionally resumable and
included defect-fix restarts, so the latest per-stage durations contain checkpoint restore time and
must not be presented as cold performance benchmarks. The diagnostic reports retain every measured
attempt under `D:\ADFi-DemoRuns\Projects`.

The corrected outputs use `adfi-final-render-v2-audible-narration`, with first cues at 7.500,
1.923 and 2.000 seconds respectively. Every timing overrun received an explicit evidence-grounded
wording review. In addition to complete decode and bundle-hash checks, narration was measured and
recognized from audio re-extracted from each final MP4. This specifically guards against validating
standalone Piper WAV files while accidentally omitting them from the muxed result. The results do
not by themselves establish final editorial quality; a complete human viewing/listening pass is
still required before publication. Exact output paths, hashes and corrected behavior are in
[Licensed real-media diagnostics](../REAL-MEDIA-DIAGNOSTICS.md). Walkthrough excerpts and stills are
indexed separately at `D:\ADFi-DemoAssets\asset-manifest.json`.
