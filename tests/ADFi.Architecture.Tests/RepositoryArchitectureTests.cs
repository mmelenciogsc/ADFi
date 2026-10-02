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
    public void DesktopCriticalControlsExposeStableScreenReaderNames()
    {
        var document = XDocument.Load(RepositoryPath("src", "ADFi.Desktop", "MainWindow.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        var requiredNames = new[]
        {
            "EmailTextBox",
            "PasswordInput",
            "SignInButton",
            "OpenInteractiveViewerButton",
            "RightsAttestationCheckBox",
            "RunHardwareProfileButton",
            "SelectLocalVideoButton",
            "LocalProjectsList",
            "RunPreparationButton",
            "RunVisualUnderstandingButton",
            "PipelineProgressBar",
            "DescriptionsList",
            "ConversationTextBox",
            "RefreshDiscoverButton",
            "DiscoverProjects",
            "OpenDiscoverProjectButton",
            "OpenDiscoverViewerButton",
        };

        foreach (var name in requiredNames)
        {
            var control = document.Descendants().Single(element => element.Attribute(xaml + "Name")?.Value == name);
            Assert.False(
                string.IsNullOrWhiteSpace(control.Attribute("AutomationProperties.Name")?.Value),
                $"Critical control {name} needs a stable accessible name.");
        }
    }

    [Fact]
    public void DesktopUsesNativeControlsAndExplicitKeyboardOrder()
    {
        var document = XDocument.Load(RepositoryPath("src", "ADFi.Desktop", "MainWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        Assert.Empty(document.Descendants(presentation + "Canvas"));

        var tabIndices = document.Descendants()
            .Select(element => element.Attribute("TabIndex")?.Value)
            .Where(value => value is not null)
            .Select(value => int.Parse(value!, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        Assert.Equal(Enumerable.Range(1, 32), tabIndices.Order());
    }

    [Fact]
    public void InteractiveViewerHasDeterministicQuestionFocusOrderAndNoCanvas()
    {
        var document = XDocument.Load(RepositoryPath("src", "ADFi.Desktop", "InteractiveVideoWindow.xaml"));
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        Assert.Empty(document.Descendants(presentation + "Canvas"));

        var question = document.Descendants().Single(element => element.Attribute(xaml + "Name")?.Value == "MomentQuestionTextBox");
        var dictate = document.Descendants().Single(element => element.Attribute(xaml + "Name")?.Value == "DictateButton");
        var connectChatGpt = document.Descendants().Single(element => element.Attribute(xaml + "Name")?.Value == "ContinueWithChatGptButton");
        Assert.Equal("15", question.Attribute("TabIndex")?.Value);
        Assert.Equal("16", dictate.Attribute("TabIndex")?.Value);
        Assert.Equal("Ask about this moment", question.Attribute("AutomationProperties.Name")?.Value);
        Assert.Equal("DICTATE question using Windows voice typing", dictate.Attribute("AutomationProperties.Name")?.Value);
        Assert.Equal("Continue with ChatGPT in the system browser", connectChatGpt.Attribute("AutomationProperties.Name")?.Value);
        var tabIndices = document.Descendants()
            .Select(element => element.Attribute("TabIndex")?.Value)
            .Where(value => value is not null)
            .Select(value => int.Parse(value!, System.Globalization.CultureInfo.InvariantCulture))
            .Order()
            .ToArray();
        Assert.Equal(Enumerable.Range(1, 29), tabIndices);
    }

    [Fact]
    public void YoutubeViewerUsesOfficialIframeAndContainsNoDownloaderDependency()
    {
        var playback = File.ReadAllText(RepositoryPath("src", "ADFi.Desktop", "InteractivePlaybackControllers.cs"));
        var project = File.ReadAllText(RepositoryPath("src", "ADFi.Desktop", "ADFi.Desktop.csproj"));
        Assert.Contains("https://www.youtube.com/iframe_api", playback, StringComparison.Ordinal);
        Assert.Contains("pauseVideo()", playback, StringComparison.Ordinal);
        Assert.Contains("playVideo()", playback, StringComparison.Ordinal);
        Assert.Contains("getCurrentTime()", playback, StringComparison.Ordinal);
        Assert.DoesNotContain("yt-dlp", playback + project, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("youtube-dl", playback + project, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void YoutubeCaptureFeatureFlagDefaultsOffInProductionConfiguration()
    {
        var json = File.ReadAllText(RepositoryPath("src", "ADFi.Api", "appsettings.json"));
        Assert.Contains("\"YoutubeEphemeralAccessibilityCaptureEnabled\": false", json, StringComparison.Ordinal);
        Assert.Contains("\"ChatGptPlanUsageIntegrationEnabled\": false", json, StringComparison.Ordinal);
        var migration = File.ReadAllText(RepositoryPath(
            "src", "ADFi.Infrastructure", "Migrations", "0004_interactive_viewer_vqa.sql"));
        Assert.Contains("'false'::jsonb", migration, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopRaisesOneUiAutomationLiveRegionEventPerAnnouncement()
    {
        var code = File.ReadAllText(RepositoryPath("src", "ADFi.Desktop", "MainWindow.xaml.cs"));
        Assert.Contains("AutomationEvents.LiveRegionChanged", code, StringComparison.Ordinal);
        Assert.Contains("private void Announce(string message)", code, StringComparison.Ordinal);
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

    [Fact]
    public void InitialPostgresMigrationContainsRequiredNormalizedDomains()
    {
        var sql = File.ReadAllText(RepositoryPath(
            "src",
            "ADFi.Infrastructure",
            "Migrations",
            "0001_initial.sql"));
        var requiredTables = new[]
        {
            "users", "email_verification_tokens", "auth_sessions", "devices",
            "subscription_plans", "subscriptions", "subscription_events", "projects",
            "project_publications", "youtube_assets", "youtube_status_events",
            "project_contributors", "contribution_events", "feedback_entries",
            "referral_links", "referral_visits", "referral_relationships",
            "qualified_downstream_actions", "playlists", "playlist_items",
            "project_split_versions", "split_participants", "rights_interests",
            "rights_claims", "economic_policy_versions", "revenue_periods",
            "net_revenue_components", "economic_events", "member_earnings",
            "wallets", "wallet_entries", "payout_requests", "payout_batches",
            "moderation_reports", "moderation_actions", "audit_events",
            "feature_configuration",
        };

        foreach (var table in requiredTables)
        {
            Assert.Contains($"CREATE TABLE {table} (", sql, StringComparison.Ordinal);
        }

        Assert.Contains("('COMMUNITY', 'Community', 200, false, false, true)", sql, StringComparison.Ordinal);
        Assert.Contains("('CREATOR', 'Creator', 900, true, true, true)", sql, StringComparison.Ordinal);
        Assert.Contains("community_basis_points = 7000", sql, StringComparison.Ordinal);
        Assert.Contains("adfi_basis_points = 3000", sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UbuntuActivationMigratesBeforeSwitchAndRollsBackFailedRelease()
    {
        var script = File.ReadAllText(RepositoryPath("deploy", "ubuntu", "install-release.sh"));
        var migration = script.IndexOf("--migrate-only", StringComparison.Ordinal);
        var activation = script.IndexOf("ln -sfn \"${release_directory}\"", StringComparison.Ordinal);

        Assert.True(migration >= 0 && migration < activation);
        Assert.Contains("previous_release", script, StringComparison.Ordinal);
        Assert.Contains("health/ready", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PayPalWebhookInboxSupportsIdempotentAndOutOfOrderDelivery()
    {
        var sql = File.ReadAllText(RepositoryPath(
            "src", "ADFi.Infrastructure", "Migrations", "0002_paypal_webhook_inbox.sql"));
        Assert.Contains("provider_event_id text PRIMARY KEY", sql, StringComparison.Ordinal);
        Assert.Contains("WAITING_FOR_SUBSCRIPTION", sql, StringComparison.Ordinal);
        Assert.Contains("provider_status_at_utc", sql, StringComparison.Ordinal);
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
