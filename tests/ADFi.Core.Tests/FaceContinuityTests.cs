using ADFi.Media;
using OpenCvSharp;

namespace ADFi.Core.Tests;

public sealed class FaceContinuityTests
{
    [Fact]
    public void ConservativeUnambiguousMatchReturnsAnonymousProjectEntity()
    {
        var clusters = new[]
        {
            new FaceCluster("Person_01", [1f, 0f, 0f], 3, TimeSpan.Zero, TimeSpan.FromSeconds(5)),
            new FaceCluster("Person_02", [0f, 1f, 0f], 2, TimeSpan.Zero, TimeSpan.FromSeconds(5)),
        };

        var match = FaceContinuityPolicy.Match([0.99f, 0.01f, 0f], clusters);

        Assert.NotNull(match);
        Assert.Equal("Person_01", match.EntityId);
        Assert.True(match.IsCertain);
    }

    [Fact]
    public void WeakSimilarityCreatesNoIdentityClaim()
    {
        var clusters = new[]
        {
            new FaceCluster("Person_01", [1f, 0f, 0f], 3, TimeSpan.Zero, TimeSpan.FromSeconds(5)),
        };

        Assert.Null(FaceContinuityPolicy.Match([0f, 1f, 0f], clusters));
    }

    [Fact]
    public void AmbiguousSimilarityIsExplicitlyUncertain()
    {
        var clusters = new[]
        {
            new FaceCluster("Person_01", [1f, 0f], 3, TimeSpan.Zero, TimeSpan.FromSeconds(5)),
            new FaceCluster("Person_02", [0.99f, 0.01f], 2, TimeSpan.Zero, TimeSpan.FromSeconds(5)),
        };

        var match = FaceContinuityPolicy.Match([1f, 0f], clusters);

        Assert.NotNull(match);
        Assert.False(match.IsCertain);
    }

    [Fact]
    public void InstalledOpenCvZooModelsLoadThroughMaintainedApis()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var modelRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ADFi",
            "models",
            "opencv");
        var detector = Path.Combine(modelRoot, "face_detection_yunet_2023mar.onnx");
        var recognizer = Path.Combine(modelRoot, "face_recognition_sface_2021dec.onnx");
        if (!File.Exists(detector) || !File.Exists(recognizer))
        {
            return;
        }

        var imagePath = Path.Combine(Path.GetTempPath(), $"adfi-face-smoke-{Guid.NewGuid():N}.png");
        try
        {
            using (var image = new Mat(240, 320, MatType.CV_8UC3, Scalar.Black))
            {
                Cv2.ImWrite(imagePath, image);
            }

            using var analyzer = new OpenCvFaceContinuityAnalyzer(detector, recognizer);
            Assert.Empty(analyzer.DetectAndEmbed("frame-smoke", imagePath));
        }
        finally
        {
            File.Delete(imagePath);
        }
    }
}
