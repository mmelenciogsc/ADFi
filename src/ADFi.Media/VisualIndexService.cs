using System.Security.Cryptography;
using System.Text.Json;
using ADFi.Core;
using OpenCvSharp;

namespace ADFi.Media;

public sealed record VisualIndexManifest(
    Guid ProjectId,
    string SourceSha256,
    TimeSpan SampleInterval,
    int MaximumImageDimension,
    int MaximumFramesPerScene,
    IReadOnlyList<VisualScene> Scenes,
    DateTimeOffset CreatedAtUtc);

public sealed class VisualIndexService(TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<VisualIndexManifest> BuildAsync(
        Guid projectId,
        ImmutableSourceMetadata source,
        string projectDirectory,
        int maximumImageDimension,
        int maximumFramesPerScene,
        IReadOnlyList<TranscriptSegment> transcript,
        IReadOnlyList<MediaInterval> narrationWindows,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maximumFramesPerScene is < 1 or > 9)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFramesPerScene));
        }

        var duration = source.Probe.Duration;
        var sampleInterval = TimeSpan.FromSeconds(Math.Max(0.5, duration.TotalSeconds / 7_200));
        var candidates = await OpenCvVisualPreprocessor.AnalyzeCandidatesAsync(
            source.SourcePath,
            sampleInterval,
            maximumImageDimension,
            cancellationToken).ConfigureAwait(false);
        var boundaries = ShotBoundaryPolicy.Detect(candidates);
        var intervals = SceneIntervalBuilder.Build(duration, boundaries, TimeSpan.FromMilliseconds(500));
        var root = Path.GetFullPath(projectDirectory);
        var framesDirectory = Path.Combine(root, "visual", "frames");
        Directory.CreateDirectory(framesDirectory);

        var scenes = await Task.Run(
            () => ExtractScenes(
                source.SourcePath,
                intervals,
                candidates,
                maximumImageDimension,
                maximumFramesPerScene,
                transcript,
                narrationWindows,
                root,
                framesDirectory,
                cancellationToken),
            cancellationToken).ConfigureAwait(false);
        var manifest = new VisualIndexManifest(
            projectId,
            source.SourceSha256,
            sampleInterval,
            maximumImageDimension,
            maximumFramesPerScene,
            scenes,
            timeProvider.GetUtcNow());
        await WriteManifestAsync(
            Path.Combine(root, "manifests", "visual-index.json"),
            manifest,
            cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    private static VisualScene[] ExtractScenes(
        string sourcePath,
        IReadOnlyList<MediaInterval> intervals,
        IReadOnlyList<FrameCandidate> candidates,
        int maximumDimension,
        int maximumFrames,
        IReadOnlyList<TranscriptSegment> transcript,
        IReadOnlyList<MediaInterval> narrationWindows,
        string root,
        string framesDirectory,
        CancellationToken cancellationToken)
    {
        using var capture = new VideoCapture(sourcePath);
        if (!capture.IsOpened())
        {
            throw new InvalidOperationException("OpenCV could not open the source for keyframe extraction.");
        }

        var scenes = new List<VisualScene>(intervals.Count);
        for (var sceneIndex = 0; sceneIndex < intervals.Count; sceneIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var interval = intervals[sceneIndex];
            var sceneId = $"scene-{sceneIndex:D5}";
            var times = SelectTimes(interval, candidates, maximumFrames);
            var keyframes = new List<KeyframeEvidence>();
            using var previousHistogram = new Mat();
            foreach (var time in times)
            {
                capture.Set(VideoCaptureProperties.PosMsec, time.TotalMilliseconds);
                using var decoded = new Mat();
                if (!capture.Read(decoded) || decoded.Empty())
                {
                    continue;
                }

                using var frame = ResizeBounded(decoded, maximumDimension);
                using var histogram = CreateNormalizedHistogram(frame);
                var similarity = previousHistogram.Empty()
                    ? 0m
                    : 1m - Math.Clamp(
                        (decimal)Cv2.CompareHist(previousHistogram, histogram, HistCompMethods.Bhattacharyya),
                        0m,
                        1m);
                if (!previousHistogram.Empty() && similarity >= 0.985m)
                {
                    continue;
                }

                var timestampMilliseconds = checked((long)Math.Round(time.TotalMilliseconds));
                var relative = Path.Combine("visual", "frames", $"{sceneId}-{timestampMilliseconds:D10}.jpg")
                    .Replace(Path.DirectorySeparatorChar, '/');
                var absolute = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
                var temporary = Path.Combine(framesDirectory, $".{Guid.NewGuid():N}.partial.jpg");
                Cv2.ImEncode(
                    ".jpg",
                    frame,
                    out var encoded,
                    [new ImageEncodingParam(ImwriteFlags.JpegQuality, 92)]);
                if (encoded.Length == 0)
                {
                    throw new InvalidOperationException("OpenCV could not encode a selected keyframe.");
                }

                try
                {
                    File.WriteAllBytes(temporary, encoded);
                    File.Move(temporary, absolute, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporary))
                    {
                        File.Delete(temporary);
                    }
                }

                var nearest = candidates.MinBy(candidate => Math.Abs((candidate.SourceTime - time).Ticks));
                var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(absolute))).ToLowerInvariant();
                keyframes.Add(new(
                    $"frame-{hash[..20]}",
                    time,
                    relative,
                    frame.Width,
                    frame.Height,
                    similarity,
                    nearest?.MotionScore ?? 0,
                    nearest?.PotentialText ?? false));
                histogram.CopyTo(previousHistogram);
            }

            if (keyframes.Count == 0)
            {
                throw new InvalidOperationException($"No decodable keyframe was found for {sceneId}.");
            }

            scenes.Add(new(
                sceneId,
                interval,
                keyframes,
                transcript.Where(segment => segment.SourceInterval.Overlaps(interval))
                    .Select(segment => segment.SegmentId)
                    .ToArray(),
                narrationWindows.Select((window, index) => (window, index))
                    .Where(item => item.window.Overlaps(interval))
                    .Select(item => $"narration-window-{item.index:D5}")
                    .ToArray()));
        }

        return scenes.ToArray();
    }

    private static TimeSpan[] SelectTimes(
        MediaInterval interval,
        IReadOnlyList<FrameCandidate> candidates,
        int maximumFrames)
    {
        var planned = KeyframeSequencePlanner.SelectTimes(interval, maximumFrames);
        var salient = candidates
            .Where(candidate => candidate.SourceTime >= interval.Start && candidate.SourceTime < interval.End)
            .OrderByDescending(candidate => candidate.PotentialText)
            .ThenByDescending(candidate => candidate.MotionScore + candidate.StructuralDifference + candidate.ColorDifference)
            .Select(candidate => candidate.SourceTime);
        return planned.Concat(salient)
            .Distinct()
            .Order()
            .Take(maximumFrames)
            .ToArray();
    }

    private static Mat ResizeBounded(Mat source, int maximumDimension)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= maximumDimension)
        {
            return source.Clone();
        }

        var scale = maximumDimension / (double)longest;
        var frame = new Mat();
        Cv2.Resize(
            source,
            frame,
            new Size(
                Math.Max(1, (int)Math.Round(source.Width * scale)),
                Math.Max(1, (int)Math.Round(source.Height * scale))),
            interpolation: InterpolationFlags.Area);
        return frame;
    }

    private static Mat CreateNormalizedHistogram(Mat frame)
    {
        using var gray = new Mat();
        Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
        var histogram = new Mat();
        using var mask = new Mat();
        Cv2.CalcHist([gray], [0], mask, histogram, 1, [64], [new Rangef(0, 256)]);
        Cv2.Normalize(histogram, histogram, 1, 0, NormTypes.L1);
        return histogram;
    }

    private static async Task WriteManifestAsync(
        string path,
        VisualIndexManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".partial";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, overwrite: true);
    }
}
