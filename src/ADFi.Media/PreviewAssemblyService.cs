using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ADFi.Core;

namespace ADFi.Media;

public sealed record PreviewAssemblyRequest(
    Guid ProjectId,
    string ProjectDirectory,
    string SourceVideoPath,
    MediaProbeResult SourceProbe,
    IReadOnlyList<TimedNarrationAsset> Narration,
    MediaInterval? FocusWindow = null,
    string? SourceSha256 = null,
    string? NarrationManifestSha256 = null);

public sealed record ReviewPreviewManifest(
    Guid ProjectId,
    string SourceSha256,
    string NarrationInputSha256,
    string FullDraftRelativePath,
    string FullDraftSha256,
    string? FocusPreviewRelativePath,
    string? FocusPreviewSha256,
    MediaInterval? FocusWindow,
    bool VideoStreamCopied,
    TimeSpan Duration,
    string ReviewState,
    DateTimeOffset UpdatedAtUtc);

public sealed record PreviewAssemblyResult(
    string FullDraftPath,
    string? FocusPreviewPath,
    string ReviewState,
    bool ReusedFullDraft,
    ReviewPreviewManifest Manifest,
    PipelineCheckpoint Checkpoint);

/// <summary>
/// Builds the review artifact by remuxing the untouched source video with a temporary narration
/// mix. A valid content-addressed full draft is reused. Only an explicitly requested short focus
/// clip is re-encoded for frame-accurate seeking, and a changed focus never rerenders the full draft.
/// </summary>
public sealed class PreviewAssemblyService(
    IFinalMediaRenderer renderer,
    string ffmpegPath,
    IPipelineCheckpointStore checkpointStore,
    TimeProvider timeProvider)
{
    private const string ManifestRelativePath = "manifests/review-in-progress.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<PreviewAssemblyResult> AssembleAsync(
        PreviewAssemblyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var projectDirectory = Path.GetFullPath(request.ProjectDirectory);
        var sourcePath = LocalSourcePolicy.ValidateExistingLocalVideo(request.SourceVideoPath);
        var actualSourceSha256 = await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        if (request.SourceSha256 is { Length: > 0 }
            && !string.Equals(request.SourceSha256, actualSourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The immutable source no longer matches its validated SHA-256.");
        }

        var narrationAssetSha256 = await ComputeNarrationFingerprintAsync(request.Narration, cancellationToken)
            .ConfigureAwait(false);
        var narrationInputSha256 = request.NarrationManifestSha256 is { Length: > 0 } manifestSha256
            ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{manifestSha256.ToUpperInvariant()}\n{narrationAssetSha256}")))
            : narrationAssetSha256;
        var restored = await TryRestoreAsync(
            request,
            projectDirectory,
            actualSourceSha256,
            narrationInputSha256,
            cancellationToken).ConfigureAwait(false);
        if (restored is not null)
        {
            return restored;
        }

        var previewDirectory = Path.Combine(projectDirectory, "previews");
        Directory.CreateDirectory(previewDirectory);
        var fullDraftPath = Path.Combine(previewDirectory, "full-draft.mp4");
        var render = await renderer.RenderAsync(
            new(sourcePath, request.SourceProbe, request.Narration, fullDraftPath),
            cancellationToken).ConfigureAwait(false);
        if (Math.Abs((render.Duration - request.SourceProbe.Duration).TotalMilliseconds) > 250)
        {
            throw new InvalidDataException("The draft preview duration does not match the immutable source.");
        }

        var focus = request.FocusWindow is { } focusWindow
            ? await CreateFocusPreviewAsync(
                fullDraftPath, focusWindow, request.SourceProbe.Duration, previewDirectory, cancellationToken)
                .ConfigureAwait(false)
            : null;
        var manifest = new ReviewPreviewManifest(
            request.ProjectId,
            actualSourceSha256,
            narrationInputSha256,
            Relative(projectDirectory, fullDraftPath),
            render.Sha256,
            focus?.RelativePath,
            focus?.Sha256,
            request.FocusWindow,
            render.VideoStreamCopied,
            request.SourceProbe.Duration,
            "REVIEW_IN_PROGRESS",
            timeProvider.GetUtcNow());
        var checkpoint = await PersistAsync(projectDirectory, manifest, cancellationToken).ConfigureAwait(false);
        return new(fullDraftPath, focus?.AbsolutePath, manifest.ReviewState, false, manifest, checkpoint);
    }

    private async Task<PreviewAssemblyResult?> TryRestoreAsync(
        PreviewAssemblyRequest request,
        string projectDirectory,
        string sourceSha256,
        string narrationInputSha256,
        CancellationToken cancellationToken)
    {
        var checkpoint = (await checkpointStore.LoadAsync(request.ProjectId, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item =>
                item.Phase == AdficationPhase.InitialPreviewAssembly
                && item.Status == CheckpointStatus.Complete);
        var manifestPath = Path.Combine(projectDirectory, ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (checkpoint is null
            || checkpoint.ManifestRelativePath != ManifestRelativePath
            || string.IsNullOrWhiteSpace(checkpoint.OutputSha256)
            || !File.Exists(manifestPath)
            || !string.Equals(
                checkpoint.OutputSha256,
                await ComputeSha256Async(manifestPath, cancellationToken).ConfigureAwait(false),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        ReviewPreviewManifest manifest;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer.DeserializeAsync<ReviewPreviewManifest>(
                           stream,
                           JsonOptions,
                           cancellationToken).ConfigureAwait(false)
                       ?? throw new InvalidDataException("The review manifest is empty.");
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException)
        {
            return null;
        }

        if (manifest.ProjectId != request.ProjectId
            || !string.Equals(manifest.SourceSha256, sourceSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.NarrationInputSha256, narrationInputSha256, StringComparison.OrdinalIgnoreCase)
            || manifest.Duration != request.SourceProbe.Duration
            || manifest.ReviewState != "REVIEW_IN_PROGRESS")
        {
            return null;
        }

        string fullDraftPath;
        try
        {
            fullDraftPath = ResolveProjectArtifact(projectDirectory, manifest.FullDraftRelativePath);
        }
        catch (InvalidDataException)
        {
            return null;
        }

        if (!File.Exists(fullDraftPath)
            || !string.Equals(
                manifest.FullDraftSha256,
                await ComputeSha256Async(fullDraftPath, cancellationToken).ConfigureAwait(false),
                StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (request.FocusWindow is not { } requestedFocus)
        {
            return new(
                fullDraftPath,
                null,
                manifest.ReviewState,
                true,
                manifest,
                checkpoint);
        }

        if (manifest.FocusWindow == requestedFocus
            && manifest.FocusPreviewRelativePath is { Length: > 0 } focusRelative
            && manifest.FocusPreviewSha256 is { Length: > 0 } focusSha256)
        {
            string focusPath;
            try
            {
                focusPath = ResolveProjectArtifact(projectDirectory, focusRelative);
            }
            catch (InvalidDataException)
            {
                focusPath = string.Empty;
            }
            if (File.Exists(focusPath)
                && string.Equals(
                    focusSha256,
                    await ComputeSha256Async(focusPath, cancellationToken).ConfigureAwait(false),
                    StringComparison.OrdinalIgnoreCase))
            {
                return new(fullDraftPath, focusPath, manifest.ReviewState, true, manifest, checkpoint);
            }
        }

        var focus = await CreateFocusPreviewAsync(
            fullDraftPath,
            requestedFocus,
            manifest.Duration,
            Path.Combine(projectDirectory, "previews"),
            cancellationToken).ConfigureAwait(false);
        var updated = manifest with
        {
            FocusPreviewRelativePath = focus.RelativePath,
            FocusPreviewSha256 = focus.Sha256,
            FocusWindow = requestedFocus,
            UpdatedAtUtc = timeProvider.GetUtcNow(),
        };
        var updatedCheckpoint = await PersistAsync(projectDirectory, updated, cancellationToken).ConfigureAwait(false);
        return new(fullDraftPath, focus.AbsolutePath, updated.ReviewState, true, updated, updatedCheckpoint);
    }

    private async Task<FocusArtifact> CreateFocusPreviewAsync(
        string fullDraftPath,
        MediaInterval window,
        TimeSpan mediaDuration,
        string previewDirectory,
        CancellationToken cancellationToken)
    {
        window.ValidateWithin(mediaDuration);
        Directory.CreateDirectory(previewDirectory);
        var focusPath = Path.Combine(previewDirectory, "focus-preview.mp4");
        var partial = focusPath + ".partial.mp4";
        try
        {
            var result = await ExternalProcess.RunAsync(
                ffmpegPath,
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-ss", Format(window.Start), "-i", fullDraftPath,
                    "-t", Format(window.Duration),
                    "-map", "0:v:0", "-map", "0:a:0",
                    "-c:v", "libx264", "-preset", "veryfast", "-crf", "18",
                    "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", partial,
                ],
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || !File.Exists(partial))
            {
                throw new InvalidOperationException($"PREVIEW_CLIP_FAILED: {Sanitize(result.StandardError)}");
            }

            File.Move(partial, focusPath, true);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }

        return new(
            focusPath,
            Path.Combine("previews", "focus-preview.mp4").Replace('\\', '/'),
            await ComputeSha256Async(focusPath, cancellationToken).ConfigureAwait(false));
    }

    private async Task<PipelineCheckpoint> PersistAsync(
        string projectDirectory,
        ReviewPreviewManifest manifest,
        CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(projectDirectory, ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        await WriteManifestAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        var checkpoint = new PipelineCheckpoint(
            manifest.ProjectId,
            AdficationPhase.InitialPreviewAssembly,
            CheckpointStatus.Complete,
            manifest.UpdatedAtUtc,
            ManifestRelativePath,
            await ComputeSha256Async(manifestPath, cancellationToken).ConfigureAwait(false),
            null);
        await checkpointStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        return checkpoint;
    }

    private static async Task<string> ComputeNarrationFingerprintAsync(
        IReadOnlyList<TimedNarrationAsset> narration,
        CancellationToken cancellationToken)
    {
        var entries = new List<object>(narration.Count);
        foreach (var item in narration.OrderBy(value => value.Window.Start))
        {
            if (!File.Exists(item.AbsoluteAudioPath))
            {
                throw new FileNotFoundException("A narration segment is missing.", item.AbsoluteAudioPath);
            }

            entries.Add(new
            {
                item.SegmentId,
                item.Window,
                item.MeasuredDuration,
                AudioSha256 = await ComputeSha256Async(item.AbsoluteAudioPath, cancellationToken).ConfigureAwait(false),
            });
        }

        var canonical = JsonSerializer.Serialize(entries);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string ResolveProjectArtifact(string projectDirectory, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("A preview manifest contains an invalid project-relative path.");
        }

        var root = Path.GetFullPath(projectDirectory);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A preview manifest path escapes the project directory.");
        }

        return path;
    }

    private static async Task WriteManifestAsync(
        string path,
        ReviewPreviewManifest value,
        CancellationToken cancellationToken)
    {
        var partial = path + ".partial";
        try
        {
            await using (var stream = new FileStream(
                             partial,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(partial, path, true);
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }

    private static string Relative(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');

    private static string Format(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Sanitize(string value)
    {
        var compact = string.Join(' ', value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        return compact[..Math.Min(compact.Length, 500)];
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
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

    private sealed record FocusArtifact(string AbsolutePath, string RelativePath, string Sha256);
}
