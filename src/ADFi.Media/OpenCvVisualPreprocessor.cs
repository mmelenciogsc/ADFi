using ADFi.Core;
using OpenCvSharp;

namespace ADFi.Media;

public sealed class OpenCvVisualPreprocessor
{
    public static Task<IReadOnlyList<FrameCandidate>> AnalyzeCandidatesAsync(
        string localVideoPath,
        TimeSpan sampleInterval,
        int maximumDimension,
        CancellationToken cancellationToken)
    {
        var validatedPath = LocalSourcePolicy.ValidateExistingLocalVideo(localVideoPath);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(sampleInterval, TimeSpan.Zero);

        if (maximumDimension is < 256 or > 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        }

        return Task.Run<IReadOnlyList<FrameCandidate>>(
            () => Analyze(validatedPath, sampleInterval, maximumDimension, cancellationToken),
            cancellationToken);
    }

    private static FrameCandidate[] Analyze(
        string path,
        TimeSpan sampleInterval,
        int maximumDimension,
        CancellationToken cancellationToken)
    {
        using var capture = new VideoCapture(path);
        if (!capture.IsOpened())
        {
            throw new InvalidOperationException("OpenCV could not open the local source video.");
        }

        var durationMilliseconds = capture.FrameCount / Math.Max(capture.Fps, 0.001) * 1000;
        var candidates = new List<FrameCandidate>();
        using var previousGray = new Mat();
        using var previousHistogram = new Mat();
        for (var position = 0d; position < durationMilliseconds; position += sampleInterval.TotalMilliseconds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            capture.Set(VideoCaptureProperties.PosMsec, position);
            using var decoded = new Mat();
            if (!capture.Read(decoded) || decoded.Empty())
            {
                continue;
            }

            using var frame = ResizeBounded(decoded, maximumDimension);
            using var gray = new Mat();
            Cv2.CvtColor(frame, gray, ColorConversionCodes.BGR2GRAY);
            using var histogram = CreateNormalizedHistogram(gray);
            var hasPrevious = !previousGray.Empty();
            var structuralDifference = hasPrevious ? StructuralDifference(previousGray, gray) : 0m;
            var histogramDifference = hasPrevious
                ? Clamp01((decimal)Cv2.CompareHist(previousHistogram, histogram, HistCompMethods.Bhattacharyya))
                : 0m;
            var motionScore = hasPrevious ? MotionDifference(previousGray, gray) : 0m;
            var potentialText = HasTextLikeEdgeDensity(gray);
            candidates.Add(new(
                TimeSpan.FromMilliseconds(position),
                structuralDifference,
                histogramDifference,
                motionScore,
                potentialText));
            gray.CopyTo(previousGray);
            histogram.CopyTo(previousHistogram);
        }

        return candidates.ToArray();
    }

    private static Mat ResizeBounded(Mat source, int maximumDimension)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= maximumDimension)
        {
            return source.Clone();
        }

        var scale = maximumDimension / (double)longest;
        var destination = new Mat();
        Cv2.Resize(
            source,
            destination,
            new Size(
                Math.Max(1, (int)Math.Round(source.Width * scale)),
                Math.Max(1, (int)Math.Round(source.Height * scale))),
            interpolation: InterpolationFlags.Area);
        return destination;
    }

    private static Mat CreateNormalizedHistogram(Mat gray)
    {
        var histogram = new Mat();
        using var mask = new Mat();
        Cv2.CalcHist(
            [gray],
            [0],
            mask,
            histogram,
            1,
            [64],
            [new Rangef(0, 256)],
            uniform: true,
            accumulate: false);
        Cv2.Normalize(histogram, histogram, 1, 0, NormTypes.L1);
        return histogram;
    }

    private static decimal StructuralDifference(Mat previous, Mat current)
    {
        using var difference = new Mat();
        Cv2.Absdiff(previous, current, difference);
        return Clamp01((decimal)(Cv2.Mean(difference).Val0 / 255d));
    }

    private static decimal MotionDifference(Mat previous, Mat current)
    {
        using var previousSmall = new Mat();
        using var currentSmall = new Mat();
        using var difference = new Mat();
        Cv2.Resize(previous, previousSmall, new Size(64, 36), interpolation: InterpolationFlags.Area);
        Cv2.Resize(current, currentSmall, new Size(64, 36), interpolation: InterpolationFlags.Area);
        Cv2.Absdiff(previousSmall, currentSmall, difference);
        Cv2.Threshold(difference, difference, 18, 255, ThresholdTypes.Binary);
        return Clamp01(Cv2.CountNonZero(difference) / (decimal)(difference.Rows * difference.Cols));
    }

    private static bool HasTextLikeEdgeDensity(Mat gray)
    {
        using var edges = new Mat();
        Cv2.Canny(gray, edges, 100, 200);
        var density = Cv2.CountNonZero(edges) / (decimal)(edges.Rows * edges.Cols);
        var meanLuminance = (decimal)Cv2.Mean(gray).Val0;
        // Sparse title cards can occupy well under three percent of an otherwise black frame.
        // A higher floor caused opening titles to bypass OCR and left no trustworthy narration
        // fact. Restrict the lower threshold to dark frames so low-detail natural imagery does not
        // get routed through OCR. The VLM/OCR stages still decide whether edges are meaningful text.
        return density <= 0.35m
               && (density >= 0.03m || meanLuminance <= 40m && density >= 0.0025m);
    }

    private static decimal Clamp01(decimal value) => Math.Clamp(value, 0m, 1m);
}

public static class SceneIntervalBuilder
{
    public static IReadOnlyList<MediaInterval> Build(
        TimeSpan mediaDuration,
        IEnumerable<TimeSpan> boundaries,
        TimeSpan minimumSceneDuration,
        TimeSpan? maximumSceneDuration = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(mediaDuration, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumSceneDuration, TimeSpan.Zero);
        if (maximumSceneDuration is { } configuredMaximum
            && (configuredMaximum <= TimeSpan.Zero || configuredMaximum < minimumSceneDuration))
        {
            throw new ArgumentOutOfRangeException(nameof(maximumSceneDuration));
        }
        var accepted = boundaries
            .Where(time => time > TimeSpan.Zero && time < mediaDuration)
            .Order()
            .Aggregate(new List<TimeSpan>(), (result, time) =>
            {
                var prior = result.Count == 0 ? TimeSpan.Zero : result[^1];
                if (time - prior >= minimumSceneDuration)
                {
                    result.Add(time);
                }

                return result;
            });
        var scenes = new List<MediaInterval>(accepted.Count + 1);
        var start = TimeSpan.Zero;
        foreach (var end in accepted.Append(mediaDuration))
        {
            while (maximumSceneDuration is { } maximum && end - start > maximum)
            {
                var boundedEnd = start + maximum;
                scenes.Add(new(start, boundedEnd));
                start = boundedEnd;
            }

            scenes.Add(new(start, end));
            start = end;
        }

        return scenes;
    }
}
