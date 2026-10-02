using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using ADFi.Core;
using ADFi.LocalAI;
using ADFi.Media;

namespace ADFi.Conversation;

public sealed record NormalizedImageRegion(decimal X, decimal Y, decimal Width, decimal Height);

public sealed record VisualTextCandidate(
    string CandidateId,
    string FrameEvidenceId,
    string Text,
    decimal Confidence,
    bool Meaningful,
    bool ExactCharactersRequired,
    NormalizedImageRegion? Region);

public sealed record VisualTextSceneResult(
    string SceneId,
    IReadOnlyList<VisualTextCandidate> VlmCandidates,
    IReadOnlyList<ReconciledOcr> ReconciledReadings,
    PromptDecisionProvenance Provenance,
    string? InputFingerprint = null,
    string? Uncertainty = null);

public sealed record VisualTextAnalysisManifest(
    Guid ProjectId,
    string SourceSha256,
    IReadOnlyList<VisualTextSceneResult> Scenes,
    DateTimeOffset CompletedAtUtc,
    string? PromptSha256 = null,
    string? ModelName = null);

/// <summary>
/// Uses the VLM only on deterministically text-likely frames, unloads it, and then invokes
/// Tesseract solely for bounded regions where literal verification is justified.
/// </summary>
public sealed class VisualTextAnalysisCoordinator(
    ILocalModelRuntime runtime,
    string modelName,
    string promptPath,
    TesseractOcrService tesseract,
    TimeProvider timeProvider,
    IProgress<(int Completed, int Total, string Announcement)>? progress = null)
{
    private const string OutputSchema =
        """
        {"type":"object","required":["sceneId","candidates"],"properties":{"sceneId":{"type":"string"},"candidates":{"type":"array","items":{"type":"object","required":["candidateId","frameEvidenceId","text","confidence","meaningful","exactCharactersRequired","region"],"properties":{"candidateId":{"type":"string"},"frameEvidenceId":{"type":"string"},"text":{"type":"string"},"confidence":{"type":"number","minimum":0,"maximum":1},"meaningful":{"type":"boolean"},"exactCharactersRequired":{"type":"boolean"},"region":{"type":["object","null"],"properties":{"x":{"type":"number","minimum":0,"maximum":1},"y":{"type":"number","minimum":0,"maximum":1},"width":{"type":"number","exclusiveMinimum":0,"maximum":1},"height":{"type":"number","exclusiveMinimum":0,"maximum":1}},"required":["x","y","width","height"],"additionalProperties":false}},"additionalProperties":false}}},"additionalProperties":false}
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<VisualTextAnalysisManifest> AnalyzeAsync(
        Guid projectId,
        string sourceSha256,
        VisualIndexManifest visualIndex,
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        if (visualIndex.ProjectId != projectId
            || !string.Equals(visualIndex.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The visual-text input does not match the selected project.");
        }

        var prompt = await PromptResourceLoader.LoadAsync(promptPath, cancellationToken).ConfigureAwait(false);
        var candidateScenes = visualIndex.Scenes
            .Where(scene => scene.Keyframes.Any(frame => frame.PotentialText))
            .ToArray();
        var drafts = new List<(
            VisualScene Scene,
            VisualTextDraft Draft,
            PromptDecisionProvenance Provenance,
            string InputFingerprint,
            string? Uncertainty)>();
        if (candidateScenes.Length > 0)
        {
            var modelLoaded = false;
            try
            {
                for (var index = 0; index < candidateScenes.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var scene = candidateScenes[index];
                    var frames = scene.Keyframes.Where(frame => frame.PotentialText).ToArray();
                    var fingerprint = ComputeInputFingerprint(scene);
                    var existing = await TryLoadLocatorAsync(
                        projectDirectory,
                        scene,
                        prompt,
                        fingerprint,
                        cancellationToken).ConfigureAwait(false);
                    if (existing is not null)
                    {
                        drafts.Add((
                            scene,
                            new(scene.SceneId, existing.VlmCandidates),
                            existing.Provenance,
                            fingerprint,
                            existing.Uncertainty));
                        progress?.Report((
                            index + 1,
                            candidateScenes.Length,
                            $"Restored verified visual-text location {index + 1} of {candidateScenes.Length}."));
                        continue;
                    }

                    if (!modelLoaded)
                    {
                        await runtime.EnsureModelAvailableAsync(modelName, cancellationToken).ConfigureAwait(false);
                        modelLoaded = true;
                    }

                    var (draft, response, uncertainty) = await GenerateDraftAsync(
                        projectId,
                        scene,
                        frames,
                        prompt,
                        projectDirectory,
                        cancellationToken).ConfigureAwait(false);
                    var provenance = new PromptDecisionProvenance(
                        prompt.Id,
                        prompt.Version.ToString(),
                        prompt.Sha256,
                        runtime.RuntimeName,
                        response.Model,
                        timeProvider.GetUtcNow());
                    await PersistLocatorAsync(
                        projectDirectory,
                        new(scene.SceneId, draft.Candidates, provenance, fingerprint, uncertainty),
                        cancellationToken).ConfigureAwait(false);
                    drafts.Add((scene, draft, provenance, fingerprint, uncertainty));
                    progress?.Report((
                        index + 1,
                        candidateScenes.Length,
                        $"Locating meaningful on-screen text, scene {index + 1} of {candidateScenes.Length}."));
                }
            }
            finally
            {
                if (modelLoaded)
                {
                    await runtime.UnloadAsync(modelName, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }

        var results = new List<VisualTextSceneResult>(drafts.Count);
        for (var sceneIndex = 0; sceneIndex < drafts.Count; sceneIndex++)
        {
            var item = drafts[sceneIndex];
            var readings = new List<ReconciledOcr>();
            foreach (var candidate in item.Draft.Candidates.Where(value => value.Meaningful))
            {
                var vlm = new OcrCandidate(
                    candidate.CandidateId + "-vlm",
                    OcrEvidenceSource.VlmContext,
                    candidate.Text,
                    candidate.Confidence,
                    candidate.ExactCharactersRequired);
                if ((candidate.ExactCharactersRequired || candidate.Confidence < 0.90m)
                    && candidate.Region is not null)
                {
                    var frame = item.Scene.Keyframes.Single(value => value.EvidenceId == candidate.FrameEvidenceId);
                    var crop = CropRegion(frame, candidate.Region);
                    var cropRelative = Path.Combine(
                        "visual",
                        "ocr",
                        "crops",
                        candidate.CandidateId + ".png").Replace(Path.DirectorySeparatorChar, '/');
                    OcrCropService.ExtractNormalizedCrop(
                        AbsoluteFrame(projectDirectory, frame),
                        crop,
                        Path.Combine(projectDirectory, cropRelative.Replace('/', Path.DirectorySeparatorChar)));
                    var literal = await tesseract.ReadExactAsync(
                        candidate.CandidateId + "-tesseract",
                        Path.Combine(projectDirectory, cropRelative.Replace('/', Path.DirectorySeparatorChar)),
                        ["eng"],
                        cancellationToken).ConfigureAwait(false);
                    readings.Add(OcrReconciler.Reconcile([vlm, literal]));
                }
                else
                {
                    readings.Add(OcrReconciler.Reconcile([vlm]));
                }
            }

            results.Add(new(
                item.Scene.SceneId,
                item.Draft.Candidates,
                readings,
                item.Provenance,
                item.InputFingerprint,
                item.Uncertainty));
            progress?.Report((
                sceneIndex + 1,
                drafts.Count,
                $"Verifying on-screen text, scene {sceneIndex + 1} of {drafts.Count}."));
        }

        var manifest = new VisualTextAnalysisManifest(
            projectId,
            sourceSha256,
            results,
            timeProvider.GetUtcNow(),
            prompt.Sha256,
            modelName);
        await WriteAsync(
            Path.Combine(projectDirectory, "manifests", "visual-text-analysis.json"),
            manifest,
            cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    private async Task<(VisualTextDraft Draft, LocalModelResponse Response, string? Uncertainty)> GenerateDraftAsync(
        Guid projectId,
        VisualScene scene,
        IReadOnlyList<KeyframeEvidence> frames,
        PromptResource prompt,
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        Exception? validationFailure = null;
        LocalModelResponse? lastResponse = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await runtime.GenerateAsync(
                modelName,
                new(
                    prompt.Content,
                    BuildInput(projectId, scene, frames, isRetry: attempt > 0),
                    frames.Select(frame => AbsoluteFrame(projectDirectory, frame)).ToArray(),
                    OutputSchema,
                    attempt == 0 ? 900 : 700),
                cancellationToken).ConfigureAwait(false);
            lastResponse = response;
            try
            {
                var draft = JsonSerializer.Deserialize<VisualTextDraft>(response.Content, JsonOptions)
                            ?? throw new InvalidDataException("The visual-text model returned no result.");
                draft = NormalizeCropRegions(draft);
                ValidateDraft(scene, draft);
                return (draft, response, null);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
            {
                validationFailure = exception;
                await PersistRejectedOutputAsync(
                    projectDirectory,
                    scene.SceneId,
                    attempt + 1,
                    response,
                    exception,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // A potential-text signal is not itself proof that readable text exists. If both
        // bounded attempts remain malformed or unsupported, preserve the rejected evidence
        // for audit and promote no text. Downstream scene analysis still sees PotentialText
        // on the frame and can request exact verification; fabricated OCR never enters it.
        return (
            new(scene.SceneId, []),
            lastResponse ?? throw new InvalidOperationException("The visual-text runtime returned no response."),
            $"No text was promoted after bounded validation failed: {validationFailure?.Message}");
    }

    private static string BuildInput(
        Guid projectId,
        VisualScene scene,
        IReadOnlyList<KeyframeEvidence> frames,
        bool isRetry) =>
        JsonSerializer.Serialize(new
        {
            projectId,
            scene.SceneId,
            frames = frames.Select(frame => new
            {
                frame.EvidenceId,
                timestampMilliseconds = (long)frame.SourceTime.TotalMilliseconds,
                frame.Width,
                frame.Height,
            }),
            instruction = isRetry
                ? "The prior structured response was invalid. Return one short, complete JSON object only, with no more than three candidates and no candidate text over 120 characters. Use the exact scene ID and exact frame evidence IDs listed here. Candidate IDs must be nonempty and unique within this scene. For dense credits or name lists, report one concise category or heading instead of transcribing names. Omit uncertain candidates rather than inventing text or evidence."
                : "Locate only potentially meaningful visible text. Return no more than six candidates and keep every candidate text under 300 characters. For dense credits or name lists, report a concise category or meaningful heading rather than enumerating names. Give a conservative contextual reading and a normalized crop rectangle. Use only the exact scene and frame evidence IDs supplied. Candidate IDs must be nonempty and unique within this scene. Do not invent obscured characters.",
        });

    private async Task<VisualTextLocatorArtifact?> TryLoadLocatorAsync(
        string projectDirectory,
        VisualScene scene,
        PromptResource prompt,
        string fingerprint,
        CancellationToken cancellationToken)
    {
        var path = LocatorPath(projectDirectory, scene.SceneId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var existing = await JsonSerializer.DeserializeAsync<VisualTextLocatorArtifact>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (existing is null
                || existing.Provenance.PromptId != prompt.Id
                || existing.Provenance.PromptVersion != prompt.Version.ToString()
                || existing.Provenance.PromptSha256 != prompt.Sha256
                || existing.Provenance.ModelName != modelName
                || existing.InputFingerprint != fingerprint)
            {
                return null;
            }

            ValidateDraft(scene, new(existing.SceneId, existing.VlmCandidates));
            return existing;
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    private async Task PersistRejectedOutputAsync(
        string projectDirectory,
        string sceneId,
        int attempt,
        LocalModelResponse response,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(
            Path.GetFullPath(projectDirectory),
            "visual",
            "ocr",
            "rejected",
            $"{sceneId}-attempt-{attempt:D2}.json");
        await WriteAsync(path, new
        {
            sceneId,
            attempt,
            response.Model,
            response.Content,
            parseError = exception.Message,
            createdAtUtc = timeProvider.GetUtcNow(),
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Task PersistLocatorAsync(
        string projectDirectory,
        VisualTextLocatorArtifact artifact,
        CancellationToken cancellationToken) =>
        WriteAsync(LocatorPath(projectDirectory, artifact.SceneId), artifact, cancellationToken);

    private static string LocatorPath(string projectDirectory, string sceneId) =>
        Path.Combine(Path.GetFullPath(projectDirectory), "visual", "ocr", "locator", $"{sceneId}.json");

    internal static string ComputeInputFingerprint(VisualScene scene)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            scene.SceneId,
            scene.SourceInterval,
            Frames = scene.Keyframes.Where(frame => frame.PotentialText).Select(frame => new
            {
                frame.EvidenceId,
                frame.SourceTime,
                frame.RelativeImagePath,
                frame.Width,
                frame.Height,
                frame.PotentialText,
            }),
            OutputSchema,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void ValidateDraft(VisualScene scene, VisualTextDraft draft)
    {
        if (!string.Equals(scene.SceneId, draft.SceneId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("Visual-text analysis references the wrong scene.");
        }

        var evidence = scene.Keyframes.Select(frame => frame.EvidenceId).ToHashSet(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in draft.Candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.CandidateId))
            {
                throw new InvalidDataException("A visual-text candidate has no identifier.");
            }

            if (!ids.Add(candidate.CandidateId))
            {
                throw new InvalidDataException("Visual-text candidate identifiers must be unique within a scene.");
            }

            if (string.IsNullOrWhiteSpace(candidate.FrameEvidenceId)
                || !evidence.Contains(candidate.FrameEvidenceId))
            {
                throw new InvalidDataException("A visual-text candidate cites unavailable frame evidence.");
            }

            if (candidate.Confidence is < 0 or > 1)
            {
                throw new InvalidDataException("Visual-text confidence must be between zero and one.");
            }

            if (candidate.Meaningful && string.IsNullOrWhiteSpace(candidate.Text))
            {
                throw new InvalidDataException("A meaningful visual-text candidate has no supported reading.");
            }

            if (candidate.Region is { } region
                && (region.X < 0 || region.Y < 0 || region.Width <= 0 || region.Height <= 0
                    || region.X + region.Width > 1 || region.Y + region.Height > 1))
            {
                throw new InvalidDataException("Visual-text analysis contains an invalid crop region.");
            }
        }
    }

    private static VisualTextDraft NormalizeCropRegions(VisualTextDraft draft)
    {
        var candidates = draft.Candidates.Select(candidate =>
        {
            if (candidate.Region is not { } region
                || region.X < 0
                || region.Y < 0
                || region.X >= 1
                || region.Y >= 1
                || region.Width <= 0
                || region.Height <= 0)
            {
                return candidate;
            }

            // Small models can return individually valid normalized coordinates whose sums
            // cross the image edge through rounding (for example x=.82, width=.20). Clipping
            // the rectangle to the actual frame preserves its evidence while remaining bounded.
            var normalized = region with
            {
                Width = Math.Min(region.Width, 1 - region.X),
                Height = Math.Min(region.Height, 1 - region.Y),
            };
            return candidate with { Region = normalized };
        }).ToArray();
        return draft with { Candidates = candidates };
    }

    private static ImageRegion CropRegion(KeyframeEvidence frame, NormalizedImageRegion region)
    {
        var x = Math.Clamp((int)Math.Floor(region.X * frame.Width), 0, frame.Width - 1);
        var y = Math.Clamp((int)Math.Floor(region.Y * frame.Height), 0, frame.Height - 1);
        var width = Math.Clamp((int)Math.Ceiling(region.Width * frame.Width), 1, frame.Width - x);
        var height = Math.Clamp((int)Math.Ceiling(region.Height * frame.Height), 1, frame.Height - y);
        return new(x, y, width, height);
    }

    private static string AbsoluteFrame(string projectDirectory, KeyframeEvidence frame)
    {
        var path = Path.Combine(
            Path.GetFullPath(projectDirectory),
            frame.RelativeImagePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException("A visual-text evidence frame is missing.", path);
    }

    private static async Task WriteAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".partial";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private sealed record VisualTextDraft(string SceneId, IReadOnlyList<VisualTextCandidate> Candidates);

    private sealed record VisualTextLocatorArtifact(
        string SceneId,
        IReadOnlyList<VisualTextCandidate> VlmCandidates,
        PromptDecisionProvenance Provenance,
        string InputFingerprint,
        string? Uncertainty = null);
}
