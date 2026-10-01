using System.IO;
using System.Text.Json;
using System.Windows.Controls;
using ADFi.Core;
using ADFi.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using OpenCvSharp;

namespace ADFi.Desktop;

public sealed class MediaElementPlaybackController(MediaElement player) : IPlaybackController
{
    private InteractivePlaybackState state = InteractivePlaybackState.Stopped;

    public async Task<PlaybackSnapshot> GetSnapshotAsync(CancellationToken cancellationToken) =>
        await player.Dispatcher.InvokeAsync(
            () => new PlaybackSnapshot(
                state,
                Math.Max(0, (long)player.Position.TotalMilliseconds),
                (decimal)player.SpeedRatio,
                null),
            System.Windows.Threading.DispatcherPriority.Normal,
            cancellationToken);

    public async Task PauseAsync(CancellationToken cancellationToken) =>
        await player.Dispatcher.InvokeAsync(
            () =>
            {
                player.Pause();
                state = InteractivePlaybackState.Paused;
            },
            System.Windows.Threading.DispatcherPriority.Normal,
            cancellationToken);

    public async Task SeekAsync(long timestampMs, CancellationToken cancellationToken) =>
        await player.Dispatcher.InvokeAsync(
            () => player.Position = TimeSpan.FromMilliseconds(Math.Max(0, timestampMs)),
            System.Windows.Threading.DispatcherPriority.Normal,
            cancellationToken);

    public async Task PlayAsync(CancellationToken cancellationToken) =>
        await player.Dispatcher.InvokeAsync(
            () =>
            {
                player.Play();
                state = InteractivePlaybackState.Playing;
            },
            System.Windows.Threading.DispatcherPriority.Normal,
            cancellationToken);

    public void MarkOpened() => state = InteractivePlaybackState.Paused;

    public void MarkEnded() => state = InteractivePlaybackState.Ended;

    public void MarkUnavailable() => state = InteractivePlaybackState.Unavailable;
}

public sealed class YouTubeIframePlaybackController(WebView2 player) : IPlaybackController
{
    public async Task LoadAsync(string videoId, CancellationToken cancellationToken)
    {
        if (!YoutubeVideoUrl.TryParseVideoId(videoId, out var normalized))
        {
            throw new ArgumentException("A valid YouTube video ID is required.", nameof(videoId));
        }

        await player.Dispatcher.InvokeAsync(
            async () =>
            {
                await player.EnsureCoreWebView2Async().ConfigureAwait(true);
                player.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;
                player.CoreWebView2.Settings.IsStatusBarEnabled = true;
                player.NavigateToString(CreatePlayerDocument(normalized));
            },
            System.Windows.Threading.DispatcherPriority.Normal,
            cancellationToken).Task.Unwrap().ConfigureAwait(false);
    }

    public async Task<PlaybackSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var encoded = await ExecuteAsync("JSON.stringify(adfiPlayerSnapshot())", cancellationToken)
            .ConfigureAwait(false);
        var json = JsonSerializer.Deserialize<string>(encoded)
            ?? throw new InvalidOperationException("The official YouTube player returned no state.");
        var snapshot = JsonSerializer.Deserialize<YouTubeSnapshot>(json, JsonOptions)
            ?? throw new InvalidOperationException("The official YouTube player returned invalid state.");
        return new(
            snapshot.State switch
            {
                0 => InteractivePlaybackState.Ended,
                1 => InteractivePlaybackState.Playing,
                2 => InteractivePlaybackState.Paused,
                5 => InteractivePlaybackState.Paused,
                _ => InteractivePlaybackState.Stopped,
            },
            Math.Max(0, (long)(snapshot.CurrentTime * 1000)),
            snapshot.PlaybackRate <= 0 ? 1m : snapshot.PlaybackRate,
            "Managed by the official YouTube player");
    }

    public async Task PauseAsync(CancellationToken cancellationToken)
    {
        await ExecuteAsync("adfiPauseVideo()", cancellationToken).ConfigureAwait(false);
        await ConfirmStateAsync(InteractivePlaybackState.Paused, cancellationToken).ConfigureAwait(false);
    }

    public async Task SeekAsync(long timestampMs, CancellationToken cancellationToken)
    {
        var seconds = (Math.Max(0, timestampMs) / 1000m)
            .ToString(System.Globalization.CultureInfo.InvariantCulture);
        await ExecuteAsync($"adfiSeekVideo({seconds})", cancellationToken).ConfigureAwait(false);
    }

    public Task PlayAsync(CancellationToken cancellationToken) =>
        ExecuteAsync("adfiPlayVideo()", cancellationToken);

    private async Task<string> ExecuteAsync(string script, CancellationToken cancellationToken) =>
        await player.Dispatcher.InvokeAsync(
            () => player.CoreWebView2 is null
                ? throw new InvalidOperationException("The official YouTube player is unavailable.")
                : player.CoreWebView2.ExecuteScriptAsync(script),
            System.Windows.Threading.DispatcherPriority.Normal,
            cancellationToken).Task.Unwrap().ConfigureAwait(false);

    private async Task ConfirmStateAsync(
        InteractivePlaybackState expected,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            if ((await GetSnapshotAsync(cancellationToken).ConfigureAwait(false)).State == expected)
            {
                return;
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException("The official YouTube player did not confirm the requested state.");
    }

    private static string CreatePlayerDocument(string videoId) => $$"""
        <!doctype html>
        <html lang="en">
        <head>
          <meta charset="utf-8">
          <meta name="viewport" content="width=device-width,initial-scale=1">
          <title>Official YouTube player</title>
          <style>html,body,#player{margin:0;width:100%;height:100%;background:#000;overflow:hidden}</style>
        </head>
        <body>
          <div id="player"></div>
          <script src="https://www.youtube.com/iframe_api"></script>
          <script>
            let player;
            function onYouTubeIframeAPIReady() {
              player = new YT.Player('player', {
                videoId: '{{videoId}}',
                playerVars: { controls: 1, playsinline: 1, fs: 1, rel: 0 }
              });
            }
            function adfiRequirePlayer() { if (!player || !player.getCurrentTime) throw new Error('player_not_ready'); }
            function adfiPauseVideo() { adfiRequirePlayer(); player.pauseVideo(); return true; }
            function adfiPlayVideo() { adfiRequirePlayer(); player.playVideo(); return true; }
            function adfiSeekVideo(seconds) { adfiRequirePlayer(); player.seekTo(seconds, true); return true; }
            function adfiPlayerSnapshot() {
              adfiRequirePlayer();
              return { state: player.getPlayerState(), currentTime: player.getCurrentTime(), playbackRate: player.getPlaybackRate() };
            }
          </script>
        </body>
        </html>
        """;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private sealed record YouTubeSnapshot(int State, decimal CurrentTime, decimal PlaybackRate);
}

public sealed class WebView2PlayerRegionCapture(WebView2 player) : IPlayerRegionCapture
{
    public async Task<CapturedPlayerRegion> CapturePausedVideoRegionAsync(
        int maximumImageDimension,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumImageDimension, 1);
        var bytes = await player.Dispatcher.InvokeAsync(
            async () =>
            {
                if (player.CoreWebView2 is null)
                {
                    throw new InvalidOperationException("The official YouTube player is unavailable.");
                }

                await using var output = new MemoryStream();
                await player.CoreWebView2.CapturePreviewAsync(
                    CoreWebView2CapturePreviewImageFormat.Png,
                    output).ConfigureAwait(true);
                return output.ToArray();
            },
            System.Windows.Threading.DispatcherPriority.Normal,
            cancellationToken).Task.Unwrap().ConfigureAwait(false);
        using var source = Cv2.ImDecode(bytes, ImreadModes.Color);
        if (source.Empty())
        {
            throw new InvalidOperationException("The paused player-region image could not be validated.");
        }

        var scale = Math.Min(1d, maximumImageDimension / (double)Math.Max(source.Width, source.Height));
        if (scale >= 1d)
        {
            return new(bytes, source.Width, source.Height);
        }

        using var resized = new Mat();
        Cv2.Resize(source, resized, new Size(), scale, scale, InterpolationFlags.Area);
        Cv2.ImEncode(".png", resized, out var encoded);
        return new(encoded, resized.Width, resized.Height);
    }
}
