using System.Text.Json;
using System.IO;
using ADFi.Core;
using ADFi.Infrastructure;
using ADFi.Media;

namespace ADFi.Desktop;

public sealed record LocalProjectSummary(
    Guid ProjectId,
    string ProjectDirectory,
    string SourceFileName,
    TimeSpan Duration,
    AdficationPhase LatestPhase,
    CheckpointStatus LatestStatus)
{
    public string AccessibleLabel =>
        $"{SourceFileName}. {FormatDuration(Duration)}. {LatestPhase}: {LatestStatus}.";

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} hours {duration.Minutes} minutes"
            : $"{duration.Minutes} minutes {duration.Seconds} seconds";
}

public sealed class LocalProjectCatalog(string projectRoot)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public string CreateProjectDirectory(Guid projectId) =>
        Path.Combine(Path.GetFullPath(projectRoot), projectId.ToString("D"));

    public async Task<IReadOnlyList<LocalProjectSummary>> LoadAsync(CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(projectRoot);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var results = new List<LocalProjectSummary>();
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifest = Path.Combine(directory, "manifests", "source-validated.json");
            if (!File.Exists(manifest))
            {
                continue;
            }

            try
            {
                await using var stream = File.OpenRead(manifest);
                var intake = await JsonSerializer.DeserializeAsync<SourceIntakeResult>(
                    stream,
                    JsonOptions,
                    cancellationToken).ConfigureAwait(false);
                if (intake is null)
                {
                    continue;
                }

                var checkpoints = await new FilePipelineCheckpointStore(projectRoot)
                    .LoadAsync(intake.Checkpoint.ProjectId, cancellationToken).ConfigureAwait(false);
                var latest = checkpoints
                    .OrderByDescending(item => item.Phase)
                    .FirstOrDefault() ?? intake.Checkpoint;
                results.Add(new(
                    intake.Checkpoint.ProjectId,
                    directory,
                    Path.GetFileName(intake.Source.SourcePath),
                    intake.Source.Probe.Duration,
                    latest.Phase,
                    latest.Status));
            }
            catch (JsonException)
            {
                // A corrupt or partial project is not presented as resumable. Technical diagnostics
                // are emitted by the caller without leaking private manifest content.
            }
        }

        return results
            .OrderByDescending(item => Directory.GetLastWriteTimeUtc(item.ProjectDirectory))
            .ToArray();
    }
}
