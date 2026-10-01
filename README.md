# ADFi

ADFi is a screenreader-first Windows 11 application and companion Ubuntu service for turning an authorized local source video into a reviewed audio-described (ADFied) MP4. The canonical source tree is this monorepo; Ubuntu receives reproducible published artifacts and migrations from it rather than a separate codebase.

## Product boundary

The Windows application has exactly two top-level destinations:

- **MY ADFi** contains private account data, playlists, projects, drafts, works, integrations, contribution/referral/earnings history, and Creator tools.
- **DISCOVER** is publicly browsable and contains eligible published ADFied works using the official YouTube player plus ADFi metadata, accessibility details, attribution, playlists, qualified community feedback, sharing, and subscription calls to action.

There are exactly two paid plans. Community costs USD $2/month and does not permit ADFication creation or conversational project editing. Creator costs USD $9/month and adds local ADFication, local VLM VQA, conversational editing, rendering, and publication to the subscriber's own YouTube channel. Entitlements are enforced server-side; disabled desktop controls are not a security boundary.

Source intake accepts supported local video files only. ADFi does not acquire source media from YouTube. YouTube is used solely for authorized OAuth-based publication to the Creator's own channel and official-player playback after publication.

## Repository layout

| Path | Responsibility |
| --- | --- |
| `src/ADFi.Desktop` | Native .NET 10 WPF client with JAWS/NVDA/keyboard semantics |
| `src/ADFi.Core` | Domain contracts, project state, rights attestation, pipeline phases |
| `src/ADFi.LocalAI` | Hardware policy and replaceable Ollama/direct-runtime abstraction |
| `src/ADFi.Media` | Local media validation, ffprobe and deterministic media work |
| `src/ADFi.Conversation` | Schema-validated plans and sequential atomic task execution |
| `src/ADFi.YouTube` | Publication-only OAuth/channel/upload contracts |
| `src/ADFi.Economics` | Auditable qualification, ledger and 70/30 revenue policy |
| `src/ADFi.Infrastructure` | File/database/provider implementations |
| `src/ADFi.Api` | Hardened ASP.NET Core API boundary for Ubuntu |
| `src/ADFi.Web` | Public DISCOVER web surface components |
| `tests` | Unit, architecture and later integration/accessibility suites |
| `prompts` | Versioned local-model system prompts and semantic-tool contracts |
| `deploy` | Windows packaging and Ubuntu deployment assets |

## Build and test on Windows 11

Install .NET SDK 10.0.401 or a compatible patch, then run from legacy Command Prompt:

```bat
cd /d D:\ADFi
scripts\test.cmd
```

To launch the desktop shell:

```bat
dotnet run --project src\ADFi.Desktop\ADFi.Desktop.csproj
```

FFmpeg/ffprobe and Ollama must be on `PATH` for local pipeline operations. The balanced model ID is `qwen3-vl:4b`; the model is not downloaded during build or committed to Git. Maintained Piper, licensed voice packages, Whisper and Tesseract are separate local dependencies described in [dependencies](docs/DEPENDENCIES.md). Reproducible packaging, local-runtime installation, accessible first run and rollback are documented in [Windows installation](docs/WINDOWS-INSTALL.md).

## Security and deployment

Never commit credentials. Windows OAuth tokens use current-user DPAPI; Ubuntu secrets belong in protected server-side environment/credential files. Deployment scripts use Windows OpenSSH and prompt or use keys. See [architecture](docs/ARCHITECTURE.md), [pipeline](docs/PIPELINE.md), [dependencies](docs/DEPENDENCIES.md), [accessibility](docs/ACCESSIBILITY.md), [security](docs/SECURITY.md), [economics](docs/ECONOMICS.md), and [Ubuntu deployment](docs/UBUNTU-DEPLOYMENT.md).

## Current implementation status

The repository is being delivered as tested vertical slices. See [implementation status](docs/IMPLEMENTATION-STATUS.md) for evidence-backed completed and pending scope; no scaffold or mock is represented as production complete.
