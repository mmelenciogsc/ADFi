using System.Collections.Concurrent;
using ADFi.Core;
using OpenCvSharp;

namespace ADFi.Media;

/// <summary>
/// Owns short-lived, per-session VQA evidence below one private application-data root.
/// Evidence is deleted on request completion, resume, cancellation, session close, startup
/// scavenging, and best-effort process exit. It is never a project asset.
/// </summary>
public sealed class PrivateEphemeralEvidenceStore : IEphemeralEvidenceLifecycle, IDisposable
{
    private readonly string root;
    private readonly ConcurrentDictionary<string, byte> ownedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;

    public PrivateEphemeralEvidenceStore(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        this.root = Path.GetFullPath(root);
        Directory.CreateDirectory(this.root);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    public string GetSessionDirectory(Guid sessionId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty session ID is required.", nameof(sessionId));
        }

        var path = EnsureWithinRoot(Path.Combine(root, sessionId.ToString("N")));
        Directory.CreateDirectory(path);
        ownedDirectories.TryAdd(path, 0);
        return path;
    }

    public string CreateRequestDirectory(Guid sessionId, Guid requestId)
    {
        if (requestId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty request ID is required.", nameof(requestId));
        }

        var path = EnsureWithinRoot(Path.Combine(GetSessionDirectory(sessionId), requestId.ToString("N")));
        Directory.CreateDirectory(path);
        ownedDirectories.TryAdd(path, 0);
        return path;
    }

    public async Task DeleteRequestAsync(
        Guid sessionId,
        Guid requestId,
        CancellationToken cancellationToken)
    {
        var path = EnsureWithinRoot(Path.Combine(root, sessionId.ToString("N"), requestId.ToString("N")));
        await DeleteDirectoryWithRetryAsync(path, cancellationToken).ConfigureAwait(false);
        ownedDirectories.TryRemove(path, out _);
    }

    public async Task DeleteSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var path = EnsureWithinRoot(Path.Combine(root, sessionId.ToString("N")));
        await DeleteDirectoryWithRetryAsync(path, cancellationToken).ConfigureAwait(false);
        foreach (var owned in ownedDirectories.Keys.Where(item => IsWithin(item, path)))
        {
            ownedDirectories.TryRemove(owned, out _);
        }
    }

    public async Task CleanupExpiredAsync(
        TimeSpan maximumAge,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maximumAge, TimeSpan.Zero);

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lastWrite = new DateTimeOffset(Directory.GetLastWriteTimeUtc(directory), TimeSpan.Zero);
            if (lastWrite + maximumAge < now)
            {
                await DeleteDirectoryWithRetryAsync(directory, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        BestEffortCleanup();
        GC.SuppressFinalize(this);
    }

    private void OnProcessExit(object? sender, EventArgs e) => BestEffortCleanup();

    private void BestEffortCleanup()
    {
        foreach (var path in ownedDirectories.Keys.OrderByDescending(item => item.Length))
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
                // Startup scavenging handles files locked during abrupt shutdown.
            }
            catch (UnauthorizedAccessException)
            {
                // Startup scavenging retries after external handles are released.
            }
        }
    }

    private string EnsureWithinRoot(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!IsWithin(fullPath, root))
        {
            throw new InvalidOperationException("Ephemeral evidence path escaped its private root.");
        }

        return fullPath;
    }

    private static bool IsWithin(string candidate, string parent)
    {
        var relative = Path.GetRelativePath(parent, candidate);
        return relative != ".."
               && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
               && !Path.IsPathRooted(relative);
    }

    private static async Task DeleteDirectoryWithRetryAsync(
        string path,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (IOException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }
}

public sealed class FfmpegLocalMomentEvidenceProvider(
    PrivateEphemeralEvidenceStore store,
    string ffmpegPath,
    MomentEvidenceLimits? limits = null) : IVideoEvidenceProvider
{
    private readonly MomentEvidenceLimits limits = limits ?? MomentEvidenceLimits.Default;

    public bool Supports(InteractiveVideoSourceType sourceType) =>
        sourceType == InteractiveVideoSourceType.LocalFile;

    public async Task<IReadOnlyList<MomentVqaEvidence>> AcquireAsync(
        EvidenceAcquisitionContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.limits.Validate();
        if (!Supports(context.Source.SourceType)
            || string.IsNullOrWhiteSpace(context.Source.PrivateLocalPath)
            || !File.Exists(context.Source.PrivateLocalPath))
        {
            throw new InvalidOperationException("The local video is unavailable.");
        }

        if (!context.ExplicitUserAskAction)
        {
            throw new InvalidOperationException("Local visual evidence requires an explicit user Ask action.");
        }

        var count = Math.Min(context.Requirement.MaximumImageCount, this.limits.MaximumImageCount);
        var span = Math.Min(context.Requirement.TemporalSpanMs, context.Requirement.Intent == MomentQuestionIntent.RecentContext
            ? this.limits.MaximumRecentContextMs
            : this.limits.MaximumTemporalSpanMs);
        var timestamps = SelectTimestamps(context.CurrentTimestampMs, count, span);
        var directory = store.CreateRequestDirectory(context.SessionId, context.RequestId);
        var evidence = new List<MomentVqaEvidence>(timestamps.Count);
        try
        {
            for (var index = 0; index < timestamps.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = Path.Combine(directory, $"moment-{index + 1:D2}.png");
                var scale = $"scale=w='min({this.limits.MaximumImageDimension},iw)':h='min({this.limits.MaximumImageDimension},ih)':force_original_aspect_ratio=decrease";
                var result = await ExternalProcess.RunAsync(
                    ffmpegPath,
                    [
                        "-hide_banner", "-loglevel", "error", "-y",
                        "-ss", TimeSpan.FromMilliseconds(timestamps[index]).ToString("c", System.Globalization.CultureInfo.InvariantCulture),
                        "-i", context.Source.PrivateLocalPath,
                        "-frames:v", "1",
                        "-vf", scale,
                        path,
                    ],
                    cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0 || !File.Exists(path))
                {
                    throw new InvalidOperationException("FFmpeg could not create bounded moment evidence.");
                }

                using var image = Cv2.ImRead(path, ImreadModes.Color);
                if (image.Empty())
                {
                    throw new InvalidOperationException("The extracted moment image could not be validated.");
                }

                evidence.Add(new(
                    $"local-frame-{context.RequestId:N}-{index + 1}",
                    MomentEvidenceKind.EphemeralImage,
                    MomentEvidenceBasis.NewLocalFrameAnalysis,
                    timestamps[index],
                    timestamps[index],
                    null,
                    path,
                    image.Width,
                    image.Height,
                    1m,
                    true));
            }

            return evidence;
        }
        catch
        {
            await store.DeleteRequestAsync(context.SessionId, context.RequestId, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private static IReadOnlyList<long> SelectTimestamps(long currentTimestampMs, int count, int spanMs)
    {
        if (count <= 1 || spanMs <= 0)
        {
            return [Math.Max(0, currentTimestampMs)];
        }

        var start = Math.Max(0, currentTimestampMs - (spanMs / 2L));
        var end = currentTimestampMs + (spanMs / 2L);
        if (count == 2)
        {
            return [start, end];
        }

        return [start, Math.Max(0, currentTimestampMs), end];
    }
}

public sealed record CapturedPlayerRegion(byte[] PngBytes, int PixelWidth, int PixelHeight);

public interface IPlayerRegionCapture
{
    Task<CapturedPlayerRegion> CapturePausedVideoRegionAsync(
        int maximumImageDimension,
        CancellationToken cancellationToken);
}

/// <summary>
/// Audit-only acquisition seam for arbitrary YouTube playback. The server-controlled policy is
/// checked for every explicit transaction and this API intentionally exposes no seek/iteration method.
/// </summary>
public sealed class BoundedYoutubeDisplayEvidenceProvider(
    PrivateEphemeralEvidenceStore store,
    IPlayerRegionCapture capture,
    YoutubeEphemeralCapturePolicy policy) : IVideoEvidenceProvider
{
    private readonly ConcurrentDictionary<Guid, byte> completedRequests = new();

    public bool Supports(InteractiveVideoSourceType sourceType) =>
        sourceType == InteractiveVideoSourceType.YouTubeStream;

    public async Task<IReadOnlyList<MomentVqaEvidence>> AcquireAsync(
        EvidenceAcquisitionContext context,
        CancellationToken cancellationToken)
    {
        var state = policy.Validate(context);
        if (state == CaptureComplianceState.YoutubeCaptureDisabled)
        {
            return [];
        }

        if (!completedRequests.TryAdd(context.RequestId, 0))
        {
            throw new InvalidOperationException("This YouTube evidence transaction has already captured its one allowed display image.");
        }

        var directory = store.CreateRequestDirectory(context.SessionId, context.RequestId);
        try
        {
            var frame = await capture.CapturePausedVideoRegionAsync(
                policy.Limits.MaximumImageDimension,
                cancellationToken).ConfigureAwait(false);
            if (frame.PngBytes.Length == 0
                || frame.PixelWidth <= 0
                || frame.PixelHeight <= 0
                || frame.PixelWidth > policy.Limits.MaximumImageDimension
                || frame.PixelHeight > policy.Limits.MaximumImageDimension)
            {
                throw new InvalidOperationException("The captured player-region evidence violates the configured image cap.");
            }

            var path = Path.Combine(directory, "paused-player-region.png");
            await File.WriteAllBytesAsync(path, frame.PngBytes, cancellationToken).ConfigureAwait(false);
            return
            [
                new(
                    $"youtube-display-{context.RequestId:N}",
                    MomentEvidenceKind.EphemeralImage,
                    MomentEvidenceBasis.NewYoutubeEphemeralCapture,
                    context.CurrentTimestampMs,
                    context.CurrentTimestampMs,
                    null,
                    path,
                    frame.PixelWidth,
                    frame.PixelHeight,
                    1m,
                    true),
            ];
        }
        catch
        {
            await store.DeleteRequestAsync(context.SessionId, context.RequestId, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }
}
