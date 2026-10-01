using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ADFi.Conversation;
using ADFi.Core;
using ADFi.LocalAI;
using ADFi.Media;
using ADFi.Infrastructure.CloudAI;
using ADFi.Infrastructure.Security;
using ADFi.Web;
using Microsoft.Win32;

namespace ADFi.Desktop;

public partial class InteractiveVideoWindow : Window, IDisposable
{
    private readonly DesktopConfiguration configuration;
    private readonly AdfiApiClient apiClient;
    private readonly SubscriptionPlan plan;
    private readonly bool entitlementVerified;
    private readonly string? adfiSessionToken;
    private readonly DiscoverSurface? initialPublication;
    private readonly PrivateEphemeralEvidenceStore evidenceStore;
    private readonly DispatcherTimer positionTimer;
    private readonly List<string> textHistory = [];
    private ViewerVqaFeaturePolicy featurePolicy = new(
        false,
        false,
        3,
        1500,
        1280,
        120,
        "YouTube visual evidence capture is disabled pending compliance approval. Playback and Ask remain available.");
    private InteractiveVideoSessionCoordinator? viewerSession;
    private IPlaybackController? playbackController;
    private OllamaModelRuntime? localRuntime;
    private HttpClient? chatGptHttpClient;
    private ChatGptProfileService? chatGptProfiles;
    private InteractiveVideoSource? currentSource;
    private TimeSpan localDuration;
    private bool sliderUpdate;
    private bool closing;

    public InteractiveVideoWindow(
        DesktopConfiguration configuration,
        AdfiApiClient apiClient,
        SubscriptionPlan plan,
        bool entitlementVerified,
        string? adfiSessionToken,
        DiscoverSurface? initialPublication = null)
    {
        InitializeComponent();
        this.configuration = configuration;
        this.apiClient = apiClient;
        this.plan = plan;
        this.entitlementVerified = entitlementVerified;
        this.adfiSessionToken = adfiSessionToken;
        this.initialPublication = initialPublication;
        evidenceStore = new(configuration.ViewerEvidenceRoot);
        positionTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, PositionTimer_OnTick, Dispatcher);
        ProviderComboBox.ItemsSource = ProviderChoices();
        ProviderComboBox.SelectedIndex = 0;
    }

    private async void Window_OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await evidenceStore.CleanupExpiredAsync(
                TimeSpan.FromHours(1),
                TimeProvider.System.GetUtcNow(),
                CancellationToken.None);
            featurePolicy = await apiClient.GetViewerVqaPolicyAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            LogSafe($"Viewer policy unavailable: {exception.GetType().Name}.");
        }

        CapturePolicyText.Text = featurePolicy.AccessibilityStatus;
        chatGptHttpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com/"),
            Timeout = TimeSpan.FromMinutes(3),
        };
        chatGptProfiles = new ChatGptProfileService(
            new DpapiFileSecretStore(configuration.ProtectedCredentialRoot, "ADFi ChatGPT plan profiles v1"),
            chatGptHttpClient,
            TimeProvider.System,
            featurePolicy.ChatGptPlanUsageIntegrationEnabled);
        await chatGptProfiles.InitializeAsync(CancellationToken.None);
        await RefreshChatGptProfilesAsync(CancellationToken.None);
        if (initialPublication is not null)
        {
            await LoadDiscoverPublicationAsync(initialPublication, CancellationToken.None);
        }

        positionTimer.Start();
        Announce("Accessible Interactive Video Viewer ready. Open a local video or paste a YouTube URL.");
    }

    private void Window_OnClosing(object? sender, CancelEventArgs e)
    {
        Dispose();
    }

    public void Dispose()
    {
        if (closing)
        {
            return;
        }

        closing = true;
        positionTimer.Stop();
        if (viewerSession is not null)
        {
            viewerSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (localRuntime is not null)
        {
            localRuntime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        if (chatGptProfiles is not null)
        {
            chatGptProfiles.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }

        chatGptHttpClient?.Dispose();

        LocalMediaPlayer.Close();
        evidenceStore.Dispose();
        GC.SuppressFinalize(this);
    }

    private async void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Q && Keyboard.Modifiers == ModifierKeys.Alt && AskMomentButton.IsEnabled)
        {
            e.Handled = true;
            await BeginQuestionAsync();
            return;
        }

        if (e.Key == Key.Escape && QuestionGroup.Visibility == Visibility.Visible)
        {
            e.Handled = true;
            await CloseQuestionAsync(resumePlayback: false);
            return;
        }

        if (e.Key == Key.Space
            && playbackController is not null
            && Keyboard.FocusedElement is not TextBox
            && Keyboard.FocusedElement is not Button
            && !YouTubePlayer.IsKeyboardFocusWithin)
        {
            e.Handled = true;
            await TogglePlaybackAsync();
        }
    }

    private async void OpenLocalVideoButton_OnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a local video for accessible viewing",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Supported video files|*.mp4;*.mov;*.mkv;*.webm;*.avi;*.m4v|All files|*.*",
        };
        if (dialog.ShowDialog(this) != true)
        {
            Announce("Open local video cancelled.");
            return;
        }

        await LoadLocalFileAsync(dialog.FileName, CancellationToken.None);
    }

    private async void LoadYouTubeButton_OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var source = InteractiveVideoSource.YouTube(YouTubeUrlTextBox.Text.Trim());
            await LoadYouTubeAsync(source, null, CancellationToken.None);
        }
        catch (Exception exception)
        {
            ReportError("Open YouTube video", exception);
            YouTubeUrlTextBox.Focus();
        }
    }

    private async Task LoadDiscoverPublicationAsync(
        DiscoverSurface publication,
        CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(publication.ProjectId, out var projectId)
            || !Guid.TryParse(publication.PublicationId, out var publicationId))
        {
            throw new InvalidOperationException("The ADFi publication identifiers are invalid.");
        }

        var source = InteractiveVideoSource.AdfiPublication(
            projectId,
            publicationId,
            publication.YouTubeVideoId);
        await LoadYouTubeAsync(source, publication.Title, cancellationToken);
    }

    private async Task LoadLocalFileAsync(string path, CancellationToken cancellationToken)
    {
        await EndCurrentSessionAsync().ConfigureAwait(true);
        var source = InteractiveVideoSource.LocalFile(path);
        currentSource = source;
        localDuration = TimeSpan.Zero;
        YouTubePlayer.Visibility = Visibility.Collapsed;
        PlayerPlaceholder.Visibility = Visibility.Collapsed;
        LocalMediaPlayer.Visibility = Visibility.Visible;
        LocalMediaPlayer.Source = new Uri(path, UriKind.Absolute);
        LocalMediaPlayer.Position = TimeSpan.Zero;
        LocalMediaPlayer.Pause();
        var controller = new MediaElementPlaybackController(LocalMediaPlayer);
        playbackController = controller;
        await InitializeSessionAsync(source, controller, cancellationToken).ConfigureAwait(true);
        OpenSourceStatusText.Text = $"Local video loaded: {Path.GetFileName(path)}. The file remains local.";
        AdfiThisVideoButton.Visibility = Visibility.Visible;
        SetPlayerActionsEnabled(true);
        Announce($"Local video loaded. {Path.GetFileName(path)}. Viewer mode does not start ADFication.");
    }

    private async Task LoadYouTubeAsync(
        InteractiveVideoSource source,
        string? publicationTitle,
        CancellationToken cancellationToken)
    {
        await EndCurrentSessionAsync().ConfigureAwait(true);
        currentSource = source;
        LocalMediaPlayer.Close();
        LocalMediaPlayer.Visibility = Visibility.Collapsed;
        PlayerPlaceholder.Visibility = Visibility.Collapsed;
        YouTubePlayer.Visibility = Visibility.Visible;
        var controller = new YouTubeIframePlaybackController(YouTubePlayer);
        playbackController = controller;
        await controller.LoadAsync(source.YouTubeVideoId!, cancellationToken).ConfigureAwait(true);
        await InitializeSessionAsync(source, controller, cancellationToken).ConfigureAwait(true);
        OpenSourceStatusText.Text = source.SourceType == InteractiveVideoSourceType.AdfiPublication
            ? $"ADFi publication loaded: {publicationTitle}. Official YouTube player."
            : $"YouTube video {source.YouTubeVideoId} loaded in the official embedded player.";
        AdfiThisVideoButton.Visibility = Visibility.Collapsed;
        SetPlayerActionsEnabled(true);
        Announce(OpenSourceStatusText.Text);
    }

    private async Task InitializeSessionAsync(
        InteractiveVideoSource source,
        IPlaybackController controller,
        CancellationToken cancellationToken)
    {
        var limits = new MomentEvidenceLimits(
            Math.Clamp(featurePolicy.MaximumCaptureCount, 1, 3),
            Math.Clamp(featurePolicy.MaximumTemporalSpanMs, 0, 2000),
            Math.Clamp(featurePolicy.MaximumImageDimension, 320, 1920),
            10_000);
        var evidenceProviders = new List<IVideoEvidenceProvider>
        {
            new FfmpegLocalMomentEvidenceProvider(evidenceStore, "ffmpeg", limits),
            new BoundedYoutubeDisplayEvidenceProvider(
                evidenceStore,
                new WebView2PlayerRegionCapture(YouTubePlayer),
                new(
                    featurePolicy.YoutubeEphemeralAccessibilityCaptureEnabled,
                    limits,
                    TimeSpan.FromSeconds(Math.Clamp(featurePolicy.TransactionLifetimeSeconds, 30, 300)))),
        };
        var providers = new List<IVqaProvider>();
        if (chatGptProfiles is not null && chatGptHttpClient is not null)
        {
            providers.Add(new ChatGptMomentVqaProvider(
                chatGptProfiles,
                new ChatGptPlanResponsesClient(chatGptHttpClient),
                TimeProvider.System));
        }
        else
        {
            providers.Add(new UnavailableVqaProvider(
                VqaProviderKind.ChatGpt,
                VqaProviderAvailabilityState.Unavailable,
                "ChatGPT profile services are unavailable."));
        }
        if (plan == SubscriptionPlan.Creator)
        {
            var assessment = await new CreatorHardwareProfileStore(
                configuration.HardwareProfileRoot,
                configuration.ProjectRoot).LoadAsync(cancellationToken).ConfigureAwait(true);
            var selectedModel = assessment?.SelectedTier.OllamaModel ?? ModelTierPolicy.Tiers[^1].OllamaModel;
            var ollamaHttp = new HttpClient
            {
                BaseAddress = new Uri("http://127.0.0.1:11434/"),
                Timeout = TimeSpan.FromMinutes(3),
            };
            var runtime = new OllamaModelRuntime(ollamaHttp);
            localRuntime = runtime;
            providers.Add(new LocalMomentVqaProvider(
                runtime,
                selectedModel,
                Path.Combine(AppContext.BaseDirectory, "prompts", "ADFI_MOMENT_VQA_SYSTEM.md"),
                TimeProvider.System));
        }

        viewerSession = new InteractiveVideoSessionCoordinator(
            source,
            controller,
            new DesktopVideoContextResolver(apiClient),
            evidenceProviders,
            providers,
            evidenceStore,
            TimeProvider.System,
            limits);
        ProviderComboBox.ItemsSource = ProviderChoices();
        ProviderComboBox.SelectedIndex = 0;
        await UpdateProviderStatusAsync(cancellationToken).ConfigureAwait(true);
        textHistory.Clear();
        QuestionHistoryList.ItemsSource = null;
    }

    private async Task EndCurrentSessionAsync()
    {
        if (viewerSession is not null)
        {
            await viewerSession.DisposeAsync().ConfigureAwait(true);
            viewerSession = null;
        }

        if (localRuntime is not null)
        {
            await localRuntime.DisposeAsync().ConfigureAwait(true);
            localRuntime = null;
        }

        QuestionGroup.Visibility = Visibility.Collapsed;
        AnswerTextBox.Clear();
        SetPlayerActionsEnabled(false);
    }

    private async void PlayButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (playbackController is null)
        {
            return;
        }

        try
        {
            await playbackController.PlayAsync(CancellationToken.None);
            Announce("Playing video.");
        }
        catch (Exception exception)
        {
            ReportError("Play video", exception);
        }
    }

    private async void PauseButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (playbackController is null)
        {
            return;
        }

        try
        {
            await playbackController.PauseAsync(CancellationToken.None);
            var snapshot = await playbackController.GetSnapshotAsync(CancellationToken.None);
            Announce($"Paused at {FormatAccessibleTime(snapshot.CurrentTimestampMs)}.");
        }
        catch (Exception exception)
        {
            ReportError("Pause video", exception);
        }
    }

    private async Task TogglePlaybackAsync()
    {
        if (playbackController is null)
        {
            return;
        }

        var snapshot = await playbackController.GetSnapshotAsync(CancellationToken.None);
        if (snapshot.State == InteractivePlaybackState.Playing)
        {
            await playbackController.PauseAsync(CancellationToken.None);
            Announce($"Paused at {FormatAccessibleTime(snapshot.CurrentTimestampMs)}.");
        }
        else
        {
            await playbackController.PlayAsync(CancellationToken.None);
            Announce("Playing video.");
        }
    }

    private async void AskMomentButton_OnClick(object sender, RoutedEventArgs e) =>
        await BeginQuestionAsync();

    private async Task BeginQuestionAsync()
    {
        if (viewerSession is null)
        {
            Announce("Open a video before asking about a moment.");
            return;
        }

        try
        {
            var checkpoint = await viewerSession.PauseForQuestionAsync(
                Keyboard.FocusedElement is FrameworkElement element ? element.Name : "video player",
                CancellationToken.None);
            QuestionGroup.Visibility = Visibility.Visible;
            PausedMomentText.Text = $"Paused at {FormatClockTime(checkpoint.CurrentTimestampMs)}. Ask about this moment.";
            ResumeButton.Content = $"_Resume from {FormatClockTime(checkpoint.CurrentTimestampMs)}";
            ReplayContextButton.IsEnabled = currentSource?.SourceType == InteractiveVideoSourceType.LocalFile;
            SuggestFeedbackButton.IsEnabled = currentSource?.SourceType == InteractiveVideoSourceType.AdfiPublication;
            Announce($"Paused at {FormatAccessibleTime(checkpoint.CurrentTimestampMs)}. Ask about this moment.");
            MomentQuestionTextBox.Focus();
            Keyboard.Focus(MomentQuestionTextBox);
        }
        catch (Exception exception)
        {
            ReportError("Ask about this moment", exception);
        }
    }

    private async void MomentQuestionTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await SubmitQuestionAsync();
        }
    }

    private void DictateButton_OnClick(object sender, RoutedEventArgs e)
    {
        MomentQuestionTextBox.Focus();
        _ = Dispatcher.BeginInvoke(() =>
        {
            WindowsDictationLauncher.OpenVoiceTyping();
            Announce("Windows voice typing opened in Ask about this moment. Dictate, then review or edit before submitting.");
        }, DispatcherPriority.Input);
    }

    private async void SubmitQuestionButton_OnClick(object sender, RoutedEventArgs e) =>
        await SubmitQuestionAsync();

    private async Task SubmitQuestionAsync()
    {
        if (viewerSession is null || ProviderComboBox.SelectedItem is not ProviderChoice choice)
        {
            Announce("Select an available question provider.");
            return;
        }

        var question = MomentQuestionTextBox.Text.Trim();
        if (question.Length == 0)
        {
            Announce("Type or dictate a question first.");
            MomentQuestionTextBox.Focus();
            return;
        }

        SetQuestionControlsEnabled(false);
        Announce("Checking this moment.");
        try
        {
            var result = await viewerSession.AskAsync(question, choice.Kind, CancellationToken.None);
            AnswerTextBox.Text = result.Answer;
            var timestamp = viewerSession.ActiveCheckpoint?.CurrentTimestampMs ?? 0;
            textHistory.Add($"{FormatClockTime(timestamp)} — Question: {question}{Environment.NewLine}Answer: {result.Answer}");
            QuestionHistoryList.ItemsSource = null;
            QuestionHistoryList.ItemsSource = textHistory.ToArray();
            Announce("Answer ready.");
            AnswerTextBox.Focus();
        }
        catch (Exception exception)
        {
            ReportError("Moment question", exception);
        }
        finally
        {
            SetQuestionControlsEnabled(true);
        }
    }

    private void AskAnotherButton_OnClick(object sender, RoutedEventArgs e)
    {
        MomentQuestionTextBox.Clear();
        AnswerTextBox.Clear();
        Announce("Ask another question about the same paused moment.");
        MomentQuestionTextBox.Focus();
    }

    private async void ResumeButton_OnClick(object sender, RoutedEventArgs e) =>
        await ResumeAsync();

    private async Task ResumeAsync()
    {
        if (viewerSession?.ActiveCheckpoint is not { } checkpoint)
        {
            return;
        }

        try
        {
            await viewerSession.ResumeAsync(CancellationToken.None);
            QuestionGroup.Visibility = Visibility.Collapsed;
            Announce($"Resumed from {FormatAccessibleTime(checkpoint.CurrentTimestampMs)}.");
            PlayButton.Focus();
        }
        catch (Exception exception)
        {
            ReportError("Resume video", exception);
        }
    }

    private async void ReplayContextButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (viewerSession?.ActiveCheckpoint is not { } checkpoint || playbackController is null)
        {
            return;
        }

        try
        {
            await viewerSession.CloseQuestionAsync(false, CancellationToken.None);
            await playbackController.SeekAsync(Math.Max(0, checkpoint.CurrentTimestampMs - 5000), CancellationToken.None);
            await playbackController.PlayAsync(CancellationToken.None);
            QuestionGroup.Visibility = Visibility.Collapsed;
            Announce("Replaying five seconds of local context. Ephemeral question evidence was deleted.");
            PlayButton.Focus();
        }
        catch (Exception exception)
        {
            ReportError("Replay context", exception);
        }
    }

    private void SuggestFeedbackButton_OnClick(object sender, RoutedEventArgs e)
    {
        var timestamp = viewerSession?.ActiveCheckpoint?.CurrentTimestampMs ?? 0;
        var selected = AnswerTextBox.SelectedText;
        FeedbackDraftTextBox.Text = selected.Length > 0
            ? $"At {FormatClockTime(timestamp)}, please review this answer: {selected}"
            : $"At {FormatClockTime(timestamp)}, correction or accessibility feedback: ";
        FeedbackDraftPanel.Visibility = Visibility.Visible;
        Announce("Editable timestamped feedback draft opened. Nothing is submitted or qualified automatically.");
        FeedbackDraftTextBox.Focus();
    }

    private async void SubmitFeedbackButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (currentSource?.ProjectId is not { } projectId
            || viewerSession?.ActiveCheckpoint is not { } checkpoint
            || string.IsNullOrWhiteSpace(adfiSessionToken))
        {
            Announce("A signed-in ADFi publication session is required to submit feedback.");
            return;
        }

        var body = FeedbackDraftTextBox.Text.Trim();
        if (body.Length is < 20 or > 2000)
        {
            Announce("Feedback must contain between 20 and 2,000 characters.");
            FeedbackDraftTextBox.Focus();
            return;
        }

        try
        {
            var receipt = await apiClient.SubmitDiscoverFeedbackAsync(
                adfiSessionToken,
                projectId,
                checkpoint.CurrentTimestampMs,
                body,
                CancellationToken.None);
            FeedbackDraftPanel.Visibility = Visibility.Collapsed;
            FeedbackDraftTextBox.Clear();
            Announce(receipt.Message);
        }
        catch (Exception exception)
        {
            ReportError("Submit feedback", exception);
        }
    }

    private void CancelFeedbackButton_OnClick(object sender, RoutedEventArgs e)
    {
        FeedbackDraftPanel.Visibility = Visibility.Collapsed;
        FeedbackDraftTextBox.Clear();
        Announce("Feedback draft cancelled. Nothing was submitted.");
        SuggestFeedbackButton.Focus();
    }

    private async void CloseQuestionButton_OnClick(object sender, RoutedEventArgs e) =>
        await CloseQuestionAsync(resumePlayback: false);

    private async Task CloseQuestionAsync(bool resumePlayback)
    {
        if (viewerSession is null)
        {
            return;
        }

        try
        {
            await viewerSession.CloseQuestionAsync(resumePlayback, CancellationToken.None);
            QuestionGroup.Visibility = Visibility.Collapsed;
            Announce("Questions closed. The video remains paused and ephemeral evidence was deleted.");
            PlayButton.Focus();
        }
        catch (Exception exception)
        {
            ReportError("Close questions", exception);
        }
    }

    private void DeleteHistoryButton_OnClick(object sender, RoutedEventArgs e)
    {
        textHistory.Clear();
        QuestionHistoryList.ItemsSource = null;
        AnswerTextBox.Clear();
        Announce("Temporary viewing question and answer history deleted.");
    }

    private void AdfiThisVideoButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (currentSource?.SourceType != InteractiveVideoSourceType.LocalFile)
        {
            return;
        }

        Announce(plan == SubscriptionPlan.Creator
            ? "Creator feature. Return to MY ADFi, confirm media rights, and create the ADFication project. The local video remains selected only in this private viewer session."
            : "ADFi this video is a Creator feature. Upgrade to Creator for local ADFication; this local file will not be uploaded to preserve the selection.");
    }

    private async void ProviderComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        await UpdateProviderStatusAsync(CancellationToken.None);

    private async void ContinueWithChatGptButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (chatGptProfiles is null || !featurePolicy.ChatGptPlanUsageIntegrationEnabled)
        {
            Announce("ChatGPT plan usage is disabled until OpenAI approves ADFi's paid application integration.");
            return;
        }

        ContinueWithChatGptButton.IsEnabled = false;
        Announce("Opening the system browser to continue with ChatGPT.");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
            var result = await chatGptProfiles.SignInAsync(
                uri =>
                {
                    Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
                    return Task.CompletedTask;
                },
                timeout.Token);
            await RefreshChatGptProfilesAsync(CancellationToken.None);
            await UpdateProviderStatusAsync(CancellationToken.None);
            Announce(result.AccessibleMessage);
        }
        catch (OperationCanceledException)
        {
            Announce("ChatGPT connection timed out. No account change was made.");
        }
        catch (Exception exception)
        {
            ReportError("Connect ChatGPT", exception);
        }
        finally
        {
            ContinueWithChatGptButton.IsEnabled = featurePolicy.ChatGptPlanUsageIntegrationEnabled;
        }
    }

    private void ManageChatGptUsageButton_OnClick(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo("https://chatgpt.com/settings/usage") { UseShellExecute = true });

    private async void DisconnectChatGptButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (chatGptProfiles is null)
        {
            return;
        }

        DisconnectChatGptButton.IsEnabled = false;
        try
        {
            await chatGptProfiles.SignOutSelectedAsync(CancellationToken.None);
            await RefreshChatGptProfilesAsync(CancellationToken.None);
            await UpdateProviderStatusAsync(CancellationToken.None);
            Announce("ChatGPT disconnected from ADFi on this Windows account.");
        }
        catch (Exception exception)
        {
            ReportError("Disconnect ChatGPT", exception);
        }
    }

    private async void ChatGptProfileComboBox_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (chatGptProfiles is null
            || ChatGptProfileComboBox.SelectedItem is not ChatGptProfileChoice choice
            || choice.IsSelected)
        {
            return;
        }

        try
        {
            await chatGptProfiles.SelectProfileAsync(choice.ProfileId, CancellationToken.None);
            await RefreshChatGptProfilesAsync(CancellationToken.None);
            await UpdateProviderStatusAsync(CancellationToken.None);
            Announce($"Selected ChatGPT profile {choice.Label}.");
        }
        catch (Exception exception)
        {
            ReportError("Select ChatGPT profile", exception);
        }
    }

    private void PreviewProviderRequestButton_OnClick(object sender, RoutedEventArgs e)
    {
        var question = MomentQuestionTextBox.Text.Trim();
        var requirement = question.Length == 0
            ? null
            : MomentQuestionClassifier.Classify(question);
        var source = currentSource?.SourceType.ToString() ?? "No video loaded";
        var provider = ProviderComboBox.SelectedItem is ProviderChoice choice ? choice.Label : "No provider selected";
        ProviderRequestPreviewTextBox.Text =
            $"Provider: {provider}.{Environment.NewLine}" +
            $"Source class: {source}. Exact timestamp: included when a video is paused.{Environment.NewLine}" +
            (requirement is null
                ? "Question/evidence: enter a question to preview its minimum evidence category."
                : $"Question category: {requirement.Intent}. Maximum images: {requirement.MaximumImageCount}. Maximum temporal span: {requirement.TemporalSpanMs} milliseconds.") +
            Environment.NewLine +
            "May leave device: question text, bounded verified ADFi context, and only required ephemeral image bytes. " +
            "Never included: local filename/path, unrelated personal metadata, ChatGPT conversations, API keys, or provider tokens.";
        Announce("Provider request privacy preview updated.");
        ProviderRequestPreviewTextBox.Focus();
    }

    private async Task UpdateProviderStatusAsync(CancellationToken cancellationToken)
    {
        if (ProviderComboBox.SelectedItem is not ProviderChoice choice)
        {
            return;
        }

        if (choice.Kind == VqaProviderKind.ChatGpt)
        {
            if (!featurePolicy.ChatGptPlanUsageIntegrationEnabled || chatGptProfiles is null)
            {
                ProviderStatusText.Text = "ChatGPT: Provider Approval Required. OpenAI approval is required before ADFi can offer plan usage in this paid application. No password or API key is requested.";
            }
            else
            {
                var provider = new ChatGptMomentVqaProvider(
                    chatGptProfiles,
                    new ChatGptPlanResponsesClient(chatGptHttpClient!),
                    TimeProvider.System);
                var availability = await provider.GetAvailabilityAsync(cancellationToken).ConfigureAwait(true);
                ProviderStatusText.Text = "ChatGPT: " + availability.AccessibleStatus;
            }
        }
        else
        {
            ProviderStatusText.Text = plan == SubscriptionPlan.Creator
                ? "Local First: Creator-only Ollama/Qwen moment VQA. The model loads only for the request and is explicitly unloaded afterward."
                : "Local First is a Creator feature and is unavailable to Community subscribers.";
        }

        await Task.CompletedTask;
    }

    private async Task RefreshChatGptProfilesAsync(CancellationToken cancellationToken)
    {
        var profiles = chatGptProfiles is null
            ? []
            : await chatGptProfiles.ListProfilesAsync(cancellationToken).ConfigureAwait(true);
        var choices = profiles
            .Select(item => new ChatGptProfileChoice(
                item.ProfileId,
                string.IsNullOrWhiteSpace(item.Email)
                    ? item.DisplayName
                    : $"{item.DisplayName} — {item.Email}",
                item.IsSelected))
            .ToArray();
        ChatGptProfileComboBox.ItemsSource = choices;
        ChatGptProfileComboBox.SelectedItem = choices.FirstOrDefault(item => item.IsSelected);
        var enabled = featurePolicy.ChatGptPlanUsageIntegrationEnabled;
        ChatGptProfileComboBox.IsEnabled = enabled && choices.Length > 1;
        ContinueWithChatGptButton.IsEnabled = enabled;
        DisconnectChatGptButton.IsEnabled = enabled && choices.Length > 0;
        ManageChatGptUsageButton.IsEnabled = enabled && choices.Length > 0;
    }

    private async void PlaybackPositionSlider_OnPreviewMouseUp(object sender, MouseButtonEventArgs e) =>
        await SeekFromSliderAsync();

    private async void PlaybackPositionSlider_OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Left or Key.Right or Key.Home or Key.End or Key.PageDown or Key.PageUp)
        {
            await SeekFromSliderAsync();
        }
    }

    private async Task SeekFromSliderAsync()
    {
        if (sliderUpdate || playbackController is null || localDuration <= TimeSpan.Zero)
        {
            return;
        }

        if (viewerSession?.ActiveCheckpoint is not null)
        {
            await viewerSession.CloseQuestionAsync(false, CancellationToken.None);
            QuestionGroup.Visibility = Visibility.Collapsed;
        }

        await playbackController.SeekAsync((long)PlaybackPositionSlider.Value, CancellationToken.None);
        Announce($"Moved to {FormatAccessibleTime((long)PlaybackPositionSlider.Value)}. Ephemeral moment evidence was deleted.");
    }

    private async void PositionTimer_OnTick(object? sender, EventArgs e)
    {
        if (playbackController is null)
        {
            return;
        }

        try
        {
            var snapshot = await playbackController.GetSnapshotAsync(CancellationToken.None);
            if (localDuration > TimeSpan.Zero)
            {
                sliderUpdate = true;
                PlaybackPositionSlider.Value = Math.Clamp(snapshot.CurrentTimestampMs, 0, (long)localDuration.TotalMilliseconds);
                sliderUpdate = false;
                PlaybackPositionText.Text = $"Position {FormatClockTime(snapshot.CurrentTimestampMs)} of {FormatClockTime((long)localDuration.TotalMilliseconds)}.";
            }
            else
            {
                PlaybackPositionText.Text = $"Position {FormatClockTime(snapshot.CurrentTimestampMs)}. Use the official YouTube controls for seeking.";
            }
        }
        catch (InvalidOperationException)
        {
            // The official player may still be loading; no live-region announcement is warranted.
        }
    }

    private void LocalMediaPlayer_OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (LocalMediaPlayer.NaturalDuration.HasTimeSpan)
        {
            localDuration = LocalMediaPlayer.NaturalDuration.TimeSpan;
            PlaybackPositionSlider.Maximum = Math.Max(1, localDuration.TotalMilliseconds);
            PlaybackPositionSlider.IsEnabled = true;
        }

        (playbackController as MediaElementPlaybackController)?.MarkOpened();
    }

    private void LocalMediaPlayer_OnMediaEnded(object sender, RoutedEventArgs e)
    {
        (playbackController as MediaElementPlaybackController)?.MarkEnded();
        Announce("Video ended.");
    }

    private void LocalMediaPlayer_OnMediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        (playbackController as MediaElementPlaybackController)?.MarkUnavailable();
        Announce("The local video could not be played. " + e.ErrorException.Message);
    }

    private List<ProviderChoice> ProviderChoices()
    {
        var choices = new List<ProviderChoice>
        {
            new(VqaProviderKind.ChatGpt, "ChatGPT — user-authenticated plan usage"),
        };
        if (plan == SubscriptionPlan.Creator)
        {
            choices.Insert(0, new(VqaProviderKind.LocalFirst, "Local first — Ollama/Qwen3-VL"));
        }

        return choices;
    }

    private void SetPlayerActionsEnabled(bool enabled)
    {
        PlayButton.IsEnabled = enabled;
        PauseButton.IsEnabled = enabled;
        AskMomentButton.IsEnabled = enabled && ViewerVqaAccessPolicy.CanUseViewer(plan, entitlementVerified);
    }

    private void SetQuestionControlsEnabled(bool enabled)
    {
        MomentQuestionTextBox.IsEnabled = enabled;
        DictateButton.IsEnabled = enabled;
        SubmitQuestionButton.IsEnabled = enabled;
        ResumeButton.IsEnabled = enabled;
    }

    private void Announce(string message)
    {
        ViewerStatusText.Text = message;
        _ = Dispatcher.BeginInvoke(() =>
        {
            var peer = FrameworkElementAutomationPeer.FromElement(ViewerStatusText)
                       ?? new TextBlockAutomationPeer(ViewerStatusText);
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        });
    }

    private void ReportError(string operation, Exception exception)
    {
        Announce($"{operation} failed. {exception.Message}");
        LogSafe($"{operation}: {exception.GetType().Name}.");
    }

    private static void LogSafe(string message) =>
        System.Diagnostics.Debug.WriteLine(message);

    private static string FormatClockTime(long timestampMs)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, timestampMs));
        return value.TotalHours >= 1
            ? $"{(int)value.TotalHours:00}:{value.Minutes:00}:{value.Seconds:00}"
            : $"{value.Minutes:00}:{value.Seconds:00}";
    }

    private static string FormatAccessibleTime(long timestampMs)
    {
        var value = TimeSpan.FromMilliseconds(Math.Max(0, timestampMs));
        var parts = new List<string>();
        if (value.TotalHours >= 1)
        {
            parts.Add($"{(int)value.TotalHours} {((int)value.TotalHours == 1 ? "hour" : "hours")}");
        }

        if (value.Minutes > 0)
        {
            parts.Add($"{value.Minutes} {(value.Minutes == 1 ? "minute" : "minutes")}");
        }

        parts.Add($"{value.Seconds} {(value.Seconds == 1 ? "second" : "seconds")}");
        return string.Join(' ', parts);
    }

    private sealed record ProviderChoice(VqaProviderKind Kind, string Label);

    private sealed record ChatGptProfileChoice(string ProfileId, string Label, bool IsSelected);
}

internal static partial class WindowsDictationLauncher
{
    private const byte VirtualKeyLeftWindows = 0x5B;
    private const byte VirtualKeyH = 0x48;
    private const uint KeyUp = 0x0002;

    public static void OpenVoiceTyping()
    {
        KeybdEvent(VirtualKeyLeftWindows, 0, 0, UIntPtr.Zero);
        KeybdEvent(VirtualKeyH, 0, 0, UIntPtr.Zero);
        KeybdEvent(VirtualKeyH, 0, KeyUp, UIntPtr.Zero);
        KeybdEvent(VirtualKeyLeftWindows, 0, KeyUp, UIntPtr.Zero);
    }

    [LibraryImport("user32.dll", EntryPoint = "keybd_event")]
    private static partial void KeybdEvent(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
