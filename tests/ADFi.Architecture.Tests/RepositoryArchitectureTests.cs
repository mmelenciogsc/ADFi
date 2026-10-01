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
