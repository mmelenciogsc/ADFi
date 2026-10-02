using ADFi.Conversation;
using ADFi.Core;
using ADFi.Infrastructure;

namespace ADFi.Core.Tests;

public sealed class ProjectMutationTests
{
    [Fact]
    public async Task MutationAndUndoAppendVersionsWithoutErasingHistory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-versions-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        try
        {
            var store = new FileProjectVersionStore(root);
            await store.InitializeAsync(
                new(projectId, 1, new string('A', 64), TimeSpan.FromMinutes(1), [], "voice-1", DateTimeOffset.UnixEpoch, null),
                TestContext.Current.CancellationToken);
            var service = new ProjectMutationService(store, TimeProvider.System);

            var changed = await service.SelectVoiceAsync(
                projectId, 1, "voice-3", "User selected voice 3.", TestContext.Current.CancellationToken);
            var undone = await service.UndoAsync(projectId, 2, TestContext.Current.CancellationToken);
            var history = await store.LoadHistoryAsync(projectId, TestContext.Current.CancellationToken);

            Assert.Equal(2, changed.Version);
            Assert.Equal("voice-3", changed.NarrationVoiceId);
            Assert.Equal(3, undone.Version);
            Assert.Equal("voice-1", undone.NarrationVoiceId);
            Assert.Equal(1, undone.RestoredFromVersion);
            Assert.Equal([1, 2, 3], history.Select(snapshot => snapshot.Version));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(root, projectId.ToString("D"), "audit", "mutations")).Length);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task StaleMutationIsRejectedByOptimisticVersionCheck()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-versions-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        try
        {
            var store = new FileProjectVersionStore(root);
            await store.InitializeAsync(
                new(projectId, 1, new string('B', 64), TimeSpan.FromMinutes(1), [], "voice-1", DateTimeOffset.UnixEpoch, null),
                TestContext.Current.CancellationToken);
            var service = new ProjectMutationService(store, TimeProvider.System);
            _ = await service.SelectVoiceAsync(
                projectId, 1, "voice-2", "First edit.", TestContext.Current.CancellationToken);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => service.SelectVoiceAsync(
                projectId, 1, "voice-4", "Stale edit.", TestContext.Current.CancellationToken));
            Assert.Equal("PROJECT_VERSION_CONFLICT", error.Message);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task NarrationRevisionCommitsTextAudioAndVoiceAsOneVersion()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-versions-{Guid.NewGuid():N}");
        var projectId = Guid.NewGuid();
        var segment = new AdSegment(
            "ad-1", "scene-1", new(TimeSpan.Zero, TimeSpan.FromSeconds(3)), "Original text.",
            ["frame-1"], "writer", "1", new string('A', 64), "narration/old.wav",
            TimeSpan.FromSeconds(1), false);
        try
        {
            var store = new FileProjectVersionStore(root);
            await store.InitializeAsync(
                new(projectId, 1, new string('C', 64), TimeSpan.FromMinutes(1), [segment], "voice-1", DateTimeOffset.UnixEpoch, null),
                TestContext.Current.CancellationToken);
            var replacement = segment with
            {
                Text = "Shorter.",
                NarrationAssetRelativePath = "narration/new.wav",
                MeasuredNarrationDuration = TimeSpan.FromMilliseconds(600),
                UserApproved = true,
            };

            var changed = await new ProjectMutationService(store, TimeProvider.System).ReplaceNarrationAsync(
                projectId,
                1,
                [replacement],
                "voice-3",
                new HashSet<string>(["frame-1"], StringComparer.Ordinal),
                new HashSet<string>(["narration/new.wav"], StringComparer.Ordinal),
                "Validated conversation edit.",
                TestContext.Current.CancellationToken);

            Assert.Equal(2, changed.Version);
            Assert.Equal("voice-3", changed.NarrationVoiceId);
            Assert.Equal("Shorter.", Assert.Single(changed.AdSegments).Text);
            Assert.True(changed.AdSegments[0].UserApproved);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
