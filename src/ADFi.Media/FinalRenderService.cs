using System.Globalization;
using System.Security.Cryptography;
using ADFi.Core;

namespace ADFi.Media;

public sealed record TimedNarrationAsset(
    string SegmentId,
    string AbsoluteAudioPath,
    MediaInterval Window,
    TimeSpan MeasuredDuration);

public sealed record FinalRenderRequest(
    string SourceVideoPath,
    MediaProbeResult SourceProbe,
    IReadOnlyList<TimedNarrationAsset> Narration,
    string OutputPath,
    bool ForceVideoReencode = false);

public sealed record FinalRenderResult(
    string OutputPath,
    string Sha256,
    bool VideoStreamCopied,
    TimeSpan Duration);

public sealed class FinalRenderService(string ffmpegPath, IMediaProbe mediaProbe)
{
    public async Task<FinalRenderResult> RenderAsync(
        FinalRenderRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var sourcePath = LocalSourcePolicy.ValidateExistingLocalVideo(request.SourceVideoPath);
        var outputPath = Path.GetFullPath(request.OutputPath);
        if (string.Equals(sourcePath, outputPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Final rendering cannot overwrite the source video.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        ValidateNarration(request.Narration, request.SourceProbe.Duration);
        var workDirectory = Path.Combine(Path.GetDirectoryName(outputPath)!, $".render-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDirectory);
        var narrationTrack = Path.Combine(workDirectory, "narration.wav");
        var mixedAudio = Path.Combine(workDirectory, "mixed.m4a");
        var temporaryOutput = outputPath + ".partial.mp4";

        try
        {
            await BuildNarrationTrackAsync(
                request.Narration,
                request.SourceProbe.Duration,
                narrationTrack,
                cancellationToken).ConfigureAwait(false);
            await MixAudioAsync(
                sourcePath,
                narrationTrack,
                request.SourceProbe.Duration,
                mixedAudio,
                cancellationToken).ConfigureAwait(false);

            var videoStreamCopied = !request.ForceVideoReencode && CanStreamCopyVideo(request.SourceProbe);
            var arguments = new List<string>
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-i", sourcePath,
                "-i", mixedAudio,
                "-map", "0:v:0",
                "-map", "1:a:0",
            };
            if (videoStreamCopied)
            {
                arguments.AddRange(["-c:v", "copy"]);
            }
            else
            {
                arguments.AddRange(
                [
                    "-c:v", "libx264", "-preset", "slow", "-crf", "18",
                    "-vf", "scale=w='min(1920,iw)':h=-2:flags=lanczos",
                ]);
            }

            // Pin the mux to the immutable source duration. `-shortest` can truncate a valid
            // source video at an AAC packet boundary when the newly mixed audio ends first.
            arguments.AddRange(
            [
                "-c:a", "aac", "-b:a", "192k", "-t", Format(request.SourceProbe.Duration),
                "-movflags", "+faststart", temporaryOutput,
            ]);
            await RunRequiredAsync(arguments, "FINAL_RENDER_FAILED", cancellationToken).ConfigureAwait(false);
            File.Move(temporaryOutput, outputPath, true);
            var outputProbe = await mediaProbe.ProbeAsync(outputPath, cancellationToken).ConfigureAwait(false);
            var hash = await ComputeSha256Async(outputPath, cancellationToken).ConfigureAwait(false);
            return new(outputPath, hash, videoStreamCopied, outputProbe.Duration);
        }
        finally
        {
            if (File.Exists(temporaryOutput))
            {
                File.Delete(temporaryOutput);
            }

            if (Directory.Exists(workDirectory))
            {
                Directory.Delete(workDirectory, recursive: true);
            }
        }
    }

    private async Task BuildNarrationTrackAsync(
        IReadOnlyList<TimedNarrationAsset> narration,
        TimeSpan duration,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (narration.Count == 0)
        {
            await RunRequiredAsync(
                [
                    "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i",
                    "anullsrc=channel_layout=stereo:sample_rate=48000", "-t", Format(duration),
                    "-c:a", "pcm_s16le", outputPath,
                ],
                "NARRATION_TRACK_FAILED",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var arguments = new List<string> { "-hide_banner", "-loglevel", "error", "-y" };
        foreach (var segment in narration)
        {
            arguments.AddRange(["-i", Path.GetFullPath(segment.AbsoluteAudioPath)]);
        }

        var filters = new List<string>(narration.Count + 1);
        for (var index = 0; index < narration.Count; index++)
        {
            var delay = checked((long)narration[index].Window.Start.TotalMilliseconds);
            filters.Add($"[{index}:a]aresample=48000,adelay={delay}|{delay}[n{index}]");
        }

        filters.Add(
            string.Concat(
                Enumerable.Range(0, narration.Count).Select(index => $"[n{index}]"))
            + $"amix=inputs={narration.Count}:duration=longest:normalize=0,apad=whole_dur={Format(duration)}[narration]");
        arguments.AddRange(
        [
            "-filter_complex", string.Join(';', filters),
            "-map", "[narration]", "-t", Format(duration), "-c:a", "pcm_s16le", outputPath,
        ]);
        await RunRequiredAsync(arguments, "NARRATION_TRACK_FAILED", cancellationToken).ConfigureAwait(false);
    }

    private async Task MixAudioAsync(
        string sourcePath,
        string narrationPath,
        TimeSpan duration,
        string outputPath,
        CancellationToken cancellationToken)
    {
        await RunRequiredAsync(
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-i", sourcePath, "-i", narrationPath,
            "-filter_complex",
            "[0:a]aresample=48000[original];[1:a]aresample=48000[narration];" +
            "[original][narration]sidechaincompress=threshold=0.025:ratio=3:attack=120:release=650[ducked];" +
            "[ducked][narration]amix=inputs=2:duration=first:normalize=0,alimiter=limit=0.95[mixed]",
            "-map", "[mixed]", "-t", Format(duration), "-c:a", "aac", "-b:a", "192k", outputPath,
        ],
        "AUDIO_MIX_FAILED",
        cancellationToken).ConfigureAwait(false);
    }

    private async Task RunRequiredAsync(
        IReadOnlyList<string> arguments,
        string failureCode,
        CancellationToken cancellationToken)
    {
        var result = await ExternalProcess.RunAsync(ffmpegPath, arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            var error = string.Join(' ', result.StandardError.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
            throw new InvalidOperationException($"{failureCode}: {error[..Math.Min(500, error.Length)]}");
        }
    }

    private static void ValidateNarration(IReadOnlyList<TimedNarrationAsset> narration, TimeSpan mediaDuration)
    {
        TimedNarrationAsset? previous = null;
        foreach (var segment in narration.OrderBy(item => item.Window.Start))
        {
            segment.Window.ValidateWithin(mediaDuration);
            if (!File.Exists(segment.AbsoluteAudioPath))
            {
                throw new FileNotFoundException("A narration segment is missing.", segment.AbsoluteAudioPath);
            }

            if (segment.MeasuredDuration > segment.Window.Duration)
            {
                throw new InvalidOperationException($"Narration segment {segment.SegmentId} exceeds its window.");
            }

            if (previous is not null && previous.Window.End > segment.Window.Start)
            {
                throw new InvalidOperationException("Narration segments overlap.");
            }

            previous = segment;
        }
    }

    private static bool CanStreamCopyVideo(MediaProbeResult probe)
    {
        var video = probe.Streams.FirstOrDefault(stream => stream.CodecType == "video");
        return video?.CodecName is "h264" or "hevc" or "av1";
    }

    private static string Format(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }
}
