using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace ADFi.Media;

public sealed record DetectedFace(
    string DetectionId,
    string FrameEvidenceId,
    Rect BoundingBox,
    decimal DetectorConfidence,
    IReadOnlyList<float> Embedding);

public sealed record FaceCluster(
    string EntityId,
    IReadOnlyList<float> Centroid,
    int ObservationCount,
    TimeSpan FirstSeen,
    TimeSpan LastSeen);

public sealed record FaceClusterMatch(string EntityId, decimal CosineSimilarity, bool IsCertain);

public static class FaceContinuityPolicy
{
    public const decimal ConservativeCosineThreshold = 0.45m;
    public const decimal AmbiguityMargin = 0.05m;

    public static FaceClusterMatch? Match(
        IReadOnlyList<float> embedding,
        IReadOnlyList<FaceCluster> clusters)
    {
        if (embedding.Count == 0)
        {
            throw new ArgumentException("A face embedding cannot be empty.", nameof(embedding));
        }

        var ranked = clusters
            .Select(cluster => new
            {
                Cluster = cluster,
                Similarity = CosineSimilarity(embedding, cluster.Centroid),
            })
            .OrderByDescending(candidate => candidate.Similarity)
            .ToArray();
        if (ranked.Length == 0 || ranked[0].Similarity < ConservativeCosineThreshold)
        {
            return null;
        }

        var isUnambiguous = ranked.Length == 1
            || ranked[0].Similarity - ranked[1].Similarity >= AmbiguityMargin;
        return new(ranked[0].Cluster.EntityId, ranked[0].Similarity, isUnambiguous);
    }

    private static decimal CosineSimilarity(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count != right.Count || left.Count == 0)
        {
            throw new ArgumentException("Face embeddings must have equal nonzero dimensions.");
        }

        double dot = 0;
        double leftMagnitude = 0;
        double rightMagnitude = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftMagnitude += left[index] * left[index];
            rightMagnitude += right[index] * right[index];
        }

        if (leftMagnitude == 0 || rightMagnitude == 0)
        {
            return 0;
        }

        return (decimal)(dot / (Math.Sqrt(leftMagnitude) * Math.Sqrt(rightMagnitude)));
    }
}

public sealed class OpenCvFaceContinuityAnalyzer : IDisposable
{
    private readonly string detectorModelPath;
    private readonly Net recognizer;
    private bool disposed;

    public OpenCvFaceContinuityAnalyzer(string detectorModelPath, string recognizerModelPath)
    {
        this.detectorModelPath = RequireModel(detectorModelPath);
        recognizer = CvDnn.ReadNetFromOnnx(RequireModel(recognizerModelPath))
            ?? throw new InvalidOperationException("OpenCV could not load the face-recognition model.");
    }

    public IReadOnlyList<DetectedFace> DetectAndEmbed(string frameEvidenceId, string imagePath)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(frameEvidenceId);
        using var image = Cv2.ImRead(imagePath, ImreadModes.Color);
        if (image.Empty())
        {
            throw new InvalidDataException("The face-analysis frame could not be decoded.");
        }

        using var detector = FaceDetectorYN.Create(
            detectorModelPath,
            string.Empty,
            image.Size(),
            scoreThreshold: 0.90f,
            nmsThreshold: 0.30f,
            topK: 2000,
            backendId: Backend.OPENCV,
            targetId: Target.CPU);
        using var detections = new Mat();
        detector.Detect(image, detections);
        var rowCount = detections.Rows;
        var results = new List<DetectedFace>(rowCount);
        for (var row = 0; row < rowCount; row++)
        {
            var rectangle = ClampRectangle(
                new(
                    (int)MathF.Round(detections.At<float>(row, 0)),
                    (int)MathF.Round(detections.At<float>(row, 1)),
                    (int)MathF.Round(detections.At<float>(row, 2)),
                    (int)MathF.Round(detections.At<float>(row, 3))),
                image.Size());
            if (rectangle.Width < 16 || rectangle.Height < 16)
            {
                continue;
            }

            using var crop = new Mat(image, rectangle);
            using var resized = new Mat();
            Cv2.Resize(crop, resized, new Size(112, 112), interpolation: InterpolationFlags.Linear);
            using var blob = CvDnn.BlobFromImage(
                resized,
                1d / 127.5d,
                new Size(112, 112),
                new Scalar(127.5, 127.5, 127.5),
                swapRB: true,
                crop: false);
            recognizer.SetInput(blob);
            using var output = recognizer.Forward();
            using var flat = output.Reshape(1, 1);
            flat.GetArray<float>(out var values);
            Normalize(values);
            results.Add(new(
                $"{frameEvidenceId}-face-{row:D2}",
                frameEvidenceId,
                rectangle,
                (decimal)detections.At<float>(row, 14),
                values));
        }

        return results;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        recognizer.Dispose();
        disposed = true;
        GC.SuppressFinalize(this);
    }

    private static string RequireModel(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("A required OpenCV face model is missing.", fullPath);
        }

        return fullPath;
    }

    private static Rect ClampRectangle(Rect rectangle, Size bounds)
    {
        var x = Math.Clamp(rectangle.X, 0, Math.Max(0, bounds.Width - 1));
        var y = Math.Clamp(rectangle.Y, 0, Math.Max(0, bounds.Height - 1));
        var width = Math.Clamp(rectangle.Width, 0, bounds.Width - x);
        var height = Math.Clamp(rectangle.Height, 0, bounds.Height - y);
        return new(x, y, width, height);
    }

    private static void Normalize(float[] values)
    {
        var magnitude = Math.Sqrt(values.Sum(value => value * value));
        if (magnitude == 0)
        {
            return;
        }

        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (float)(values[index] / magnitude);
        }
    }
}
