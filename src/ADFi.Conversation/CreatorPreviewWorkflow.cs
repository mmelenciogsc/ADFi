using System.Security.Cryptography;
using System.Text.Json;
using ADFi.Core;
using ADFi.Media;

namespace ADFi.Conversation;

public sealed record CreatorPreviewProgress(string Announcement);

/// <summary>
/// Converts the verified Phase 8 narration manifest into Phase 9 preview inputs.
/// It refuses unresolved timing, missing evidence, escaped paths, source changes, and
/// narration hash mismatches before FFmpeg is allowed to assemble a review artifact.
/// </summary>
public sealed class CreatorPreviewWorkflow(
    PreviewAssemblyService previewAssemblyService)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public async Task<PreviewAssemblyResult> RunAsync(
        Guid projectId,
        string projectDirectory,
        MediaInterval? focusWindow,
        IProgress<CreatorPreviewProgress>? progress,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(projectDirectory);
        var source = await LoadAsync<SourceIntakeResult>(
            Path.Combine(root, "manifests", "source-validated.json"), cancellationToken).ConfigureAwait(false);
        var visual = await LoadAsync<VisualIndexManifest>(
            Path.Combine(root, "manifests", "visual-index.json"), cancellationToken).ConfigureAwait(false);
        var draftPath = Path.Combine(root, "manifests", "ad-draft-complete.json");
        var draft = await LoadAsync<AdDraftCompletionManifest>(draftPath, cancellationToken).ConfigureAwait(false);
        var narrationPath = Path.Combine(root, "manifests", "narration-complete.json");
        var narration = await LoadAsync<NarrationCompletionManifest>(narrationPath, cancellationToken).ConfigureAwait(false);
        if (source.Checkpoint.ProjectId != projectId
            || visual.ProjectId != projectId
            || draft.ProjectId != projectId
            || narration.ProjectId != projectId
            || !string.Equals(source.Source.SourceSha256, visual.SourceSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(source.Source.SourceSha256, draft.SourceSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(source.Source.SourceSha256, narration.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Preview inputs do not share one immutable project source.");
        }

        if (!string.Equals(
                narration.DraftManifestSha256,
                await HashAsync(draftPath, cancellationToken).ConfigureAwait(false),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The narration manifest does not match the verified AD draft.");
        }

        progress?.Report(new("Validating description evidence, narration timing, and audio assets."));
        var timed = new List<TimedNarrationAsset>(narration.Segments.Count);
        var narrationAssets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in narration.Segments)
        {
            if (item.RequiresUserReview || !item.Fit.Fits)
            {
                throw new InvalidOperationException(
                    $"Description {item.Segment.SegmentId} requires timing review before preview assembly.");
            }

            if (item.VoiceId != narration.VoiceId
                || item.Segment.MeasuredNarrationDuration is not { } measured
                || item.Fit.Available <= TimeSpan.Zero
                || item.Fit.Available > item.Segment.Window.Duration
                || item.SynthesisAttempts != item.Fit.AttemptNumber
                || item.RequiresUserReview != item.Fit.RequiresUserReview
                || NarrationTimingPolicy.Evaluate(
                    measured,
                    item.Segment.Window.Duration,
                    item.Segment.Window.Duration - item.Fit.Available,
                    item.Fit.AttemptNumber) != item.Fit)
            {
                throw new InvalidDataException($"Narration timing provenance is invalid for {item.Segment.SegmentId}.");
            }

            var relative = item.Segment.NarrationAssetRelativePath
                           ?? throw new InvalidDataException("A description has no narration asset path.");
            var absolute = ResolveProjectArtifact(root, relative);
            if (!File.Exists(absolute)
                || !string.Equals(
                    item.AudioSha256,
                    await HashAsync(absolute, cancellationToken).ConfigureAwait(false),
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Narration asset validation failed for {item.Segment.SegmentId}.");
            }

            narrationAssets.Add(relative);
            timed.Add(new(item.Segment.SegmentId, absolute, item.Segment.Window, measured));
        }

        var evidence = visual.Scenes
            .SelectMany(scene => scene.Keyframes)
            .Select(frame => frame.EvidenceId)
            .ToHashSet(StringComparer.Ordinal);
        var issues = ProjectValidator.Validate(
            source.Source.Probe.Duration,
            narration.Segments.Select(item => item.Segment).ToArray(),
            evidence,
            narrationAssets);
        var blocking = issues.Where(issue => issue.Blocking).ToArray();
        if (blocking.Length > 0)
        {
            throw new InvalidOperationException(
                $"Preview validation found {blocking.Length} blocking issue(s). First: {blocking[0].Message}");
        }

        progress?.Report(new(focusWindow is null
            ? "Building or restoring the full audio-described draft preview."
            : "Building or restoring the selected short focus preview."));
        return await previewAssemblyService.AssembleAsync(
            new(
                projectId,
                root,
                source.Source.SourcePath,
                source.Source.Probe,
                timed,
                focusWindow,
                source.Source.SourceSha256,
                await HashAsync(narrationPath, cancellationToken).ConfigureAwait(false)),
            cancellationToken).ConfigureAwait(false);
    }

    private static string ResolveProjectArtifact(string root, string relativePath)
    {
        if (Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("Narration paths must remain project-relative.");
        }

        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A narration path escapes the private project directory.");
        }

        return path;
    }

    private static async Task<T> LoadAsync<T>(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("A required verified project manifest is missing.", path);
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException($"Project manifest is empty: {Path.GetFileName(path)}");
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
