# Implementation status

Status date: 2026-10-02. This file distinguishes executable, tested code from integration work that still needs external credentials, licensed model assets, or deployment access.

## Implemented and verified on Windows 11

- The canonical `D:\ADFi` Git monorepo builds under .NET SDK 10.0.401 with warnings as errors.
- The WPF composition root exposes exactly two top-level destinations, MY ADFi and DISCOVER, using native UI Automation controls. Architecture tests enforce this boundary and the rights-attestation text.
- There are exactly two paid plan definitions: Community at USD $2/month and Creator at USD $9/month. Creation and conversational-editing entitlements are Creator-only in domain policy and server-derived subscription results.
- Source intake accepts existing supported local video files only, requires the versioned rights attestation, hashes the untouched source, runs ffprobe, estimates temporary space, refuses insufficient space, and atomically persists source metadata.
- Phase 1 creates a normalized analysis-only mono 16 kHz stream, characterizes levels/silence, creates bounded overlapping chunks, persists each transcription chunk, deterministically merges overlap, and combines transcript dialogue with silence and meaningful-source-sound intervals. Its official OpenAI Whisper adapter keeps one hardware-selected model in a bounded Python worker, transcribes chunks sequentially with word timestamps, maps results to absolute source time, and explicitly unloads before later heavyweight stages.
- Phase 2 uses bounded OpenCV sampling, deterministic cut/text/motion metrics, temporal keyframe selection, near-duplicate suppression, actual JPEG evidence extraction, transcript/window correlation, and an atomic visual-index manifest.
- Phase 3 has project-local YuNet/SFace face detection/embedding and conservative continuity matching. It cannot perform internet or real-world identity lookup; names become usable only from explicit time-stamped evidence.
- Phase 4 has evidence-preserving VLM/Tesseract OCR reconciliation that retains conflicting readings and requests more evidence rather than selecting plausible fiction.
- Phase 5 sequentially sends only bounded indexed frames, nearby transcript and time-valid established entities to Qwen through `ILocalModelRuntime`; every response is schema/evidence checked, persisted by scene, and followed by model unload.
- Phases 6–8 include AD-necessity scoring, quiet-window selection, an evidence-bound writer, 180-WPM planning, spoiler/dialogue/evidence checks, five reviewed Piper voice choices, per-segment cached synthesis, actual WAV measurement, and at most three shortening attempts before required review. Speech is never silently time-compressed.
- Phase 12 validates media/evidence/narration invariants, builds a narration track, uses smooth source-audio ducking and limiting, stream-copies compatible H.264/HEVC/AV1 video when possible, and never overwrites the source.
- Pipeline checkpoints and project edits are atomic and append-only. Edit application uses optimistic version checks; undo and checkpoint restoration create compensating versions.
- Nontrivial conversation requests pass through a deterministic intent classifier and schema-validated local Planner. Tool names must exist in the versioned allow-list, and task execution remains strictly sequential with result persistence after every atomic task.
- Creator activation profiling records architecture, CPU features/threads, system/free RAM, NVIDIA model/total/free VRAM when available, acceleration, disk type/free space and bounded FFmpeg throughput. Ollama throughput is measured only after the selected model is installed and immediately unloaded.
- Ollama is behind `ILocalModelRuntime`; the balanced tier is official `qwen3-vl:4b`, with replaceable constrained/large tiers and no schema changes.
- Five Piper voices with noncommercial restrictions excluded are versioned in the voice catalog. Piper itself and every voice model remain separately licensed dependencies.
- Google installed-app OAuth request generation uses state and PKCE; token exchange/refresh uses the required upload/channel-read scopes; refresh tokens persist through `IProtectedSecretStore`, whose Windows implementation uses current-user DPAPI. Publication confirms the exact channel and performs upload-only resumable `videos.insert` from a finalized local MP4.
- PayPal sandbox/production REST boundaries create subscriptions from configured provider plan IDs, verify webhook signatures through PayPal, deduplicate event IDs, retain out-of-order events, apply timestamp-monotonic subscription status, and submit payouts with stable batch/item IDs. Secrets are server configuration only.
- Account registration, email verification, PBKDF2-SHA512 password hashing, opaque hashed bearer sessions, session rotation, one-use 30-minute recovery tokens and all-session revocation after reset are implemented.
- PostgreSQL migrations cover the normalized product, collaboration, moderation, publication, referral, rights, ledger, payout and feature-configuration domains. Economic allocation enforces the fixed 70/30 top-level policy and only qualified ADFi-native events.
- The anonymous API exposes liveness, dependency-aware readiness, plan and DISCOVER endpoints. The public HTML surface has landmarks, a skip link, the official privacy-enhanced YouTube player and ADFi canonical sharing rather than bare YouTube sharing.
- Ubuntu deployment stages an immutable release, runs migrations before switching the current symlink, restarts the hardened systemd unit, checks readiness and restores the prior release on failure.

The current automated suite contains 108 passing tests: 93 core/integration-policy tests, 9 API tests and 6 architecture tests. The media integration test generates a real local H.264/AAC fixture, runs ffprobe, Phase 0, audio preparation, OpenCV visual indexing and a source-preserving final render.

## Not yet production-validated

- `BLACKOUT` is reachable, but the supplied password and all existing Windows SSH keys were rejected. No Ubuntu package installation, PostgreSQL migration, service activation, HTTPS proxy configuration or live backup/restore drill has therefore been performed.
- This machine has FFmpeg, ffprobe, Ollama and an RTX 4060 Laptop GPU, but the selected Qwen package, maintained Piper/Whisper Python packages and their model/voice files, and Tesseract are not installed. The adapters are implemented, but real Qwen, Whisper, OCR and Piper end-to-end runs and the required 5/15/60-minute performance matrix remain pending those local installations.
- PayPal client credentials/webhook ID/plan IDs, SMTP credentials/public domain, Google OAuth client registration, YouTube channel authorization and API compliance verification have not been supplied. Network calls are implemented and isolated behind tested provider boundaries, but no sandbox subscription/payout or live YouTube upload has been executed.
- The WPF shell and accessibility contract exist, but complete signed-in screens, scene review/editor commands, all MY ADFi dashboards, installer/update packaging, and hands-on JAWS 2025/NVDA acceptance runs remain unfinished.
- DISCOVER currently renders the public accessible shell and validated item renderer; repository-backed feeds, comments/reviews/moderation, collaboration invitations and referral qualification endpoints remain unfinished.
- A VLM-guided OCR coordinator, preview player, full 14-phase composition root, YouTube status reconciliation, economic close worker and PayPal payout ledger worker still require implementation and integration testing.

No item in this section should be described as complete until its executable acceptance evidence is committed.
