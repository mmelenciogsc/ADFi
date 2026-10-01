using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using ADFi.Core;

namespace ADFi.Media;

public interface IMediaProbe
{
    Task<MediaProbeResult> ProbeAsync(string localVideoPath, CancellationToken cancellationToken);
}

public sealed class FfprobeMediaProbe(string ffprobePath = "ffprobe") : IMediaProbe
{
    public async Task<MediaProbeResult> ProbeAsync(
        string localVideoPath,
        CancellationToken cancellationToken)
    {
        var validatedPath = LocalSourcePolicy.ValidateExistingLocalVideo(localVideoPath);
        var startInfo = new ProcessStartInfo
        {
            FileName = ffprobePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-v");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-show_format");
        startInfo.ArgumentList.Add("-show_streams");
        startInfo.ArgumentList.Add("-of");
        startInfo.ArgumentList.Add("json");
        startInfo.ArgumentList.Add(validatedPath);

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException("ffprobe could not be started.");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffprobe rejected the selected media: {Sanitize(error)}");
        }

        return Parse(output, new FileInfo(validatedPath).Length);
    }

    internal static MediaProbeResult Parse(string json, long fileSizeBytes)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var format = root.GetProperty("format");
        var durationSeconds = ParseDecimal(format, "duration") ?? 0m;
        var streams = new List<MediaStreamMetadata>();
        var variableFrameRate = false;

        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            var averageRate = ParseRate(GetString(stream, "avg_frame_rate"));
            var realRate = ParseRate(GetString(stream, "r_frame_rate"));
            if (averageRate is not null && realRate is not null && Math.Abs(averageRate.Value - realRate.Value) > 0.001m)
            {
                variableFrameRate = true;
            }

            streams.Add(new(
                GetString(stream, "codec_type") ?? "unknown",
                GetString(stream, "codec_name") ?? "unknown",
                GetInt32(stream, "width"),
                GetInt32(stream, "height"),
                averageRate,
                ParseInt32(GetString(stream, "sample_rate")),
                GetInt32(stream, "channels"),
                ParseInt64(GetString(stream, "bit_rate"))));
        }

        return new(
            GetString(format, "format_name") ?? "unknown",
            TimeSpan.FromSeconds((double)durationSeconds),
            fileSizeBytes,
            variableFrameRate,
            streams);
    }

    private static string Sanitize(string value) =>
        string.Join(' ', value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries))[..Math.Min(500, string.Join(' ', value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries)).Length)];

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt32(out var parsed) ? parsed : null;

    private static int? ParseInt32(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static long? ParseInt64(string? value) =>
        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;

    private static decimal? ParseDecimal(JsonElement element, string name) =>
        decimal.TryParse(GetString(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    private static decimal? ParseRate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var parts = value.Split('/');
        if (parts.Length == 2
            && decimal.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && decimal.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && denominator != 0)
        {
            return numerator / denominator;
        }

        return decimal.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var direct)
            ? direct
            : null;
    }
}

