using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ADFi.Core;
using ADFi.LocalAI;
using ADFi.Media;

namespace ADFi.Conversation;

public sealed record VisualObservation(
    string ObservationId,
    string Category,
    string ObjectiveDescription,
    IReadOnlyList<string> EvidenceIds,
    decimal Certainty,
    decimal Importance,
    bool AlreadyConveyedBySourceAudio);

public sealed record VisualSceneAnalysis(
    string SceneId,
    IReadOnlyList<VisualObservation> Observations,
    IReadOnlyList<string> PotentialOnScreenText,
    bool RequiresAdditionalFrames,
    bool RequiresExactOcr,
    string? Uncertainty,
    PromptDecisionProvenance Provenance,
    string? InputFingerprint = null);

public sealed record VisualAnalysisPackage(
    VisualScene Scene,
    IReadOnlyDictionary<string, string> AbsoluteFramePaths,
    IReadOnlyDictionary<string, string> TranscriptContext,
    IReadOnlyList<ProjectEntity> EstablishedEntities,
    IReadOnlyList<ReconciledOcr>? OcrContext = null,
    IReadOnlyList<FaceObservation>? FaceContinuity = null);

public sealed record VisualAnalysisProgress(int CompletedScenes, int TotalScenes, string Announcement);

public sealed class VisualAnalysisCoordinator(
    ILocalModelRuntime runtime,
    string modelName,
    string analystPromptPath,
    TimeProvider timeProvider,
    IProgress<VisualAnalysisProgress>? progress = null)
{
    private const string OutputSchema =
        """
        {"type":"object","required":["sceneId","observations","potentialOnScreenText","requiresAdditionalFrames","requiresExactOcr"],"properties":{"sceneId":{"type":"string"},"observations":{"type":"array","maxItems":6,"items":{"type":"object","required":["observationId","category","objectiveDescription","evidenceIds","certainty","importance","alreadyConveyedBySourceAudio"],"properties":{"observationId":{"type":"string","maxLength":80},"category":{"type":"string","maxLength":64},"objectiveDescription":{"type":"string","maxLength":500},"evidenceIds":{"type":"array","minItems":1,"maxItems":3,"items":{"type":"string"}},"certainty":{"type":"number","minimum":0,"maximum":1},"importance":{"type":"number","minimum":0,"maximum":1},"alreadyConveyedBySourceAudio":{"type":"boolean"}},"additionalProperties":false}},"potentialOnScreenText":{"type":"array","maxItems":6,"items":{"type":"string","maxLength":500}},"requiresAdditionalFrames":{"type":"boolean"},"requiresExactOcr":{"type":"boolean"},"uncertainty":{"type":["string","null"],"maxLength":500}},"additionalProperties":false}
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<IReadOnlyList<VisualSceneAnalysis>> AnalyzeAsync(
        Guid projectId,
        string projectDirectory,
        IReadOnlyList<VisualAnalysisPackage> packages,
        CancellationToken cancellationToken)
    {
        var prompt = await PromptResourceLoader.LoadAsync(analystPromptPath, cancellationToken)
            .ConfigureAwait(false);
        var results = new List<VisualSceneAnalysis>(packages.Count);
        var modelLoaded = false;
        try
        {
            var ordered = packages.OrderBy(item => item.Scene.SourceInterval.Start).ToArray();
            for (var index = 0; index < ordered.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var package = ordered[index];
                var existing = await TryLoadExistingAsync(
                    projectDirectory,
                    package,
                    prompt,
                    cancellationToken).ConfigureAwait(false);
                if (existing is not null)
                {
                    results.Add(existing);
                    progress?.Report(new(
                        index + 1,
                        ordered.Length,
                        $"Restored verified scene analysis {index + 1} of {ordered.Length}."));
                    continue;
                }

                if (!modelLoaded)
                {
                    await runtime.EnsureModelAvailableAsync(modelName, cancellationToken).ConfigureAwait(false);
                    modelLoaded = true;
                }

                var (draft, response) = await GenerateDraftAsync(
                    projectId,
                    projectDirectory,
                    package,
                    prompt,
                    cancellationToken).ConfigureAwait(false);
                var result = ValidateAndAttachProvenance(package, draft, prompt, response);
                await PersistAsync(projectDirectory, result, cancellationToken).ConfigureAwait(false);
                results.Add(result);
                progress?.Report(new(
                    index + 1,
                    ordered.Length,
                    $"Analyzing scene {index + 1} of {ordered.Length}."));
            }
        }
        finally
        {
            if (modelLoaded)
            {
                await runtime.UnloadAsync(modelName, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return results;
    }

    private async Task<(VisualAnalysisDraft Draft, LocalModelResponse Response)> GenerateDraftAsync(
        Guid projectId,
        string projectDirectory,
        VisualAnalysisPackage package,
        PromptResource prompt,
        CancellationToken cancellationToken)
    {
        Exception? validationFailure = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await runtime.GenerateAsync(
                modelName,
                new(
                    prompt.Content,
                    BuildInput(projectId, package, isRetry: attempt > 0),
                    package.Scene.Keyframes.Select(frame => RequiredFramePath(package, frame.EvidenceId)).ToArray(),
                    OutputSchema,
                    attempt == 0 ? 1_500 : 2_400),
                cancellationToken).ConfigureAwait(false);
            try
            {
                var draft = JsonSerializer.Deserialize<VisualAnalysisDraft>(response.Content, JsonOptions)
                            ?? throw new InvalidDataException("The visual analyst returned no scene result.");
                draft = NormalizeObservationEvidence(package, draft);
                ValidateDraftEvidence(package, draft);
                return (draft, response);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
            {
                validationFailure = exception;
                await PersistRejectedOutputAsync(
                    projectDirectory,
                    package.Scene.SceneId,
                    attempt + 1,
                    response,
                    exception,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        throw new InvalidDataException(
            "The visual analyst returned incomplete, malformed, or unsupported structured output after one bounded retry.",
            validationFailure);
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
            "analysis",
            "rejected",
            $"{sceneId}-attempt-{attempt:D2}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".partial";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                new
                {
                    sceneId,
                    attempt,
                    response.Model,
                    response.Content,
                    parseError = exception.Message,
                    createdAtUtc = timeProvider.GetUtcNow(),
                },
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static string BuildInput(Guid projectId, VisualAnalysisPackage package, bool isRetry)
    {
        var verifiedOcr = (package.OcrContext ?? [])
            .Where(reading => !reading.RequiresAdditionalEvidence
                              && !string.IsNullOrWhiteSpace(reading.SupportedText))
            .ToArray();
        var established = package.EstablishedEntities.Select(entity => new
        {
            entity.EntityId,
            entity.Kind,
            DisplayName = entity.DisplayNameAt(package.Scene.SourceInterval.Start),
        });
        return JsonSerializer.Serialize(new
        {
            projectId,
            scene = new
            {
                package.Scene.SceneId,
                startMilliseconds = checked((long)package.Scene.SourceInterval.Start.TotalMilliseconds),
                endMilliseconds = checked((long)package.Scene.SourceInterval.End.TotalMilliseconds),
                frames = package.Scene.Keyframes.Select(frame => new
                {
                    frame.EvidenceId,
                    sourceMilliseconds = checked((long)frame.SourceTime.TotalMilliseconds),
                    frame.PotentialText,
                }),
            },
            transcript = package.TranscriptContext,
            verifiedOnScreenText = verifiedOcr,
            faceContinuity = (package.FaceContinuity ?? []).Select(face => new
            {
                face.EntityId,
                face.FrameEvidenceId,
                face.X,
                face.Y,
                face.Width,
                face.Height,
                face.MatchCertain,
            }),
            establishedEntities = established,
            instruction = isRetry
                ? "The prior structured response was invalid. Return one concise, complete JSON object only with at most six nonduplicative observations. Report only facts visible in the supplied ordered frames. Cite only the exact frame evidence IDs listed in this request, never OCR candidate IDs or an ID from another scene. Omit low-value detail before risking truncation."
                : "Report only facts visible in the supplied ordered frames, using at most six nonduplicative observations. Cite only their exact frame evidence IDs. Identify what is not already conveyed by the supplied transcript or source-sound context. Transcript lines are audio context and must never be presented as on-screen text unless the verified OCR context independently supports them.",
        });
    }

    private VisualSceneAnalysis ValidateAndAttachProvenance(
        VisualAnalysisPackage package,
        VisualAnalysisDraft draft,
        PromptResource prompt,
        LocalModelResponse response)
    {
        ValidateDraftEvidence(package, draft);

        return new(
            draft.SceneId,
            draft.Observations,
            draft.PotentialOnScreenText,
            draft.RequiresAdditionalFrames,
            draft.RequiresExactOcr,
            draft.Uncertainty,
            new(
                prompt.Id,
                prompt.Version.ToString(),
                prompt.Sha256,
                runtime.RuntimeName,
                response.Model,
                timeProvider.GetUtcNow()),
            ComputeInputFingerprint(package));
    }

    private async Task<VisualSceneAnalysis?> TryLoadExistingAsync(
        string projectDirectory,
        VisualAnalysisPackage package,
        PromptResource prompt,
        CancellationToken cancellationToken)
    {
        var path = AnalysisPath(projectDirectory, package.Scene.SceneId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var existing = await JsonSerializer.DeserializeAsync<VisualSceneAnalysis>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (existing is null
                || existing.Provenance.PromptId != prompt.Id
                || existing.Provenance.PromptVersion != prompt.Version.ToString()
                || existing.Provenance.PromptSha256 != prompt.Sha256
                || existing.Provenance.ModelName != modelName
                || existing.InputFingerprint != ComputeInputFingerprint(package))
            {
                return null;
            }

            ValidateDraftEvidence(
                package,
                new(
                    existing.SceneId,
                    existing.Observations,
                    existing.PotentialOnScreenText,
                    existing.RequiresAdditionalFrames,
                    existing.RequiresExactOcr,
                    existing.Uncertainty));
            return existing;
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    internal static string ComputeInputFingerprint(VisualAnalysisPackage package)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            package.Scene,
            Transcript = package.TranscriptContext.OrderBy(item => item.Key, StringComparer.Ordinal),
            Entities = package.EstablishedEntities.OrderBy(item => item.EntityId, StringComparer.Ordinal),
            Ocr = package.OcrContext ?? [],
            Faces = package.FaceContinuity ?? [],
            OutputSchema,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void ValidateDraftEvidence(VisualAnalysisPackage package, VisualAnalysisDraft draft)
    {
        if (!string.Equals(draft.SceneId, package.Scene.SceneId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The visual analysis references the wrong scene.");
        }

        var availableEvidence = package.Scene.Keyframes.Select(frame => frame.EvidenceId).ToHashSet(StringComparer.Ordinal);
        var observationIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var observation in draft.Observations)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(observation.ObservationId);
            ArgumentException.ThrowIfNullOrWhiteSpace(observation.Category);
            ArgumentException.ThrowIfNullOrWhiteSpace(observation.ObjectiveDescription);
            if (!observationIds.Add(observation.ObservationId))
            {
                throw new InvalidDataException("Visual analysis contains duplicate observation identifiers.");
            }

            if (observation.Certainty is < 0 or > 1 || observation.Importance is < 0 or > 1)
            {
                throw new InvalidDataException("Visual-analysis scores must be between zero and one.");
            }

            if (observation.EvidenceIds.Count == 0
                || observation.EvidenceIds.Any(id => !availableEvidence.Contains(id)))
            {
                throw new InvalidDataException("A visual observation cites unavailable frame evidence.");
            }
        }
    }

    private static VisualAnalysisDraft NormalizeObservationEvidence(
        VisualAnalysisPackage package,
        VisualAnalysisDraft draft)
    {
        var frameEvidence = package.Scene.Keyframes
            .Select(frame => frame.EvidenceId)
            .ToHashSet(StringComparer.Ordinal);
        var ocrEvidence = (package.OcrContext ?? [])
            .SelectMany(reading => reading.EvidenceIds)
            .ToHashSet(StringComparer.Ordinal);
        var auxiliaryEvidence = ocrEvidence
            .Concat(package.EstablishedEntities.Select(entity => entity.EntityId))
            .Concat((package.FaceContinuity ?? []).Select(face => face.EntityId))
            .ToHashSet(StringComparer.Ordinal);
        var verifiedText = (package.OcrContext ?? [])
            .Where(reading => !reading.RequiresAdditionalEvidence
                              && !string.IsNullOrWhiteSpace(reading.SupportedText))
            .Select(reading => reading.SupportedText!.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var observations = draft.Observations
            .Select(observation =>
            {
                var frames = observation.EvidenceIds.Where(frameEvidence.Contains).ToArray();
                var unsupported = observation.EvidenceIds.Where(id => !frameEvidence.Contains(id)).ToArray();
                return unsupported.Length > 0 && unsupported.All(auxiliaryEvidence.Contains)
                    ? frames.Length == 0
                        ? null
                        : observation with { EvidenceIds = frames }
                    : observation;
            })
            .Where(observation => observation is not null)
            .Select(observation => observation!)
            .ToArray();
        var potentialText = draft.PotentialOnScreenText
            .Select(text => text.Trim())
            .Where(text => verifiedText.Contains(text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return draft with { Observations = observations, PotentialOnScreenText = potentialText };
    }

    private static string RequiredFramePath(VisualAnalysisPackage package, string evidenceId) =>
        package.AbsoluteFramePaths.TryGetValue(evidenceId, out var path) && File.Exists(path)
            ? Path.GetFullPath(path)
            : throw new FileNotFoundException($"Visual evidence {evidenceId} is missing.");

    private static async Task PersistAsync(
        string projectDirectory,
        VisualSceneAnalysis result,
        CancellationToken cancellationToken)
    {
        var path = AnalysisPath(projectDirectory, result.SceneId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".partial";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private static string AnalysisPath(string projectDirectory, string sceneId) =>
        Path.Combine(Path.GetFullPath(projectDirectory), "visual", "analysis", sceneId + ".json");

    private sealed record VisualAnalysisDraft(
        string SceneId,
        IReadOnlyList<VisualObservation> Observations,
        IReadOnlyList<string> PotentialOnScreenText,
        bool RequiresAdditionalFrames,
        bool RequiresExactOcr,
        string? Uncertainty);
}
