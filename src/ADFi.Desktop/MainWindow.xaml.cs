using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using ADFi.Core;
using ADFi.Conversation;
using ADFi.Infrastructure;
using ADFi.Infrastructure.Security;
using ADFi.LocalAI;
using ADFi.Media;
using ADFi.Web;
using Microsoft.Win32;

namespace ADFi.Desktop;

public partial class MainWindow : Window, IDisposable
{
    private static readonly JsonSerializerOptions ConversationJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

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
    private System.Windows.Media.MediaPlayer? narrationVoicePreviewPlayer;
    private ProjectSnapshot? openedProjectSnapshot;
    private int? finalValidationSnapshotVersion;
    private bool finalValidationReady;
    private string? finalValidationBindingSha256;
    private readonly HashSet<string> finalValidationOverrideIds = new(StringComparer.Ordinal);
    private string? finalOutputPath;
    private string? loadedDraftPreviewPath;
    private bool playDraftPreviewWhenOpened;
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
        NarrationVoiceComboBox.ItemsSource = PiperVoiceCatalog.All;
        NarrationVoiceComboBox.SelectedIndex = 0;
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
        narrationVoicePreviewPlayer?.Close();
        DraftPreviewPlayer.Close();
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
            ClearOpenedProjectReview();
            UpdateCreatorAccess();
            return;
        }

        if (openedProjectSnapshot?.ProjectId != project.ProjectId)
        {
            ClearOpenedProjectReview();
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
            PresentProjectSnapshot(snapshot);
            await LoadConversationHistoryAsync(project.ProjectDirectory, lifetimeCancellation.Token);
            await LoadFinalValidationStateAsync(project.ProjectDirectory, snapshot, lifetimeCancellation.Token);
            await LoadFinalRenderStateAsync(project.ProjectDirectory, snapshot, lifetimeCancellation.Token);
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

    private async void RunPreparationButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (LocalProjectsList.SelectedItem is not LocalProjectSummary project
            || !entitlementVerified
            || entitlement?.Plan != SubscriptionPlan.Creator)
        {
            Announce("Select a local project with a current verified Creator subscription first.");
            return;
        }

        if (currentOperationCancellation is not null)
        {
            Announce("Wait for the current local operation to finish or cancel it first.");
            return;
        }

        var assessment = await hardwareProfileStore.LoadAsync(lifetimeCancellation.Token);
        if (assessment is null)
        {
            Announce("Run the Creator hardware profile before audio and scene analysis.");
            RunHardwareProfileButton.Focus();
            return;
        }

        currentOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        SetLocalOperationState(running: true);
        PipelineProgressBar.IsIndeterminate = false;
        Announce("Resuming from the last verified checkpoint. Heavyweight stages run one at a time.");
        try
        {
            var resourceProfiler = new SystemHardwareProfiler(
                project.ProjectDirectory,
                new ProcessHardwareCommandRunner());
            var (freeRamBytes, freeVramBytes) = await resourceProfiler.GetCurrentFreeResourcesAsync(
                currentOperationCancellation.Token);
            var currentProfile = assessment.Profile with
            {
                FreeSystemMemoryBytes = freeRamBytes,
                FreeVideoMemoryBytes = freeVramBytes,
            };
            var activeTier = ModelTierPolicy.Select(currentProfile);
            if (freeRamBytes < activeTier.MinimumFreeRamBytes)
            {
                throw new InvalidOperationException(
                    "Not enough free memory for safe local processing. Close other applications, then resume this project.");
            }

            LogTechnical(
                $"Project {project.ProjectId:D}: current free resources select {activeTier.Name} bounds; " +
                $"Whisper remains CPU-only and unloads before OpenCV indexing.");
            var pythonExecutable = Path.Combine(
                configuration.LocalAiRuntimeRoot,
                "python",
                "Scripts",
                "python.exe");
            var workerPath = Path.Combine(AppContext.BaseDirectory, "Workers", "whisper_worker.py");
            var modelDirectory = Path.Combine(configuration.LocalModelRoot, "whisper");
            var workflow = new CreatorPreparationWorkflow(
                new FilePipelineCheckpointStore(configuration.ProjectRoot),
                TimeProvider.System,
                () => new OpenAiWhisperRuntime(new(
                    pythonExecutable,
                    workerPath,
                    modelDirectory)));
            var progress = new Progress<CreatorPreparationProgress>(item =>
            {
                var fraction = item.TotalUnits <= 0
                    ? 0
                    : Math.Clamp((double)item.CompletedUnits / item.TotalUnits, 0, 1);
                PipelineProgressBar.Value = Math.Clamp((int)item.Phase + fraction, 0, 14);
                Announce(item.Announcement);
                LogTechnical($"Project {project.ProjectId:D}: {item.Announcement}");
            });
            var result = await workflow.RunAsync(
                project.ProjectId,
                project.ProjectDirectory,
                new(
                    "ffmpeg",
                    new(pythonExecutable, workerPath, modelDirectory),
                    WhisperModelPolicy.Select(freeRamBytes),
                    activeTier.MaximumImageDimension,
                    activeTier.MaximumTemporalFrames),
                progress,
                currentOperationCancellation.Token);
            await LoadLocalProjectsAsync(currentOperationCancellation.Token, project.ProjectId);
            PipelineProgressBar.Value = 3;
            Announce(
                $"Audio analysis and scene index complete. {result.AudioAnalysis.Transcript.Count} transcript segments and " +
                $"{result.VisualIndex.Scenes.Count} visual scenes are ready for recurring-character and text analysis.");
        }
        catch (Exception exception)
        {
            ReportError("Audio and scene analysis", exception);
        }
        finally
        {
            currentOperationCancellation.Dispose();
            currentOperationCancellation = null;
            SetLocalOperationState(running: false);
        }
    }

    private async void RunVisualUnderstandingButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (LocalProjectsList.SelectedItem is not LocalProjectSummary project
            || !entitlementVerified
            || entitlement?.Plan != SubscriptionPlan.Creator)
        {
            Announce("Select a prepared local project with a current verified Creator subscription first.");
            return;
        }

        if (currentOperationCancellation is not null)
        {
            Announce("Wait for the current local operation to finish or cancel it first.");
            return;
        }

        var assessment = await hardwareProfileStore.LoadAsync(lifetimeCancellation.Token);
        if (assessment is null)
        {
            Announce("Run the Creator hardware profile before visual understanding.");
            RunHardwareProfileButton.Focus();
            return;
        }

        currentOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        SetLocalOperationState(running: true);
        PipelineProgressBar.IsIndeterminate = false;
        Announce("Resuming recurring-character, on-screen-text, and scene analysis from verified checkpoints.");
        try
        {
            var resourceProfiler = new SystemHardwareProfiler(
                project.ProjectDirectory,
                new ProcessHardwareCommandRunner());
            var (freeRamBytes, freeVramBytes) = await resourceProfiler.GetCurrentFreeResourcesAsync(
                currentOperationCancellation.Token);
            var activeTier = ModelTierPolicy.Select(assessment.Profile with
            {
                FreeSystemMemoryBytes = freeRamBytes,
                FreeVideoMemoryBytes = freeVramBytes,
            });
            if (freeRamBytes < activeTier.MinimumFreeRamBytes)
            {
                throw new InvalidOperationException(
                    "Not enough free memory for safe visual analysis. Close other applications, then resume this project.");
            }

            var openCvModels = Path.Combine(configuration.LocalModelRoot, "opencv");
            var workflow = new CreatorVisualUnderstandingWorkflow(
                new FilePipelineCheckpointStore(configuration.ProjectRoot),
                TimeProvider.System,
                () => new OpenCvFaceContinuityAnalyzer(
                    Path.Combine(openCvModels, "face_detection_yunet_2023mar.onnx"),
                    Path.Combine(openCvModels, "face_recognition_sface_2021dec.onnx")),
                () => new OllamaModelRuntime(new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:11434/"),
                    Timeout = TimeSpan.FromMinutes(5),
                }),
                new TesseractProcessRunner());
            var progress = new Progress<CreatorVisualUnderstandingProgress>(item =>
            {
                var fraction = item.TotalUnits <= 0
                    ? 0
                    : Math.Clamp((double)item.CompletedUnits / item.TotalUnits, 0, 1);
                PipelineProgressBar.Value = Math.Clamp((int)item.Phase + fraction, 0, 14);
                Announce(item.Announcement);
                LogTechnical($"Project {project.ProjectId:D}: {item.Announcement}");
            });
            LogTechnical(
                $"Project {project.ProjectId:D}: current resources select {activeTier.Name} visual bounds and {activeTier.OllamaModel}.");
            var result = await workflow.RunAsync(
                project.ProjectId,
                project.ProjectDirectory,
                new(
                    activeTier.OllamaModel,
                    Path.Combine(openCvModels, "face_detection_yunet_2023mar.onnx"),
                    Path.Combine(openCvModels, "face_recognition_sface_2021dec.onnx"),
                    ResolveTesseractPath(),
                    Path.Combine(AppContext.BaseDirectory, "prompts", "ADFI_VISUAL_TEXT_SYSTEM.md"),
                    Path.Combine(AppContext.BaseDirectory, "prompts", "ADFI_VISUAL_ANALYST_SYSTEM.md")),
                progress,
                currentOperationCancellation.Token);
            await LoadLocalProjectsAsync(currentOperationCancellation.Token, project.ProjectId);
            PipelineProgressBar.Value = 6;
            Announce(
                $"Visual understanding complete. {result.FaceContinuity.Clusters.Count} anonymous recurring people, " +
                $"{result.VisualText.Scenes.Count} text-bearing scene results, and " +
                $"{result.SceneAnalysis.Scenes.Count} evidence-grounded scene analyses are ready.");
        }
        catch (Exception exception)
        {
            ReportError("Visual understanding", exception);
        }
        finally
        {
            currentOperationCancellation.Dispose();
            currentOperationCancellation = null;
            SetLocalOperationState(running: false);
        }
    }

    private async void PreviewNarrationVoiceButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (!entitlementVerified
            || entitlement?.Plan != SubscriptionPlan.Creator
            || NarrationVoiceComboBox.SelectedItem is not PiperVoice voice)
        {
            Announce("A current verified Creator subscription and narration voice are required.");
            return;
        }

        if (currentOperationCancellation is not null)
        {
            Announce("Wait for the current local operation to finish or cancel it first.");
            return;
        }

        currentOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        SetLocalOperationState(running: true);
        Announce($"Creating a short preview for {voice.AccessibleName}.");
        try
        {
            var service = new PiperNarrationService(
                new(ResolveLocalAiPythonPath(), Path.Combine(configuration.LocalModelRoot, "piper")),
                Path.Combine(configuration.LocalAiRuntimeRoot, "voice-preview-cache"),
                new PiperProcessRunner());
            var preview = await service.SynthesizeAsync(
                new(
                    "voice-preview",
                    "ADFi describes each important visual moment clearly and concisely.",
                    voice.Id,
                    TimeSpan.FromSeconds(12),
                    TimeSpan.FromMilliseconds(250),
                    1),
                currentOperationCancellation.Token);
            narrationVoicePreviewPlayer?.Close();
            narrationVoicePreviewPlayer = new System.Windows.Media.MediaPlayer();
            narrationVoicePreviewPlayer.Open(new Uri(preview.AudioPath, UriKind.Absolute));
            narrationVoicePreviewPlayer.Play();
            Announce($"Playing {voice.AccessibleName} preview.");
        }
        catch (Exception exception)
        {
            ReportError("Narration voice preview", exception);
        }
        finally
        {
            currentOperationCancellation.Dispose();
            currentOperationCancellation = null;
            SetLocalOperationState(running: false);
        }
    }

    private async void RunDraftNarrationButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (LocalProjectsList.SelectedItem is not LocalProjectSummary project
            || !entitlementVerified
            || entitlement?.Plan != SubscriptionPlan.Creator
            || project.LatestPhase < AdficationPhase.VlmSceneAnalytics
            || NarrationVoiceComboBox.SelectedItem is not PiperVoice voice)
        {
            Announce("Select a visually analyzed project and narration voice with a current Creator subscription first.");
            return;
        }

        if (currentOperationCancellation is not null)
        {
            Announce("Wait for the current local operation to finish or cancel it first.");
            return;
        }

        var assessment = await hardwareProfileStore.LoadAsync(lifetimeCancellation.Token);
        if (assessment is null)
        {
            Announce("Run the Creator hardware profile before description drafting.");
            RunHardwareProfileButton.Focus();
            return;
        }

        currentOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        SetLocalOperationState(running: true);
        PipelineProgressBar.IsIndeterminate = false;
        Announce("Selecting necessary descriptions, then drafting and narrating one segment at a time.");
        try
        {
            var resourceProfiler = new SystemHardwareProfiler(
                project.ProjectDirectory,
                new ProcessHardwareCommandRunner());
            var (freeRamBytes, freeVramBytes) = await resourceProfiler.GetCurrentFreeResourcesAsync(
                currentOperationCancellation.Token);
            var activeTier = ModelTierPolicy.Select(assessment.Profile with
            {
                FreeSystemMemoryBytes = freeRamBytes,
                FreeVideoMemoryBytes = freeVramBytes,
            });
            if (freeRamBytes < activeTier.MinimumFreeRamBytes)
            {
                throw new InvalidOperationException(
                    "Not enough free memory for safe drafting. Close other applications, then resume this project.");
            }

            var workflow = new CreatorDraftNarrationWorkflow(
                new FilePipelineCheckpointStore(configuration.ProjectRoot),
                TimeProvider.System,
                () => new OllamaModelRuntime(new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:11434/"),
                    Timeout = TimeSpan.FromMinutes(5),
                }),
                root => new PiperNarrationService(
                    new(ResolveLocalAiPythonPath(), Path.Combine(configuration.LocalModelRoot, "piper")),
                    Path.Combine(root, "narration", "cache"),
                    new PiperProcessRunner()));
            var progress = new Progress<CreatorDraftNarrationProgress>(item =>
            {
                var fraction = item.TotalUnits <= 0
                    ? 0
                    : Math.Clamp((double)item.CompletedUnits / item.TotalUnits, 0, 1);
                PipelineProgressBar.Value = Math.Clamp((int)item.Phase + fraction, 0, 14);
                Announce(item.Announcement);
                LogTechnical($"Project {project.ProjectId:D}: {item.Announcement}");
            });
            LogTechnical(
                $"Project {project.ProjectId:D}: drafting uses {activeTier.OllamaModel}; " +
                "Piper and Qwen operations remain sequential.");
            var result = await workflow.RunAsync(
                project.ProjectId,
                project.ProjectDirectory,
                new(
                    activeTier.OllamaModel,
                    Path.Combine(AppContext.BaseDirectory, "prompts", "ADFI_AD_WRITER_SYSTEM.md"),
                    voice.Id,
                    TimeSpan.FromSeconds(2),
                    TimeSpan.FromMilliseconds(250)),
                progress,
                currentOperationCancellation.Token);
            await PersistInitialDraftAsync(project, result, currentOperationCancellation.Token);
            await LoadLocalProjectsAsync(currentOperationCancellation.Token, project.ProjectId);
            var snapshot = await new FileProjectVersionStore(configuration.ProjectRoot)
                .LoadCurrentAsync(project.ProjectId, currentOperationCancellation.Token);
            PresentProjectSnapshot(snapshot);
            PipelineProgressBar.Value = 9;
            var reviewCount = result.Narration.Segments.Count(item => item.RequiresUserReview);
            Announce(
                $"Audio description draft and measured narration complete. {result.Narration.Segments.Count} segments are ready; " +
                (reviewCount == 0
                    ? "all fit their validated windows."
                    : $"{reviewCount} require timing review before finalization."));
        }
        catch (Exception exception)
        {
            ReportError("Description drafting and narration", exception);
        }
        finally
        {
            currentOperationCancellation.Dispose();
            currentOperationCancellation = null;
            SetLocalOperationState(running: false);
        }
    }

    private async void BuildDraftPreviewButton_OnClick(object sender, RoutedEventArgs e) =>
        await BuildAndLoadDraftPreviewAsync(focusWindow: null, autoPlay: false);

    private async void PlaySelectionPreviewButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (LocalProjectsList.SelectedItem is not LocalProjectSummary project
            || DescriptionsList.SelectedItem is not ReviewDescriptionItem selection)
        {
            Announce("Select an audio description first.");
            return;
        }

        var snapshot = openedProjectSnapshot;
        if (snapshot is null || snapshot.ProjectId != project.ProjectId)
        {
            snapshot = await new FileProjectVersionStore(configuration.ProjectRoot)
                .LoadCurrentAsync(project.ProjectId, lifetimeCancellation.Token);
            PresentProjectSnapshot(snapshot);
        }

        var start = selection.Segment.Window.Start - TimeSpan.FromSeconds(5);
        var end = selection.Segment.Window.End + TimeSpan.FromSeconds(5);
        var focus = new MediaInterval(
            start < TimeSpan.Zero ? TimeSpan.Zero : start,
            end > snapshot.MediaDuration ? snapshot.MediaDuration : end);
        await BuildAndLoadDraftPreviewAsync(focus, autoPlay: true);
    }

    private async Task BuildAndLoadDraftPreviewAsync(MediaInterval? focusWindow, bool autoPlay)
    {
        if (LocalProjectsList.SelectedItem is not LocalProjectSummary project
            || !entitlementVerified
            || entitlement?.Plan != SubscriptionPlan.Creator)
        {
            Announce("Select a narrated local project with a current Creator subscription first.");
            return;
        }

        if (currentOperationCancellation is not null)
        {
            Announce("Wait for the current local operation to finish or cancel it first.");
            return;
        }

        DraftPreviewPlayer.Stop();
        DraftPreviewPlayer.Close();
        loadedDraftPreviewPath = null;
        currentOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        SetLocalOperationState(running: true);
        PipelineProgressBar.IsIndeterminate = true;
        Announce(focusWindow is null
            ? "Validating and building the full audio-described draft preview."
            : "Validating and building a short preview around the selected description.");
        try
        {
            var workflow = new CreatorPreviewWorkflow(new PreviewAssemblyService(
                new FinalRenderService("ffmpeg", new FfprobeMediaProbe()),
                "ffmpeg",
                new FilePipelineCheckpointStore(configuration.ProjectRoot),
                TimeProvider.System));
            var progress = new Progress<CreatorPreviewProgress>(item =>
            {
                Announce(item.Announcement);
                LogTechnical($"Project {project.ProjectId:D}: {item.Announcement}");
            });
            var result = await workflow.RunAsync(
                project.ProjectId,
                project.ProjectDirectory,
                focusWindow,
                progress,
                currentOperationCancellation.Token,
                openedProjectSnapshot);
            await LoadLocalProjectsAsync(currentOperationCancellation.Token, project.ProjectId);
            PresentProjectSnapshot(await new FileProjectVersionStore(configuration.ProjectRoot)
                .LoadCurrentAsync(project.ProjectId, currentOperationCancellation.Token));
            LoadDraftPreview(
                focusWindow is null ? result.FullDraftPath : result.FocusPreviewPath!,
                autoPlay,
                focusWindow is null ? "full draft" : "selected focus preview");
            PipelineProgressBar.Value = 10;
            Announce(result.ReusedFullDraft
                ? focusWindow is null
                    ? "Verified full draft preview restored. Use Play preview to begin."
                    : "Short focus preview ready and playing. The verified full draft was reused."
                : "Full audio-described draft preview built and verified. Use Play preview to begin.");
        }
        catch (Exception exception)
        {
            ReportError("Draft preview", exception);
        }
        finally
        {
            PipelineProgressBar.IsIndeterminate = false;
            currentOperationCancellation.Dispose();
            currentOperationCancellation = null;
            SetLocalOperationState(running: false);
        }
    }

    private void DescriptionsList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateCreatorAccess();

    private void PlayDraftPreviewButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (loadedDraftPreviewPath is null)
        {
            Announce("Build or load a draft preview first.");
            return;
        }

        DraftPreviewPlayer.Play();
        DraftPreviewStatusText.Text = "Draft preview playing.";
        Announce("Draft preview playing.");
    }

    private void PauseDraftPreviewButton_OnClick(object sender, RoutedEventArgs e)
    {
        DraftPreviewPlayer.Pause();
        DraftPreviewStatusText.Text = "Draft preview paused.";
        Announce("Draft preview paused.");
    }

    private void DraftPreviewPlayer_OnMediaOpened(object sender, RoutedEventArgs e)
    {
        DraftPreviewPlayer.Volume = 1;
        if (playDraftPreviewWhenOpened)
        {
            DraftPreviewPlayer.Play();
            DraftPreviewStatusText.Text = "Selected focus preview playing.";
        }
        else
        {
            DraftPreviewPlayer.Pause();
            DraftPreviewStatusText.Text = "Draft preview loaded and paused.";
        }

        UpdateCreatorAccess();
    }

    private void DraftPreviewPlayer_OnMediaEnded(object sender, RoutedEventArgs e)
    {
        DraftPreviewStatusText.Text = "Draft preview ended.";
        Announce("Draft preview ended.");
    }

    private void DraftPreviewPlayer_OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        loadedDraftPreviewPath = null;
        DraftPreviewStatusText.Text = "Draft preview playback failed.";
        Announce("Draft preview playback failed. Rebuild the preview or check the media components.");
        LogTechnical($"Draft preview playback failed: {e.ErrorException?.GetType().Name ?? "unknown error"}.");
        UpdateCreatorAccess();
    }

    private void ConversationTextBox_OnTextChanged(object sender, TextChangedEventArgs e) =>
        UpdateCreatorAccess();

    private async void ConversationTextBox_OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter
            || Keyboard.Modifiers is not (ModifierKeys.None or ModifierKeys.Control))
        {
            return;
        }

        e.Handled = true;
        await RunCreatorConversationAsync(ConversationTextBox.Text);
    }

    private async void SubmitConversationButton_OnClick(object sender, RoutedEventArgs e) =>
        await RunCreatorConversationAsync(ConversationTextBox.Text);

    private async void UndoLastChangeButton_OnClick(object sender, RoutedEventArgs e) =>
        await RunCreatorConversationAsync("Undo that");

    private async void RunFinalValidationButton_OnClick(object sender, RoutedEventArgs e) =>
        await RunFinalValidationAsync();

    private void FinalValidationIssuesList_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateCreatorAccess();

    private async void OverrideSelectedWarningButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (FinalValidationIssuesList.SelectedItem is not FinalValidationIssueItem selected
            || !selected.Issue.CanOverride)
        {
            Announce("Select a nonblocking final-validation warning first. Blocking issues cannot be overridden.");
            return;
        }

        finalValidationOverrideIds.Add(selected.Issue.IssueId);
        Announce($"Override recorded for {selected.Issue.Code}. Running every final check again.");
        await RunFinalValidationAsync();
    }

    private async void RenderFinalVideoButton_OnClick(object sender, RoutedEventArgs e) =>
        await RenderFinalVideoAsync();

    private void OpenFinalOutputButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (finalOutputPath is null || !File.Exists(finalOutputPath))
        {
            Announce("The final output is unavailable. Render or verify the final bundle again.");
            ClearFinalRenderState();
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = Path.GetDirectoryName(finalOutputPath)!,
            UseShellExecute = true,
        });
        Announce("Final output folder opened.");
    }

    private async Task RenderFinalVideoAsync()
    {
        if (LocalProjectsList.SelectedItem is not LocalProjectSummary project
            || !entitlementVerified
            || entitlement?.Plan != SubscriptionPlan.Creator
            || openedProjectSnapshot is not { AdSegments.Count: > 0 } snapshot
            || !finalValidationReady
            || finalValidationSnapshotVersion != snapshot.Version)
        {
            Announce("Run final validation successfully for the exact opened project version first.");
            return;
        }

        if (currentOperationCancellation is not null)
        {
            Announce("Wait for the current local operation to finish or cancel it first.");
            return;
        }

        currentOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        SetLocalOperationState(running: true);
        PipelineProgressBar.IsIndeterminate = false;
        FinalRenderStatusText.Text = "Rendering final ADFied MP4.";
        Announce("Rendering final ADFied MP4.");
        try
        {
            var mediaProbe = new FfprobeMediaProbe();
            var workflow = new CreatorFinalizationWorkflow(
                new FileProjectVersionStore(configuration.ProjectRoot),
                new FilePipelineCheckpointStore(configuration.ProjectRoot),
                mediaProbe,
                new FinalRenderService("ffmpeg", mediaProbe),
                TimeProvider.System);
            var progress = new Progress<CreatorFinalizationProgress>(item =>
            {
                PipelineProgressBar.Value = 12 + Math.Clamp(
                    item.TotalSteps == 0 ? 0 : (double)item.CompletedSteps / item.TotalSteps,
                    0,
                    1);
                FinalRenderStatusText.Text = item.Announcement;
                Announce(item.Announcement);
                LogTechnical($"Project {project.ProjectId:D}: {item.Announcement}");
            });
            var result = await workflow.RunAsync(
                new(
                    project.ProjectId,
                    project.ProjectDirectory,
                    IncludeNarrationAssetsCheckBox.IsChecked == true),
                progress,
                currentOperationCancellation.Token);
            finalOutputPath = result.FinalVideoPath;
            await LoadLocalProjectsAsync(currentOperationCancellation.Token, project.ProjectId);
            PipelineProgressBar.Value = 13;
            PresentFinalRender(result.Manifest, result.FinalVideoPath);
            Announce(result.ReusedVerifiedBundle
                ? "Verified final ADFied MP4 restored. Open final output folder is available."
                : result.Manifest.VideoStreamCopied
                    ? "Final ADFied MP4 complete with zero additional video-quality loss. Open final output folder is available."
                    : "Final ADFied MP4 complete using the transparent video profile. Open final output folder is available.");
        }
        catch (Exception exception)
        {
            FinalRenderStatusText.Text = "Final rendering did not complete. Verified intermediate work was preserved.";
            ReportError("Final rendering", exception);
        }
        finally
        {
            currentOperationCancellation.Dispose();
            currentOperationCancellation = null;
            SetLocalOperationState(running: false);
        }
    }

    private async Task RunFinalValidationAsync()
    {
        if (LocalProjectsList.SelectedItem is not LocalProjectSummary project
            || !entitlementVerified
            || entitlement?.Plan != SubscriptionPlan.Creator
            || openedProjectSnapshot is not { AdSegments.Count: > 0 })
        {
            Announce("Open a narrated project with a current verified Creator subscription first.");
            return;
        }

        if (currentOperationCancellation is not null)
        {
            Announce("Wait for the current local operation to finish or cancel it first.");
            return;
        }

        currentOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        SetLocalOperationState(running: true);
        PipelineProgressBar.IsIndeterminate = true;
        FinalValidationStatusText.Text = "Checking the committed project and immutable source.";
        Announce("Running final project validation.");
        try
        {
            var service = new FinalProjectValidationService(
                new FileProjectVersionStore(configuration.ProjectRoot),
                new FilePipelineCheckpointStore(configuration.ProjectRoot),
                new FfprobeMediaProbe(),
                TimeProvider.System);
            var result = await service.RunAsync(
                project.ProjectId,
                project.ProjectDirectory,
                finalValidationOverrideIds,
                currentOperationCancellation.Token);
            await LoadLocalProjectsAsync(currentOperationCancellation.Token, project.ProjectId);
            PresentProjectSnapshot(result.Snapshot);
            PresentFinalValidation(result.Manifest);
            PipelineProgressBar.Value = result.ReadyForRender ? 12 : 11;
            if (result.ReadyForRender)
            {
                Announce("Final project validation complete. This committed version is ready for final rendering.");
            }
            else
            {
                var blockers = result.Manifest.Issues.Count(item => item.Severity == FinalValidationSeverity.Blocking);
                var warnings = result.Manifest.Issues.Count(item => item.CanOverride && !item.Overridden);
                Announce($"Final validation stopped rendering. {blockers} blocking issues and {warnings} warnings remain. Review the issue list.");
                FinalValidationIssuesList.Focus();
            }

            LogTechnical(
                $"Project {project.ProjectId:D}: Phase 11 {result.Manifest.State}; " +
                $"version {result.Manifest.ProjectVersion}, {result.Manifest.Issues.Count} issue(s), " +
                $"{result.Manifest.NarrationAssets.Count} narration asset(s) audited.");
        }
        catch (Exception exception)
        {
            ReportError("Final project validation", exception);
        }
        finally
        {
            PipelineProgressBar.IsIndeterminate = false;
            currentOperationCancellation.Dispose();
            currentOperationCancellation = null;
            SetLocalOperationState(running: false);
        }
    }

    private async Task RunCreatorConversationAsync(string request)
    {
        if (LocalProjectsList.SelectedItem is not LocalProjectSummary project
            || !entitlementVerified
            || entitlement?.Plan != SubscriptionPlan.Creator
            || openedProjectSnapshot is null
            || openedProjectSnapshot.AdSegments.Count == 0)
        {
            Announce("Open a narrated project with a current verified Creator subscription first.");
            return;
        }

        request = request.Trim();
        if (request.Length == 0)
        {
            Announce("Enter a project request first.");
            ConversationTextBox.Focus();
            return;
        }

        if (currentOperationCancellation is not null)
        {
            Announce("Wait for the current local operation to finish or cancel it first.");
            return;
        }

        var assessment = await hardwareProfileStore.LoadAsync(lifetimeCancellation.Token);
        if (assessment is null)
        {
            Announce("Run the Creator hardware profile before conversational review.");
            RunHardwareProfileButton.Focus();
            return;
        }

        var selectedSegmentId = (DescriptionsList.SelectedItem as ReviewDescriptionItem)?.Segment.SegmentId;
        MediaInterval? requestedPreview = null;
        var previewRequested = false;
        currentOperationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
        SetLocalOperationState(running: true);
        DraftPreviewPlayer.Stop();
        DraftPreviewPlayer.Close();
        loadedDraftPreviewPath = null;
        AppendConversationHistory("You", request);
        ConversationTextBox.Clear();
        Announce("Planning this project request as bounded atomic tasks.");
        try
        {
            var resourceProfiler = new SystemHardwareProfiler(
                project.ProjectDirectory,
                new ProcessHardwareCommandRunner());
            var (freeRamBytes, freeVramBytes) = await resourceProfiler.GetCurrentFreeResourcesAsync(
                currentOperationCancellation.Token);
            var activeTier = ModelTierPolicy.Select(assessment.Profile with
            {
                FreeSystemMemoryBytes = freeRamBytes,
                FreeVideoMemoryBytes = freeVramBytes,
            });
            if (freeRamBytes < activeTier.MinimumFreeRamBytes)
            {
                throw new InvalidOperationException(
                    "Not enough free memory for safe conversational review. Close other applications, then try again.");
            }

            var workflow = new CreatorConversationReviewWorkflow(
                new FileProjectVersionStore(configuration.ProjectRoot),
                () => new OllamaModelRuntime(new HttpClient
                {
                    BaseAddress = new Uri("http://127.0.0.1:11434/"),
                    Timeout = TimeSpan.FromMinutes(5),
                }),
                root => new PiperNarrationService(
                    new(ResolveLocalAiPythonPath(), Path.Combine(configuration.LocalModelRoot, "piper")),
                    Path.Combine(root, "narration", "cache"),
                    new PiperProcessRunner()),
                TimeProvider.System);
            var progress = new Progress<CreatorConversationProgress>(item =>
            {
                Announce(item.Announcement);
                LogTechnical(
                    $"Project {project.ProjectId:D}: conversation task {item.CompletedTasks} of {item.TotalTasks}. {item.Announcement}");
            });
            var result = await workflow.RunAsync(
                project.ProjectId,
                project.ProjectDirectory,
                request,
                selectedSegmentId,
                new(
                    activeTier.OllamaModel,
                    Path.Combine(AppContext.BaseDirectory, "prompts", "ADFI_PLANNER_SYSTEM.md"),
                    Path.Combine(AppContext.BaseDirectory, "prompts", "ADFI_TASK_EXECUTOR_SYSTEM.md"),
                    Path.Combine(AppContext.BaseDirectory, "prompts", "semantic-tools.json"),
                    Path.Combine(AppContext.BaseDirectory, "prompts", "ADFI_AD_WRITER_SYSTEM.md"),
                    openedProjectSnapshot.NarrationVoiceId,
                    TimeSpan.FromMilliseconds(250)),
                progress,
                currentOperationCancellation.Token);
            PresentProjectSnapshot(result.Snapshot);
            SelectDescription(result.SelectedSegmentId);
            AppendConversationHistory("ADFi", result.Manifest.Response);
            previewRequested = result.Manifest.PreviewRequested;
            requestedPreview = result.Manifest.PreviewWindow;
            PipelineProgressBar.Value = 11;
            Announce(result.Manifest.Response);
            LogTechnical(
                $"Project {project.ProjectId:D}: conversation turn {result.Manifest.TurnId:D} completed with " +
                $"{result.Manifest.Results.Count} persisted task result(s). Version {result.Snapshot.Version}.");
        }
        catch (Exception exception)
        {
            AppendConversationHistory("ADFi", $"Request failed safely. {exception.Message}");
            ReportError("Creator conversation", exception);
        }
        finally
        {
            currentOperationCancellation.Dispose();
            currentOperationCancellation = null;
            SetLocalOperationState(running: false);
            ConversationTextBox.Focus();
        }

        if (previewRequested)
        {
            await BuildAndLoadDraftPreviewAsync(requestedPreview, autoPlay: true);
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
        RunPreparationButton.IsEnabled = canCreate
                                         && selected
                                         && !operationRunning
                                         && LocalProjectsList.SelectedItem is LocalProjectSummary project
                                         && (project.LatestPhase < AdficationPhase.VisualSegmentationAndKeyframes
                                             || project.LatestStatus != CheckpointStatus.Complete);
        RunVisualUnderstandingButton.IsEnabled = canCreate
                                                 && selected
                                                 && !operationRunning
                                                 && LocalProjectsList.SelectedItem is LocalProjectSummary visualProject
                                                 && visualProject.LatestPhase >= AdficationPhase.VisualSegmentationAndKeyframes
                                                 && (visualProject.LatestPhase < AdficationPhase.VlmSceneAnalytics
                                                     || visualProject.LatestStatus != CheckpointStatus.Complete);
        NarrationVoiceComboBox.IsEnabled = canCreate && !operationRunning;
        PreviewNarrationVoiceButton.IsEnabled = canCreate
                                                && !operationRunning
                                                && NarrationVoiceComboBox.SelectedItem is PiperVoice;
        RunDraftNarrationButton.IsEnabled = canCreate
                                            && selected
                                            && !operationRunning
                                            && LocalProjectsList.SelectedItem is LocalProjectSummary draftProject
                                            && draftProject.LatestPhase >= AdficationPhase.VlmSceneAnalytics
                                            && (draftProject.LatestPhase < AdficationPhase.PiperNarrationAndTimingValidation
                                                || draftProject.LatestStatus != CheckpointStatus.Complete);
        var canPreview = canCreate
                         && selected
                         && LocalProjectsList.SelectedItem is LocalProjectSummary previewProject
                         && (previewProject.LatestPhase > AdficationPhase.PiperNarrationAndTimingValidation
                             || (previewProject.LatestPhase == AdficationPhase.PiperNarrationAndTimingValidation
                                 && previewProject.LatestStatus == CheckpointStatus.Complete));
        BuildDraftPreviewButton.IsEnabled = canPreview && !operationRunning;
        PlayDraftPreviewButton.IsEnabled = canPreview && !operationRunning && loadedDraftPreviewPath is not null;
        PauseDraftPreviewButton.IsEnabled = canPreview && !operationRunning && loadedDraftPreviewPath is not null;
        PlaySelectionPreviewButton.IsEnabled = canPreview
                                               && !operationRunning
                                               && DescriptionsList.SelectedItem is ReviewDescriptionItem;
        ReviewGroup.IsEnabled = selected;
        var canRunConversation = canReviewConversationally
                                 && canPreview
                                 && openedProjectSnapshot is { AdSegments.Count: > 0 }
                                 && !operationRunning;
        ConversationTextBox.IsEnabled = canRunConversation;
        SubmitConversationButton.IsEnabled = canRunConversation
                                             && !string.IsNullOrWhiteSpace(ConversationTextBox.Text);
        UndoLastChangeButton.IsEnabled = canRunConversation && openedProjectSnapshot!.Version > 1;
        var canFinalize = canCreate
                          && selected
                          && openedProjectSnapshot is { AdSegments.Count: > 0 }
                          && !operationRunning;
        RunFinalValidationButton.IsEnabled = canFinalize;
        OverrideSelectedWarningButton.IsEnabled = canFinalize
                                                  && FinalValidationIssuesList.SelectedItem is FinalValidationIssueItem item
                                                  && item.Issue.CanOverride
                                                  && !item.Issue.Overridden;
        IncludeNarrationAssetsCheckBox.IsEnabled = canFinalize;
        RenderFinalVideoButton.IsEnabled = canFinalize
                                           && finalValidationReady
                                           && finalValidationSnapshotVersion == openedProjectSnapshot?.Version;
        OpenFinalOutputButton.IsEnabled = !operationRunning
                                          && finalOutputPath is not null
                                          && File.Exists(finalOutputPath);
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

    private static string ResolveTesseractPath()
    {
        var installed = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "Tesseract-OCR",
            "tesseract.exe");
        return File.Exists(installed) ? installed : "tesseract";
    }

    private string ResolveLocalAiPythonPath() => Path.Combine(
        configuration.LocalAiRuntimeRoot,
        "python",
        "Scripts",
        "python.exe");

    private async Task PersistInitialDraftAsync(
        LocalProjectSummary project,
        CreatorDraftNarrationResult result,
        CancellationToken cancellationToken)
    {
        var store = new FileProjectVersionStore(configuration.ProjectRoot);
        var current = await store.LoadCurrentAsync(project.ProjectId, cancellationToken);
        var segments = result.Narration.Segments.Select(item => item.Segment).ToArray();
        if (!string.Equals(current.SourceSha256, result.Narration.SourceSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The generated draft does not belong to the selected immutable source.");
        }

        if (current.AdSegments.Count > 0)
        {
            if (current.NarrationVoiceId == result.Narration.VoiceId
                && SegmentsMatch(current.AdSegments, segments))
            {
                return;
            }

            throw new InvalidOperationException(
                "The project already contains a review draft. A resumed pipeline will not overwrite reviewed project state.");
        }

        var now = TimeProvider.System.GetUtcNow();
        var next = current with
        {
            Version = current.Version + 1,
            AdSegments = segments,
            NarrationVoiceId = result.Narration.VoiceId,
            UpdatedAtUtc = now,
            RestoredFromVersion = null,
        };
        await store.AppendAsync(
            next,
            new(
                Guid.NewGuid(),
                project.ProjectId,
                current.Version,
                next.Version,
                ProjectMutationKind.GenerateInitialDraft,
                "Generated initial evidence-grounded draft and measured Piper narration.",
                now),
            current.Version,
            cancellationToken);
    }

    private void PresentProjectSnapshot(ProjectSnapshot snapshot)
    {
        if (finalValidationSnapshotVersion != snapshot.Version)
        {
            ClearFinalValidationState();
        }

        openedProjectSnapshot = snapshot;
        DescriptionsList.ItemsSource = snapshot.AdSegments
            .Select(segment => new ReviewDescriptionItem(
                segment,
                $"{FormatTime(segment.Window.Start)} to {FormatTime(segment.Window.End)}. {segment.Text}"))
            .ToArray();
        UpdateCreatorAccess();
    }

    private void PresentFinalValidation(FinalProjectValidationManifest manifest)
    {
        finalValidationSnapshotVersion = manifest.ProjectVersion;
        finalValidationReady = manifest.State == "READY_FOR_FINAL_RENDER"
                               && manifest.Issues.All(item => item.Overridden);
        finalValidationBindingSha256 = FinalProjectValidationBinding.ComputeManifestBindingSha256(manifest);
        finalValidationOverrideIds.Clear();
        finalValidationOverrideIds.UnionWith(manifest.AppliedOverrideIssueIds);
        FinalValidationIssuesList.ItemsSource = manifest.Issues
            .Select(issue => new FinalValidationIssueItem(
                issue,
                $"{issue.Severity}. {issue.Code}. " +
                (issue.SegmentId is null ? string.Empty : $"Segment {issue.SegmentId}. ") +
                $"{issue.Message} " +
                (issue.Overridden ? "Explicitly overridden." : issue.CanOverride ? "Override available." : "Must be fixed.")))
            .ToArray();
        FinalValidationStatusText.Text = manifest.State == "READY_FOR_FINAL_RENDER"
            ? $"Ready for final rendering. Project version {manifest.ProjectVersion}; {manifest.AppliedOverrideIssueIds.Count} explicit warning override(s)."
            : $"Rendering is blocked for project version {manifest.ProjectVersion}. Select each issue for details and correct all blocking issues.";
        UpdateCreatorAccess();
    }

    private void PresentFinalRender(FinalRenderCompletionManifest manifest, string outputPath)
    {
        finalOutputPath = outputPath;
        FinalRenderStatusText.Text =
            $"Final output complete for project version {manifest.ProjectVersion}. " +
            (manifest.VideoStreamCopied
                ? "The source video stream was copied without re-encoding. "
                : "The source video required transparent re-encoding. ") +
            $"Narration assets {(manifest.NarrationAssetsIncluded ? "are included" : "are not included")}.";
        UpdateCreatorAccess();
    }

    private async Task LoadFinalValidationStateAsync(
        string projectDirectory,
        ProjectSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(projectDirectory, "manifests", "final-project-validation.json");
        var checkpoint = (await new FilePipelineCheckpointStore(configuration.ProjectRoot)
                .LoadAsync(snapshot.ProjectId, cancellationToken))
            .SingleOrDefault(item =>
                item.Phase == AdficationPhase.FinalProjectValidation);
        if (!File.Exists(path)
            || checkpoint is null
            || checkpoint.ManifestRelativePath != "manifests/final-project-validation.json"
            || !string.Equals(
                checkpoint.OutputSha256,
                await ComputeFileSha256Async(path, cancellationToken),
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var manifest = await JsonSerializer.DeserializeAsync<FinalProjectValidationManifest>(
                stream,
                ConversationJsonOptions,
                cancellationToken);
            if (manifest is not null
                && manifest.ProjectId == snapshot.ProjectId
                && manifest.ProjectVersion == snapshot.Version
                && string.Equals(
                    manifest.ProjectStateSha256,
                    FinalProjectValidationBinding.ComputeProjectStateSha256(snapshot),
                    StringComparison.OrdinalIgnoreCase)
                && ((manifest.State == "READY_FOR_FINAL_RENDER"
                     && manifest.Issues.All(item => item.Overridden)
                     && checkpoint.Status == CheckpointStatus.Complete)
                    || (manifest.State == "BLOCKED"
                        && checkpoint.Status == CheckpointStatus.Failed)))
            {
                PresentFinalValidation(manifest);
            }
        }
        catch (JsonException)
        {
            LogTechnical("Ignored one invalid final-validation manifest. Run final validation again.");
        }
    }

    private async Task LoadFinalRenderStateAsync(
        string projectDirectory,
        ProjectSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var checkpoint = (await new FilePipelineCheckpointStore(configuration.ProjectRoot)
                .LoadAsync(snapshot.ProjectId, cancellationToken))
            .SingleOrDefault(item =>
                item.Phase == AdficationPhase.FinalMp4Rendering
                && item.Status == CheckpointStatus.Complete);
        if (checkpoint is null || string.IsNullOrWhiteSpace(checkpoint.ManifestRelativePath))
        {
            return;
        }

        try
        {
            var root = Path.GetFullPath(projectDirectory);
            var path = Path.GetFullPath(Path.Combine(
                root,
                checkpoint.ManifestRelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!string.Equals(
                    checkpoint.OutputSha256,
                    await ComputeFileSha256Async(path, cancellationToken),
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            await using var stream = File.OpenRead(path);
            var manifest = await JsonSerializer.DeserializeAsync<FinalRenderCompletionManifest>(
                stream,
                ConversationJsonOptions,
                cancellationToken);
            if (manifest is not null
                && manifest.ProjectId == snapshot.ProjectId
                && manifest.ProjectVersion == snapshot.Version
                && manifest.State == "FINAL_RENDER_COMPLETE"
                && string.Equals(
                    manifest.FinalValidationBindingSha256,
                    finalValidationBindingSha256,
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    manifest.ProjectStateSha256,
                    FinalProjectValidationBinding.ComputeProjectStateSha256(snapshot),
                    StringComparison.OrdinalIgnoreCase))
            {
                var output = Path.GetFullPath(Path.Combine(
                    root,
                    manifest.FinalVideoRelativePath.Replace('/', Path.DirectorySeparatorChar)));
                if (output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    && File.Exists(output)
                    && string.Equals(
                        manifest.FinalVideoSha256,
                        await ComputeFileSha256Async(output, cancellationToken),
                        StringComparison.OrdinalIgnoreCase))
                {
                    PresentFinalRender(manifest, output);
                }
            }
        }
        catch (Exception error) when (error is JsonException or IOException or InvalidDataException)
        {
            LogTechnical($"Ignored one invalid final-render manifest: {error.GetType().Name}.");
        }
    }

    private void ClearFinalValidationState()
    {
        finalValidationSnapshotVersion = null;
        finalValidationReady = false;
        finalValidationBindingSha256 = null;
        finalValidationOverrideIds.Clear();
        FinalValidationIssuesList.ItemsSource = null;
        FinalValidationStatusText.Text = "Final validation has not run for this project version.";
        ClearFinalRenderState();
    }

    private void ClearFinalRenderState()
    {
        finalOutputPath = null;
        FinalRenderStatusText.Text = "No final output exists for this project version.";
        OpenFinalOutputButton.IsEnabled = false;
    }

    private static async Task<string> ComputeFileSha256Async(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }

    private void SelectDescription(string? segmentId)
    {
        if (segmentId is null || DescriptionsList.ItemsSource is not IEnumerable<ReviewDescriptionItem> items)
        {
            return;
        }

        var selected = items.SingleOrDefault(item => item.Segment.SegmentId == segmentId);
        if (selected is not null)
        {
            DescriptionsList.SelectedItem = selected;
            DescriptionsList.ScrollIntoView(selected);
        }
    }

    private void AppendConversationHistory(string speaker, string text)
    {
        if (ConversationHistoryTextBox.Text.Length > 0)
        {
            ConversationHistoryTextBox.AppendText(Environment.NewLine + Environment.NewLine);
        }

        ConversationHistoryTextBox.AppendText($"{speaker}: {text.Trim()}");
        ConversationHistoryTextBox.ScrollToEnd();
    }

    private async Task LoadConversationHistoryAsync(
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        ConversationHistoryTextBox.Clear();
        var turns = Path.Combine(Path.GetFullPath(projectDirectory), "conversation", "turns");
        if (!Directory.Exists(turns))
        {
            return;
        }

        var paths = Directory.EnumerateFiles(turns, "turn.json", SearchOption.AllDirectories)
            .OrderBy(path => File.GetLastWriteTimeUtc(path))
            .TakeLast(20)
            .ToArray();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(path);
                var turn = await JsonSerializer.DeserializeAsync<CreatorConversationTurnManifest>(
                    stream,
                    ConversationJsonOptions,
                    cancellationToken);
                if (turn is not null)
                {
                    AppendConversationHistory("You", turn.UserRequest);
                    AppendConversationHistory("ADFi", turn.Response);
                }
            }
            catch (JsonException)
            {
                LogTechnical("Ignored one invalid local conversation-turn record for project history.");
            }
        }
    }

    private void LoadDraftPreview(string path, bool autoPlay, string label)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The generated draft preview is missing.", fullPath);
        }

        DraftPreviewPlayer.Stop();
        DraftPreviewPlayer.Close();
        loadedDraftPreviewPath = fullPath;
        playDraftPreviewWhenOpened = autoPlay;
        DraftPreviewPlayer.Volume = 0;
        DraftPreviewPlayer.Source = new Uri(fullPath, UriKind.Absolute);
        DraftPreviewPlayer.Play();
        DraftPreviewStatusText.Text = $"Loading {label}.";
        UpdateCreatorAccess();
    }

    private void ClearOpenedProjectReview()
    {
        openedProjectSnapshot = null;
        DescriptionsList.ItemsSource = null;
        ConversationHistoryTextBox.Clear();
        ConversationTextBox.Clear();
        ClearFinalValidationState();
        loadedDraftPreviewPath = null;
        playDraftPreviewWhenOpened = false;
        DraftPreviewPlayer.Stop();
        DraftPreviewPlayer.Close();
        DraftPreviewStatusText.Text = "No draft preview is loaded.";
    }

    private static bool SegmentsMatch(
        IReadOnlyList<AdSegment> left,
        AdSegment[] right) =>
        left.Count == right.Length
        && left.Zip(right).All(pair =>
            pair.First.SegmentId == pair.Second.SegmentId
            && pair.First.SceneId == pair.Second.SceneId
            && pair.First.Window == pair.Second.Window
            && pair.First.Text == pair.Second.Text
            && pair.First.EvidenceIds.SequenceEqual(pair.Second.EvidenceIds, StringComparer.Ordinal)
            && pair.First.PromptId == pair.Second.PromptId
            && pair.First.PromptVersion == pair.Second.PromptVersion
            && pair.First.PromptSha256 == pair.Second.PromptSha256
            && pair.First.NarrationAssetRelativePath == pair.Second.NarrationAssetRelativePath
            && pair.First.MeasuredNarrationDuration == pair.Second.MeasuredNarrationDuration
            && pair.First.UserApproved == pair.Second.UserApproved
            && pair.First.InputFingerprint == pair.Second.InputFingerprint);

    private sealed record ReviewDescriptionItem(AdSegment Segment, string AccessibleLabel);

    private sealed record FinalValidationIssueItem(FinalValidationIssue Issue, string AccessibleLabel);
}
