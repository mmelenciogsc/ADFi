using ADFi.Web;

namespace ADFi.Core.Tests;

public sealed class DiscoverSharingTests
{
    [Fact]
    public void ExternalShareRetainsAdfiCanonicalUrlAndSignedReferral()
    {
        var result = DiscoverSharingPolicy.CreateReferralUrl(
            new Uri("https://adfi.example/discover/project-1"),
            "signed/value+1");

        Assert.Equal("adfi.example", result.Host);
        Assert.Equal("/discover/project-1", result.AbsolutePath);
        Assert.Contains("ref=signed%2Fvalue%2B1", result.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("youtube.com", result.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PublicShareRejectsInsecureCanonicalUrl()
    {
        Assert.Throws<ArgumentException>(() => DiscoverSharingPolicy.CreateReferralUrl(
            new Uri("http://adfi.example/discover/project-1"),
            "signed-value"));
    }

    [Fact]
    public void DiscoverRendererRejectsMalformedYouTubeVideoId()
    {
        var project = new DiscoverSurface(
            "project-1",
            "Accessible project",
            "not/a/video/id",
            "Audio description included.",
            "Published by the project creator.",
            new Uri("https://adfi.example/discover/project-1"));

        Assert.Throws<ArgumentException>(() => DiscoverHtmlRenderer.RenderIndex([project]));
    }
}
