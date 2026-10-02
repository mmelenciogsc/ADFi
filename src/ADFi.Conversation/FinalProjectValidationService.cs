using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using ADFi.Core;
using ADFi.Media;

namespace ADFi.Conversation;

[JsonConverter(typeof(JsonStringEnumConverter<FinalValidationSeverity>))]
public enum FinalValidationSeverity
{
    Warning,
    Blocking,
}

public sealed record FinalValidationIssue(
    string IssueId,
    string Code,
    string Message,
    FinalValidationSeverity Severity,
    bool CanOverride,
    bool Overridden,
    string? SegmentId,
    IReadOnlyList<string> EvidenceIds);

public sealed record NarrationAssetAudit(
    string SegmentId,
    string RelativePath,
    string Sha256,
    TimeSpan MeasuredDuration,
    decimal PeakAmplitude,
    decimal RmsAmplitude,
    decimal ClippedSampleFraction,
    int SuspectedDiscontinuities);

public sealed record FinalProjectValidationManifest(
    Guid ProjectId,
    int ProjectVersion,
    string SourceSha256,
    string ProjectStateSha256,
    TimeSpan MediaDuration,
    string State,
    IReadOnlyList<FinalValidationIssue> Issues,
    IReadOnlyList<NarrationAssetAudit> NarrationAssets,
    IReadOnlyList<string> AppliedOverrideIssueIds,
    DateTimeOffset CompletedAtUtc);

public sealed record FinalProjectValidationResult(
    ProjectSnapshot Snapshot,
    FinalProjectValidationManifest Manifest,
    PipelineCheckpoint Checkpoint)
{
    public bool ReadyForRender => Checkpoint.Status == CheckpointStatus.Complete;
}

/// <summary>
/// Produces the content binding used by both the Phase 11 gate and Phase 12 renderer.
/// A committed project version cannot be rendered through a validation result created for
/// different text, timings, evidence, voice, or narration asset references.
/// </summary>
public static class FinalProjectValidationBinding
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ComputeProjectStateSha256(ProjectSnapshot snapshot) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions)));

    public static string ComputeManifestBindingSha256(FinalProjectValidationManifest manifest)
    {
        var binding = new
        {
            manifest.ProjectId,
            manifest.ProjectVersion,
            manifest.SourceSha256,
            manifest.ProjectStateSha256,
            manifest.MediaDuration,
            manifest.State,
            manifest.Issues,
            manifest.NarrationAssets,
            manifest.AppliedOverrideIssueIds,
        };
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(binding, JsonOptions)));
    }
}

/// <summary>
/// Performs the deterministic Phase 11 gate against committed state and persisted evidence.
/// Warnings require a deliberate per-issue override; evidence, timing, source, and audit-chain
/// failures are never overrideable.
/// </summary>
public sealed partial class FinalProjectValidationService(
    IProjectVersionStore projectStore,
    IPipelineCheckpointStore checkpointStore,
    IMediaProbe mediaProbe,
    TimeProvider timeProvider)
{
    private const string ManifestRelativePath = "manifests/final-project-validation.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<FinalProjectValidationResult> RunAsync(
        Guid projectId,
        string projectDirectory,
        IReadOnlySet<string>? overrideIssueIds,
        CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(projectDirectory);
        var snapshot = await projectStore.LoadCurrentAsync(projectId, cancellationToken).ConfigureAwait(false);
        if (snapshot.ProjectId != projectId)
        {
            throw new InvalidDataException("PROJECT_ID_MISMATCH");
        }

        var issues = new List<FinalValidationIssue>();
        var narrationAudits = new List<NarrationAssetAudit>();
        var source = await TryLoadAsync<SourceIntakeResult>(root, "source-validated.json", issues, cancellationToken)
            .ConfigureAwait(false);
        var audio = await TryLoadAsync<AudioAnalysisCompletionManifest>(root, "audio-analysis-complete.json", issues, cancellationToken)
            .ConfigureAwait(false);
        var visual = await TryLoadAsync<VisualIndexManifest>(root, "visual-index.json", issues, cancellationToken)
            .ConfigureAwait(false);
        var analysis = await TryLoadAsync<VisualUnderstandingManifest>(root, "visual-analysis-complete.json", issues, cancellationToken)
            .ConfigureAwait(false);
        var faces = await TryLoadAsync<FaceContinuityManifest>(root, "face-continuity.json", issues, cancellationToken)
            .ConfigureAwait(false);

        await ValidateSourceAsync(projectId, snapshot, source, issues, cancellationToken).ConfigureAwait(false);
        ValidateManifestIdentity(projectId, snapshot.SourceSha256, audio?.ProjectId, audio?.SourceSha256, "AUDIO", issues);
        ValidateManifestIdentity(projectId, snapshot.SourceSha256, visual?.ProjectId, visual?.SourceSha256, "VISUAL", issues);
        ValidateManifestIdentity(projectId, snapshot.SourceSha256, analysis?.ProjectId, analysis?.SourceSha256, "ANALYSIS", issues);
        ValidateManifestIdentity(projectId, snapshot.SourceSha256, faces?.ProjectId, faces?.SourceSha256, "ENTITY", issues);

        await ValidateVersionHistoryAsync(projectId, snapshot, root, issues, cancellationToken).ConfigureAwait(false);
        await ValidateSegmentsAsync(snapshot, root, audio, visual, analysis, faces, issues, narrationAudits, cancellationToken)
            .ConfigureAwait(false);
        ValidateCrossSegmentAudio(narrationAudits, issues);
        RejectPartialArtifacts(root, issues);

        var requestedOverrides = overrideIssueIds ?? new HashSet<string>(StringComparer.Ordinal);
        var knownIds = issues.Select(item => item.IssueId).ToHashSet(StringComparer.Ordinal);
        var unknownOverrides = requestedOverrides.Where(id => !knownIds.Contains(id)).ToArray();
        if (unknownOverrides.Length > 0)
        {
            throw new InvalidOperationException("UNKNOWN_VALIDATION_OVERRIDE");
        }

        if (issues.Any(item => requestedOverrides.Contains(item.IssueId) && !item.CanOverride))
        {
            throw new InvalidOperationException("BLOCKING_ISSUE_CANNOT_BE_OVERRIDDEN");
        }

        var resolvedIssues = issues
            .Select(item => item with { Overridden = item.CanOverride && requestedOverrides.Contains(item.IssueId) })
            .OrderByDescending(item => item.Severity)
            .ThenBy(item => item.SegmentId, StringComparer.Ordinal)
            .ThenBy(item => item.Code, StringComparer.Ordinal)
            .ToArray();
        var ready = resolvedIssues.All(item => item.Overridden);
        var stateHash = FinalProjectValidationBinding.ComputeProjectStateSha256(snapshot);
        var manifest = new FinalProjectValidationManifest(
            projectId,
            snapshot.Version,
            snapshot.SourceSha256,
            stateHash,
            snapshot.MediaDuration,
            ready ? "READY_FOR_FINAL_RENDER" : "BLOCKED",
            resolvedIssues,
            narrationAudits.OrderBy(item => item.SegmentId, StringComparer.Ordinal).ToArray(),
            resolvedIssues.Where(item => item.Overridden).Select(item => item.IssueId).ToArray(),
            timeProvider.GetUtcNow());
        var manifestPath = Path.Combine(root, ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar));
        await WriteAtomicallyAsync(manifestPath, manifest, cancellationToken).ConfigureAwait(false);
        var manifestHash = await HashFileAsync(manifestPath, cancellationToken).ConfigureAwait(false);
        var checkpoint = new PipelineCheckpoint(
            projectId,
            AdficationPhase.FinalProjectValidation,
            ready ? CheckpointStatus.Complete : CheckpointStatus.Failed,
            timeProvider.GetUtcNow(),
            ManifestRelativePath,
            manifestHash,
            ready ? null : "FINAL_VALIDATION_BLOCKED");
        await checkpointStore.SaveAsync(checkpoint, cancellationToken).ConfigureAwait(false);
        return new(snapshot, manifest, checkpoint);
    }

    private async Task ValidateSourceAsync(
        Guid projectId,
        ProjectSnapshot snapshot,
        SourceIntakeResult? source,
        List<FinalValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        if (source is null)
        {
            return;
        }

        if (source.Attestation.ProjectId != projectId
            || source.Checkpoint.ProjectId != projectId
            || !source.Attestation.Accepted
            || source.Checkpoint.Status != CheckpointStatus.Complete)
        {
            Add(issues, "SOURCE_ATTESTATION_INVALID", "The immutable source authorization record is incomplete or belongs to another project.");
        }

        if (!string.Equals(source.Source.SourceSha256, snapshot.SourceSha256, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(source.Attestation.SourceSha256, snapshot.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            Add(issues, "SOURCE_IDENTITY_MISMATCH", "The committed project does not match its authorized source hash.");
        }

        if (!File.Exists(source.Source.SourcePath))
        {
            Add(issues, "SOURCE_MISSING", "The authorized local source video is no longer available.");
            return;
        }

        var actualHash = await HashFileAsync(source.Source.SourcePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualHash, source.Source.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            Add(issues, "SOURCE_CHANGED", "The local source video has changed since authorization.");
        }

        MediaProbeResult probe;
        try
        {
            probe = await mediaProbe.ProbeAsync(source.Source.SourcePath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or InvalidOperationException)
        {
            Add(issues, "SOURCE_PROBE_FAILED", "The current source video could not be probed for final validation.");
            return;
        }
        if (Math.Abs((probe.Duration - snapshot.MediaDuration).TotalMilliseconds) > 250
            || Math.Abs((probe.Duration - source.Source.Probe.Duration).TotalMilliseconds) > 250)
        {
            Add(issues, "SOURCE_DURATION_MISMATCH", "The current source duration differs from committed project metadata.");
        }

        if (probe.FileSizeBytes != source.Source.SourceSizeBytes)
        {
            Add(issues, "SOURCE_SIZE_MISMATCH", "The current source file size differs from immutable source metadata.");
        }
    }

    private async Task ValidateVersionHistoryAsync(
        Guid projectId,
        ProjectSnapshot snapshot,
        string root,
        List<FinalValidationIssue> issues,
        CancellationToken cancellationToken)
    {
        var history = await projectStore.LoadHistoryAsync(projectId, cancellationToken).ConfigureAwait(false);
        var expected = Enumerable.Range(1, snapshot.Version).ToArray();
        if (!history.Select(item => item.Version).SequenceEqual(expected)
            || history.Count == 0
            || !string.Equals(HashJson(history[^1]), HashJson(snapshot), StringComparison.Ordinal))
        {
            Add(issues, "PROJECT_HISTORY_INVALID", "The append-only project version chain is incomplete or inconsistent.");
            return;
        }

        for (var version = 2; version <= snapshot.Version; version++)
        {
            var path = Path.Combine(root, "audit", "mutations", $"{version:D8}.json");
            if (!File.Exists(path))
            {
                Add(issues, "MUTATION_AUDIT_MISSING", $"Project version {version} has no immutable mutation record.");
                continue;
            }

            try
            {
                await using var stream = File.OpenRead(path);
                var mutation = await JsonSerializer.DeserializeAsync<ProjectMutationRecord>(stream, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                if (mutation is null
                    || mutation.ProjectId != projectId
                    || mutation.PreviousVersion != version - 1
                    || mutation.ResultVersion != version)
                {
                    Add(issues, "MUTATION_AUDIT_INVALID", $"Project version {version} has inconsistent mutation metadata.");
                }
            }
            catch (JsonException)
            {
                Add(issues, "MUTATION_AUDIT_INVALID", $"Project version {version} has an unreadable mutation record.");
            }
        }
    }

    private static async Task ValidateSegmentsAsync(
        ProjectSnapshot snapshot,
        string root,
        AudioAnalysisCompletionManifest? audio,
        VisualIndexManifest? visual,
        VisualUnderstandingManifest? analysis,
        FaceContinuityManifest? faces,
        List<FinalValidationIssue> issues,
        List<NarrationAssetAudit> narrationAudits,
        CancellationToken cancellationToken)
    {
        var availableEvidence = visual?.Scenes.SelectMany(scene => scene.Keyframes).Select(frame => frame.EvidenceId)
            .ToHashSet(StringComparer.Ordinal) ?? [];
        var availableAssets = snapshot.AdSegments
            .Select(item => item.NarrationAssetRelativePath)
            .Where(path => ResolveContainedPath(root, path) is { } fullPath && File.Exists(fullPath))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        foreach (var issue in ProjectValidator.Validate(snapshot.MediaDuration, snapshot.AdSegments, availableEvidence, availableAssets))
        {
            Add(issues, issue.Code, issue.Message, issue.SegmentId, issue.SegmentId is null ? [] : EvidenceFor(snapshot, issue.SegmentId));
        }

        var scenes = visual?.Scenes.ToDictionary(item => item.SceneId, StringComparer.Ordinal)
                     ?? new Dictionary<string, VisualScene>(StringComparer.Ordinal);
        var keyframes = visual?.Scenes.SelectMany(item => item.Keyframes)
            .ToDictionary(item => item.EvidenceId, StringComparer.Ordinal)
            ?? new Dictionary<string, KeyframeEvidence>(StringComparer.Ordinal);
        var analyses = analysis?.Scenes.ToDictionary(item => item.SceneId, StringComparer.Ordinal)
                       ?? new Dictionary<string, VisualSceneAnalysis>(StringComparer.Ordinal);
        foreach (var segment in snapshot.AdSegments)
        {
            if (!scenes.TryGetValue(segment.SceneId, out var scene))
            {
                Add(issues, "SCENE_MISSING", "Description references a scene that is absent from the final visual index.", segment.SegmentId, segment.EvidenceIds);
            }
            else
            {
                var sceneEvidence = scene.Keyframes.Select(item => item.EvidenceId).ToHashSet(StringComparer.Ordinal);
                if (segment.EvidenceIds.Any(id => !sceneEvidence.Contains(id)))
                {
                    Add(issues, "EVIDENCE_SCENE_MISMATCH", "Description evidence does not belong to its committed scene.", segment.SegmentId, segment.EvidenceIds);
                }
            }

            foreach (var evidenceId in segment.EvidenceIds)
            {
                if (!keyframes.TryGetValue(evidenceId, out var keyframe))
                {
                    continue;
                }

                var framePath = ResolveContainedPath(root, keyframe.RelativeImagePath);
                if (framePath is null || !File.Exists(framePath))
                {
                    Add(issues, "EVIDENCE_ASSET_MISSING", "A cited visual-evidence image is unavailable or outside the private project directory.", segment.SegmentId, [evidenceId]);
                }
            }

            if (audio?.SourceAudioTimeline.Any(item =>
                    item.Kind == SourceAudioKind.Dialogue && item.Interval.Overlaps(segment.Window)) == true)
            {
                Add(issues, "DIALOGUE_COLLISION", "Narration overlaps source dialogue.", segment.SegmentId, segment.EvidenceIds);
            }

            if (audio?.SourceAudioTimeline.Any(item =>
                    item.Kind == SourceAudioKind.MeaningfulNonDialogue && item.Interval.Overlaps(segment.Window)) == true)
            {
                AddWarning(issues, "MEANINGFUL_SOUND_COLLISION", "Narration overlaps meaningful source sound; listen before overriding.", segment.SegmentId, segment.EvidenceIds);
            }

            if (analyses.TryGetValue(segment.SceneId, out var sceneAnalysis)
                && (sceneAnalysis.RequiresAdditionalFrames || !string.IsNullOrWhiteSpace(sceneAnalysis.Uncertainty)))
            {
                AddWarning(issues, "UNRESOLVED_VISUAL_UNCERTAINTY", "The source scene still records unresolved visual uncertainty.", segment.SegmentId, segment.EvidenceIds);
            }

            var futureNames = faces?.Entities
                .SelectMany(entity => entity.IdentityEvidence)
                .Where(item => item.EstablishedAt > segment.Window.Start)
                .Select(item => item.Value)
                .Where(name => !string.IsNullOrWhiteSpace(name)) ?? [];
            if (futureNames.Any(name => ContainsWholeTerm(segment.Text, name)))
            {
                Add(issues, "IDENTITY_SPOILER", "Description uses a person's name before the source establishes it.", segment.SegmentId, segment.EvidenceIds);
            }

            var path = ResolveContainedPath(root, segment.NarrationAssetRelativePath);
            if (path is null || !File.Exists(path))
            {
                continue;
            }

            try
            {
                var audit = await NarrationWaveAuditor.AuditAsync(segment.SegmentId, segment.NarrationAssetRelativePath!, path, cancellationToken)
                    .ConfigureAwait(false);
                narrationAudits.Add(audit);
                if (segment.MeasuredNarrationDuration is null
                    || Math.Abs((audit.MeasuredDuration - segment.MeasuredNarrationDuration.Value).TotalMilliseconds) > 40)
                {
                    Add(issues, "NARRATION_DURATION_MISMATCH", "Narration duration differs from the committed measurement.", segment.SegmentId, segment.EvidenceIds);
                }

                if (audit.MeasuredDuration > segment.Window.Duration)
                {
                    Add(issues, "NARRATION_OVERRUN", "Narration audio extends beyond its available window.", segment.SegmentId, segment.EvidenceIds);
                }

                if (audit.RmsAmplitude < 0.002m)
                {
                    Add(issues, "NARRATION_SILENT", "Narration audio is silent or effectively inaudible.", segment.SegmentId, segment.EvidenceIds);
                }

                if (audit.ClippedSampleFraction > 0.001m)
                {
                    Add(issues, "NARRATION_CLIPPING", "Narration audio contains sustained digital clipping.", segment.SegmentId, segment.EvidenceIds);
                }

                if (audit.SuspectedDiscontinuities > 2)
                {
                    AddWarning(issues, "NARRATION_CLICKS", "Narration audio contains abrupt sample discontinuities that may sound like clicks.", segment.SegmentId, segment.EvidenceIds);
                }
            }
            catch (InvalidDataException)
            {
                Add(issues, "NARRATION_WAV_INVALID", "Narration asset is not a supported, readable PCM WAV file.", segment.SegmentId, segment.EvidenceIds);
            }
        }

        var ordered = snapshot.AdSegments.OrderBy(item => item.Window.Start).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            for (var other = index + 1; other < ordered.Length; other++)
            {
                if (WordSimilarity(ordered[index].Text, ordered[other].Text) >= 0.85m)
                {
                    AddWarning(issues, "DUPLICATE_DESCRIPTION", $"Description closely repeats {ordered[index].SegmentId}.", ordered[other].SegmentId, ordered[other].EvidenceIds);
                }
            }
        }
    }

    private static void ValidateCrossSegmentAudio(
        IReadOnlyList<NarrationAssetAudit> audits,
        List<FinalValidationIssue> issues)
    {
        var audible = audits.Where(item => item.RmsAmplitude >= 0.002m).ToArray();
        if (audible.Length < 2)
        {
            return;
        }

        var minimum = audible.Min(item => item.RmsAmplitude);
        var maximum = audible.Max(item => item.RmsAmplitude);
        var differenceDb = 20 * Math.Log10((double)(maximum / minimum));
        if (differenceDb > 12)
        {
            AddWarning(issues, "NARRATION_LEVEL_VARIANCE", "Narration loudness varies by more than 12 decibels across segments; listen before overriding.");
        }
    }

    private static void ValidateManifestIdentity(
        Guid projectId,
        string sourceSha256,
        Guid? manifestProjectId,
        string? manifestSourceSha256,
        string prefix,
        List<FinalValidationIssue> issues)
    {
        if (manifestProjectId is null)
        {
            return;
        }

        if (manifestProjectId != projectId
            || !string.Equals(sourceSha256, manifestSourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            Add(issues, $"{prefix}_MANIFEST_IDENTITY_MISMATCH", $"The {prefix.ToLowerInvariant()} manifest belongs to a different project or source.");
        }
    }

    private static void RejectPartialArtifacts(string root, List<FinalValidationIssue> issues)
    {
        var partial = Directory.EnumerateFiles(root, "*.partial", SearchOption.AllDirectories).FirstOrDefault();
        if (partial is not null)
        {
            Add(issues, "INCOMPLETE_ATOMIC_WRITE", "An unfinished project artifact remains from an interrupted write.");
        }
    }

    private static async Task<T?> TryLoadAsync<T>(
        string root,
        string name,
        List<FinalValidationIssue> issues,
        CancellationToken cancellationToken) where T : class
    {
        var path = Path.Combine(root, "manifests", name);
        if (!File.Exists(path))
        {
            Add(issues, "MANIFEST_MISSING", $"Required manifest {name} is missing.");
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
                   ?? throw new JsonException();
        }
        catch (JsonException)
        {
            Add(issues, "MANIFEST_INVALID", $"Required manifest {name} is unreadable.");
            return null;
        }
    }

    private static string? ResolveContainedPath(string root, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
        {
            return null;
        }

        var full = Path.GetFullPath(Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    private static IReadOnlyList<string> EvidenceFor(ProjectSnapshot snapshot, string segmentId) =>
        snapshot.AdSegments.SingleOrDefault(item => item.SegmentId == segmentId)?.EvidenceIds ?? [];

    private static void Add(
        List<FinalValidationIssue> issues,
        string code,
        string message,
        string? segmentId = null,
        IReadOnlyList<string>? evidenceIds = null) => AddCore(
        issues, code, message, FinalValidationSeverity.Blocking, false, segmentId, evidenceIds);

    private static void AddWarning(
        List<FinalValidationIssue> issues,
        string code,
        string message,
        string? segmentId = null,
        IReadOnlyList<string>? evidenceIds = null) => AddCore(
        issues, code, message, FinalValidationSeverity.Warning, true, segmentId, evidenceIds);

    private static void AddCore(
        List<FinalValidationIssue> issues,
        string code,
        string message,
        FinalValidationSeverity severity,
        bool canOverride,
        string? segmentId,
        IReadOnlyList<string>? evidenceIds)
    {
        var evidence = evidenceIds?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() ?? [];
        var material = $"{code}\n{segmentId}\n{message}\n{string.Join('\n', evidence)}";
        var id = $"issue-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16].ToLowerInvariant()}";
        if (issues.All(item => item.IssueId != id))
        {
            issues.Add(new(id, code, message, severity, canOverride, false, segmentId, evidence));
        }
    }

    private static bool ContainsWholeTerm(string text, string term) => Regex.IsMatch(
        text,
        $@"(?<![\p{{L}}\p{{N}}]){Regex.Escape(term)}(?![\p{{L}}\p{{N}}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static decimal WordSimilarity(string left, string right)
    {
        var first = Words().Matches(left).Select(match => match.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var second = Words().Matches(right).Select(match => match.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var union = first.Union(second, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : first.Intersect(second, StringComparer.OrdinalIgnoreCase).Count() / (decimal)union;
    }

    private static string HashJson<T>(T value) => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions)));

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static async Task WriteAtomicallyAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.partial";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    [GeneratedRegex(@"[\p{L}\p{N}']+", RegexOptions.CultureInvariant)]
    private static partial Regex Words();
}

public static class NarrationWaveAuditor
{
    public static async Task<NarrationAssetAudit> AuditAsync(
        string segmentId,
        string relativePath,
        string absolutePath,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            absolutePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);
        if (ReadFourCc(reader) != "RIFF" || (reader.ReadUInt32() <= 0) || ReadFourCc(reader) != "WAVE")
        {
            throw new InvalidDataException("Invalid RIFF WAVE header.");
        }

        ushort format = 0;
        ushort channels = 0;
        uint sampleRate = 0;
        ushort bitsPerSample = 0;
        long samples = 0;
        double sumSquares = 0;
        var peak = 0d;
        long clipped = 0;
        var discontinuities = 0;
        short previous = 0;
        var hasPrevious = false;
        var foundData = false;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunk = ReadFourCc(reader);
            var size = reader.ReadUInt32();
            if (size > int.MaxValue || stream.Position + size > stream.Length)
            {
                throw new InvalidDataException("Truncated WAV chunk.");
            }

            if (chunk == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("Invalid WAV format chunk.");
                format = reader.ReadUInt16();
                channels = reader.ReadUInt16();
                sampleRate = reader.ReadUInt32();
                _ = reader.ReadUInt32();
                _ = reader.ReadUInt16();
                bitsPerSample = reader.ReadUInt16();
                stream.Position += size - 16;
            }
            else if (chunk == "data")
            {
                if ((size & 1) != 0)
                {
                    throw new InvalidDataException("PCM 16-bit WAV data has an odd byte count.");
                }

                foundData = true;
                var dataEnd = stream.Position + size;
                while (stream.Position + 1 < dataEnd)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sample = reader.ReadInt16();
                    var normalized = sample / 32768d;
                    var absolute = Math.Abs(normalized);
                    peak = Math.Max(peak, absolute);
                    sumSquares += normalized * normalized;
                    if (Math.Abs((int)sample) >= 32760) clipped++;
                    if (hasPrevious && Math.Abs(sample - (int)previous) >= 29_491) discontinuities++;
                    previous = sample;
                    hasPrevious = true;
                    samples++;
                }
            }
            else
            {
                stream.Position += size;
            }

            if ((size & 1) != 0 && stream.Position < stream.Length) stream.Position++;
        }

        if (format != 1 || channels == 0 || sampleRate == 0 || bitsPerSample != 16 || !foundData || samples == 0)
        {
            throw new InvalidDataException("Only PCM 16-bit WAV narration is supported.");
        }

        var duration = TimeSpan.FromSeconds(samples / (double)(sampleRate * channels));
        var hash = await HashFileAsync(absolutePath, cancellationToken).ConfigureAwait(false);
        return new(
            segmentId,
            relativePath,
            hash,
            duration,
            (decimal)peak,
            (decimal)Math.Sqrt(sumSquares / samples),
            clipped / (decimal)samples,
            discontinuities);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static string ReadFourCc(BinaryReader reader) => Encoding.ASCII.GetString(reader.ReadBytes(4));
}
