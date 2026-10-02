using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using ADFi.Core;
using ADFi.LocalAI;
using ADFi.Media;

namespace ADFi.Conversation;

public sealed record AdDraftPackage(
    ProposedAdInterval Opportunity,
    IReadOnlyList<VisualFact> VerifiedFacts,
    IReadOnlyList<string> NearbyDialogue,
    IReadOnlyList<ProjectEntity> Entities);

public sealed record AdDraftCompletionManifest(
    Guid ProjectId,
    IReadOnlyList<AdSegment> Segments,
    string? SourceSha256 = null,
    string? OpportunityManifestSha256 = null,
    string? FaceContinuitySha256 = null);

public sealed class AdDraftCoordinator(
    ILocalModelRuntime runtime,
    string modelName,
    string writerPromptPath,
    IProgress<(int Completed, int Total, string Announcement)>? progress = null)
{
    private const string OutputSchema =
        """
        {"type":"object","required":["text","evidenceIds","uncertainty","reason"],"properties":{"text":{"type":"string"},"evidenceIds":{"type":"array","minItems":1,"items":{"type":"string"}},"uncertainty":{"type":["string","null"]},"reason":{"type":"string"}},"additionalProperties":false}
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public async Task<IReadOnlyList<AdSegment>> DraftAsync(
        Guid projectId,
        string projectDirectory,
        IReadOnlyList<AdDraftPackage> packages,
        CancellationToken cancellationToken)
    {
        var prompt = await PromptResourceLoader.LoadAsync(writerPromptPath, cancellationToken)
            .ConfigureAwait(false);
        var segments = new List<AdSegment>(packages.Count);
        var modelLoaded = false;
        try
        {
            var ordered = packages.OrderBy(item => item.Opportunity.Window.Start).ToArray();
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
                    segments.Add(existing);
                    progress?.Report((index + 1, ordered.Length, $"Drafting description {index + 1} of {ordered.Length}."));
                    continue;
                }

                if (!modelLoaded)
                {
                    await runtime.EnsureModelAvailableAsync(modelName, cancellationToken).ConfigureAwait(false);
                    modelLoaded = true;
                }

                var draft = await GenerateFittingDraftAsync(projectId, package, prompt, cancellationToken)
                    .ConfigureAwait(false);
                var segment = ValidateAndCreate(package, draft, prompt);
                await PersistAsync(projectDirectory, segment, cancellationToken).ConfigureAwait(false);
                segments.Add(segment);
                progress?.Report((index + 1, ordered.Length, $"Drafting description {index + 1} of {ordered.Length}."));
            }
        }
        finally
        {
            if (modelLoaded)
            {
                await runtime.UnloadAsync(modelName, CancellationToken.None).ConfigureAwait(false);
            }
        }

        await PersistCompletionAsync(projectDirectory, projectId, segments, cancellationToken).ConfigureAwait(false);
        return segments;
    }

    private async Task<AdDraftResponse> GenerateFittingDraftAsync(
        Guid projectId,
        AdDraftPackage package,
        PromptResource prompt,
        CancellationToken cancellationToken)
    {
        AdDraftResponse? previous = null;
        var usable = package.Opportunity.Window.Duration - TimeSpan.FromMilliseconds(250);
        for (var attempt = 1; attempt <= NarrationTimingPolicy.MaximumAutoFitAttempts; attempt++)
        {
            var maxWords = Math.Max(
                1,
                (int)Math.Floor(usable.TotalMinutes * NarrationTimingPolicy.DefaultPlanningWordsPerMinute));
            var input = JsonSerializer.Serialize(new
            {
                projectId,
                interval = package.Opportunity,
                verifiedFacts = package.VerifiedFacts,
                package.NearbyDialogue,
                establishedEntities = package.Entities.Select(entity => new
                {
                    entity.EntityId,
                    DisplayName = entity.DisplayNameAt(package.Opportunity.Window.Start),
                }),
                maximumWords = maxWords,
                priorOverlongDraft = previous?.Text,
                attempt,
            });
            var response = await runtime.GenerateAsync(
                modelName,
                new(prompt.Content, input, [], OutputSchema, 500),
                cancellationToken).ConfigureAwait(false);
            var candidate = JsonSerializer.Deserialize<AdDraftResponse>(response.Content, JsonOptions)
                            ?? throw new InvalidDataException("The AD writer returned no proposal.");
            ValidateEvidence(package, candidate);
            if (NarrationTimingPolicy.EstimateSpeechDuration(candidate.Text) <= usable)
            {
                return candidate with { Model = response.Model };
            }

            previous = candidate;
        }

        throw new InvalidOperationException("AD_DRAFT_EXCEEDS_ESTIMATED_WINDOW");
    }

    private static AdSegment ValidateAndCreate(
        AdDraftPackage package,
        AdDraftResponse draft,
        PromptResource prompt)
    {
        if (package.NearbyDialogue.Any(dialogue =>
                dialogue.Length >= 8
                && draft.Text.Contains(dialogue, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The AD draft repeats nearby dialogue.");
        }

        var futureNames = package.Entities
            .SelectMany(entity => entity.IdentityEvidence)
            .Where(evidence => evidence.EstablishedAt > package.Opportunity.Window.Start)
            .Select(evidence => evidence.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        if (futureNames.Any(name => draft.Text.Contains(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("The AD draft reveals an entity name before it is established.");
        }

        return new(
            $"ad-{package.Opportunity.IntervalId}",
            package.Opportunity.SceneId,
            package.Opportunity.Window,
            draft.Text.Trim(),
            draft.EvidenceIds,
            prompt.Id,
            prompt.Version.ToString(),
            prompt.Sha256,
            null,
            null,
            false,
            BuildInputFingerprint(package));
    }

    private static void ValidateEvidence(AdDraftPackage package, AdDraftResponse draft)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Text);
        ArgumentException.ThrowIfNullOrWhiteSpace(draft.Reason);
        var supported = package.VerifiedFacts
            .SelectMany(fact => fact.EvidenceIds)
            .ToHashSet(StringComparer.Ordinal);
        if (draft.EvidenceIds.Count == 0 || draft.EvidenceIds.Any(id => !supported.Contains(id)))
        {
            throw new InvalidDataException("The AD draft cites unsupported visual evidence.");
        }
    }

    private static async Task<AdSegment?> TryLoadExistingAsync(
        string projectDirectory,
        AdDraftPackage package,
        PromptResource prompt,
        CancellationToken cancellationToken)
    {
        var path = DraftPath(projectDirectory, package.Opportunity.IntervalId);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var segment = await JsonSerializer.DeserializeAsync<AdSegment>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (segment is null
                || segment.SegmentId != $"ad-{package.Opportunity.IntervalId}"
                || segment.SceneId != package.Opportunity.SceneId
                || segment.Window != package.Opportunity.Window
                || segment.PromptId != prompt.Id
                || segment.PromptVersion != prompt.Version.ToString()
                || segment.PromptSha256 != prompt.Sha256
                || segment.InputFingerprint != BuildInputFingerprint(package)
                || NarrationTimingPolicy.EstimateSpeechDuration(segment.Text)
                   > package.Opportunity.Window.Duration - TimeSpan.FromMilliseconds(250))
            {
                return null;
            }

            ValidateEvidence(package, new(segment.Text, segment.EvidenceIds, null, "Persisted verified draft"));
            _ = ValidateAndCreate(
                package,
                new(segment.Text, segment.EvidenceIds, null, "Persisted verified draft"),
                prompt);
            return segment with
            {
                NarrationAssetRelativePath = null,
                MeasuredNarrationDuration = null,
                UserApproved = false,
            };
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        {
            return null;
        }
    }

    private static Task PersistAsync(
        string projectDirectory,
        AdSegment segment,
        CancellationToken cancellationToken) =>
        WriteJsonAsync(
            DraftPath(projectDirectory, segment.SegmentId["ad-".Length..]),
            segment,
            cancellationToken);

    private static Task PersistCompletionAsync(
        string projectDirectory,
        Guid projectId,
        IReadOnlyList<AdSegment> segments,
        CancellationToken cancellationToken) =>
        WriteJsonAsync(
            Path.Combine(Path.GetFullPath(projectDirectory), "manifests", "ad-draft-complete.json"),
            new AdDraftCompletionManifest(projectId, segments),
            cancellationToken);

    private static string DraftPath(string projectDirectory, string intervalId) =>
        Path.Combine(Path.GetFullPath(projectDirectory), "ad", "drafts", $"ad-{intervalId}.json");

    private static string BuildInputFingerprint(AdDraftPackage package)
    {
        var canonical = JsonSerializer.Serialize(new
        {
            package.Opportunity,
            Facts = package.VerifiedFacts.OrderBy(fact => fact.FactId, StringComparer.Ordinal),
            package.NearbyDialogue,
            Entities = package.Entities.OrderBy(entity => entity.EntityId, StringComparer.Ordinal),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static async Task WriteJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
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

    private sealed record AdDraftResponse(
        string Text,
        IReadOnlyList<string> EvidenceIds,
        string? Uncertainty,
        string Reason,
        string? Model = null);
}
