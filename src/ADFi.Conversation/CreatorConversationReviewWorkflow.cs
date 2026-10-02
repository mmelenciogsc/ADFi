using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ADFi.Core;
using ADFi.LocalAI;
using ADFi.Media;

namespace ADFi.Conversation;

public sealed record CreatorConversationConfiguration(
    string QwenModel,
    string PlannerPromptPath,
    string TaskExecutorPromptPath,
    string SemanticToolsPath,
    string WriterPromptPath,
    string VoiceId,
    TimeSpan BreathingRoom);

public sealed record CreatorTaskOutput(
    string Summary,
    string? SceneId = null,
    string? SegmentId = null,
    string? ProposedText = null,
    int? ProjectVersion = null,
    MediaInterval? PreviewWindow = null,
    IReadOnlyList<ProjectValidationIssue>? ValidationIssues = null);

/// <summary>
/// Runs one Creator review turn as a bounded plan followed by a strictly sequential
/// task queue. Model output can propose or assess work, but only deterministic host
/// services can read project state, synthesize audio, create previews, or append a
/// project mutation.
/// </summary>
public sealed class CreatorConversationReviewWorkflow(
    IProjectVersionStore projectStore,
    Func<ILocalModelRuntime> runtimeFactory,
    Func<string, INarrationSynthesizer> narrationSynthesizerFactory,
    TimeProvider timeProvider)
{
    public static IReadOnlySet<string> SupportedTools { get; } = new HashSet<string>(
        [
            "FindSceneByTime",
            "GetScene",
            "GetTranscriptInterval",
            "GetRelevantFrames",
            "AnalyzeSceneSemantics",
            "IdentifyPeopleObjectsActions",
            "RunVlmOcr",
            "ReconcileOcrResults",
            "RewriteAdSegment",
            "CheckForSpoilers",
            "CheckRedundancy",
            "ValidateNarrationWindow",
            "EstimateSpeechDuration",
            "SynthesizeNarration",
            "MeasureRenderedNarration",
            "ApplyProjectMutation",
            "UndoLastMutation",
            "ValidateProject",
            "GenerateVideoPreview",
        ],
        StringComparer.Ordinal);

    public async Task<CreatorConversationTurnResult> RunAsync(
        Guid projectId,
        string projectDirectory,
        string userRequest,
        string? selectedSegmentId,
        CreatorConversationConfiguration configuration,
        IProgress<CreatorConversationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ValidateConfiguration(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(userRequest);
        if (userRequest.Length > 4_000)
        {
            throw new ArgumentException("A project request may contain at most 4,000 characters.", nameof(userRequest));
        }

        var root = Path.GetFullPath(projectDirectory);
        var started = timeProvider.GetUtcNow();
        var turnId = Guid.NewGuid();
        var initial = await projectStore.LoadCurrentAsync(projectId, cancellationToken).ConfigureAwait(false);
        var evidence = await ConversationProjectEvidence.LoadAsync(
            projectId,
            root,
            initial,
            cancellationToken).ConfigureAwait(false);
        var classification = ConversationIntentClassifier.Classify(userRequest);
        PlannedConversation planned;
        if (classification.RequiresPlanner)
        {
            await using var plannerRuntime = runtimeFactory();
            planned = await new LocalConversationPlanner(
                    plannerRuntime,
                    configuration.QwenModel,
                    configuration.PlannerPromptPath,
                    configuration.SemanticToolsPath,
                    timeProvider)
                .PlanAsync(
                    userRequest,
                    new(
                        projectId,
                        initial.Version,
                        initial.MediaDuration,
                        evidence.Visual.Scenes.Select(scene => scene.SceneId).ToArray(),
                        evidence.Analysis.Scenes
                            .Where(scene => scene.RequiresAdditionalFrames || scene.Uncertainty is not null)
                            .Select(scene => scene.SceneId)
                            .ToArray(),
                        SupportedTools),
                    cancellationToken).ConfigureAwait(false);
        }
        else
        {
            planned = new(classification, CreateMinimalPlan(userRequest, classification), null);
        }

        var plan = planned.Plan ?? throw new InvalidDataException("The conversation turn produced no executable plan.");
        TaskPlanValidator.Validate(plan, SupportedTools);
        var taskStore = new FileConversationTaskResultStore(root, turnId);
        await using var executorRuntime = runtimeFactory();
        var executor = new CreatorConversationTaskExecutor(
            initial,
            evidence,
            root,
            userRequest,
            classification,
            selectedSegmentId,
            configuration,
            projectStore,
            new LocalAtomicTaskReasoner(
                executorRuntime,
                configuration.QwenModel,
                configuration.TaskExecutorPromptPath,
                timeProvider),
            runtimeFactory,
            narrationSynthesizerFactory(root),
            timeProvider,
            progress,
            plan.Tasks.Count);
        var results = await new SequentialTaskProcessor(executor, taskStore)
            .ExecuteAsync(plan, cancellationToken).ConfigureAwait(false);
        var response = BuildResponse(results, executor.Snapshot.Version);
        var manifest = new CreatorConversationTurnManifest(
            turnId,
            projectId,
            initial.Version,
            executor.Snapshot.Version,
            userRequest.Trim(),
            classification.Intent,
            classification.ReferencedTime,
            executor.SelectedSegmentId,
            plan,
            planned.Provenance,
            results,
            response,
            executor.PreviewWindow,
            executor.PreviewRequested,
            started,
            timeProvider.GetUtcNow());
        await FileConversationTaskResultStore.PersistTurnAsync(root, manifest, cancellationToken)
            .ConfigureAwait(false);
        return new(manifest, executor.Snapshot, executor.SelectedSegmentId);
    }

    private static void ValidateConfiguration(CreatorConversationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(configuration.QwenModel);
        _ = PiperVoiceCatalog.Get(configuration.VoiceId);
        foreach (var path in new[]
                 {
                     configuration.PlannerPromptPath,
                     configuration.TaskExecutorPromptPath,
                     configuration.SemanticToolsPath,
                     configuration.WriterPromptPath,
                 })
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("A required versioned conversation resource is missing.", path);
            }
        }

        if (configuration.BreathingRoom < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(configuration));
        }
    }

    private static TaskPlan CreateMinimalPlan(
        string userRequest,
        ClassifiedConversationIntent classification)
    {
        var tool = classification.Intent switch
        {
            ConversationIntent.TrivialVqa => "AnalyzeSceneSemantics",
            ConversationIntent.Playback => "GenerateVideoPreview",
            ConversationIntent.Navigate => "GetScene",
            _ => throw new InvalidOperationException("This request requires local planning."),
        };
        return new(
            TaskPlanValidator.CurrentSchemaVersion,
            userRequest,
            [new("task-001", userRequest, "One bounded project result", [], [tool])]);
    }

    private static string BuildResponse(IReadOnlyList<TaskResult> results, int version)
    {
        if (results.Count == 0)
        {
            return "No project task was performed.";
        }

        var last = results[^1];
        var output = TryReadOutput(last.OutputJson);
        if (!last.Succeeded)
        {
            return $"Request stopped at {last.ToolName ?? last.TaskId}. " +
                   $"{output?.Summary ?? "The task could not be completed safely."}";
        }

        var changed = results.Any(result =>
            result.Succeeded
            && (result.ToolName == "ApplyProjectMutation" || result.ToolName == "UndoLastMutation"));
        return (output?.Summary ?? "Request complete.")
               + (changed ? $" Committed project version {version}." : string.Empty);
    }

    private static CreatorTaskOutput? TryReadOutput(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<CreatorTaskOutput>(json, ConversationJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed class CreatorConversationTaskExecutor : IAtomicTaskExecutor
{
    private static readonly Regex VoiceRegex = new(
        @"\bvoice\s*(?:number\s*)?([1-5])\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex RemoveDescriptionRegex = new(
        @"\bremove\s+(?:(?:this|the)\s+)?(?:audio\s+)?(?:description|cue|segment)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly ConversationProjectEvidence evidence;
    private readonly string projectRoot;
    private readonly string userRequest;
    private readonly ClassifiedConversationIntent classification;
    private readonly CreatorConversationConfiguration configuration;
    private readonly IProjectVersionStore projectStore;
    private readonly IAtomicTaskReasoner reasoner;
    private readonly Func<ILocalModelRuntime> runtimeFactory;
    private readonly INarrationSynthesizer narrationSynthesizer;
    private readonly TimeProvider timeProvider;
    private readonly IProgress<CreatorConversationProgress>? progress;
    private readonly int totalTasks;
    private AdSegment? proposedSegment;
    private IReadOnlyList<AdSegment>? preparedSegments;
    private string? preparedVoiceId;
    private bool spoilerChecked;
    private bool redundancyChecked;
    private bool timingChecked;
    private int completedTasks;

    public CreatorConversationTaskExecutor(
        ProjectSnapshot snapshot,
        ConversationProjectEvidence evidence,
        string projectRoot,
        string userRequest,
        ClassifiedConversationIntent classification,
        string? selectedSegmentId,
        CreatorConversationConfiguration configuration,
        IProjectVersionStore projectStore,
        IAtomicTaskReasoner reasoner,
        Func<ILocalModelRuntime> runtimeFactory,
        INarrationSynthesizer narrationSynthesizer,
        TimeProvider timeProvider,
        IProgress<CreatorConversationProgress>? progress,
        int totalTasks)
    {
        Snapshot = snapshot;
        this.evidence = evidence;
        this.projectRoot = projectRoot;
        this.userRequest = userRequest;
        this.classification = classification;
        SelectedSegmentId = selectedSegmentId;
        this.configuration = configuration;
        this.projectStore = projectStore;
        this.reasoner = reasoner;
        this.runtimeFactory = runtimeFactory;
        this.narrationSynthesizer = narrationSynthesizer;
        this.timeProvider = timeProvider;
        this.progress = progress;
        this.totalTasks = totalTasks;
    }

    public ProjectSnapshot Snapshot { get; private set; }

    public string? SelectedSegmentId { get; private set; }

    public bool PreviewRequested { get; private set; }

    public MediaInterval? PreviewWindow { get; private set; }

    public async Task<TaskResult> ExecuteAsync(AtomicTask task, CancellationToken cancellationToken)
    {
        var tool = task.AllowedTools.Single();
        progress?.Report(new(completedTasks, totalTasks, $"Starting {ReadableTool(tool)}."));
        ReasonedAtomicTask? reasoned = null;
        try
        {
            var context = BuildBoundedContext();
            reasoned = await reasoner.ReasonAsync(
                task,
                userRequest,
                context.Payload,
                context.EvidenceIds,
                cancellationToken).ConfigureAwait(false);
            if (!reasoned.Decision.CanProceed)
            {
                return Failure(task, tool, reasoned, "TASK_NOT_SUPPORTED_BY_EVIDENCE", reasoned.Decision.Summary);
            }

            var output = await ExecuteHostToolAsync(tool, reasoned.Decision, cancellationToken).ConfigureAwait(false);
            completedTasks++;
            progress?.Report(new(completedTasks, totalTasks, $"{ReadableTool(tool)} complete."));
            return new(
                task.TaskId,
                true,
                JsonSerializer.Serialize(output, ConversationJson.Options),
                reasoned.Decision.EvidenceIds,
                null,
                tool,
                reasoned.Provenance);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error) when (error is InvalidOperationException
                                           or InvalidDataException
                                           or ArgumentException
                                           or IOException
                                           or UnauthorizedAccessException)
        {
            var code = ErrorCode(error);
            return Failure(
                task,
                tool,
                reasoned,
                code,
                $"{ReadableTool(tool)} could not complete safely. {UserSafeError(code)}");
        }
    }

    private async Task<CreatorTaskOutput> ExecuteHostToolAsync(
        string tool,
        AtomicTaskDecision decision,
        CancellationToken cancellationToken) => tool switch
    {
        "FindSceneByTime" => FindScene(),
        "GetScene" => GetScene(),
        "GetTranscriptInterval" => GetTranscript(),
        "GetRelevantFrames" => GetFrames(),
        "AnalyzeSceneSemantics" or "IdentifyPeopleObjectsActions" => AnalyzeScene(decision),
        "RunVlmOcr" or "ReconcileOcrResults" => ReadOnScreenText(),
        "RewriteAdSegment" => RewriteSegment(decision),
        "CheckForSpoilers" => CheckSpoilers(),
        "CheckRedundancy" => CheckRedundancy(),
        "ValidateNarrationWindow" or "EstimateSpeechDuration" => ValidateTiming(),
        "SynthesizeNarration" => await SynthesizeAsync(decision, cancellationToken).ConfigureAwait(false),
        "MeasureRenderedNarration" => MeasureNarration(),
        "ApplyProjectMutation" => await ApplyMutationAsync(cancellationToken).ConfigureAwait(false),
        "UndoLastMutation" => await UndoAsync(cancellationToken).ConfigureAwait(false),
        "ValidateProject" => ValidateProject(),
        "GenerateVideoPreview" => RequestPreview(),
        _ => throw new InvalidOperationException("SEMANTIC_TOOL_NOT_IMPLEMENTED"),
    };

    private CreatorTaskOutput FindScene()
    {
        var scene = TargetScene();
        return new(
            $"Resolved {FormatTime(TargetTime())} to {scene.SceneId}.",
            scene.SceneId,
            SelectedSegmentId);
    }

    private CreatorTaskOutput GetScene()
    {
        if (classification.Intent == ConversationIntent.Navigate && Snapshot.AdSegments.Count > 0)
        {
            var ordered = Snapshot.AdSegments.OrderBy(segment => segment.Window.Start).ToArray();
            var current = Array.FindIndex(ordered, segment => segment.SegmentId == SelectedSegmentId);
            var backwards = userRequest.Contains("previous", StringComparison.OrdinalIgnoreCase);
            var next = backwards
                ? Math.Max(0, current <= 0 ? 0 : current - 1)
                : Math.Min(ordered.Length - 1, current < 0 ? 0 : current + 1);
            SelectedSegmentId = ordered[next].SegmentId;
        }

        var scene = TargetScene();
        var observations = evidence.Analysis.Scenes
            .Single(item => item.SceneId == scene.SceneId)
            .Observations.Count;
        return new(
            $"Selected {scene.SceneId}, containing {observations} verified visual observation{(observations == 1 ? string.Empty : "s")}.",
            scene.SceneId,
            SelectedSegmentId);
    }

    private CreatorTaskOutput GetTranscript()
    {
        var scene = TargetScene();
        var transcript = evidence.Audio.Transcript
            .Where(segment => segment.SourceInterval.Overlaps(scene.SourceInterval))
            .ToArray();
        return new(
            transcript.Length == 0
                ? $"No transcribed dialogue overlaps {scene.SceneId}."
                : $"Retrieved {transcript.Length} timestamped dialogue segment{(transcript.Length == 1 ? string.Empty : "s")} for {scene.SceneId}.",
            scene.SceneId,
            SelectedSegmentId);
    }

    private CreatorTaskOutput GetFrames()
    {
        var scene = TargetScene();
        return new(
            $"Retrieved {scene.Keyframes.Count} existing representative frame{(scene.Keyframes.Count == 1 ? string.Empty : "s")} for {scene.SceneId}; no new frames were extracted.",
            scene.SceneId,
            SelectedSegmentId);
    }

    private CreatorTaskOutput AnalyzeScene(AtomicTaskDecision decision)
    {
        var scene = TargetScene();
        if (decision.EvidenceIds.Count == 0)
        {
            throw new InvalidDataException("VISUAL_EVIDENCE_REQUIRED");
        }

        return new(decision.Summary.Trim(), scene.SceneId, SelectedSegmentId);
    }

    private CreatorTaskOutput ReadOnScreenText()
    {
        var scene = TargetScene();
        var text = evidence.VisualText.Scenes
            .Where(item => item.SceneId == scene.SceneId)
            .SelectMany(item => item.ReconciledReadings)
            .Where(item => item.SupportedText is not null && !item.RequiresAdditionalEvidence)
            .Select(item => item.SupportedText!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (text.Length == 0)
        {
            throw new InvalidOperationException("EXACT_TEXT_UNAVAILABLE");
        }

        return new(
            $"Verified on-screen text: {string.Join("; ", text)}",
            scene.SceneId,
            SelectedSegmentId);
    }

    private CreatorTaskOutput RewriteSegment(AtomicTaskDecision decision)
    {
        var current = TargetSegment();
        ArgumentException.ThrowIfNullOrWhiteSpace(decision.ProposedText);
        if (!current.EvidenceIds.ToHashSet(StringComparer.Ordinal).SetEquals(decision.EvidenceIds))
        {
            throw new InvalidDataException("REWRITE_EVIDENCE_CHANGED");
        }

        var text = decision.ProposedText.Trim();
        if (string.Equals(text, current.Text, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("REWRITE_UNCHANGED");
        }

        if (userRequest.Contains("shorten", StringComparison.OrdinalIgnoreCase)
            && WordCount(text) >= WordCount(current.Text))
        {
            throw new InvalidDataException("REWRITE_NOT_SHORTER");
        }

        proposedSegment = current with
        {
            Text = text,
            NarrationAssetRelativePath = null,
            MeasuredNarrationDuration = null,
            UserApproved = true,
            InputFingerprint = ComputeEditFingerprint(current, text, userRequest),
        };
        spoilerChecked = false;
        redundancyChecked = false;
        timingChecked = false;
        preparedSegments = null;
        preparedVoiceId = null;
        return new("A replacement description was proposed but has not been committed.", current.SceneId, current.SegmentId, text);
    }

    private CreatorTaskOutput CheckSpoilers()
    {
        var proposal = RequireProposal();
        var futureNames = evidence.Faces.Entities
            .SelectMany(entity => entity.IdentityEvidence)
            .Where(item => item.EstablishedAt > proposal.Window.Start)
            .Select(item => item.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value));
        if (futureNames.Any(name => proposal.Text.Contains(name, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("SPOILER_DETECTED");
        }

        spoilerChecked = true;
        return new("The proposal passed the time-aware established-name and spoiler check.", proposal.SceneId, proposal.SegmentId);
    }

    private CreatorTaskOutput CheckRedundancy()
    {
        var proposal = RequireProposal();
        var dialogue = evidence.Audio.Transcript
            .Where(item => item.SourceInterval.Overlaps(proposal.Window))
            .Select(item => item.Text)
            .Where(text => text.Length >= 8);
        if (dialogue.Any(text => proposal.Text.Contains(text, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("REDUNDANT_WITH_DIALOGUE");
        }

        var duplicate = Snapshot.AdSegments
            .Where(item => item.SegmentId != proposal.SegmentId)
            .Any(item => WordSimilarity(item.Text, proposal.Text) >= 0.85m);
        if (duplicate)
        {
            throw new InvalidDataException("REDUNDANT_DESCRIPTION");
        }

        redundancyChecked = true;
        return new("The proposal passed nearby-dialogue and description-redundancy checks.", proposal.SceneId, proposal.SegmentId);
    }

    private CreatorTaskOutput ValidateTiming()
    {
        var segment = proposedSegment ?? TargetSegment();
        var available = segment.Window.Duration - configuration.BreathingRoom;
        if (available <= TimeSpan.Zero
            || NarrationTimingPolicy.EstimateSpeechDuration(segment.Text) > available)
        {
            throw new InvalidDataException("WINDOW_TOO_SHORT");
        }

        if (proposedSegment is not null)
        {
            timingChecked = true;
        }

        return new(
            $"The estimated narration fits with {Math.Max(0, (int)available.TotalMilliseconds)} milliseconds available before synthesis.",
            segment.SceneId,
            segment.SegmentId);
    }

    private async Task<CreatorTaskOutput> SynthesizeAsync(
        AtomicTaskDecision decision,
        CancellationToken cancellationToken)
    {
        var requestedVoice = ResolveRequestedVoice(decision);
        if (requestedVoice is not null)
        {
            var regenerated = new List<AdSegment>(Snapshot.AdSegments.Count);
            foreach (var segment in Snapshot.AdSegments.OrderBy(item => item.Window.Start))
            {
                regenerated.Add(await SynthesizeSegmentAsync(
                    segment with { NarrationAssetRelativePath = null, MeasuredNarrationDuration = null, UserApproved = true },
                    requestedVoice,
                    cancellationToken).ConfigureAwait(false));
            }

            preparedSegments = regenerated;
            preparedVoiceId = requestedVoice;
            return new(
                $"Generated and measured {regenerated.Count} narration segment{(regenerated.Count == 1 ? string.Empty : "s")} with {PiperVoiceCatalog.Get(requestedVoice).AccessibleName}; nothing is committed yet.",
                SegmentId: SelectedSegmentId);
        }

        var proposal = RequireProposal();
        if (!spoilerChecked || !redundancyChecked || !timingChecked)
        {
            throw new InvalidOperationException("EDIT_VALIDATION_INCOMPLETE");
        }

        var rendered = await SynthesizeSegmentAsync(
            proposal,
            Snapshot.NarrationVoiceId,
            cancellationToken).ConfigureAwait(false);
        preparedSegments = Snapshot.AdSegments
            .Where(item => item.SegmentId != rendered.SegmentId)
            .Append(rendered)
            .OrderBy(item => item.Window.Start)
            .ToArray();
        preparedVoiceId = Snapshot.NarrationVoiceId;
        return new(
            $"Generated and measured replacement narration for {rendered.SegmentId}; nothing is committed yet.",
            rendered.SceneId,
            rendered.SegmentId,
            rendered.Text);
    }

    private CreatorTaskOutput MeasureNarration()
    {
        if (preparedSegments is null)
        {
            throw new InvalidOperationException("NARRATION_NOT_SYNTHESIZED");
        }

        foreach (var segment in preparedSegments)
        {
            if (segment.MeasuredNarrationDuration is not { } duration || duration > segment.Window.Duration)
            {
                throw new InvalidDataException("NARRATION_OVERRUN");
            }
        }

        return new($"Validated measured duration for {preparedSegments.Count} synthesized narration segment{(preparedSegments.Count == 1 ? string.Empty : "s")}.");
    }

    private async Task<CreatorTaskOutput> ApplyMutationAsync(CancellationToken cancellationToken)
    {
        var mutationService = new ProjectMutationService(projectStore, timeProvider);
        if (RemoveDescriptionRegex.IsMatch(userRequest) && proposedSegment is null && preparedSegments is null)
        {
            var target = TargetSegment();
            Snapshot = await mutationService.RemoveAdSegmentAsync(
                Snapshot.ProjectId,
                Snapshot.Version,
                target.SegmentId,
                $"Creator conversation explicitly removed {target.SegmentId}.",
                cancellationToken).ConfigureAwait(false);
            return new("The selected description was removed through an append-only project mutation.", target.SceneId, target.SegmentId, ProjectVersion: Snapshot.Version);
        }

        if (preparedSegments is null || preparedVoiceId is null)
        {
            throw new InvalidOperationException("MUTATION_HAS_NO_VALIDATED_NARRATION");
        }

        var availableNarration = preparedSegments
            .Select(item => item.NarrationAssetRelativePath)
            .Where(path => path is not null && File.Exists(ResolveProjectArtifact(path)))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        Snapshot = await mutationService.ReplaceNarrationAsync(
            Snapshot.ProjectId,
            Snapshot.Version,
            preparedSegments,
            preparedVoiceId,
            evidence.AvailableEvidenceIds,
            availableNarration,
            proposedSegment is null
                ? $"Creator conversation regenerated narration with {preparedVoiceId}."
                : $"Creator conversation replaced {proposedSegment.SegmentId} after evidence, spoiler, redundancy, timing, and synthesis validation.",
            cancellationToken).ConfigureAwait(false);
        return new(
            proposedSegment is null
                ? "The newly measured narration voice was committed."
                : "The validated description and measured narration were committed.",
            proposedSegment?.SceneId,
            proposedSegment?.SegmentId ?? SelectedSegmentId,
            proposedSegment?.Text,
            Snapshot.Version);
    }

    private async Task<CreatorTaskOutput> UndoAsync(CancellationToken cancellationToken)
    {
        Snapshot = await new ProjectMutationService(projectStore, timeProvider)
            .UndoAsync(Snapshot.ProjectId, Snapshot.Version, cancellationToken).ConfigureAwait(false);
        proposedSegment = null;
        preparedSegments = null;
        preparedVoiceId = null;
        if (SelectedSegmentId is not null
            && Snapshot.AdSegments.All(item => item.SegmentId != SelectedSegmentId))
        {
            SelectedSegmentId = Snapshot.AdSegments.OrderBy(item => item.Window.Start).FirstOrDefault()?.SegmentId;
        }

        return new(
            $"Restored project version {Snapshot.RestoredFromVersion} as compensating version {Snapshot.Version}; audit history was preserved.",
            SegmentId: SelectedSegmentId,
            ProjectVersion: Snapshot.Version);
    }

    private CreatorTaskOutput ValidateProject()
    {
        var assets = Snapshot.AdSegments
            .Select(item => item.NarrationAssetRelativePath)
            .Where(path => path is not null && File.Exists(ResolveProjectArtifact(path)))
            .Cast<string>()
            .ToHashSet(StringComparer.Ordinal);
        var issues = ProjectValidator.Validate(
            Snapshot.MediaDuration,
            Snapshot.AdSegments,
            evidence.AvailableEvidenceIds,
            assets);
        var blockers = issues.Count(item => item.Blocking);
        return new(
            blockers == 0
                ? $"Project validation passed with {issues.Count} nonblocking issue{(issues.Count == 1 ? string.Empty : "s")}."
                : $"Project validation found {blockers} blocking issue{(blockers == 1 ? string.Empty : "s")}.",
            SegmentId: SelectedSegmentId,
            ProjectVersion: Snapshot.Version,
            ValidationIssues: issues);
    }

    private CreatorTaskOutput RequestPreview()
    {
        PreviewRequested = true;
        if (!userRequest.Contains("full", StringComparison.OrdinalIgnoreCase))
        {
            var segment = TargetSegment();
            var start = segment.Window.Start - TimeSpan.FromSeconds(5);
            var end = segment.Window.End + TimeSpan.FromSeconds(5);
            PreviewWindow = new(
                start < TimeSpan.Zero ? TimeSpan.Zero : start,
                end > Snapshot.MediaDuration ? Snapshot.MediaDuration : end);
        }

        return new(
            PreviewWindow is null
                ? "The committed full-draft preview was requested."
                : $"A committed-state preview around {SelectedSegmentId} was requested.",
            TargetScene().SceneId,
            SelectedSegmentId,
            ProjectVersion: Snapshot.Version,
            PreviewWindow: PreviewWindow);
    }

    private async Task<AdSegment> SynthesizeSegmentAsync(
        AdSegment segment,
        string voiceId,
        CancellationToken cancellationToken)
    {
        await using var fitRuntime = runtimeFactory();
        var fitted = await new NarrationAutoFitCoordinator(
                narrationSynthesizer,
                fitRuntime,
                configuration.QwenModel,
                configuration.WriterPromptPath)
            .FitAsync(segment, voiceId, configuration.BreathingRoom, cancellationToken)
            .ConfigureAwait(false);
        if (fitted.RequiresUserReview || !fitted.Fit.Fits)
        {
            throw new InvalidOperationException("NARRATION_REQUIRES_USER_REVIEW");
        }

        var audio = fitted.Segment.NarrationAssetRelativePath
                    ?? throw new InvalidDataException("NARRATION_MISSING");
        var absolute = Path.IsPathRooted(audio)
            ? Path.GetFullPath(audio)
            : Path.GetFullPath(Path.Combine(projectRoot, audio));
        if (!IsInsideProject(absolute) || !File.Exists(absolute))
        {
            throw new InvalidDataException("NARRATION_OUTSIDE_PROJECT");
        }

        var measured = WaveDurationReader.Measure(absolute);
        if (measured != fitted.Segment.MeasuredNarrationDuration
            || measured > segment.Window.Duration - configuration.BreathingRoom)
        {
            throw new InvalidDataException("NARRATION_DURATION_MISMATCH");
        }

        return fitted.Segment with
        {
            NarrationAssetRelativePath = Path.GetRelativePath(projectRoot, absolute).Replace('\\', '/'),
            MeasuredNarrationDuration = measured,
            UserApproved = true,
        };
    }

    private (object Payload, IReadOnlySet<string> EvidenceIds) BuildBoundedContext()
    {
        var scene = TargetScene();
        var analysis = evidence.Analysis.Scenes.Single(item => item.SceneId == scene.SceneId);
        var text = evidence.VisualText.Scenes.Where(item => item.SceneId == scene.SceneId).ToArray();
        var transcript = evidence.Audio.Transcript
            .Where(item => item.SourceInterval.Overlaps(scene.SourceInterval))
            .Select(item => new
            {
                item.SegmentId,
                startMilliseconds = (long)item.SourceInterval.Start.TotalMilliseconds,
                endMilliseconds = (long)item.SourceInterval.End.TotalMilliseconds,
                item.Text,
            })
            .ToArray();
        var segment = TryTargetSegment();
        var ids = scene.Keyframes.Select(item => item.EvidenceId)
            .Concat(segment?.EvidenceIds ?? [])
            .ToHashSet(StringComparer.Ordinal);
        var payload = new
        {
            project = new
            {
                Snapshot.ProjectId,
                Snapshot.Version,
                mediaDurationMilliseconds = (long)Snapshot.MediaDuration.TotalMilliseconds,
                selectedSegmentId = SelectedSegmentId,
                referencedTimeMilliseconds = classification.ReferencedTime is null
                    ? null
                    : (long?)classification.ReferencedTime.Value.TotalMilliseconds,
            },
            scene = new
            {
                scene.SceneId,
                startMilliseconds = (long)scene.SourceInterval.Start.TotalMilliseconds,
                endMilliseconds = (long)scene.SourceInterval.End.TotalMilliseconds,
                observations = analysis.Observations.Select(item => new
                {
                    item.ObjectiveDescription,
                    item.EvidenceIds,
                    item.Certainty,
                    item.Importance,
                    item.AlreadyConveyedBySourceAudio,
                }),
                onScreenText = text.SelectMany(item => item.ReconciledReadings).Select(item => new
                {
                    item.SupportedText,
                    item.Confidence,
                    item.RequiresAdditionalEvidence,
                    item.EvidenceIds,
                }),
                establishedEntities = evidence.Faces.Entities.Select(item => new
                {
                    item.EntityId,
                    displayName = item.DisplayNameAt(scene.SourceInterval.Start),
                }),
                transcript,
            },
            committedDescription = segment is null ? null : new
            {
                segment.SegmentId,
                segment.SceneId,
                startMilliseconds = (long)segment.Window.Start.TotalMilliseconds,
                endMilliseconds = (long)segment.Window.End.TotalMilliseconds,
                segment.Text,
                segment.EvidenceIds,
                Snapshot.NarrationVoiceId,
            },
            pendingProposal = proposedSegment?.Text,
            hostState = new
            {
                spoilerChecked,
                redundancyChecked,
                timingChecked,
                narrationPrepared = preparedSegments is not null,
            },
        };
        return (payload, ids);
    }

    private VisualScene TargetScene()
    {
        var segment = TryTargetSegment();
        if (segment is not null)
        {
            return evidence.Visual.Scenes.Single(scene => scene.SceneId == segment.SceneId);
        }

        var time = TargetTime();
        return evidence.Visual.Scenes.FirstOrDefault(scene =>
                   time >= scene.SourceInterval.Start && time < scene.SourceInterval.End)
               ?? evidence.Visual.Scenes.LastOrDefault(scene => time == scene.SourceInterval.End)
               ?? throw new InvalidOperationException("SCENE_NOT_FOUND");
    }

    private TimeSpan TargetTime()
    {
        if (classification.ReferencedTime is { } referenced)
        {
            if (referenced < TimeSpan.Zero || referenced > Snapshot.MediaDuration)
            {
                throw new ArgumentOutOfRangeException(nameof(classification), "INVALID_TIME");
            }

            return referenced;
        }

        var selected = TryTargetSegment();
        return selected is null
            ? TimeSpan.Zero
            : selected.Window.Start + TimeSpan.FromTicks(selected.Window.Duration.Ticks / 2);
    }

    private AdSegment TargetSegment() =>
        TryTargetSegment() ?? throw new InvalidOperationException("DESCRIPTION_TARGET_REQUIRED");

    private AdSegment? TryTargetSegment()
    {
        if (SelectedSegmentId is not null)
        {
            var selected = Snapshot.AdSegments.SingleOrDefault(item => item.SegmentId == SelectedSegmentId);
            if (selected is not null)
            {
                return selected;
            }
        }

        if (classification.ReferencedTime is not { } time || Snapshot.AdSegments.Count == 0)
        {
            return null;
        }

        var target = Snapshot.AdSegments.FirstOrDefault(item =>
                         time >= item.Window.Start && time <= item.Window.End)
                     ?? Snapshot.AdSegments.MinBy(item =>
                         Math.Abs((item.Window.Start + TimeSpan.FromTicks(item.Window.Duration.Ticks / 2) - time).Ticks));
        SelectedSegmentId = target?.SegmentId;
        return target;
    }

    private AdSegment RequireProposal() =>
        proposedSegment ?? throw new InvalidOperationException("NO_EDIT_PROPOSAL");

    private string? ResolveRequestedVoice(AtomicTaskDecision decision)
    {
        if (classification.Intent != ConversationIntent.ChangeVoice)
        {
            return null;
        }

        var match = VoiceRegex.Match(userRequest);
        var explicitVoice = match.Success ? $"voice-{match.Groups[1].Value}" : null;
        if (explicitVoice is null)
        {
            throw new InvalidOperationException("VOICE_SELECTION_REQUIRED");
        }

        if (decision.RequestedVoiceId is not null
            && !string.Equals(decision.RequestedVoiceId, explicitVoice, StringComparison.Ordinal))
        {
            throw new InvalidDataException("VOICE_SELECTION_MISMATCH");
        }

        _ = PiperVoiceCatalog.Get(explicitVoice);
        return explicitVoice;
    }

    private string ResolveProjectArtifact(string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
        {
            return string.Empty;
        }

        var path = Path.GetFullPath(Path.Combine(projectRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        return IsInsideProject(path) ? path : string.Empty;
    }

    private bool IsInsideProject(string path) =>
        path.StartsWith(projectRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static TaskResult Failure(
        AtomicTask task,
        string tool,
        ReasonedAtomicTask? reasoned,
        string code,
        string summary) => new(
            task.TaskId,
            false,
            JsonSerializer.Serialize(new CreatorTaskOutput(summary), ConversationJson.Options),
            reasoned?.Decision.EvidenceIds ?? [],
            code,
            tool,
            reasoned?.Provenance);

    private static string ErrorCode(Exception error)
    {
        var candidate = error.Message.Split(':', 2)[0].Trim();
        return candidate.Length is > 0 and <= 80
               && candidate.All(character => char.IsUpper(character) || char.IsDigit(character) || character == '_')
            ? candidate
            : error switch
            {
                InvalidDataException => "PROJECT_EVIDENCE_INVALID",
                IOException => "PROJECT_IO_FAILED",
                UnauthorizedAccessException => "PROJECT_ACCESS_DENIED",
                _ => "TASK_VALIDATION_FAILED",
            };
    }

    private static string UserSafeError(string code) => code switch
    {
        "DESCRIPTION_TARGET_REQUIRED" => "Select a description or include a timestamp.",
        "EXACT_TEXT_UNAVAILABLE" => "Verified exact on-screen text is unavailable for this scene.",
        "WINDOW_TOO_SHORT" => "The proposed wording does not leave enough narration time.",
        "EDIT_VALIDATION_INCOMPLETE" => "Spoiler, redundancy, and timing checks must run before synthesis.",
        "MUTATION_HAS_NO_VALIDATED_NARRATION" => "No measured narration is ready to commit.",
        "NOTHING_TO_UNDO" => "There is no earlier project version to restore.",
        _ => $"Validation code: {code}.",
    };

    private static string ReadableTool(string tool) => tool switch
    {
        "FindSceneByTime" => "finding the requested scene",
        "GetScene" => "retrieving the scene",
        "GetTranscriptInterval" => "retrieving nearby dialogue",
        "GetRelevantFrames" => "retrieving verified frames",
        "AnalyzeSceneSemantics" => "answering from verified scene evidence",
        "IdentifyPeopleObjectsActions" => "checking visible people, objects, and actions",
        "RunVlmOcr" or "ReconcileOcrResults" => "checking verified on-screen text",
        "RewriteAdSegment" => "proposing the description edit",
        "CheckForSpoilers" => "checking for spoilers",
        "CheckRedundancy" => "checking redundancy",
        "ValidateNarrationWindow" or "EstimateSpeechDuration" => "checking narration time",
        "SynthesizeNarration" => "generating measured narration",
        "MeasureRenderedNarration" => "verifying rendered narration",
        "ApplyProjectMutation" => "committing the validated project change",
        "UndoLastMutation" => "restoring the prior project version",
        "ValidateProject" => "validating the project",
        "GenerateVideoPreview" => "preparing the preview request",
        _ => "performing the project task",
    };

    private static int WordCount(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    private static decimal WordSimilarity(string left, string right)
    {
        var first = Words(left);
        var second = Words(right);
        if (first.Count == 0 || second.Count == 0)
        {
            return 0;
        }

        var intersection = first.Intersect(second, StringComparer.OrdinalIgnoreCase).Count();
        var union = first.Union(second, StringComparer.OrdinalIgnoreCase).Count();
        return union == 0 ? 0 : intersection / (decimal)union;
    }

    private static HashSet<string> Words(string value) => Regex
        .Matches(value, @"[\p{L}\p{N}']+", RegexOptions.CultureInvariant)
        .Select(match => match.Value)
        .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string ComputeEditFingerprint(AdSegment segment, string text, string request) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"creator-edit-v1\n{segment.InputFingerprint}\n{text}\n{request.Trim()}")));

    private static string FormatTime(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";
}

internal sealed record ConversationProjectEvidence(
    VisualIndexManifest Visual,
    AudioAnalysisCompletionManifest Audio,
    VisualUnderstandingManifest Analysis,
    VisualTextAnalysisManifest VisualText,
    FaceContinuityManifest Faces,
    ISet<string> AvailableEvidenceIds)
{
    public static async Task<ConversationProjectEvidence> LoadAsync(
        Guid projectId,
        string projectDirectory,
        ProjectSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var visual = await LoadManifestAsync<VisualIndexManifest>(projectDirectory, "visual-index.json", cancellationToken)
            .ConfigureAwait(false);
        var audio = await LoadManifestAsync<AudioAnalysisCompletionManifest>(projectDirectory, "audio-analysis-complete.json", cancellationToken)
            .ConfigureAwait(false);
        var analysis = await LoadManifestAsync<VisualUnderstandingManifest>(projectDirectory, "visual-analysis-complete.json", cancellationToken)
            .ConfigureAwait(false);
        var text = await LoadManifestAsync<VisualTextAnalysisManifest>(projectDirectory, "visual-text-analysis.json", cancellationToken)
            .ConfigureAwait(false);
        var faces = await LoadManifestAsync<FaceContinuityManifest>(projectDirectory, "face-continuity.json", cancellationToken)
            .ConfigureAwait(false);
        var sourceHashes = new[]
        {
            visual.SourceSha256,
            audio.SourceSha256,
            analysis.SourceSha256,
            text.SourceSha256,
            faces.SourceSha256,
        };
        if (snapshot.ProjectId != projectId
            || visual.ProjectId != projectId
            || audio.ProjectId != projectId
            || analysis.ProjectId != projectId
            || text.ProjectId != projectId
            || faces.ProjectId != projectId
            || sourceHashes.Any(hash => !string.Equals(hash, snapshot.SourceSha256, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException("Conversation evidence does not share the committed immutable source.");
        }

        var ids = visual.Scenes.SelectMany(scene => scene.Keyframes)
            .Select(frame => frame.EvidenceId)
            .ToHashSet(StringComparer.Ordinal);
        if (analysis.Scenes.SelectMany(scene => scene.Observations)
            .SelectMany(observation => observation.EvidenceIds)
            .Any(id => !ids.Contains(id)))
        {
            throw new InvalidDataException("Conversation scene analysis cites unavailable frame evidence.");
        }

        return new(visual, audio, analysis, text, faces, ids);
    }

    private static async Task<T> LoadManifestAsync<T>(
        string root,
        string fileName,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(root, "manifests", fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException("A required verified project manifest is missing.", path);
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, ConversationJson.Options, cancellationToken)
                   .ConfigureAwait(false)
               ?? throw new InvalidDataException($"Project manifest is empty: {fileName}");
    }
}

internal static class ConversationJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
}
