using System.Security.Cryptography;
using System.Text;

namespace ADFi.Media;

public sealed record PiperVoice(
    string Id,
    string AccessibleName,
    string ModelName,
    string ModelLicense,
    Uri ModelCard);

public static class PiperVoiceCatalog
{
    public static IReadOnlyList<PiperVoice> All { get; } =
    [
        new("voice-1", "Voice 1, Cori, British English", "en_GB-cori-high", "Public domain", new Uri("https://huggingface.co/rhasspy/piper-voices/blob/main/en/en_GB/cori/high/MODEL_CARD")),
        new("voice-2", "Voice 2, LJSpeech, United States English", "en_US-ljspeech-high", "Public domain", new Uri("https://huggingface.co/rhasspy/piper-voices/blob/main/en/en_US/ljspeech/high/MODEL_CARD")),
        new("voice-3", "Voice 3, Bryce, United States English", "en_US-bryce-medium", "Public domain", new Uri("https://huggingface.co/rhasspy/piper-voices/blob/main/en/en_US/bryce/medium/MODEL_CARD")),
        new("voice-4", "Voice 4, Joe, United States English", "en_US-joe-medium", "CC0-1.0", new Uri("https://huggingface.co/rhasspy/piper-voices/blob/main/en/en_US/joe/medium/MODEL_CARD")),
        new("voice-5", "Voice 5, Sam, United States English", "en_US-sam-medium", "Apache-2.0", new Uri("https://huggingface.co/rhasspy/piper-voices/blob/main/en/en_US/sam/medium/MODEL_CARD")),
    ];

    public static PiperVoice Get(string id) =>
        All.SingleOrDefault(voice => string.Equals(voice.Id, id, StringComparison.Ordinal))
        ?? throw new ArgumentException("The selected Piper voice is not in ADFi's reviewed catalog.", nameof(id));
}

public sealed record PiperConfiguration(string PythonExecutable, string VoiceDataDirectory);

public sealed record NarrationSynthesisRequest(
    string SegmentId,
    string Text,
    string VoiceId,
    TimeSpan WindowDuration,
    TimeSpan BreathingRoom,
    int AutoFitAttempt);

public sealed record NarrationSynthesisResult(
    string SegmentId,
    string AudioPath,
    TimeSpan MeasuredDuration,
    NarrationFitResult Fit,
    bool ReusedCachedAudio,
    PiperVoice Voice);

public sealed record NarrationProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface INarrationProcessRunner
{
    Task<NarrationProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public interface INarrationSynthesizer
{
    Task<NarrationSynthesisResult> SynthesizeAsync(
        NarrationSynthesisRequest request,
        CancellationToken cancellationToken);
}

public sealed class PiperProcessRunner : INarrationProcessRunner
{
    public async Task<NarrationProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await ExternalProcess.RunAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
        return new(result.ExitCode, result.StandardOutput, result.StandardError);
    }
}

public sealed class PiperNarrationService(
    PiperConfiguration configuration,
    string cacheDirectory,
    INarrationProcessRunner processRunner) : INarrationSynthesizer
{
    public async Task<NarrationSynthesisResult> SynthesizeAsync(
        NarrationSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SegmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Text);
        var voice = PiperVoiceCatalog.Get(request.VoiceId);
        if (request.WindowDuration <= TimeSpan.Zero || request.BreathingRoom < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        Directory.CreateDirectory(cacheDirectory);
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"piper-cli-v1\n{voice.ModelName}\n{request.Text}"))).ToLowerInvariant();
        var outputPath = Path.Combine(Path.GetFullPath(cacheDirectory), cacheKey + ".wav");
        var reused = File.Exists(outputPath);
        if (!reused)
        {
            var partialPath = outputPath + ".partial";
            try
            {
                var result = await processRunner.RunAsync(
                    configuration.PythonExecutable,
                    [
                        "-m", "piper",
                        "--data-dir", Path.GetFullPath(configuration.VoiceDataDirectory),
                        "-m", voice.ModelName,
                        "-f", partialPath,
                        "--", request.Text,
                    ],
                    cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0 || !File.Exists(partialPath))
                {
                    throw new InvalidOperationException(
                        $"PIPER_SYNTHESIS_FAILED: {Sanitize(result.StandardError)}");
                }

                File.Move(partialPath, outputPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(partialPath))
                {
                    File.Delete(partialPath);
                }
            }
        }

        var duration = WaveDurationReader.Measure(outputPath);
        var fit = NarrationTimingPolicy.Evaluate(
            duration,
            request.WindowDuration,
            request.BreathingRoom,
            request.AutoFitAttempt);
        return new(request.SegmentId, outputPath, duration, fit, reused, voice);
    }

    private static string Sanitize(string value)
    {
        var sanitized = string.Join(' ', value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        return sanitized[..Math.Min(sanitized.Length, 500)];
    }
}

public static class WaveDurationReader
{
    public static TimeSpan Measure(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: false);
        if (new string(reader.ReadChars(4)) != "RIFF")
        {
            throw new InvalidDataException("Piper output is not a RIFF WAV file.");
        }

        _ = reader.ReadUInt32();
        if (new string(reader.ReadChars(4)) != "WAVE")
        {
            throw new InvalidDataException("Piper output is not a WAVE file.");
        }

        uint byteRate = 0;
        long dataBytes = -1;
        while (stream.Position + 8 <= stream.Length)
        {
            var chunkId = new string(reader.ReadChars(4));
            var chunkSize = reader.ReadUInt32();
            var next = checked(stream.Position + chunkSize + (chunkSize % 2));
            if (next > stream.Length)
            {
                throw new InvalidDataException("Piper WAV contains a truncated chunk.");
            }

            if (chunkId == "fmt ")
            {
                if (chunkSize < 16)
                {
                    throw new InvalidDataException("Piper WAV has an invalid format chunk.");
                }

                _ = reader.ReadUInt16();
                _ = reader.ReadUInt16();
                _ = reader.ReadUInt32();
                byteRate = reader.ReadUInt32();
            }
            else if (chunkId == "data")
            {
                dataBytes = chunkSize;
            }

            stream.Position = next;
        }

        if (byteRate == 0 || dataBytes < 0)
        {
            throw new InvalidDataException("Piper WAV is missing duration metadata.");
        }

        return TimeSpan.FromSeconds(dataBytes / (double)byteRate);
    }
}
