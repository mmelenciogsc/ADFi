using ADFi.Conversation;
using ADFi.Core;
using ADFi.Media;
using System.Text.Json;

namespace ADFi.Core.Tests;

public sealed class InteractiveVideoTests
{
    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?t=17", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/shorts/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    public void YoutubeUrlsAreReducedToOrdinaryVideoIdentifiers(string url, string expected)
    {
        Assert.True(YoutubeVideoUrl.TryParseVideoId(url, out var actual));
        Assert.Equal(expected, actual);
        Assert.Equal(
            $"https://www.youtube-nocookie.com/embed/{expected}?enablejsapi=1&controls=1&playsinline=1",
            YoutubeVideoUrl.CreatePrivacyEnhancedEmbedUri(actual).AbsoluteUri);
    }

    [Theory]
    [InlineData("http://www.youtube.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://example.com/watch?v=dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=too-short")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQmore")]
    public void UnsupportedYoutubeInputsAreRejected(string value)
    {
        Assert.False(YoutubeVideoUrl.TryParseVideoId(value, out _));
    }

    [Fact]
    public void PastedYoutubeSourceRequiresAUrlRatherThanABareIdentifier()
    {
        Assert.Throws<ArgumentException>(() => InteractiveVideoSource.YouTube("dQw4w9WgXcQ"));
    }

    [Theory]
    [InlineData("What does that sign say?", MomentQuestionIntent.ExactText, 1, 0)]
    [InlineData("What changed?", MomentQuestionIntent.Change, 3, 1500)]
    [InlineData("What's happening?", MomentQuestionIntent.Action, 3, 1500)]
    [InlineData("Who is speaking?", MomentQuestionIntent.Speaker, 1, 0)]
    [InlineData("Who is she?", MomentQuestionIntent.PersonContinuity, 1, 0)]
    [InlineData("What color is the car?", MomentQuestionIntent.StaticVisual, 1, 0)]
    public void QuestionIntentRequestsOnlyMinimumBoundedEvidence(
        string question,
        MomentQuestionIntent intent,
        int frames,
        int span)
    {
        var requirement = MomentQuestionClassifier.Classify(question);

        Assert.Equal(intent, requirement.Intent);
        Assert.Equal(frames, requirement.MaximumImageCount);
        Assert.Equal(span, requirement.TemporalSpanMs);
    }

    [Fact]
    public void WhatDidIMissIsBoundedEvenAfterLongPlayback()
    {
        var requirement = MomentQuestionClassifier.Classify(
            "What did I miss?",
            elapsedSinceLastConfirmedPlaybackMs: 120_000);

        Assert.Equal(MomentQuestionIntent.RecentContext, requirement.Intent);
        Assert.Equal(10_000, requirement.TemporalSpanMs);
        Assert.Equal(3, requirement.MaximumImageCount);
    }

    [Theory]
    [InlineData(SubscriptionPlan.Community, true, true, false)]
    [InlineData(SubscriptionPlan.Creator, true, true, true)]
    [InlineData(SubscriptionPlan.None, true, false, false)]
    [InlineData(SubscriptionPlan.Community, false, false, false)]
    public void ViewerAndLocalProviderEntitlementsRemainDistinct(
        SubscriptionPlan plan,
        bool verified,
        bool viewer,
        bool localFirst)
    {
        Assert.Equal(viewer, ViewerVqaAccessPolicy.CanUseViewer(plan, verified));
        Assert.Equal(localFirst, ViewerVqaAccessPolicy.CanUseLocalFirst(plan, verified));
    }

    [Fact]
    public void ViewerModeCannotCallMutationTools()
    {
        Assert.False(ViewerVqaAccessPolicy.IsViewerToolAllowed("RewriteAdSegment"));
        Assert.False(ViewerVqaAccessPolicy.IsViewerToolAllowed("ApplyProjectMutation"));
        Assert.False(ViewerVqaAccessPolicy.IsViewerToolAllowed("RenderFinalVideo"));
        Assert.True(ViewerVqaAccessPolicy.IsViewerToolAllowed("GetScene"));
        Assert.Equal("Viewer — questions do not change this project.", ViewerVqaAccessPolicy.ModeAnnouncement(false));
    }

    [Fact]
    public void ProviderRequestRejectsLocalPathDisclosure()
    {
        var request = ValidRequest() with { SourceReference = @"C:\private\family-video.mp4" };

        Assert.Throws<InvalidOperationException>(() => MomentVqaRequestValidator.Validate(request));
    }

    [Fact]
    public void PrivatePathsAreExcludedFromSerializedContext()
    {
        var source = InteractiveVideoSource.LocalFile(@"C:\private\family-video.mp4");
        var request = ValidRequest() with
        {
            Evidence =
            [
                ImageEvidence(1, 1000) with { PrivateEphemeralPath = @"C:\private\frame.png" },
            ],
        };

        var json = JsonSerializer.Serialize(new { source, request });

        Assert.DoesNotContain("family-video", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("frame.png", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PrivateLocalPath", json, StringComparison.Ordinal);
        Assert.DoesNotContain("PrivateEphemeralPath", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderRequestRejectsCaptureCountAboveHardCap()
    {
        var evidence = Enumerable.Range(1, 4)
            .Select(index => ImageEvidence(index, index * 100L))
            .ToArray();
        var request = ValidRequest() with { Evidence = evidence };

        Assert.Throws<InvalidOperationException>(() => MomentVqaRequestValidator.Validate(request));
    }

    [Fact]
    public void ProductionYoutubeCapturePolicyDefaultsOff()
    {
        var context = YoutubeContext(explicitUserAsk: true);

        Assert.Equal(
            CaptureComplianceState.YoutubeCaptureDisabled,
            YoutubeEphemeralCapturePolicy.ProductionDefault.Validate(context));
    }

    [Fact]
    public void YoutubeCaptureAlwaysRequiresExplicitUserAsk()
    {
        var context = YoutubeContext(explicitUserAsk: false);

        Assert.Throws<InvalidOperationException>(() =>
            YoutubeEphemeralCapturePolicy.ProductionDefault.Validate(context));
    }

    [Fact]
    public async Task PrivateEvidenceStoreDeletesRequestAndSessionContent()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-viewer-test-{Guid.NewGuid():N}");
        var sessionId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        using var store = new PrivateEphemeralEvidenceStore(root);
        var requestDirectory = store.CreateRequestDirectory(sessionId, requestId);
        await File.WriteAllTextAsync(
            Path.Combine(requestDirectory, "frame.png"),
            "private",
            TestContext.Current.CancellationToken);

        await store.DeleteRequestAsync(sessionId, requestId, TestContext.Current.CancellationToken);

        Assert.False(Directory.Exists(requestDirectory));
        await store.DeleteSessionAsync(sessionId, TestContext.Current.CancellationToken);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DisabledYoutubeCaptureDoesNotTouchThePlayerRegion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-youtube-test-{Guid.NewGuid():N}");
        using var store = new PrivateEphemeralEvidenceStore(root);
        var capture = new FakePlayerRegionCapture();
        var provider = new BoundedYoutubeDisplayEvidenceProvider(
            store,
            capture,
            YoutubeEphemeralCapturePolicy.ProductionDefault);

        var evidence = await provider.AcquireAsync(
            YoutubeContext(explicitUserAsk: true),
            TestContext.Current.CancellationToken);

        Assert.Empty(evidence);
        Assert.Equal(0, capture.Count);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task EnabledYoutubeCaptureAllowsOneBoundedImagePerRequest()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-youtube-test-{Guid.NewGuid():N}");
        var context = YoutubeContext(explicitUserAsk: true);
        using var store = new PrivateEphemeralEvidenceStore(root);
        var capture = new FakePlayerRegionCapture();
        var provider = new BoundedYoutubeDisplayEvidenceProvider(
            store,
            capture,
            YoutubeEphemeralCapturePolicy.ProductionDefault with { EnabledAfterComplianceApproval = true });

        var evidence = await provider.AcquireAsync(context, TestContext.Current.CancellationToken);

        var item = Assert.Single(evidence);
        Assert.True(item.Ephemeral);
        Assert.Equal(MomentEvidenceBasis.NewYoutubeEphemeralCapture, item.Basis);
        Assert.True(File.Exists(item.PrivateEphemeralPath));
        Assert.Equal(1, capture.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.AcquireAsync(
            context,
            TestContext.Current.CancellationToken));
        await store.DeleteSessionAsync(context.SessionId, TestContext.Current.CancellationToken);
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PauseAskAnswerDoesNotAutoResumeAndResumeReturnsToExactCheckpoint()
    {
        var playback = new FakePlayback(InteractivePlaybackState.Playing, 197_250);
        var evidence = new FakeEvidenceProvider(InteractiveVideoSourceType.LocalFile);
        var provider = new FakeVqaProvider();
        var lifecycle = new FakeEvidenceLifecycle();
        await using var session = new InteractiveVideoSessionCoordinator(
            InteractiveVideoSource.LocalFile(Path.GetTempFileName()),
            playback,
            new FixedContextResolver(existingCanAnswer: false),
            [evidence],
            [provider],
            lifecycle,
            TimeProvider.System);

        var checkpoint = await session.PauseForQuestionAsync(
            "PlayButton",
            TestContext.Current.CancellationToken);
        var answer = await session.AskAsync(
            "What's happening?",
            VqaProviderKind.ChatGpt,
            TestContext.Current.CancellationToken);

        Assert.Equal(197_250, checkpoint.CurrentTimestampMs);
        Assert.Equal(InteractivePlaybackState.Paused, playback.State);
        Assert.Equal("A verified answer.", answer.Answer);
        Assert.Equal(1, evidence.AcquisitionCount);
        Assert.Equal(3, provider.LastRequest!.Evidence.Count);

        await session.ResumeAsync(TestContext.Current.CancellationToken);

        Assert.Equal(InteractivePlaybackState.Playing, playback.State);
        Assert.Equal(197_250, playback.TimestampMs);
        Assert.Equal(1, lifecycle.DeletedRequestCount);
        Assert.Null(session.ActiveCheckpoint);
    }

    [Fact]
    public async Task AdfiPublicationReusesStoredEvidenceWithoutNewCapture()
    {
        var playback = new FakePlayback(InteractivePlaybackState.Paused, 8000);
        var evidence = new FakeEvidenceProvider(InteractiveVideoSourceType.AdfiPublication);
        var provider = new FakeVqaProvider();
        var lifecycle = new FakeEvidenceLifecycle();
        await using var session = new InteractiveVideoSessionCoordinator(
            InteractiveVideoSource.AdfiPublication(Guid.NewGuid(), Guid.NewGuid(), "dQw4w9WgXcQ"),
            playback,
            new FixedContextResolver(existingCanAnswer: true),
            [evidence],
            [provider],
            lifecycle,
            TimeProvider.System);
        await session.PauseForQuestionAsync("player", TestContext.Current.CancellationToken);

        await session.AskAsync(
            "What does the sign say?",
            VqaProviderKind.ChatGpt,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, evidence.AcquisitionCount);
        Assert.Contains(provider.LastRequest!.Evidence, item => item.Basis == MomentEvidenceBasis.ExistingAdfiAnalysis);
    }

    [Fact]
    public async Task ProviderFailureDeletesEphemeralEvidence()
    {
        var lifecycle = new FakeEvidenceLifecycle();
        await using var session = new InteractiveVideoSessionCoordinator(
            InteractiveVideoSource.LocalFile(Path.GetTempFileName()),
            new FakePlayback(InteractivePlaybackState.Paused, 500),
            new FixedContextResolver(existingCanAnswer: false),
            [new FakeEvidenceProvider(InteractiveVideoSourceType.LocalFile)],
            [new FakeVqaProvider(throwOnAsk: true)],
            lifecycle,
            TimeProvider.System);
        await session.PauseForQuestionAsync("player", TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => session.AskAsync(
            "What is happening?",
            VqaProviderKind.ChatGpt,
            TestContext.Current.CancellationToken));

        Assert.Equal(1, lifecycle.DeletedRequestCount);
    }

    private static MomentVqaRequest ValidRequest() => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        InteractiveVideoSourceType.LocalFile,
        $"local:{Guid.NewGuid():N}",
        1000,
        "What is happening?",
        MomentQuestionIntent.Action,
        [ImageEvidence(1, 1000)],
        null,
        [],
        [],
        [],
        VqaProviderKind.ChatGpt,
        EvidenceRetentionPolicy.DeleteAfterRequest,
        CaptureComplianceState.AllowedLocalFile);

    private static MomentVqaEvidence ImageEvidence(int index, long timestamp) => new(
        $"frame-{index}",
        MomentEvidenceKind.EphemeralImage,
        MomentEvidenceBasis.NewLocalFrameAnalysis,
        timestamp,
        timestamp,
        null,
        $"private-{index}.png",
        640,
        360,
        1m,
        true);

    private static EvidenceAcquisitionContext YoutubeContext(bool explicitUserAsk) => new(
        Guid.NewGuid(),
        Guid.NewGuid(),
        InteractiveVideoSource.YouTube("https://youtu.be/dQw4w9WgXcQ"),
        1000,
        MomentQuestionClassifier.Classify("What changed?"),
        explicitUserAsk,
        Path.GetTempPath(),
        DateTimeOffset.UtcNow);

    private sealed class FakePlayback(InteractivePlaybackState initialState, long timestampMs) : IPlaybackController
    {
        public InteractivePlaybackState State { get; private set; } = initialState;
        public long TimestampMs { get; private set; } = timestampMs;

        public Task<PlaybackSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new PlaybackSnapshot(State, TimestampMs, 1m, null));

        public Task PauseAsync(CancellationToken cancellationToken)
        {
            State = InteractivePlaybackState.Paused;
            return Task.CompletedTask;
        }

        public Task SeekAsync(long timestamp, CancellationToken cancellationToken)
        {
            TimestampMs = timestamp;
            return Task.CompletedTask;
        }

        public Task PlayAsync(CancellationToken cancellationToken)
        {
            State = InteractivePlaybackState.Playing;
            return Task.CompletedTask;
        }
    }

    private sealed class FixedContextResolver(bool existingCanAnswer) : IVideoContextResolver
    {
        public Task<VideoContextResolution> ResolveAsync(
            InteractiveVideoSource source,
            long currentTimestampMs,
            MomentEvidenceRequirement requirement,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<MomentVqaEvidence> evidence = existingCanAnswer
                ?
                [
                    new(
                        "stored-scene",
                        MomentEvidenceKind.PersistedSceneAnalysis,
                        MomentEvidenceBasis.ExistingAdfiAnalysis,
                        7000,
                        9000,
                        "A red EXIT sign is centered above the door.",
                        null,
                        null,
                        null,
                        0.95m,
                        false),
                ]
                : [];
            return Task.FromResult(new VideoContextResolution(
                existingCanAnswer ? new("scene-1", 7000, 9000, "A red EXIT sign.", [], 0.95m) : null,
                evidence,
                [],
                existingCanAnswer ? ["EXIT"] : [],
                [],
                existingCanAnswer));
        }
    }

    private sealed class FakeEvidenceProvider(InteractiveVideoSourceType sourceType) : IVideoEvidenceProvider
    {
        public int AcquisitionCount { get; private set; }

        public bool Supports(InteractiveVideoSourceType candidate) => candidate == sourceType;

        public Task<IReadOnlyList<MomentVqaEvidence>> AcquireAsync(
            EvidenceAcquisitionContext context,
            CancellationToken cancellationToken)
        {
            AcquisitionCount++;
            IReadOnlyList<MomentVqaEvidence> evidence = Enumerable.Range(1, context.Requirement.MaximumImageCount)
                .Select(index => ImageEvidence(index, context.CurrentTimestampMs - 750 + ((index - 1) * 750)))
                .ToArray();
            return Task.FromResult(evidence);
        }
    }

    private sealed class FakeVqaProvider(bool throwOnAsk = false) : IVqaProvider
    {
        public VqaProviderKind Kind => VqaProviderKind.ChatGpt;
        public MomentVqaRequest? LastRequest { get; private set; }

        public Task<VqaProviderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new VqaProviderAvailability(
                Kind,
                VqaProviderAvailabilityState.PlanUsageAvailable,
                true,
                "ChatGPT plan usage available.",
                null));

        public Task<MomentVqaResult> AskAsync(MomentVqaRequest request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (throwOnAsk)
            {
                throw new InvalidOperationException("Provider failed.");
            }

            return Task.FromResult(new MomentVqaResult(
                "A verified answer.",
                request.Evidence.Select(item => item.Basis).Distinct().ToArray(),
                0.9m,
                [],
                null,
                Kind,
                DateTimeOffset.UtcNow));
        }
    }

    private sealed class FakeEvidenceLifecycle : IEphemeralEvidenceLifecycle
    {
        public int DeletedRequestCount { get; private set; }
        public int DeletedSessionCount { get; private set; }

        public string GetSessionDirectory(Guid sessionId) => Path.GetTempPath();

        public Task DeleteRequestAsync(Guid sessionId, Guid requestId, CancellationToken cancellationToken)
        {
            DeletedRequestCount++;
            return Task.CompletedTask;
        }

        public Task DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken)
        {
            DeletedSessionCount++;
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlayerRegionCapture : IPlayerRegionCapture
    {
        public int Count { get; private set; }

        public Task<CapturedPlayerRegion> CapturePausedVideoRegionAsync(
            int maximumImageDimension,
            CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new CapturedPlayerRegion([137, 80, 78, 71], 640, 360));
        }
    }
}
