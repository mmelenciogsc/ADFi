using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class MediaPolicyTests
{
    [Theory]
    [InlineData("https://example.test/video.mp4")]
    [InlineData("ftp://example.test/video.mp4")]
    public void IntakeRejectsRemoteSources(string source)
    {
        var exception = Assert.Throws<ArgumentException>(() =>
            LocalSourcePolicy.ValidateExistingLocalVideo(source));
        Assert.Contains("local video files only", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IntakeRejectsUnsupportedLocalExtensionsBeforeFileAccess()
    {
        Assert.Throws<NotSupportedException>(() =>
            LocalSourcePolicy.ValidateExistingLocalVideo("C:\\media\\source.txt"));
    }
}

