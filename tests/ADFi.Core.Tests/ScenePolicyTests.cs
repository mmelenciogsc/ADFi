using ADFi.Core;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class ScenePolicyTests
{
    [Fact]
    public void SceneBoundariesAreSortedDeduplicatedAndMinimumSpaced()
    {
        var scenes = SceneIntervalBuilder.Build(
            TimeSpan.FromSeconds(10),
            [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5.2)],
            TimeSpan.FromSeconds(1));

        Assert.Equal(3, scenes.Count);
        Assert.Equal(new MediaInterval(TimeSpan.Zero, TimeSpan.FromSeconds(1)), scenes[0]);
        Assert.Equal(new MediaInterval(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5)), scenes[1]);
        Assert.Equal(new MediaInterval(TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10)), scenes[2]);
    }

    [Fact]
    public void TextAppearanceCanTriggerDenserBoundarySelection()
    {
        var boundaries = ShotBoundaryPolicy.Detect(
        [
            new(TimeSpan.Zero, 0, 0, 0, false),
            new(TimeSpan.FromSeconds(1), 0.42m, 0.05m, 0.02m, true),
            new(TimeSpan.FromSeconds(2), 0.1m, 0.1m, 0.1m, false),
        ]);

        Assert.Equal([TimeSpan.FromSeconds(1)], boundaries);
    }
}
