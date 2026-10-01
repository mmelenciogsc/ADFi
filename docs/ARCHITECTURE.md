# Architecture

## Deployment boundary

`D:\ADFi` is the canonical Git working tree. The Windows 11 desktop owns private source media, extracted frames, transcripts, narration assets and renders by default. Ubuntu runs the API, public DISCOVER web surface, PostgreSQL and server-authoritative subscription/economic state. Deployment publishes immutable output from this repository over Windows OpenSSH; server edits are prohibited.

## Dependencies

Dependencies point inward toward `ADFi.Core`. `ADFi.Media`, `ADFi.LocalAI`, `ADFi.YouTube` and `ADFi.Economics` implement bounded capabilities. `ADFi.Conversation` can coordinate those capabilities through typed contracts but cannot execute arbitrary shell commands. `ADFi.Infrastructure` supplies persistence/provider implementations. Desktop and API are composition roots. `ADFi.Web` supplies the public surface.

## Local ADFication invariants

1. Intake accepts an existing supported local video and a recorded rights attestation.
2. The original source is immutable and addressed by SHA-256.
3. Phases execute sequentially; a heavyweight runtime is unloaded and resource reclamation checked before the next heavyweight phase.
4. Every durable phase writes validated output before a complete checkpoint.
5. Restart resumes after the last verified checkpoint.
6. Visual inference consumes bounded timestamped scene sequences selected by deterministic preprocessing, never every frame.
7. Face similarity is project-internal continuity, never real-world identity inference.
8. Only `ApplyProjectMutation` commits model-proposed edits.
9. Final rendering refuses blocking validation errors and never overwrites the source.
10. YouTube integration publishes authorized final output only; it is not a source-intake mechanism.

## Prompt provenance

Every model decision records prompt ID, semantic version and content SHA-256 with its output. Prompt text and semantic-tool contracts are versioned under `prompts/`. Model output is untrusted data and must pass deterministic schema, evidence, time-window and mutation validation.

