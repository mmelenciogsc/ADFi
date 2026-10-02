# Licensed real-media diagnostics — 2026-10-02

This record covers reproducible Phases 0–12 validation on the Windows 11 development machine. It is
diagnostic evidence, not a claim that the required 5-, 15-, and 60-minute acceptance matrix or
hands-on JAWS/NVDA review is complete.

## Sources and scope

Two Creative Commons Attribution 3.0 Blender Foundation films were retained outside the repository
under `D:\ADFi-TestMedia`. Their source URLs, license URLs, retrieval dates, attribution and hashes
are recorded in `D:\ADFi-TestMedia\LICENSE_AND_PROVENANCE.md`.

- `01_Sintel_YouTube_CC-BY-3.0.mp4`: 14:48, 1920x818, H.264/AAC. This was the one expressly approved
  one-time YouTube download for evaluation; ADFi did not perform it and contains no downloader.
- `02_Tears_of_Steel_Direct_CC-BY-3.0.mov`: 12:14, 1280x534, H.264/MP3, obtained directly from the
  Blender Foundation media host.

The bounded runs used the first three minutes of each source. FFmpeg created the excerpts with
stream copy, and both inputs passed a complete decode before ADFi processed them:

| Input | SHA-256 | Bytes |
| --- | --- | ---: |
| `01_Sintel_03m_diagnostic.mp4` | `7ad2fa7110a235386997ee71143b580a858c656eee1fe18aecdbf66d8ca2fbf1` | 90,894,106 |
| `02_Tears_of_Steel_03m_diagnostic.mp4` | `28e59b42648a2c8220ceedf142f12802a666a6d3419ae90a26eb0b6ea9df2a3d` | 67,897,135 |

## Reusable runner

`tools\ADFi.Diagnostics` exercises the production workflow services rather than duplicating the
pipeline. It accepts only a supported existing local file, requires `--rights-confirmed`, derives a
stable project identity from the source hash, resumes verified checkpoints, records time/resource
measurements and produces the same gated Phase 12 bundle as the desktop app.

Run it from native Windows Command Prompt:

```bat
cd /d D:\ADFi
dotnet run --project tools\ADFi.Diagnostics\ADFi.Diagnostics.csproj --configuration Release -- --source "D:\ADFi-TestMedia\diagnostics\01_Sintel_03m_diagnostic.mp4" --label "Sintel-03m" --projects-root "D:\ADFi-DiagnosticRuns\Projects" --rights-confirmed
```

The optional `--approved-review-text` argument is deliberately narrow: it can apply an explicitly
human-approved replacement only when exactly one generated segment is timing-blocked. It is not an
automatic bypass and cannot overwrite reviewed project state.

## Results

The machine had roughly 2.9 GB free system RAM at these runs, so the dynamic policy selected the
constrained `qwen3-vl:2b-instruct` tier despite 8 GB total GPU VRAM. Heavy stages remained
sequential. Whisper unloaded before OpenCV/Qwen, and Qwen unloaded before later phases.

| Measurement | Sintel excerpt | Tears of Steel excerpt |
| --- | ---: | ---: |
| Transcript segments | 23 | 39 |
| Visual scenes | 14 | 13 |
| Recurring-person clusters | 1 | 1 |
| Text-likely scene records | 8 | 10 |
| Analyzed scenes | 14 | 13 |
| Safe AD opportunities / narration segments | 0 / 0 | 0 / 0 |
| Cold Phases 1–2 | 59.109 s | 52.028 s |
| Phases 3–5 | 5 min 33.181 s | 6 min 1.491 s |
| Phase 9 preview | 10.925 s | 12.669 s |
| Phase 12 render | 9.975 s | 11.558 s |

The latest Tears of Steel report shows a 0.118-second Phases 1–2 restore because the cold result was
already checkpointed. Its 52.028-second cold measurement was observed during the preceding run.

Both excerpts are dialogue- and meaningful-source-sound-dense and contain no verified quiet window
of at least two seconds under the current conservative narration policy. Producing zero narration is
therefore the correct safe result: the pipeline did not manufacture a window or mask important
source sound merely to demonstrate output. These clips validate intake, transcription, indexing,
face continuity, OCR/visual reasoning, evidence gating, checkpoint resume, preview, final validation
and source-preserving finalization. A separate excerpt with verified five-to-eight-second narration
gaps is still needed for a polished promotional AD listening example.

Final outputs:

- Sintel:
  `D:\ADFi-DiagnosticRuns\Projects\71fad27a-a210-3835-6997-ee71143b580a\final\v00000002-94dfb3ceb047-with-narration\01_Sintel_03m_diagnostic-ADFi.mp4`
  — SHA-256 `a6034178a4b09418e0ba675f0fe81677ab9a9814aec110a0ad2821230717d445`.
- Tears of Steel:
  `D:\ADFi-DiagnosticRuns\Projects\429be528-8a64-822c-20ce-edf142f12802\final\v00000004-802521a6eabf-with-narration\02_Tears_of_Steel_03m_diagnostic-ADFi.mp4`
  — SHA-256 `2f77080c1748747bbfeb7cfa19d4b6251d2659177a7f480a9e7060702be22041`.

Each output is exactly 180 seconds and contains stream-copied H.264 video plus a newly muxed AAC
audio stream. Complete FFmpeg decode reported no error. Every MP4, UTF-8 AD script and structured
project-export hash matches its `final-render-complete.json`; both manifests report
`FINAL_RENDER_COMPLETE` and `videoStreamCopied: true`.

## Defects exposed and corrected

The runs found issues synthetic fixtures did not reveal. The corrections are general and contain no
film-specific branch:

- Whisper results are UTF-8 safe on Windows and invalid or out-of-chunk timestamps are rejected or
  safely clamped before absolute-time merging.
- Ollama receives an object-valued JSON schema, a bounded context window and bounded error text.
- VLM OCR crops are clipped safely at normalized image boundaries.
- A 15-second scene-duration safety cap prevents a missed cut from becoming one minute-long scene
  represented by only a few frames.
- Scene analysis is schema-bounded, evidence-ID validated and retried once when incomplete. Rejected
  private outputs are retained for diagnosis, while unsupported OCR/entity-only observations are not
  promoted to visual facts.
- Only Tesseract-reconciled, verified on-screen text may enter scene analysis as verified text. This
  prevents transcript dialogue from being mistaken for a visible overlay.
- OCR and scene-analysis checkpoints now bind prompt/model/input fingerprints, enabling safe
  per-scene resume while invalidating stale semantic output.
- Narration opportunity selection defaults to at least two seconds. Draft fitting uses bounded,
  evidence-preserving retries and retains unresolved overrun for human review instead of looping,
  truncating or time-compressing speech.

Earlier pre-fix diagnostic state was preserved—not deleted—under
`D:\ADFi-DiagnosticRuns\Archived` for forensic comparison.
