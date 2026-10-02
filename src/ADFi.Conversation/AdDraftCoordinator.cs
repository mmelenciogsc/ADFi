using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        {"type":"object","required":["text","evidenceIds","uncertainty","reason"],"properties":{"text":{"type":"string","maxLength":500},"evidenceIds":{"type":"array","minItems":1,"maxItems":3,"items":{"type":"string","maxLength":100}},"uncertainty":{"type":["string","null"],"maxLength":500},"reason":{"type":"string","maxLength":500}},"additionalProperties":false}
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

                var draft = await GenerateFittingDraftAsync(
                        projectId,
                        projectDirectory,
                        package,
                        prompt,
                        cancellationToken)
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
        string projectDirectory,
        AdDraftPackage package,
        PromptResource prompt,
        CancellationToken cancellationToken)
    {
        AdDraftResponse? shortest = null;
        string? shortestModel = null;
        Exception? validationFailure = null;
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
                priorOverlongDraft = shortest?.Text,
                attempt,
                instruction = validationFailure is null
                    ? $"Write no more than {maxWords} spoken words. Preserve only the most essential supported action or state. The word limit is mandatory. Keep the reason concise and return one complete JSON object."
                    : $"The prior response was malformed or cited unsupported evidence. Return one short, complete JSON object only. Write no more than {maxWords} spoken words, cite only the supplied evidence IDs, and keep the reason under 30 words.",
            });
            var response = await runtime.GenerateAsync(
                modelName,
                new(
                    prompt.Content,
                    input,
                    [],
                    OutputSchema,
                    Math.Clamp((maxWords * 3) + 120, 200, 400)),
                cancellationToken).ConfigureAwait(false);
            AdDraftResponse candidate;
            try
            {
                candidate = JsonSerializer.Deserialize<AdDraftResponse>(response.Content, JsonOptions)
                            ?? throw new InvalidDataException("The AD writer returned no proposal.");
                ValidateEvidence(package, candidate);
            }
            catch (Exception exception) when (exception is JsonException or InvalidDataException or ArgumentException)
            {
                await PersistRejectedOutputAsync(
                    projectDirectory,
                    package.Opportunity.IntervalId,
                    attempt,
                    response,
                    exception,
                    cancellationToken).ConfigureAwait(false);
                if (!TryRecoverCompleteCoreFields(response, package, out candidate))
                {
                    validationFailure = exception;
                    continue;
                }
            }

            if (NarrationTimingPolicy.EstimateSpeechDuration(candidate.Text) <= usable)
            {
                return candidate with { Model = response.Model };
            }

            // Small local models sometimes ignore an otherwise valid word limit by appending
            // decorative detail. Prefer an intact leading sentence or clause copied verbatim
            // from the evidence-valid response; never cut through a word or synthesize facts.
            var compacted = CompactToCompleteUnit(candidate.Text, maxWords);
            if (compacted is not null)
            {
                return candidate with { Text = compacted, Model = response.Model };
            }

            if (shortest is null || CountWords(candidate.Text) < CountWords(shortest.Text))
            {
                shortest = candidate;
                shortestModel = response.Model;
            }
        }

        // The measured Piper phase owns the final timing decision and flags an unresolved
        // overrun for user review. Keep the shortest evidence-valid proposal after the
        // bounded retries instead of aborting the entire resumable project.
        return (shortest ?? throw new InvalidDataException(
            "The AD writer returned malformed or unsupported structured output after bounded retries.",
            validationFailure)) with
        {
            Model = shortestModel,
        };
    }

    private static bool TryRecoverCompleteCoreFields(
        LocalModelResponse response,
        AdDraftPackage package,
        out AdDraftResponse candidate)
    {
        candidate = null!;
        try
        {
            // A constrained local model can exhaust its token cap while writing an auxiliary
            // reason after already returning complete text and evidence fields. Recover only
            // those fully closed JSON values, then run the normal evidence validation. Never
            // guess at a cut-off narration string or evidence identifier.
            var textMatch = Regex.Match(
                response.Content,
                "\\\"text\\\"\\s*:\\s*(?<value>\\\"(?:\\\\.|[^\\\"\\\\])*\\\")",
                RegexOptions.CultureInvariant);
            var evidenceMatch = Regex.Match(
                response.Content,
                "\\\"evidenceIds\\\"\\s*:\\s*(?<value>\\[(?:\\s*\\\"(?:\\\\.|[^\\\"\\\\])*\\\"\\s*,?)*\\s*\\])",
                RegexOptions.CultureInvariant | RegexOptions.Singleline);
            if (!textMatch.Success || !evidenceMatch.Success)
            {
                return false;
            }

            var text = JsonSerializer.Deserialize<string>(textMatch.Groups["value"].Value, JsonOptions);
            var evidenceIds = JsonSerializer.Deserialize<IReadOnlyList<string>>(
                evidenceMatch.Groups["value"].Value,
                JsonOptions);
            candidate = new(
                text ?? string.Empty,
                evidenceIds ?? [],
                "The model response ended after its complete core fields; auxiliary fields were discarded.",
                "Recovered complete evidence-grounded core fields from a truncated structured response.",
                response.Model);
            ValidateEvidence(package, candidate);
            return true;
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException)
        {
            candidate = null!;
            return false;
        }
    }

    private static Task PersistRejectedOutputAsync(
        string projectDirectory,
        string intervalId,
        int attempt,
        LocalModelResponse response,
        Exception exception,
        CancellationToken cancellationToken) =>
        WriteJsonAsync(
            Path.Combine(
                Path.GetFullPath(projectDirectory),
                "ad",
                "drafts",
                "rejected",
                $"ad-{intervalId}-attempt-{attempt:D2}.json"),
            new
            {
                intervalId,
                attempt,
                response.Model,
                response.Content,
                parseError = exception.Message,
                createdAtUtc = DateTimeOffset.UtcNow,
            },
            cancellationToken);

    private static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static string? CompactToCompleteUnit(string text, int maximumWords)
    {
        var normalized = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (CountWords(normalized) <= maximumWords)
        {
            return normalized;
        }

        foreach (var marker in new[] { '.', '!', '?', ';' })
        {
            var end = normalized.IndexOf(marker, StringComparison.Ordinal);
            if (end > 0)
            {
                var complete = normalized[..(end + 1)].Trim();
                if (CountWords(complete) <= maximumWords)
                {
                    return complete;
                }
            }
        }

        string? clause = null;
        for (var index = 0; index < normalized.Length; index++)
        {
            if (normalized[index] != ',')
            {
                continue;
            }

            var candidate = normalized[..index].TrimEnd() + ".";
            if (CountWords(candidate) <= maximumWords)
            {
                clause = candidate;
            }
            else
            {
                break;
            }
        }

        return clause;
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
            AdSegment? segment;
            await using (var stream = File.OpenRead(path))
            {
                segment = await JsonSerializer.DeserializeAsync<AdSegment>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
            }

            // The stream must be closed before a compacted draft replaces the same file.
            // Windows denies overwrite/rename while the source handle remains open.
            if (segment is null
                || segment.SegmentId != $"ad-{package.Opportunity.IntervalId}"
                || segment.SceneId != package.Opportunity.SceneId
                || segment.Window != package.Opportunity.Window
                || segment.PromptId != prompt.Id
                || segment.PromptVersion != prompt.Version.ToString()
                || segment.PromptSha256 != prompt.Sha256
                || segment.InputFingerprint != BuildInputFingerprint(package))
            {
                return null;
            }

            ValidateEvidence(package, new(segment.Text, segment.EvidenceIds, null, "Persisted verified draft"));
            _ = ValidateAndCreate(
                package,
                new(segment.Text, segment.EvidenceIds, null, "Persisted verified draft"),
                prompt);
            var usable = package.Opportunity.Window.Duration - TimeSpan.FromMilliseconds(250);
            if (NarrationTimingPolicy.EstimateSpeechDuration(segment.Text) > usable)
            {
                var maximumWords = Math.Max(
                    1,
                    (int)Math.Floor(usable.TotalMinutes * NarrationTimingPolicy.DefaultPlanningWordsPerMinute));
                var compacted = CompactToCompleteUnit(segment.Text, maximumWords);
                if (compacted is null)
                {
                    return null;
                }

                segment = segment with { Text = compacted };
                await PersistAsync(projectDirectory, segment, cancellationToken).ConfigureAwait(false);
            }

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
        var temporary = path + $".{Guid.NewGuid():N}.partial";
        try
        {
            await using (var stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private sealed record AdDraftResponse(
        string Text,
        IReadOnlyList<string> EvidenceIds,
        string? Uncertainty,
        string Reason,
        string? Model = null);
}
