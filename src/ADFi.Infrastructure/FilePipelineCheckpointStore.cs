using System.Text.Json;
using ADFi.Core;

namespace ADFi.Infrastructure;

public sealed class FilePipelineCheckpointStore(string workspaceRoot) : IPipelineCheckpointStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    public async Task<IReadOnlyList<PipelineCheckpoint>> LoadAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var path = GetPath(projectId);
        if (!File.Exists(path))
        {
            return [];
        }

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<List<PipelineCheckpoint>>(
                   stream,
                   JsonOptions,
                   cancellationToken).ConfigureAwait(false)
               ?? [];
    }

    public async Task SaveAsync(PipelineCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        var existing = (await LoadAsync(checkpoint.ProjectId, cancellationToken).ConfigureAwait(false)).ToList();
        existing.RemoveAll(item => item.Phase == checkpoint.Phase);
        existing.Add(checkpoint);
        existing.Sort((left, right) => left.Phase.CompareTo(right.Phase));

        var path = GetPath(checkpoint.ProjectId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".partial";
        await using (var stream = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, existing, JsonOptions, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, path, true);
    }

    private string GetPath(Guid projectId) => Path.Combine(
        Path.GetFullPath(workspaceRoot),
        projectId.ToString("D"),
        "checkpoints.json");
}

