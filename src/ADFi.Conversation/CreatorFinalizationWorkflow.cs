using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ADFi.Core;
using ADFi.Media;

namespace ADFi.Conversation;

public sealed record CreatorFinalizationRequest(
    Guid ProjectId,
    string ProjectDirectory,
    bool IncludeNarrationAssets = true,
    bool ForceVideoReencode = false);

public sealed record CreatorFinalizationProgress(string Announcement, int CompletedSteps, int TotalSteps);

public sealed record FinalNarrationExport(
    string SegmentId,
    string RelativePath,
    string Sha256,
    TimeSpan MeasuredDuration);

public sealed record FinalizedAdScriptEntry(
    string SegmentId,
    string SceneId,
    MediaInterval Window,
    string Text,
    IReadOnlyList<string> EvidenceIds);

public sealed record FinalizedProjectExport(
    string SchemaVersion,
    ProjectSnapshot Project,
    string FinalVideoRelativePath,
    string FinalVideoSha256,
    string FinalValidationBindingSha256,
    IReadOnlyList<FinalizedAdScriptEntry> AudioDescriptionScript,
    IReadOnlyList<FinalNarrationExport> NarrationAssets,
    DateTimeOffset CreatedAtUtc);

public sealed record FinalRenderCompletionManifest(
    Guid ProjectId,
    int ProjectVersion,
    string SourceSha256,
    string ProjectStateSha256,
    string FinalValidationRelativePath,
    string FinalValidationManifestSha256,
    string FinalValidationBindingSha256,
    string FinalVideoRelativePath,
    string FinalVideoSha256,
    bool VideoStreamCopied,
    TimeSpan Duration,
    string AdScriptRelativePath,
    string AdScriptSha256,
    string ProjectExportRelativePath,
    string ProjectExportSha256,
    bool NarrationAssetsIncluded,
    IReadOnlyList<FinalNarrationExport> NarrationAssets,
    string State,
    DateTimeOffset CompletedAtUtc);

public sealed record CreatorFinalizationResult(
    string FinalVideoPath,
    string AdScriptPath,
    string ProjectExportPath,
    FinalRenderCompletionManifest Manifest,
    PipelineCheckpoint Checkpoint,
    bool ReusedVerifiedBundle);

/// <summary>
/// Produces a content-addressed Phase 12 bundle only for the exact committed project state that
/// passed Phase 11. The immutable source is checked before and after rendering. A completed bundle
/// is reusable only after every exported artifact and the media duration are verified again.
/// </summary>
public sealed class CreatorFinalizationWorkflow(
    IProjectVersionStore projectStore,
    IPipelineCheckpointStore checkpointStore,
    IMediaProbe mediaProbe,
    IFinalMediaRenderer renderer,
    TimeProvider timeProvider)
{
    private const string ValidationRelativePath = "manifests/final-project-validation.json";
    private const int StepCount = 5;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<CreatorFinalizationResult> RunAsync(
        CreatorFinalizationRequest request,
        IProgress<CreatorFinalizationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var root = Path.GetFullPath(request.ProjectDirectory);
        var staging = string.Empty;
        try
        {
            progress?.Report(new("Verifying the final-validation gate and immutable source.", 0, StepCount));
            var gate = await LoadAndVerifyGateAsync(request.ProjectId, root, cancellationToken).ConfigureAwait(false);
            var bundleRelative = BundleRelativePath(
                gate.Snapshot.Version,
                gate.ValidationBindingSha256,
                request.IncludeNarrationAssets);
            var bundle = ResolveProjectArtifact(root, bundleRelative);
            var completionRelative = CombineRelative(bundleRelative, "final-render-complete.json");
            var completionPath = ResolveProjectArtifact(root, completionRelative);

            var restored = await TryRestoreAsync(
                    request,
                    gate,
                    completionRelative,
                    completionPath,
                    cancellationToken)
                .ConfigureAwait(false);
            if (restored is not null)
            {
                progress?.Report(new("Verified final ADFied MP4 bundle restored.", StepCount, StepCount));
                return restored;
            }

            if (Directory.Exists(bundle))
            {
                throw new InvalidDataException("FINAL_BUNDLE_CONFLICT: The expected bundle exists but failed integrity verification.");
            }

            staging = Path.Combine(root, "final", $".phase12-{Guid.NewGuid():N}.partial");
            Directory.CreateDirectory(staging);
            var safeSourceName = SanitizeFileName(Path.GetFileNameWithoutExtension(gate.Source.Source.SourcePath));
            var finalVideoName = $"{safeSourceName}-ADFi.mp4";
            var stagedVideo = Path.Combine(staging, finalVideoName);
            var narration = await BuildNarrationInputsAsync(
                    root,
                    gate.Snapshot,
                    gate.Validation,
                    cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new("Rendering final ADFied MP4.", 1, StepCount));
            var render = await renderer.RenderAsync(
                    new(
                        gate.Source.Source.SourcePath,
                        gate.CurrentProbe,
                        narration,
                        stagedVideo,
                        request.ForceVideoReencode),
                    cancellationToken)
                .ConfigureAwait(false);
            if (Math.Abs((render.Duration - gate.Snapshot.MediaDuration).TotalMilliseconds) > 250)
            {
                throw new InvalidDataException("FINAL_RENDER_DURATION_MISMATCH");
            }

            var finalVideoRelative = CombineRelative(bundleRelative, finalVideoName);
            var script = gate.Snapshot.AdSegments
                .OrderBy(item => item.Window.Start)
                .Select(item => new FinalizedAdScriptEntry(
                    item.SegmentId,
                    item.SceneId,
                    item.Window,
                    item.Text,
                    item.EvidenceIds))
                .ToArray();
            progress?.Report(new("Writing the separate audio-description script.", 2, StepCount));
            var scriptName = $"{safeSourceName}-AD-script.txt";
            var stagedScript = Path.Combine(staging, scriptName);
            await File.WriteAllTextAsync(
                    stagedScript,
                    FormatAdScript(script, gate.Snapshot.NarrationVoiceId),
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            var scriptHash = await HashFileAsync(stagedScript, cancellationToken).ConfigureAwait(false);

            progress?.Report(new(
                request.IncludeNarrationAssets
                    ? "Copying the selected narration assets into the final bundle."
                    : "Narration asset export is not selected.",
                3,
                StepCount));
            var exportedNarration = request.IncludeNarrationAssets
                ? await ExportNarrationAsync(root, staging, bundleRelative, gate.Validation, cancellationToken)
                    .ConfigureAwait(false)
                : [];

            progress?.Report(new("Writing the structured project export and final integrity manifest.", 4, StepCount));
            var exportName = $"{safeSourceName}-ADFi-project.json";
            var stagedExport = Path.Combine(staging, exportName);
            var completedAt = timeProvider.GetUtcNow();
            var projectExport = new FinalizedProjectExport(
                "adfi.finalized-project.v1",
                gate.Snapshot,
                finalVideoRelative,
                render.Sha256,
                gate.ValidationBindingSha256,
                script,
                exportedNarration,
                completedAt);
            await WriteJsonAsync(stagedExport, projectExport, cancellationToken).ConfigureAwait(false);
            var exportHash = await HashFileAsync(stagedExport, cancellationToken).ConfigureAwait(false);
            var completion = new FinalRenderCompletionManifest(
                request.ProjectId,
                gate.Snapshot.Version,
                gate.Snapshot.SourceSha256,
                gate.Validation.ProjectStateSha256,
                ValidationRelativePath,
                gate.ValidationManifestSha256,
                gate.ValidationBindingSha256,
                finalVideoRelative,
                render.Sha256,
                render.VideoStreamCopied,
                render.Duration,
                CombineRelative(bundleRelative, scriptName),
                scriptHash,
                CombineRelative(bundleRelative, exportName),
                exportHash,
                request.IncludeNarrationAssets,
                exportedNarration,
                "FINAL_RENDER_COMPLETE",
                completedAt);
            await WriteJsonAsync(Path.Combine(staging, "final-render-complete.json"), completion, cancellationToken)
                .ConfigureAwait(false);

            var sourceHashAfterRender = await HashFileAsync(gate.Source.Source.SourcePath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(sourceHashAfterRender, gate.Snapshot.SourceSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("SOURCE_CHANGED_DURING_FINAL_RENDER");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(bundle)!);
            Directory.Move(staging, bundle);
            staging = string.Empty;
            var completionHash = await HashFileAsync(completionPath, cancellationToken).ConfigureAwait(false);
            var checkpoint = new PipelineCheckpoint(
                request.ProjectId,
                AdficationPhase.FinalMp4Rendering,
                CheckpointStatus.Complete,
                completedAt,
                completionRelative,
                completionHash,
                null);
            await checkpointStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
            progress?.Report(new("Final ADFied MP4 complete.", StepCount, StepCount));
            return new(
                ResolveProjectArtifact(root, completion.FinalVideoRelativePath),
                ResolveProjectArtifact(root, completion.AdScriptRelativePath),
                ResolveProjectArtifact(root, completion.ProjectExportRelativePath),
                completion,
                checkpoint,
                false);
        }
        catch (Exception error)
        {
            var failed = new PipelineCheckpoint(
                request.ProjectId,
                AdficationPhase.FinalMp4Rendering,
                CheckpointStatus.Failed,
                timeProvider.GetUtcNow(),
                string.Empty,
                null,
                error is OperationCanceledException ? "CANCELLED" : "FINAL_RENDER_FAILED");
            await checkpointStore.SaveAsync(failed, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (staging.Length > 0 && Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private async Task<GateState> LoadAndVerifyGateAsync(
        Guid projectId,
        string root,
        CancellationToken cancellationToken)
    {
        var snapshot = await projectStore.LoadCurrentAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (snapshot.ProjectId != projectId)
        {
            throw new InvalidDataException("PROJECT_ID_MISMATCH");
        }

        var validationPath = ResolveProjectArtifact(root, ValidationRelativePath);
        var validation = await ReadJsonAsync<FinalProjectValidationManifest>(validationPath, cancellationToken)
            .ConfigureAwait(false);
        var validationHash = await HashFileAsync(validationPath, cancellationToken).ConfigureAwait(false);
        var validationCheckpoint = (await checkpointStore.LoadAsync(projectId, cancellationToken).ConfigureAwait(false))
            .SingleOrDefault(item => item.Phase == AdficationPhase.FinalProjectValidation);
        if (validationCheckpoint is null
            || validationCheckpoint.Status != CheckpointStatus.Complete
            || validationCheckpoint.ManifestRelativePath != ValidationRelativePath
            || !string.Equals(validationCheckpoint.OutputSha256, validationHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("FINAL_VALIDATION_CHECKPOINT_INVALID");
        }

        var stateHash = FinalProjectValidationBinding.ComputeProjectStateSha256(snapshot);
        if (validation.ProjectId != projectId
            || validation.ProjectVersion != snapshot.Version
            || validation.State != "READY_FOR_FINAL_RENDER"
            || validation.Issues.Any(item => !item.Overridden)
            || !string.Equals(validation.SourceSha256, snapshot.SourceSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(validation.ProjectStateSha256, stateHash, StringComparison.OrdinalIgnoreCase)
            || validation.MediaDuration != snapshot.MediaDuration)
        {
            throw new InvalidOperationException("FINAL_VALIDATION_NOT_CURRENT");
        }

        var source = await ReadJsonAsync<SourceIntakeResult>(
                ResolveProjectArtifact(root, "manifests/source-validated.json"),
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(source.Source.SourceSha256, snapshot.SourceSha256, StringComparison.OrdinalIgnoreCase)
            || source.Attestation.ProjectId != projectId
            || !source.Attestation.Accepted)
        {
            throw new InvalidDataException("SOURCE_AUTHORIZATION_MISMATCH");
        }

        var sourcePath = LocalSourcePolicy.ValidateExistingLocalVideo(source.Source.SourcePath);
        var actualSourceHash = await HashFileAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualSourceHash, snapshot.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("SOURCE_CHANGED_AFTER_FINAL_VALIDATION");
        }

        var currentProbe = await mediaProbe.ProbeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        if (Math.Abs((currentProbe.Duration - snapshot.MediaDuration).TotalMilliseconds) > 250)
        {
            throw new InvalidDataException("SOURCE_DURATION_CHANGED_AFTER_FINAL_VALIDATION");
        }

        return new(
            root,
            snapshot,
            validation,
            validationHash,
            FinalProjectValidationBinding.ComputeManifestBindingSha256(validation),
            source,
            currentProbe);
    }

    private async Task<CreatorFinalizationResult?> TryRestoreAsync(
        CreatorFinalizationRequest request,
        GateState gate,
        string completionRelative,
        string completionPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(completionPath))
        {
            return null;
        }

        FinalRenderCompletionManifest manifest;
        try
        {
            manifest = await ReadJsonAsync<FinalRenderCompletionManifest>(completionPath, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or JsonException or InvalidDataException)
        {
            return null;
        }

        if (manifest.ProjectId != request.ProjectId
            || manifest.ProjectVersion != gate.Snapshot.Version
            || manifest.State != "FINAL_RENDER_COMPLETE"
            || manifest.NarrationAssetsIncluded != request.IncludeNarrationAssets
            || manifest.Duration != gate.Snapshot.MediaDuration
            || !string.Equals(manifest.SourceSha256, gate.Snapshot.SourceSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.ProjectStateSha256, gate.Validation.ProjectStateSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(manifest.FinalValidationBindingSha256, gate.ValidationBindingSha256, StringComparison.OrdinalIgnoreCase)
            || manifest.FinalValidationRelativePath != ValidationRelativePath
            || (!manifest.NarrationAssetsIncluded && manifest.NarrationAssets.Count != 0)
            || (manifest.NarrationAssetsIncluded
                && manifest.NarrationAssets.Count != gate.Validation.NarrationAssets.Count))
        {
            return null;
        }

        if (!await ArtifactMatchesAsync(gate, manifest.FinalVideoRelativePath, manifest.FinalVideoSha256, cancellationToken)
                .ConfigureAwait(false)
            || !await ArtifactMatchesAsync(gate, manifest.AdScriptRelativePath, manifest.AdScriptSha256, cancellationToken)
                .ConfigureAwait(false)
            || !await ArtifactMatchesAsync(gate, manifest.ProjectExportRelativePath, manifest.ProjectExportSha256, cancellationToken)
                .ConfigureAwait(false))
        {
            return null;
        }

        foreach (var asset in manifest.NarrationAssets)
        {
            if (!await ArtifactMatchesAsync(gate, asset.RelativePath, asset.Sha256, cancellationToken)
                    .ConfigureAwait(false))
            {
                return null;
            }
        }

        var finalVideo = ResolveProjectArtifact(gate.ProjectRoot, manifest.FinalVideoRelativePath);
        var finalProbe = await mediaProbe.ProbeAsync(finalVideo, cancellationToken).ConfigureAwait(false);
        if (Math.Abs((finalProbe.Duration - gate.Snapshot.MediaDuration).TotalMilliseconds) > 250)
        {
            return null;
        }

        var completionHash = await HashFileAsync(completionPath, cancellationToken).ConfigureAwait(false);
        var checkpoint = new PipelineCheckpoint(
            request.ProjectId,
            AdficationPhase.FinalMp4Rendering,
            CheckpointStatus.Complete,
            timeProvider.GetUtcNow(),
            completionRelative,
            completionHash,
            null);
        await checkpointStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        return new(
            finalVideo,
            ResolveProjectArtifact(gate.ProjectRoot, manifest.AdScriptRelativePath),
            ResolveProjectArtifact(gate.ProjectRoot, manifest.ProjectExportRelativePath),
            manifest,
            checkpoint,
            true);
    }

    private static async Task<IReadOnlyList<TimedNarrationAsset>> BuildNarrationInputsAsync(
        string root,
        ProjectSnapshot snapshot,
        FinalProjectValidationManifest validation,
        CancellationToken cancellationToken)
    {
        var audits = validation.NarrationAssets.ToDictionary(item => item.SegmentId, StringComparer.Ordinal);
        var result = new List<TimedNarrationAsset>(snapshot.AdSegments.Count);
        foreach (var segment in snapshot.AdSegments.OrderBy(item => item.Window.Start))
        {
            if (segment.NarrationAssetRelativePath is not { Length: > 0 } relative
                || segment.MeasuredNarrationDuration is null
                || !audits.TryGetValue(segment.SegmentId, out var audit))
            {
                throw new InvalidDataException($"NARRATION_NOT_VALIDATED: {segment.SegmentId}");
            }

            var path = ResolveProjectArtifact(root, relative);
            var hash = await HashFileAsync(path, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, audit.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"NARRATION_CHANGED_AFTER_FINAL_VALIDATION: {segment.SegmentId}");
            }

            result.Add(new(segment.SegmentId, path, segment.Window, audit.MeasuredDuration));
        }

        return result;
    }

    private static async Task<IReadOnlyList<FinalNarrationExport>> ExportNarrationAsync(
        string root,
        string staging,
        string bundleRelative,
        FinalProjectValidationManifest validation,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.Combine(staging, "narration");
        Directory.CreateDirectory(outputDirectory);
        var exports = new List<FinalNarrationExport>(validation.NarrationAssets.Count);
        var index = 0;
        foreach (var asset in validation.NarrationAssets.OrderBy(item => item.SegmentId, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            var source = ResolveProjectArtifact(root, asset.RelativePath);
            var extension = Path.GetExtension(source);
            var fileName = $"{index:000}-{SanitizeFileName(asset.SegmentId)}{extension}";
            var destination = Path.Combine(outputDirectory, fileName);
            await using (var input = File.OpenRead(source))
            await using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, true))
            {
                await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            var hash = await HashFileAsync(destination, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(hash, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"NARRATION_EXPORT_HASH_MISMATCH: {asset.SegmentId}");
            }

            exports.Add(new(
                asset.SegmentId,
                CombineRelative(bundleRelative, "narration", fileName),
                hash,
                asset.MeasuredDuration));
        }

        return exports;
    }

    private static string FormatAdScript(
        IReadOnlyList<FinalizedAdScriptEntry> entries,
        string voiceId)
    {
        var text = new StringBuilder();
        text.AppendLine("ADFi audio-description script");
        text.AppendLine(CultureInfo.InvariantCulture, $"Narration voice: {voiceId}");
        text.AppendLine();
        foreach (var entry in entries)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"[{FormatTimestamp(entry.Window.Start)} --> {FormatTimestamp(entry.Window.End)}] {entry.SegmentId} / {entry.SceneId}");
            text.AppendLine(entry.Text.Trim());
            text.AppendLine();
        }

        return text.ToString();
    }

    private static string FormatTimestamp(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";

    private static string BundleRelativePath(int version, string stateHash, bool includeNarration) =>
        CombineRelative(
            "final",
            $"v{version:00000000}-{stateHash[..12].ToLowerInvariant()}-{(includeNarration ? "with-narration" : "without-narration")}");

    private static string CombineRelative(params string[] parts) =>
        string.Join('/', parts.Select(part => part.Trim('/', '\\')).Where(part => part.Length > 0));

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var safe = new string(value.Select(character => invalid.Contains(character) ? '-' : character).ToArray()).Trim();
        if (string.IsNullOrWhiteSpace(safe))
        {
            return "ADFi-project";
        }

        return safe.Length <= 80 ? safe : safe[..80].TrimEnd();
    }

    private static string ResolveProjectArtifact(string projectRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            throw new InvalidDataException("A finalization artifact path must be project-relative.");
        }

        var root = Path.GetFullPath(projectRoot);
        var path = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("A finalization artifact path escapes the project directory.");
        }

        return path;
    }

    private static async Task<bool> ArtifactMatchesAsync(
        GateState gate,
        string relativePath,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        try
        {
            var path = ResolveProjectArtifact(gate.ProjectRoot, relativePath);
            return File.Exists(path)
                   && string.Equals(
                       expectedHash,
                       await HashFileAsync(path, cancellationToken).ConfigureAwait(false),
                       StringComparison.OrdinalIgnoreCase);
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static async Task<T> ReadJsonAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException($"The required {typeof(T).Name} manifest is empty.");
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
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

    private sealed record GateState(
        string ProjectRoot,
        ProjectSnapshot Snapshot,
        FinalProjectValidationManifest Validation,
        string ValidationManifestSha256,
        string ValidationBindingSha256,
        SourceIntakeResult Source,
        MediaProbeResult CurrentProbe);
}
