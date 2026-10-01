# Accessible Interactive Video Viewer and moment VQA

Internal product summary: **Pause. Ask. Know. Resume.** This viewer is a read-only accessibility workflow available to verified Community and Creator subscribers. It is not ADFication and does not make a video into a persistent editable project.

## Deterministic interaction

1. Open a local video, an ADFi DISCOVER publication, or a pasted YouTube URL.
2. Play normally. `Space` retains its normal play/pause role. `Alt+Q` and the real **Ask about this moment** button invoke the same action.
3. ADFi pauses first, confirms the paused player state, records `PlaybackCheckpoint`, announces “Paused at … Ask about this moment,” and focuses the plainly named edit field.
4. The immediately following Tab target is **DICTATE**. It opens Windows voice typing in that same field; dictated text remains editable.
5. `Enter` or `Ctrl+Enter` submits. ADFi announces “Checking this moment,” obtains the minimum evidence for the classified question, and waits for a completed provider result.
6. ADFi emits one “Answer ready” UI Automation event. It puts the complete answer in ordinary selectable text and never streams token deltas into a live region.
7. Playback remains paused. **Resume from HH:MM:SS** seeks to the exact checkpoint, returns focus to player controls and resumes only when selected. `Escape`/Back closes Q&A without asking or resuming.

The WPF implementation uses native controls, deterministic Tab order and no canvas-only state. Android should reuse the contracts in `ADFi.Core`, mapping Tab to TalkBack Swipe Right and the real buttons/actions to Android semantic controls.

## Source and evidence rules

| Source | Playback | Evidence priority | New visual evidence |
| --- | --- | --- | --- |
| Local file | Local WPF media player | Existing active transaction, then bounded FFmpeg frames | Allowed after explicit Ask; private, downscaled, ephemeral |
| ADFi DISCOVER publication | Official YouTube IFrame | Timestamped ADFi scene/OCR/entity/AD/dialogue evidence | Not needed when stored evidence is sufficient; no arbitrary capture fallback is assumed |
| Arbitrary YouTube URL | Official YouTube IFrame | Matching ADFi publication data, then official metadata or user context | Implemented as one bounded player-region capture, but production-disabled until the required policy/legal clearance |

`YoutubeVideoUrl` only accepts supported HTTPS YouTube hosts and ordinary 11-character video identifiers. ADFi does not download, proxy, reconstruct, transcode, cache, or expose media URLs. It has no `yt-dlp`/`youtube-dl` dependency. The embedded player retains normal YouTube controls and branding and uses documented `pauseVideo()`, `playVideo()`, `seekTo()`, `getCurrentTime()`, state and playback-rate methods.

For DISCOVER and pasted URLs, the resolver first asks the server whether the YouTube ID and timestamp map to public ADFi scene evidence. The public endpoint returns only the current bounded scene. It never returns a frame archive.

## Minimum-evidence classifier

The deterministic classifier runs before any model call:

- “What does the sign say?” requests one best current frame and exact-text handling.
- “What color is the car?” and “Describe the room” request one current representative frame.
- “What’s happening?” and “What changed?” request no more than three frames over the configured short span, normally before/current/after.
- “Who is she?” can use only entities already established at that source time; it cannot perform external face identification.
- “Who is speaking?” prefers legitimate dialogue and established-entity metadata.
- “What did I miss?” is capped to the configured recent interval and can never trigger an unbounded scan.

Production defaults are at most three images, 1.5 seconds total temporal span and 1,280 pixels on the largest side. The local Creator tier may lower these limits according to its hardware profile. Validators reject over-cap, persistent, path-leaking or non-user-initiated evidence before provider submission.

## Contracts and read-only boundary

`IInteractiveVideoSession`, `IPlaybackController`, `IVideoContextResolver`, `IVideoEvidenceProvider`, `IVqaProvider` and `IEphemeralEvidenceLifecycle` are UI-neutral. `MomentVqaRequest` records session/request IDs, source class and opaque reference, timestamp, classified intent, bounded evidence, ADFi scene/entity/OCR/dialogue context, provider, retention and compliance state. `MomentVqaResult` records answer, evidence basis, confidence, uncertainties, follow-up, provider and UTC creation time.

Viewer mode forbids `RewriteAdSegment`, `ApplyProjectMutation`, `SynthesizeNarration`, `RenderFinalVideo` and `PublishToYouTube`. A Creator must deliberately enter Creator editing before the existing Planner and one-atomic-task mutation workflow applies. Neither asking nor watching creates an economic event. A DISCOVER answer can seed an editable feedback draft; only explicit submission creates a `PENDING` feedback entry subject to the existing qualification process.

## Private evidence lifecycle

Each session and request gets an unguessable directory below the current Windows user’s local application-data directory. Local paths and ephemeral paths are marked non-serializable and are excluded from provider prompt context. Evidence is deleted on request failure, resume, Q&A close, seek, source replacement, window close and best-effort process exit. Startup scavenging removes abandoned directories. Follow-up questions may reuse evidence only while the same paused transaction remains active. There is no screenshot browser, export action, cross-session reuse, background capture, scheduled capture, scene harvesting or model iteration interface.

Provider tokens use DPAPI/Windows Credential Manager boundaries. The cloud request includes only the question, timestamp, applicable verified context and required image bytes. It excludes the local filename/path and unrelated account/device metadata. Advanced privacy UI must show this request class before production cloud enablement. Telemetry is coarse (`viewer_session_started`, `ask_invoked`, category, success, resume, provider class and latency bucket); question text, images, filenames and YouTube engagement are prohibited and require separate explicit consent before any richer analytics.

## ChatGPT plan usage status

ADFi’s transport follows the official flow: system-browser OAuth with loopback callback, state/nonce/PKCE, separately granted `chatgpt.tokens.use.direct`, account-specific model discovery, public `POST /v1/responses`, `store: false`, `stream: true`, and success only after `response.completed`. Identity alone never enables inference, and the integration never grants access to prior ChatGPT conversations. The UI distinguishes Not Connected, Connected without plan usage, Plan Usage Available, Limit Reached, Reauthorization Required, Provider Approval Required and Unavailable.

OpenAI’s current documentation says ChatGPT plan usage is available for open-source/local projects and selected private clients, while a paid or remotely hosted app must request access before offering it. Therefore `ChatGptPlanUsageIntegrationEnabled` defaults to `false` until ADFi receives approval. ADFi does not silently switch to an ADFi-paid API key. Official references reviewed 2026-10-02:

- [Sign in with ChatGPT cookbook](https://developers.openai.com/cookbook/articles/sign-in-with-chatgpt)
- [Registration and sign-in](https://developers.openai.com/siwc/token-sharing-open-source/sign-in)
- [Models and inference](https://developers.openai.com/siwc/token-sharing-open-source/models-and-inference)
- [Errors and recovery](https://developers.openai.com/siwc/token-sharing-open-source/errors-and-recovery)
- [UI/UX guidelines](https://developers.openai.com/siwc/ui-ux-guidelines)

Community never loads Ollama, Qwen, Whisper, Piper, CUDA or Vulkan for viewer VQA. Creator may select Local First, which uses the same bounded schema, loads the selected Qwen3-VL model only for that request and unloads it in `finally`.

## YouTube policy and legal separation

Fair-use/accessibility rationale and YouTube contractual/API compliance are independent evaluations. The design minimizes evidence because a blind user explicitly asks about one paused moment, evidence is private and accessibility-only, and it is immediately deleted. That rationale is not a legal conclusion and does not override platform terms.

YouTube’s Developer Policies prohibit downloading, importing, backing up, caching or storing copies of audiovisual content without prior written approval and impose transparency/deletion requirements. `YoutubeEphemeralAccessibilityCaptureEnabled` therefore defaults to `false` in both server configuration and PostgreSQL. The fully bounded player-region provider exists for audit/development, but the production flag may be enabled only after documented YouTube compliance/audit confirmation and legal review of this exact workflow. The server can disable it without a client release. When disabled, official playback, pause/time controls, Ask/Dictate/Resume, user context and matching ADFi metadata continue to work; unsupported visual questions receive a specific evidence-unavailable response.

Official references reviewed 2026-10-02:

- [YouTube IFrame Player API](https://developers.google.com/youtube/iframe_api_reference)
- [YouTube API Services Developer Policies](https://developers.google.com/youtube/terms/developer-policies)
- [YouTube policy guide](https://developers.google.com/youtube/terms/developer-policies-guide)

## Production launch gates

- Obtain and record OpenAI approval for paid-app ChatGPT plan usage before enabling its flag.
- Complete YouTube API compliance/audit and legal review for moment-triggered ephemeral player-region capture before enabling its separate flag.
- Run hands-on JAWS 2025, NVDA and WebView2 shortcut/focus tests; confirm `Alt+Q` has no conflict in the supported matrix.
- Verify player controls, DICTATE one-Tab adjacency, one live event per state, no automatic answer speech, explicit resume and deterministic Escape behavior.
- Test ChatGPT no-scope, limit, invalid-user, unavailable, network and cloud-refusal branches with real approved credentials.
- Test enabled YouTube capture in an audit build, including player-region confinement, one-capture enforcement and cleanup after success/error/cancel/crash.
- Complete privacy review and provider-request preview UI acceptance.

Automated tests enforce the current code-level boundaries. They supplement rather than replace the external approvals and hands-on screenreader/platform acceptance listed above.
