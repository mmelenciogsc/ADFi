# Local ADFication pipeline

The immutable source and all private evidence remain under the user's Windows project directory. Every phase consumes persisted outputs from earlier phases, writes new outputs atomically, validates them, records their hashes/checkpoint, disposes heavyweight resources, and confirms current free resources before the next heavyweight phase.

1. Source intake records the exact rights attestation, user/project/time/source SHA-256 and ffprobe stream data; unsupported paths, remote URIs and insufficient working disk fail closed.
2. Audio preparation creates analysis-only mono 16 kHz audio, level/silence measurements and bounded 30-second chunks with one-second overlap. Whisper processes chunks sequentially; results are persisted immediately and merged by absolute time. Dialogue overrides apparent silence, while active non-dialogue sound remains protected.
3. Visual indexing uses inexpensive OpenCV metrics before inference. It detects scene boundaries, selects a capped temporal sequence, suppresses near-duplicates, saves timestamped frame evidence and relates each scene to transcript and narration-window IDs.
4. Face continuity uses local detection/embeddings only to cluster a recurring person within the project. It never identifies a person from appearance. Names have explicit evidence and an establishment time.
5. OCR uses VLM context first, then focused Tesseract crops when exact characters matter or evidence disagrees. Competing readings remain explicit.
6. Qwen visual analysis processes one bounded scene package at a time, cites known frame evidence and is unloaded after the stage.
7. AD necessity protects dialogue and meaningful sound, prioritizes significant information absent from audio and creates no interval merely because a cut occurred.
8. Drafting uses only verified facts and time-valid names. A 180-WPM plan leaves breathing room; exact Piper output is then measured. At most three semantics-preserving rewrites are attempted, after which review is mandatory.
9. Preview and review operate on committed project versions. Only `ApplyProjectMutation` persists a proposed edit; undo/restore append compensating versions.
10. Final validation blocks missing evidence/audio, overlaps, overruns and media mismatches. Rendering mixes narration smoothly and stream-copies a compatible video stream whenever possible.
11. Publication is optional and upload-only. The user confirms the OAuth-authenticated channel and visibility. DISCOVER activation is a later independent choice and uses the official YouTube player.

The `prompts/semantic-tools.json` contract is the authoritative allow-list for model-planned operations. It contains no arbitrary command tool.
