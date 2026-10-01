using System.IO;
using System.Net.Http;
using System.Text.Json;
using ADFi.LocalAI;

namespace ADFi.Desktop;

public sealed class CreatorHardwareProfileStore(string profileDirectory, string workingDirectory)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly string profilePath = Path.Combine(Path.GetFullPath(profileDirectory), "creator-hardware.json");

    public bool HasProfile => File.Exists(profilePath);

    public async Task<CreatorHardwareAssessment?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(profilePath))
        {
            return null;
        }

        await using var existing = File.OpenRead(profilePath);
        return await JsonSerializer.DeserializeAsync<CreatorHardwareAssessment>(
            existing,
            JsonOptions,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CreatorHardwareAssessment> GetOrMeasureAsync(
        bool force,
        CancellationToken cancellationToken)
    {
        if (!force && File.Exists(profilePath))
        {
            await using var existing = File.OpenRead(profilePath);
            return await JsonSerializer.DeserializeAsync<CreatorHardwareAssessment>(
                       existing,
                       JsonOptions,
                       cancellationToken).ConfigureAwait(false)
                   ?? throw new InvalidDataException("The saved Creator hardware profile is empty.");
        }

        using var ollama = new HttpClient
        {
            BaseAddress = new Uri("http://127.0.0.1:11434/"),
            Timeout = TimeSpan.FromMinutes(5),
        };
        var coordinator = new CreatorHardwareAssessmentCoordinator(
            workingDirectory,
            new ProcessHardwareCommandRunner(),
            model => new OllamaInferenceBenchmark(ollama, model));
        var result = await coordinator.MeasureAsync(cancellationToken).ConfigureAwait(false);

        Directory.CreateDirectory(Path.GetDirectoryName(profilePath)!);
        var temporary = profilePath + ".partial";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, result, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, profilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        return result;
    }
}
