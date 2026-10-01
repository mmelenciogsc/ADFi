using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using ADFi.Core;

namespace ADFi.Media;

public sealed record AudioCharacterization(
    int AnalysisSampleRate,
    int AnalysisChannels,
    decimal? MeanVolumeDb,
    decimal? PeakVolumeDb,
    IReadOnlyList<SourceAudioInterval> InitialAudioTimeline);

public sealed record AudioPreparationManifest(
    Guid ProjectId,
    string SourceSha256,
    string AnalysisAudioRelativePath,
    AudioCharacterization Characterization,
    IReadOnlyList<AudioChunk> Chunks,
    DateTimeOffset CreatedAtUtc);

public sealed partial class AudioPreparationService(
    string ffmpegPath,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<AudioPreparationManifest> PrepareAsync(
        Guid projectId,
        ImmutableSourceMetadata source,
        string projectDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        var sourcePath = LocalSourcePolicy.ValidateExistingLocalVideo(source.SourcePath);
        var root = Path.GetFullPath(projectDirectory);
        var audioDirectory = Path.Combine(root, "audio");
        var chunksDirectory = Path.Combine(audioDirectory, "chunks");
        Directory.CreateDirectory(chunksDirectory);
        var analysisAudioPath = Path.Combine(audioDirectory, "analysis-mono-16k.wav");

        await RunRequiredAsync(
            [
                "-hide_banner", "-loglevel", "error", "-y", "-i", sourcePath,
                "-vn", "-ac", "1", "-ar", "16000",
                "-af", "loudnorm=I=-23:TP=-2:LRA=7",
                "-c:a", "pcm_s16le", analysisAudioPath,
            ],
            "AUDIO_EXTRACTION_FAILED",
            cancellationToken).ConfigureAwait(false);

        var volumeTask = MeasureVolumeAsync(analysisAudioPath, cancellationToken);
        var silenceTask = DetectSilenceAsync(analysisAudioPath, source.Probe.Duration, cancellationToken);
        await Task.WhenAll(volumeTask, silenceTask).ConfigureAwait(false);

        var chunks = AudioChunkPlanner.Plan(
            source.Probe.Duration,
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(1));
        foreach (var chunk in chunks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var outputPath = Path.Combine(root, chunk.RelativeAudioPath.Replace('/', Path.DirectorySeparatorChar));
            await RunRequiredAsync(
                [
                    "-hide_banner", "-loglevel", "error", "-y",
                    "-ss", FormatTime(chunk.SourceInterval.Start),
                    "-i", analysisAudioPath,
                    "-t", FormatTime(chunk.SourceInterval.Duration),
                    "-c:a", "pcm_s16le", outputPath,
                ],
                "AUDIO_CHUNK_FAILED",
                cancellationToken).ConfigureAwait(false);
        }

        var volume = await volumeTask.ConfigureAwait(false);
        var characterization = new AudioCharacterization(
            16_000,
            1,
            volume.Mean,
            volume.Peak,
            await silenceTask.ConfigureAwait(false));
        var manifest = new AudioPreparationManifest(
            projectId,
            source.SourceSha256,
            "audio/analysis-mono-16k.wav",
            characterization,
            chunks,
            timeProvider.GetUtcNow());
        await WriteManifestAsync(
            Path.Combine(root, "manifests", "audio-prepared.json"),
            manifest,
            cancellationToken).ConfigureAwait(false);
        return manifest;
    }

    private async Task<(decimal? Mean, decimal? Peak)> MeasureVolumeAsync(
        string analysisAudioPath,
        CancellationToken cancellationToken)
    {
        var result = await ExternalProcess.RunAsync(
            ffmpegPath,
            ["-hide_banner", "-nostats", "-i", analysisAudioPath, "-af", "volumedetect", "-f", "null", "-"],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("AUDIO_CHARACTERIZATION_FAILED");
        }

        return (ParseDecibels(MeanVolumeRegex().Match(result.StandardError)), ParseDecibels(PeakVolumeRegex().Match(result.StandardError)));
    }

    private async Task<IReadOnlyList<SourceAudioInterval>> DetectSilenceAsync(
        string analysisAudioPath,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        var result = await ExternalProcess.RunAsync(
            ffmpegPath,
            ["-hide_banner", "-nostats", "-i", analysisAudioPath, "-af", "silencedetect=noise=-35dB:d=0.35", "-f", "null", "-"],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("AUDIO_ACTIVITY_ANALYSIS_FAILED");
        }

        var starts = SilenceStartRegex().Matches(result.StandardError)
            .Select(match => ParseSeconds(match.Groups[1].Value))
            .ToArray();
        var ends = SilenceEndRegex().Matches(result.StandardError)
            .Select(match => ParseSeconds(match.Groups[1].Value))
            .ToArray();
        var timeline = new List<SourceAudioInterval>();
        var cursor = TimeSpan.Zero;
        for (var index = 0; index < starts.Length; index++)
        {
            var silenceStart = Clamp(starts[index], TimeSpan.Zero, duration);
            var silenceEnd = Clamp(index < ends.Length ? ends[index] : duration, silenceStart, duration);
            if (silenceStart > cursor)
            {
                timeline.Add(new(new(cursor, silenceStart), SourceAudioKind.MeaningfulNonDialogue, 0.5m, $"audio-active-{index:D5}"));
            }

            if (silenceEnd > silenceStart)
            {
                timeline.Add(new(new(silenceStart, silenceEnd), SourceAudioKind.QuietCandidate, 0.8m, $"audio-quiet-{index:D5}"));
            }

            cursor = silenceEnd;
        }

        if (cursor < duration)
        {
            timeline.Add(new(new(cursor, duration), SourceAudioKind.MeaningfulNonDialogue, 0.5m, "audio-active-final"));
        }

        return timeline;
    }

    private async Task RunRequiredAsync(
        IReadOnlyList<string> arguments,
        string failureCode,
        CancellationToken cancellationToken)
    {
        var result = await ExternalProcess.RunAsync(ffmpegPath, arguments, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"{failureCode}: {Sanitize(result.StandardError)}");
        }
    }

    private static async Task WriteManifestAsync(
        string destination,
        AudioPreparationManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".partial";
        await using (var stream = File.Create(temporary))
        {
            await JsonSerializer.SerializeAsync(stream, manifest, JsonOptions, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, destination, true);
    }

    private static decimal? ParseDecibels(Match match) =>
        match.Success
        && decimal.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;

    private static TimeSpan ParseSeconds(string value) =>
        TimeSpan.FromSeconds(double.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture));

    private static TimeSpan Clamp(TimeSpan value, TimeSpan minimum, TimeSpan maximum) =>
        value < minimum ? minimum : value > maximum ? maximum : value;

    private static string FormatTime(TimeSpan value) => value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Sanitize(string value)
    {
        var sanitized = string.Join(' ', value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        return sanitized[..Math.Min(500, sanitized.Length)];
    }

    [GeneratedRegex(@"mean_volume:\s*(-?[0-9]+(?:\.[0-9]+)?)\s*dB", RegexOptions.CultureInvariant)]
    private static partial Regex MeanVolumeRegex();

    [GeneratedRegex(@"max_volume:\s*(-?[0-9]+(?:\.[0-9]+)?)\s*dB", RegexOptions.CultureInvariant)]
    private static partial Regex PeakVolumeRegex();

    [GeneratedRegex(@"silence_start:\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex SilenceStartRegex();

    [GeneratedRegex(@"silence_end:\s*([0-9]+(?:\.[0-9]+)?)", RegexOptions.CultureInvariant)]
    private static partial Regex SilenceEndRegex();
}

