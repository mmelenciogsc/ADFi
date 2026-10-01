using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using ADFi.Core;
using ADFi.Infrastructure;
using ADFi.Infrastructure.Security;
using ADFi.LocalAI;
using ADFi.Media;
using ADFi.Web;
using Microsoft.Win32;

namespace ADFi.Desktop;

public partial class MainWindow : Window, IDisposable
{
    private readonly DesktopConfiguration configuration;
    private readonly HttpClient httpClient;
    private readonly AdfiApiClient apiClient;
    private readonly DesktopSessionStore sessionStore;
    private readonly LocalProjectCatalog projectCatalog;
    private readonly CreatorHardwareProfileStore hardwareProfileStore;
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private DesktopSession? session;
    private DesktopEntitlement? entitlement;
    private bool entitlementVerified;
    private CancellationTokenSource? currentOperationCancellation;
    private bool disposed;

    public MainWindow()
    {
        InitializeComponent();
        configuration = DesktopConfiguration.Load();
        httpClient = new HttpClient
        {
            BaseAddress = configuration.ApiBaseUri,
            Timeout = TimeSpan.FromSeconds(20),
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ADFi-Desktop/0.1");
        apiClient = new AdfiApiClient(httpClient);
        sessionStore = new DesktopSessionStore(new DpapiFileSecretStore(
            configuration.ProtectedCredentialRoot,
            "ADFi desktop API session v1"));
        projectCatalog = new LocalProjectCatalog(configuration.ProjectRoot);
        hardwareProfileStore = new CreatorHardwareProfileStore(
            configuration.HardwareProfileRoot,
            configuration.ProjectRoot);
    }

    private async void MainWindow_OnLoaded(object sender, RoutedEventArgs e)
    {
        LogTechnical($"Desktop started. API endpoint: {configuration.ApiBaseUri.Host}.");
        await LoadLocalProjectsAsync(lifetimeCancellation.Token);
        await RestoreSessionAsync(lifetimeCancellation.Token);
        await RefreshDiscoverAsync(announceResult: false, lifetimeCancellation.Token);

        var accountSummary = session is null ? "Not signed in." : $"Signed in as {session.Account.DisplayName}.";
        Announce($"ADFi ready. {accountSummary} DISCOVER is available without signing in.");
    }

    private void MainWindow_OnClosed(object? sender, EventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lifetimeCancellation.Cancel();
        currentOperationCancellation?.Cancel();
        lifetimeCancellation.Dispose();
        httpClient.Dispose();
        GC.SuppressFinalize(this);
    }

    private void PrimaryNavigation_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, PrimaryNavigation))
        {
            return;
        }

        Announce(PrimaryNavigation.SelectedIndex == 0
            ? "MY ADFi selected. Private account and local project workspace."
            : "DISCOVER selected. Public ADFied works do not require sign-in.");
    }

    private async void SignInButton_OnClick(object sender, RoutedEventArgs e)
    {
        var email = EmailTextBox.Text.Trim();
        var password = PasswordInput.Password;
        PasswordInput.Clear();
        if (email.Length == 0 || password.Length == 0)
        {
            Announce("Enter both email address and password.");
            return;
        }

        SignInButton.IsEnabled = false;
        Announce("Signing in.");
        try
        {
            session = await apiClient.LoginAsync(email, password, lifetimeCancellation.Token);
            await sessionStore.SaveAsync(session, lifetimeCancellation.Token);
            await RefreshEntitlementAsync(announceResult: false, lifetimeCancellation.Token);
            UpdateAccountPresentation();
            Announce($"Signed in as {session.Account.DisplayName}. {SubscriptionAnnouncement()}.");
            LogTechnical("Account sign-in and protected session storage completed.");
        }
        catch (Exception exception)
        {
            ReportError("Sign in", exception);
        }
        finally
        {
            SignInButton.IsEnabled = true;
        }
    }

    private async void RegisterButton_OnClick(object sender, RoutedEventArgs e)
    {
        var email = RegistrationEmailTextBox.Text.Trim();
        var displayName = RegistrationNameTextBox.Text.Trim();
        var password = RegistrationPasswordInput.Password;
        var confirmation = RegistrationPasswordConfirmationInput.Password;
        RegistrationPasswordInput.Clear();
        RegistrationPasswordConfirmationInput.Clear();
        if (email.Length == 0 || displayName.Length == 0 || password.Length == 0)
        {
            Announce("Display name, email address, and password are required to create an account.");
            return;
        }

        if (!string.Equals(password, confirmation, StringComparison.Ordinal))
        {
            Announce("The new password and confirmation do not match.");
            return;
        }

        Announce("Creating the account.");
        try
        {
            var receipt = await apiClient.RegisterAsync(
                email,
                displayName,
                password,
                lifetimeCancellation.Token);
            EmailTextBox.Text = email;
            Announce(receipt.Message);
            LogTechnical($"Registration request accepted for account {receipt.UserId:D}.");
        }
        catch (Exception exception)
        {
            ReportError("Account registration", exception);
        }
    }

    private async void PasswordResetButton_OnClick(object sender, RoutedEventArgs e)
    {
        var email = EmailTextBox.Text.Trim();
        if (email.Length == 0)
        {
            Announce("Enter the account email address before requesting a password reset.");
            EmailTextBox.Focus();
            return;
        }

        Announce("Requesting a password reset.");
        try
        {
            var result = await apiClient.RequestPasswordResetAsync(email, lifetimeCancellation.Token);
            Announce(result);
            LogTechnical("Password-reset request completed without disclosing account existence.");
        }
        catch (Exception exception)
        {
            ReportError("Password reset", exception);
        }
    }

    private async void RefreshEntitlementButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshEntitlementAsync(announceResult: true, lifetimeCancellation.Token);
        }
        catch (Exception exception)
        {
            ReportError("Subscription refresh", exception);
        }
    }

    private async void SignOutButton_OnClick(object sender, RoutedEventArgs e)
    {
        var current = session;
        if (current is null)
        {
            return;
        }

        Announce("Signing out.");
        try
        {
            await apiClient.LogoutAsync(current.Token, lifetimeCancellation.Token);
        }
        catch (Exception exception)
        {
            LogTechnical($"Remote sign-out did not complete: {exception.GetType().Name}.");
        }
        finally
        {
            await sessionStore.DeleteAsync(CancellationToken.None);
            session = null;
            entitlement = null;
            entitlementVerified = false;
            RightsAttestationCheckBox.IsChecked = false;
            UpdateAccountPresentation();
            Announce("Signed out. The protected local session was removed.");
        }
    }

    private void RightsAttestationCheckBox_OnChanged(object sender, RoutedEventArgs e) =>
        UpdateCreatorAccess();

    private async void RunHardwareProfileButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RunCreatorHardwareProfileAsync(force: true, lifetimeCancellation.Token);
    }

    private async void SelectLocalVideoButton_OnClick(object sender, RoutedEventArgs e)
    {
        var activePlan = entitlement?.Plan ?? SubscriptionPlan.None;
        if (session is null || !CreatorToolAccess.CanSelectLocalSource(
                activePlan,
                entitlementVerified,
                RightsAttestationCheckBox.IsChecked == true))
        {
            Announce("A current verified Creator subscription and rights confirmation are required.");
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Select an authorized local source video",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Supported video files|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true)
        {
            Announce("Source selection cancelled.");
            return;
        }

        currentOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        SetLocalOperationState(running: true);
        PipelineProgressBar.IsIndeterminate = true;
        Announce("Validating the local source. Checking format, duration, disk space, and file hash.");
        var projectId = Guid.NewGuid();
        try
        {
            var projectDirectory = projectCatalog.CreateProjectDirectory(projectId);
            var intake = await new SourceIntakeService(new FfprobeMediaProbe(), TimeProvider.System)
                .ValidateAsync(
                    dialog.FileName,
                    projectDirectory,
                    session.Account.UserId,
                    projectId,
                    rightsAccepted: true,
                    currentOperationCancellation.Token);
            await new FilePipelineCheckpointStore(configuration.ProjectRoot)
                .SaveAsync(intake.Checkpoint, CancellationToken.None);
            await new FileProjectVersionStore(configuration.ProjectRoot).InitializeAsync(
                new ProjectSnapshot(
                    projectId,
                    1,
                    intake.Source.SourceSha256,
                    intake.Source.Probe.Duration,
                    [],
                    "not-selected",
                    TimeProvider.System.GetUtcNow(),
                    null),
                CancellationToken.None);

            await LoadLocalProjectsAsync(currentOperationCancellation.Token, projectId);
            PipelineProgressBar.IsIndeterminate = false;
            PipelineProgressBar.Value = 1;
            Announce(
                $"Source validated. {Path.GetFileName(dialog.FileName)}, {FormatDuration(intake.Source.Probe.Duration)}. " +
                "The immutable source manifest and resumable checkpoint were saved.");
            LogTechnical($"Project {projectId:D}: SOURCE_VALIDATED checkpoint persisted.");
            RightsAttestationCheckBox.IsChecked = false;
        }
        catch (Exception exception)
        {
            ReportError("Source validation", exception);
        }
        finally
        {
            PipelineProgressBar.IsIndeterminate = false;
            currentOperationCancellation.Dispose();
            currentOperationCancellation = null;
            SetLocalOperationState(running: false);
        }
    }

    private void CancelOperationButton_OnClick(object sender, RoutedEventArgs e)
    {
        CancelOperationButton.IsEnabled = false;
        currentOperationCancellation?.Cancel();
        Announce("Cancellation requested. ADFi will stop at the next safe boundary.");
    }

    private void LocalProjectsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LocalProjectsList.SelectedItem is not LocalProjectSummary project)
        {
            SelectedProjectText.Text = "No local project selected.";
            PipelineProgressBar.Value = 0;
            UpdateCreatorAccess();
            return;
        }

        SelectedProjectText.Text = project.AccessibleLabel;
        PipelineProgressBar.Value = Math.Clamp((int)project.LatestPhase + 1, 0, 14);
        UpdateCreatorAccess();
    }

    private async void OpenLocalProjectButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (LocalProjectsList.SelectedItem is not LocalProjectSummary project)
        {
            Announce("Select a local project first.");
            return;
        }

        try
        {
            var snapshot = await new FileProjectVersionStore(configuration.ProjectRoot)
                .LoadCurrentAsync(project.ProjectId, lifetimeCancellation.Token);
            DescriptionsList.ItemsSource = snapshot.AdSegments.Select(segment =>
                $"{FormatTime(segment.Window.Start)} to {FormatTime(segment.Window.End)}. {segment.Text}").ToArray();
            Announce(snapshot.AdSegments.Count == 0
                ? $"{project.SourceFileName} opened at {project.LatestPhase}. No draft descriptions exist yet."
                : $"{project.SourceFileName} opened with {snapshot.AdSegments.Count} audio description segments.");
            LogTechnical($"Project {project.ProjectId:D}: version {snapshot.Version} loaded for review.");
            UpdateCreatorAccess();
        }
        catch (Exception exception)
        {
            ReportError("Open local project", exception);
        }
    }

    private async void RefreshDiscoverButton_OnClick(object sender, RoutedEventArgs e)
    {
        await RefreshDiscoverAsync(announceResult: true, lifetimeCancellation.Token);
    }

    private void DiscoverProjects_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var selected = DiscoverProjects.SelectedItem is DiscoverSurface;
        OpenDiscoverProjectButton.IsEnabled = selected;
        CopyDiscoverProjectButton.IsEnabled = selected;
        OpenDiscoverViewerButton.IsEnabled = selected
                                             && ViewerVqaAccessPolicy.CanUseViewer(
                                                 entitlement?.Plan ?? SubscriptionPlan.None,
                                                 entitlementVerified);
    }

    private void OpenInteractiveViewerButton_OnClick(object sender, RoutedEventArgs e) =>
        OpenInteractiveViewer(null);

    private void OpenDiscoverViewerButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DiscoverProjects.SelectedItem is DiscoverSurface project)
        {
            OpenInteractiveViewer(project);
        }
    }

    private void OpenInteractiveViewer(DiscoverSurface? publication)
    {
        var plan = entitlement?.Plan ?? SubscriptionPlan.None;
        if (!ViewerVqaAccessPolicy.CanUseViewer(plan, entitlementVerified))
        {
            Announce("A current verified Community or Creator subscription is required for moment-specific video questions.");
            return;
        }

        var viewer = new InteractiveVideoWindow(
            configuration,
            apiClient,
            plan,
            entitlementVerified,
            session?.Token,
            publication)
        {
            Owner = this,
        };
        viewer.Show();
        Announce("Accessible Interactive Video Viewer opened. Viewer questions do not change projects.");
    }

    private void OpenDiscoverProjectButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DiscoverProjects.SelectedItem is not DiscoverSurface project)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(project.CanonicalAdfiUrl.AbsoluteUri) { UseShellExecute = true });
            Announce($"Opening {project.Title} in the default browser.");
        }
        catch (Exception exception)
        {
            ReportError("Open DISCOVER project", exception);
        }
    }

    private void CopyDiscoverProjectButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (DiscoverProjects.SelectedItem is not DiscoverSurface project)
        {
            return;
        }

        try
        {
            Clipboard.SetText(project.CanonicalAdfiUrl.AbsoluteUri);
            Announce($"Copied the ADFi project page for {project.Title}.");
        }
        catch (Exception exception)
        {
            ReportError("Copy DISCOVER project page", exception);
        }
    }

    private async Task RestoreSessionAsync(CancellationToken cancellationToken)
    {
        try
        {
            session = await sessionStore.LoadAsync(cancellationToken);
            if (session is null)
            {
                UpdateAccountPresentation();
                return;
            }

            if (session.ExpiresAtUtc <= TimeProvider.System.GetUtcNow())
            {
                await sessionStore.DeleteAsync(cancellationToken);
                session = null;
                UpdateAccountPresentation();
                LogTechnical("Expired protected desktop session removed.");
                return;
            }

            UpdateAccountPresentation();
            try
            {
                await RefreshEntitlementAsync(announceResult: false, cancellationToken);
            }
            catch (Exception exception)
            {
                entitlement = null;
                entitlementVerified = false;
                UpdateAccountPresentation();
                LogTechnical($"Cached account restored; server entitlement not verified: {exception.GetType().Name}.");
            }
        }
        catch (Exception exception)
        {
            session = null;
            entitlement = null;
            entitlementVerified = false;
            UpdateAccountPresentation();
            LogTechnical($"Protected session could not be restored: {exception.GetType().Name}.");
        }
    }

    private async Task RefreshEntitlementAsync(bool announceResult, CancellationToken cancellationToken)
    {
        if (session is null)
        {
            entitlement = null;
            entitlementVerified = false;
            UpdateAccountPresentation();
            if (announceResult)
            {
                Announce("Sign in before refreshing subscription access.");
            }

            return;
        }

        try
        {
            entitlement = await apiClient.GetEntitlementAsync(session.Token, cancellationToken);
            entitlementVerified = true;
            UpdateAccountPresentation();
            if (entitlement.Plan == SubscriptionPlan.Creator && !hardwareProfileStore.HasProfile)
            {
                await RunCreatorHardwareProfileAsync(force: false, cancellationToken);
            }

            if (announceResult)
            {
                Announce(SubscriptionAnnouncement() + ".");
            }

            LogTechnical($"Server-authoritative entitlement refreshed: {entitlement.Status}.");
        }
        catch (AdfiApiException exception) when (exception.StatusCode == HttpStatusCode.Unauthorized)
        {
            await sessionStore.DeleteAsync(CancellationToken.None);
            session = null;
            entitlement = null;
            entitlementVerified = false;
            UpdateAccountPresentation();
            throw new AdfiApiException("The session expired. Sign in again.", exception.StatusCode, exception);
        }
        catch
        {
            entitlement = null;
            entitlementVerified = false;
            UpdateAccountPresentation();
            throw;
        }
    }

    private async Task RefreshDiscoverAsync(bool announceResult, CancellationToken cancellationToken)
    {
        RefreshDiscoverButton.IsEnabled = false;
        DiscoverStateText.Text = "Loading public projects.";
        try
        {
            var projects = await apiClient.GetDiscoverAsync(cancellationToken);
            DiscoverProjects.ItemsSource = projects;
            DiscoverStateText.Text = projects.Count == 0
                ? "No eligible public ADFied works are available yet."
                : $"{projects.Count} public ADFied works loaded.";
            if (announceResult)
            {
                Announce(DiscoverStateText.Text);
            }

            LogTechnical($"DISCOVER refresh completed with {projects.Count} eligible projects.");
        }
        catch (Exception exception)
        {
            DiscoverProjects.ItemsSource = null;
            DiscoverStateText.Text = "DISCOVER is temporarily unavailable. Use Refresh DISCOVER to try again.";
            if (announceResult)
            {
                ReportError("DISCOVER refresh", exception);
            }
            else
            {
                LogTechnical($"DISCOVER startup refresh failed: {exception.GetType().Name}.");
            }
        }
        finally
        {
            RefreshDiscoverButton.IsEnabled = true;
        }
    }

    private async Task LoadLocalProjectsAsync(
        CancellationToken cancellationToken,
        Guid? selectProjectId = null)
    {
        try
        {
            var projects = await projectCatalog.LoadAsync(cancellationToken);
            LocalProjectsList.ItemsSource = projects;
            if (selectProjectId is not null)
            {
                var selection = projects.FirstOrDefault(item => item.ProjectId == selectProjectId);
                if (selection is not null)
                {
                    LocalProjectsList.SelectedItem = selection;
                    LocalProjectsList.ScrollIntoView(selection);
                }
            }

            LogTechnical($"Local project catalog loaded with {projects.Count} projects.");
        }
        catch (Exception exception)
        {
            LocalProjectsList.ItemsSource = null;
            LogTechnical($"Local project catalog load failed: {exception.GetType().Name}.");
        }
    }

    private async Task RunCreatorHardwareProfileAsync(bool force, CancellationToken cancellationToken)
    {
        if (!entitlementVerified || entitlement?.Plan != SubscriptionPlan.Creator)
        {
            Announce("A current verified Creator subscription is required for hardware profiling.");
            return;
        }

        if (currentOperationCancellation is not null)
        {
            Announce("Wait for the current local operation to finish or cancel it first.");
            return;
        }

        currentOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        SetLocalOperationState(running: true);
        Announce(force
            ? "Rechecking hardware and running bounded local benchmarks."
            : "Creator access verified. Profiling hardware and running bounded local benchmarks.");
        try
        {
            var assessment = await hardwareProfileStore.GetOrMeasureAsync(
                force,
                currentOperationCancellation.Token);
            HardwareProfileText.Text = HardwareSummary(assessment);
            Announce(HardwareProfileText.Text);
            LogTechnical(
                $"Hardware profile saved. Tier {assessment.SelectedTier.Name}; " +
                $"inference {assessment.Profile.MiniatureInferenceTokensPerSecond:F2} tokens/second; " +
                $"transcode {assessment.Profile.MiniatureTranscodeFramesPerSecond:F2} frames/second.");
        }
        catch (Exception exception)
        {
            HardwareProfileText.Text = "Hardware profiling did not complete. Creator processing remains fail-closed.";
            ReportError("Hardware profile", exception);
        }
        finally
        {
            currentOperationCancellation.Dispose();
            currentOperationCancellation = null;
            SetLocalOperationState(running: false);
        }
    }

    private void UpdateAccountPresentation()
    {
        var signedIn = session is not null;
        SignedOutPanel.Visibility = signedIn ? Visibility.Collapsed : Visibility.Visible;
        SignedInPanel.Visibility = signedIn ? Visibility.Visible : Visibility.Collapsed;
        AccountStateText.Text = signedIn
            ? $"Signed in as {session!.Account.DisplayName}, {session.Account.Email}."
            : "Not signed in.";
        SubscriptionStateText.Text = signedIn
            ? SubscriptionAnnouncement() + "."
            : "Subscription access has not been verified.";
        UpdateCreatorAccess();
    }

    private string SubscriptionAnnouncement()
    {
        if (!entitlementVerified)
        {
            return "Subscription access is unverified, so paid features remain disabled";
        }

        if (entitlement?.Plan is null)
        {
            return $"No active subscription. Server status: {entitlement?.Status ?? "NONE"}";
        }

        return $"Active {entitlement.Plan} subscription verified";
    }

    private void UpdateCreatorAccess()
    {
        var plan = entitlement?.Plan ?? SubscriptionPlan.None;
        var rightsAccepted = RightsAttestationCheckBox.IsChecked == true;
        var operationRunning = currentOperationCancellation is not null;
        var selected = LocalProjectsList.SelectedItem is LocalProjectSummary;
        var canCreate = entitlementVerified && Entitlements.CanCreate(plan);
        var canReviewConversationally = CreatorToolAccess.CanUseConversationReview(
            plan,
            entitlementVerified,
            selected);

        OpenInteractiveViewerButton.IsEnabled = ViewerVqaAccessPolicy.CanUseViewer(plan, entitlementVerified);
        OpenDiscoverViewerButton.IsEnabled = DiscoverProjects.SelectedItem is DiscoverSurface
                                             && ViewerVqaAccessPolicy.CanUseViewer(plan, entitlementVerified);

        RunHardwareProfileButton.IsEnabled = canCreate && !operationRunning;
        RightsAttestationCheckBox.IsEnabled = canCreate && !operationRunning;
        SelectLocalVideoButton.IsEnabled = CreatorToolAccess.CanSelectLocalSource(
            plan,
            entitlementVerified,
            rightsAccepted) && !operationRunning;
        OpenLocalProjectButton.IsEnabled = selected && !operationRunning;
        ReviewGroup.IsEnabled = selected;
        ConversationTextBox.IsEnabled = canReviewConversationally;
        SubmitConversationButton.IsEnabled = false;
    }

    private void SetLocalOperationState(bool running)
    {
        CancelOperationButton.IsEnabled = running;
        LocalProjectsList.IsEnabled = !running;
        UpdateCreatorAccess();
    }

    private void Announce(string message)
    {
        StatusText.Text = message;
        _ = Dispatcher.BeginInvoke(() =>
        {
            var peer = FrameworkElementAutomationPeer.FromElement(StatusText)
                       ?? new TextBlockAutomationPeer(StatusText);
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        });
    }

    private void ReportError(string operation, Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            Announce($"{operation} cancelled. Completed checkpoints were preserved.");
        }
        else
        {
            Announce($"{operation} failed. {exception.Message}");
        }

        LogTechnical($"{operation}: {exception.GetType().Name}. {exception.Message}");
    }

    private void LogTechnical(string message)
    {
        TechnicalActivityTextBox.AppendText($"{DateTimeOffset.Now:HH:mm:ss}  {message}{Environment.NewLine}");
        TechnicalActivityTextBox.ScrollToEnd();
    }

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} hours, {duration.Minutes} minutes, {duration.Seconds} seconds"
            : $"{duration.Minutes} minutes, {duration.Seconds} seconds";

    private static string FormatTime(TimeSpan value) =>
        $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}.{value.Milliseconds:000}";

    private static string HardwareSummary(CreatorHardwareAssessment assessment)
    {
        var profile = assessment.Profile;
        var gpu = profile.GpuModel is null
            ? "CPU fallback"
            : $"{profile.GpuModel}, {ToGibibytes(profile.FreeVideoMemoryBytes):F1} GB VRAM free";
        var benchmark = profile.MiniatureInferenceTokensPerSecond > 0
            ? $"{profile.MiniatureInferenceTokensPerSecond:F1} local-model tokens per second"
            : "local-model benchmark deferred";
        return $"Recommended {assessment.SelectedTier.Name} tier, {assessment.SelectedTier.OllamaModel}. " +
               $"{ToGibibytes(profile.FreeSystemMemoryBytes):F1} GB RAM free; {gpu}; " +
               $"{benchmark}; {profile.MiniatureTranscodeFramesPerSecond:F1} transcode frames per second.";
    }

    private static decimal ToGibibytes(long bytes) => bytes / (1024m * 1024m * 1024m);
}
