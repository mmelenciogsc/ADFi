using System.Text.Json;
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
    PromptDecisionProvenance Provenance);

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
        var drafts = new List<(VisualScene Scene, VisualTextDraft Draft, PromptDecisionProvenance Provenance)>();
        if (candidateScenes.Length > 0)
        {
            await runtime.EnsureModelAvailableAsync(modelName, cancellationToken).ConfigureAwait(false);
            try
            {
                for (var index = 0; index < candidateScenes.Length; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var scene = candidateScenes[index];
                    var frames = scene.Keyframes.Where(frame => frame.PotentialText).ToArray();
                    var response = await runtime.GenerateAsync(
                        modelName,
                        new(
                            prompt.Content,
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
                                instruction = "Locate only potentially meaningful visible text. Give a conservative contextual reading and a normalized crop rectangle. Do not invent obscured characters.",
                            }),
                            frames.Select(frame => AbsoluteFrame(projectDirectory, frame)).ToArray(),
                            OutputSchema,
                            900),
                        cancellationToken).ConfigureAwait(false);
                    var draft = JsonSerializer.Deserialize<VisualTextDraft>(response.Content, JsonOptions)
                                ?? throw new InvalidDataException("The visual-text model returned no result.");
                    draft = NormalizeCropRegions(draft);
                    ValidateDraft(scene, draft);
                    drafts.Add((scene, draft, new(
                        prompt.Id,
                        prompt.Version.ToString(),
                        prompt.Sha256,
                        runtime.RuntimeName,
                        response.Model,
                        timeProvider.GetUtcNow())));
                    progress?.Report((
                        index + 1,
                        candidateScenes.Length,
                        $"Locating meaningful on-screen text, scene {index + 1} of {candidateScenes.Length}."));
                }
            }
            finally
            {
                await runtime.UnloadAsync(modelName, CancellationToken.None).ConfigureAwait(false);
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

            results.Add(new(item.Scene.SceneId, item.Draft.Candidates, readings, item.Provenance));
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
            if (string.IsNullOrWhiteSpace(candidate.CandidateId)
                || !ids.Add(candidate.CandidateId)
                || !evidence.Contains(candidate.FrameEvidenceId)
                || candidate.Confidence is < 0 or > 1
                || candidate.Meaningful && string.IsNullOrWhiteSpace(candidate.Text))
            {
                throw new InvalidDataException("Visual-text analysis contains an unsupported candidate.");
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

    private static async Task WriteAsync(
        string path,
        VisualTextAnalysisManifest value,
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
}
