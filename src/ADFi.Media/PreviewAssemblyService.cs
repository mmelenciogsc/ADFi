using System.Globalization;
using System.Text.Json;
using ADFi.Core;

namespace ADFi.Media;

public sealed record PreviewAssemblyRequest(
    Guid ProjectId,
    string ProjectDirectory,
    string SourceVideoPath,
    MediaProbeResult SourceProbe,
    IReadOnlyList<TimedNarrationAsset> Narration,
    MediaInterval? FocusWindow = null);

public sealed record PreviewAssemblyResult(
    string FullDraftPath,
    string? FocusPreviewPath,
    string ReviewState,
    PipelineCheckpoint Checkpoint);

/// <summary>
/// Builds the review artifact by remuxing the untouched source video with a temporary narration
/// mix. Only an explicitly requested short focus clip is re-encoded for frame-accurate seeking.
/// </summary>
public sealed class PreviewAssemblyService(
    FinalRenderService renderer,
    string ffmpegPath,
    IPipelineCheckpointStore checkpointStore,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<PreviewAssemblyResult> AssembleAsync(
        PreviewAssemblyRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var projectDirectory = Path.GetFullPath(request.ProjectDirectory);
        var previewDirectory = Path.Combine(projectDirectory, "previews");
        Directory.CreateDirectory(previewDirectory);
        var fullDraftPath = Path.Combine(previewDirectory, "full-draft.mp4");
        var render = await renderer.RenderAsync(
            new(request.SourceVideoPath, request.SourceProbe, request.Narration, fullDraftPath),
            cancellationToken).ConfigureAwait(false);

        string? focusPath = null;
        if (request.FocusWindow is { } window)
        {
            window.ValidateWithin(render.Duration);
            focusPath = Path.Combine(previewDirectory, "focus-preview.mp4");
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
                if (result.ExitCode != 0)
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
        }

        var manifestPath = Path.Combine(projectDirectory, "manifests", "review-in-progress.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        var checkpoint = new PipelineCheckpoint(
            request.ProjectId,
            AdficationPhase.InitialPreviewAssembly,
            CheckpointStatus.Complete,
            timeProvider.GetUtcNow(),
            Path.GetRelativePath(projectDirectory, manifestPath).Replace('\\', '/'),
            render.Sha256,
            null);
        await WriteManifestAsync(
            manifestPath,
            new
            {
                request.ProjectId,
                ReviewState = "REVIEW_IN_PROGRESS",
                FullDraftPath = Path.GetRelativePath(projectDirectory, fullDraftPath).Replace('\\', '/'),
                FocusPreviewPath = focusPath is null
                    ? null
                    : Path.GetRelativePath(projectDirectory, focusPath).Replace('\\', '/'),
                render.VideoStreamCopied,
                render.Duration,
                render.Sha256,
                checkpoint.UpdatedAtUtc,
            },
            cancellationToken).ConfigureAwait(false);
        await checkpointStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        return new(fullDraftPath, focusPath, "REVIEW_IN_PROGRESS", checkpoint);
    }

    private static async Task WriteManifestAsync(
        string path,
        object value,
        CancellationToken cancellationToken)
    {
        var partial = path + ".partial";
        await using (var stream = File.Create(partial))
        {
            await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(partial, path, true);
    }

    private static string Format(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Sanitize(string value)
    {
        var compact = string.Join(' ', value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        return compact[..Math.Min(compact.Length, 500)];
    }
}
