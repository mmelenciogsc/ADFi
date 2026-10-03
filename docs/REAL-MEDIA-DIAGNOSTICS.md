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
human-approved replacement only when exactly one generated segment is timing-blocked. For a larger
full-film review, `--approved-review-file` accepts a versioned JSON list containing exactly one
explicitly reviewed replacement for every timing-blocked segment. Both paths synthesize and measure
the replacements, append one audited project mutation and refuse stale or changed reviewed state;
neither is an automatic validation bypass. `--accept-generated-redraft` permits the diagnostic
runner to replace its own unreviewed generated baseline when an algorithm-version change correctly
invalidates downstream checkpoints; it does not overwrite accepted user edits.

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

## Next accessibility-focused three-source evaluation set

The dialogue-dense excerpts above were useful conservative-failure tests but were not strong AD
demonstrations. Three complementary external inputs were therefore selected and fully
decode-validated under `D:\ADFi-TestMedia\ADFi-ready-candidates`:

| Input | Rights | Duration and media | Verified AD value |
| --- | --- | --- | --- |
| `01_Big_Buck_Bunny_YouTube_CC-BY-3.0.mp4` | Official Blender YouTube upload explicitly reports `Creative Commons Attribution license (reuse allowed)`; Blender also publishes it under CC BY 3.0 | 10:34.625; H.264 1920x1080 60 FPS plus AAC | Nonverbal visual narrative; nine raw quiet spans of at least 2.5 seconds totaling 39.165 seconds |
| `02_A_Trip_to_the_Moon_Public_Domain.mp4` | Internet Archive metadata applies Public Domain Mark 1.0 to Georges Méliès's 1902 film | 12:41.522; H.264 480x360 25 FPS plus silent AAC | Entire story is visually conveyed and the complete audio bed is silent, providing unrestricted timing coverage |
| `03_Cosmos_Laundromat_Dialogue_CC-BY-SA-4.0.mp4` | Blender Foundation source readme specifies CC BY-SA 4.0; downloaded directly from the publisher-listed media host and matched its published SHA-256 | 12:10.607; H.264 1718x720 24 FPS plus AAC | Real dialogue, music and effects; ten raw quiet spans of at least 2.5 seconds totaling 54.645 seconds, including eight within the narrative |

Big Buck Bunny was the user-authorized one-time YouTube acquisition and was downloaded outside
ADFi with an isolated temporary tool that was removed afterward. ADFi still contains no downloader
or acquisition dependency. The selected H.264 rendition maximizes ordinary Windows compatibility;
the smaller initial AV1 rendition was retained separately as a recoverable, non-selected copy.

The set is intentionally complementary. Cosmos Laundromat tests narration placement around actual
dialogue and meaningful source sound. Big Buck Bunny tests preservation of a nonverbal music/effects
bed. The public-domain silent film tests unrestricted narration timing plus ensemble staging,
period imagery, practical effects, transitions and uncertain low-resolution evidence. Exact URLs,
attribution, license evidence, hashes, share-alike obligations, thematic notes and limitations are
recorded beside the media in `D:\ADFi-TestMedia\ADFi-ready-candidates\README.md`.

## Full three-source Phases 0–12 results

All three complete sources subsequently ran through the production workflow on the Windows 11
development machine. Every source retained its intake SHA-256. Every final MP4 passed a complete
FFmpeg decode, matched the SHA-256 in its finalization manifest, and contains H.264 video plus the
new AAC stereo mix.

| Case | Scenes | AD segments | First cue | Final MP4 | Final SHA-256 |
| --- | ---: | ---: | ---: | --- | --- |
| Dialogue: *Cosmos Laundromat* | 197 | 36 | 00:07.500 | `D:\ADFi-DemoRuns\Projects\18f09f03-ebec-74f0-6a3d-6b68e74de10c\final\v00000013-8c68e589dd40-mixdfd741c9-with-narration\03_Cosmos_Laundromat_Dialogue_CC-BY-SA-4.0-ADFi.mp4` | `ea37f0ce9869a582a65a348d8ddf329799a5d46c3b40d07342a9a979ae57d5f4` |
| Music/effects without dialogue: *Big Buck Bunny* | 183 | 42 | 00:01.923 | `D:\ADFi-DemoRuns\Projects\035a0e3e-6edc-3e96-9725-8bf80043b56e\final\v00000007-2eaed73d6a0b-mixdfd741c9-with-narration\01_Big_Buck_Bunny_YouTube_CC-BY-3.0-ADFi.mp4` | `16f87109494a907a2bc72bc5037aef93d65df3f85d03aad98c0c5bb0aae65ffc` |
| Fully silent: *A Trip to the Moon* | 70 | 57 | 00:02.000 | `D:\ADFi-DemoRuns\Projects\53c9122c-fc3a-30a5-0d70-2e7776b437b4\final\v00000007-12498e791c1e-mixdfd741c9-with-narration\02_A_Trip_to_the_Moon_Public_Domain-ADFi.mp4` | `c294aac55d16e721278cd5ea6aa2a721361af3c99715cab4df40b20c0191b294` |

The run exposed and corrected additional general defects: Phase 4 now persists and fingerprints its
per-scene text-locator result and falls back conservatively after bounded malformed-output retries;
final validation resolves a frame hash within its owning scene rather than assuming the same image
hash cannot recur in another scene; full-film timing review accepts an exact, versioned set of
explicitly reviewed replacements; AD drafting retains malformed private output for diagnosis,
recovers only complete evidence-validated core fields when a constrained model truncates an
auxiliary field, compacts only intact source sentences/clauses, and closes an existing Windows file
handle before replacing a resumed compacted draft. A second correction pass found that FFmpeg's
narration input pad had been consumed by `sidechaincompress` and then reused illegally by `amix`, so
the final mix could contain only the ducked source. The renderer now explicitly `asplit`s narration
into independent sidechain and audible-mix branches and records
`adfi-final-render-v2-audible-narration` in preview/final manifests and content-addressed paths so
an older silent render can never be restored as current. It also corrected digital-silence
normalization, repeated single-character Whisper music hallucinations, sparse opening-title
detection, temporal keyframe displacement, stale downstream checkpoint binding, and the policy that
previously treated every music/effects interval as an absolute narration prohibition. The fallback
now uses bounded, spaced, smoothly ducked dialogue-free windows while still excluding dialogue.

Every timing overrun in these corrected runs received an explicit, evidence-grounded wording review
before finalization. The corrected MP4s pass full FFmpeg decode and manifest-hash verification.
Audio extracted from each final MP4—not merely its standalone Piper assets—was independently checked
for cue-level energy and recognizable speech. For example, Cosmos rises from -48.4 dB mean in the
source opening interval to -15.5 dB in the final, and Whisper recovers both the opening title and a
later description from the final mix. These are diagnostic outputs; normal human editorial viewing
and listening is still required before public release.

Walkthrough-ready paired excerpts, nine native-resolution stills, contact sheets, alt text, hashes
and license/provenance metadata are stored outside Git at `D:\ADFi-DemoAssets`. The canonical index
is `D:\ADFi-DemoAssets\asset-manifest.json`; `D:\ADFi-DemoAssets\README.md` gives the recommended
three-case walkthrough sequence and required attribution.
