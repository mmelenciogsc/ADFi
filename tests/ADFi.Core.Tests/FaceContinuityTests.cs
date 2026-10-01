using ADFi.Media;

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
}
