using System.Xml.Linq;

namespace ADFi.Architecture.Tests;

public sealed class RepositoryArchitectureTests
{
    [Fact]
    public void DesktopHasExactlyTwoTopLevelNavigationItems()
    {
        var document = XDocument.Load(RepositoryPath("src", "ADFi.Desktop", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var tabControl = document.Descendants(presentation + "TabControl").Single();
        var tabItems = tabControl.Elements(presentation + "TabItem").ToArray();

        Assert.Equal(2, tabItems.Length);
        Assert.Equal("_MY ADFi", tabItems[0].Attribute("Header")?.Value);
        Assert.Equal("_DISCOVER", tabItems[1].Attribute("Header")?.Value);
    }

    [Fact]
    public void RightsAttestationIsVisibleAndAccessible()
    {
        var xaml = File.ReadAllText(RepositoryPath("src", "ADFi.Desktop", "MainWindow.xaml"));
        Assert.Contains("I confirm that I own this media", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"Rights and authorization confirmation\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void RequiredMonorepoProjectsExist()
    {
        var required = new[]
        {
            "ADFi.Desktop", "ADFi.Core", "ADFi.LocalAI", "ADFi.Media", "ADFi.Conversation",
            "ADFi.YouTube", "ADFi.Api", "ADFi.Web", "ADFi.Economics", "ADFi.Infrastructure",
        };

        foreach (var project in required)
        {
            Assert.True(
                File.Exists(RepositoryPath("src", project, $"{project}.csproj")),
                $"Missing project: {project}");
        }
    }

    private static string RepositoryPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ADFi.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. parts]);
    }
}
