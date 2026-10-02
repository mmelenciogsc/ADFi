# Windows installation and update

ADFi's primary client target is 64-bit Windows 11. Release packaging is performed from native legacy Command Prompt so the output is tested through the same command environment used for development.

## Reproducible application package

From `D:\ADFi`, run:

```bat
scripts\test.cmd
deploy\windows\publish-desktop.cmd 0.1.0
```

The second command creates a self-contained `artifacts\windows\ADFi-0.1.0-win-x64.zip` and adjacent SHA-256 file. Verify the digest before distribution. A production release must be Authenticode-signed with the publisher's protected code-signing certificate; the repository intentionally contains no certificate or signing secret.

Extract the package to a user-writable versioned directory such as `%LOCALAPPDATA%\Programs\ADFi\0.1.0` and launch `ADFi.Desktop.exe`. User workspaces, model caches and DPAPI-protected credentials belong under `%LOCALAPPDATA%\ADFi`, outside the application directory. Do not run ADFi as administrator.

## Local media and AI dependencies

The Community viewer requires the supported Microsoft Edge WebView2 Evergreen Runtime for official YouTube playback and FFmpeg for bounded local-file moment frames. It does not install or load Ollama, Qwen, Whisper, Piper, CUDA or Vulkan. Windows 11 normally includes WebView2, but the installer must detect it and offer Microsoft's official bootstrapper if absent.

For Creator local ADFication, install Python 3.12 x64 plus current official 64-bit FFmpeg/ffprobe, Ollama and Tesseract packages, add their executable directories to the user `PATH`, and validate their publisher signatures or release checksums. Then run:

```bat
deploy\windows\install-local-ai.cmd
ollama pull qwen3-vl:4b-instruct
```

The first command creates an isolated per-user Python environment and installs the exact Whisper and Piper versions in `requirements-local-ai.txt`; it does not modify system Python. Ollama model data, Whisper weights, Piper voice assets, OpenCV face models and Tesseract language data are never committed to Git. Record their version, license, source URL and SHA-256 in the deployment release manifest. Qwen3-VL is loaded only during bounded visual phases, Whisper normally remains on CPU, and every heavyweight runtime is unloaded before the next phase.

Download the five reviewed Piper voice names from `PiperVoiceCatalog` into
`%LOCALAPPDATA%\ADFi\models\piper` with `python -m piper.download_voices`. The desktop writes
narration text to a short-lived UTF-8 input file for Piper and deletes it immediately after each
segment; narration text is not placed on a process command line.

After Phase 0 source validation, select the local project and activate **Run or resume audio and scene analysis**. ADFi rechecks free resources, verifies the immutable source, resumes valid per-chunk transcript work, unloads Whisper, and only then performs bounded OpenCV scene indexing. Cancellation preserves completed chunks/checkpoints; selecting the action again resumes rather than restarting verified work.

Next, activate **Continue with characters, text, and scene understanding**. The local-AI installer obtains the pinned YuNet and SFace files from the official OpenCV Zoo media endpoint and refuses a hash mismatch. ADFi creates only anonymous project-local person clusters, limits Qwen text analysis to text-likely frames, invokes Tesseract only for justified focused crops, and then performs evidence-cited scene analysis. Each model is unloaded before the next heavyweight stage.

Choose one of the five screenreader-named entries in **Narration voice** and use **Preview selected voice** if desired. Then activate **Create description draft and narration**. ADFi selects time-valid quiet windows, creates and saves each evidence-grounded draft, synthesizes one Piper segment at a time, measures its real duration and performs at most three bounded Qwen shortening attempts when necessary. Valid drafts and WAV files resume without regeneration; a mismatched prompt, voice, hash or corrupt audio invalidates the affected checkpoint. Any remaining overrun is announced and held for review rather than time-compressed.

Activate **Build or resume full draft preview** after narration completes. ADFi validates the source, evidence, timing and audio hashes before creating or restoring the private review MP4. Use the native **Play preview** and **Pause preview** controls. To inspect one cue, select it in **Audio descriptions** and activate **Play around selected description**; ADFi reuses the full draft and creates an exact short clip bounded to the source duration.

For Phase 10, open the narrated project, select a description when the request refers to “this,” and enter one instruction in **Project request**. Enter or Control+Enter submits. Requests such as “What happens here?”, “Shorten this, then play it,” “Use voice 3,” “Validate the project,” and “Undo that” are decomposed into bounded sequential work. The selectable **Conversation history** shows only completed results; task progress is announced without token chatter. An edit is not committed until evidence, spoiler, redundancy, timing and measured-Piper checks pass. **Undo last committed change** appends a compensating version rather than deleting history. A preview requested by the turn is built from the resulting committed version, never from stale Phase 8 text.

## Accessible first run

Start JAWS 2025 or NVDA before ADFi. Keyboard focus begins in the native two-destination navigation. DISCOVER remains usable without login. Creator processing remains disabled until the user signs in, has a server-authoritative Creator entitlement, checks the rights/authorization attestation and selects an existing local video. The first Creator activation runs the accessible hardware profile and miniature benchmark before recommending a model tier.

## Updates and rollback

Close ADFi, verify the new ZIP digest and signature, extract it into a new versioned directory, then launch and validate MY ADFi, DISCOVER and a short local preview before updating any Start-menu shortcut. Keep the immediately prior application directory until validation succeeds. Because projects and protected credentials are stored outside the binary directory, rollback consists of pointing the shortcut back to the prior signed build; never delete or rewrite user project data during an application rollback.
