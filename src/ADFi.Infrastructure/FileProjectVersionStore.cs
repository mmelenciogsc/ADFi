using System.Collections.Concurrent;
using System.Text.Json;
using ADFi.Core;

namespace ADFi.Infrastructure;

public sealed class FileProjectVersionStore(string workspaceRoot) : IProjectVersionStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Locks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task InitializeAsync(ProjectSnapshot initial, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(initial);
        if (initial.Version != 1)
        {
            throw new ArgumentException("An initial project snapshot must be version 1.", nameof(initial));
        }

        var gate = GetLock(initial.ProjectId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var currentPath = CurrentPath(initial.ProjectId);
            if (File.Exists(currentPath))
            {
                throw new InvalidOperationException("PROJECT_ALREADY_EXISTS");
            }

            await WriteSnapshotAsync(initial, cancellationToken).ConfigureAwait(false);
            await WriteCurrentPointerAsync(initial.ProjectId, initial.Version, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ProjectSnapshot> LoadCurrentAsync(Guid projectId, CancellationToken cancellationToken)
    {
        var currentPath = CurrentPath(projectId);
        if (!File.Exists(currentPath))
        {
            throw new FileNotFoundException("Project state was not initialized.", currentPath);
        }

        var pointer = await ReadAsync<CurrentPointer>(currentPath, cancellationToken).ConfigureAwait(false);
        return await LoadVersionAsync(projectId, pointer.Version, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectSnapshot>> LoadHistoryAsync(
        Guid projectId,
        CancellationToken cancellationToken)
    {
        var directory = VersionDirectory(projectId);
        if (!Directory.Exists(directory))
        {
            return [];
        }

        var snapshots = new List<ProjectSnapshot>();
        foreach (var path in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            snapshots.Add(await ReadAsync<ProjectSnapshot>(path, cancellationToken).ConfigureAwait(false));
        }

        return snapshots;
    }

    public async Task AppendAsync(
        ProjectSnapshot snapshot,
        ProjectMutationRecord mutation,
        int expectedCurrentVersion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(mutation);
        if (snapshot.ProjectId != mutation.ProjectId
            || mutation.PreviousVersion != expectedCurrentVersion
            || mutation.ResultVersion != snapshot.Version
            || snapshot.Version != expectedCurrentVersion + 1)
        {
            throw new ArgumentException("Project mutation version metadata is inconsistent.", nameof(mutation));
        }

        var gate = GetLock(snapshot.ProjectId);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = await LoadCurrentAsync(snapshot.ProjectId, cancellationToken).ConfigureAwait(false);
            if (current.Version != expectedCurrentVersion)
            {
                throw new InvalidOperationException("PROJECT_VERSION_CONFLICT");
            }

            await WriteSnapshotAsync(snapshot, cancellationToken).ConfigureAwait(false);
            await WriteJsonAtomicallyAsync(MutationPath(snapshot.ProjectId, snapshot.Version), mutation, cancellationToken)
                .ConfigureAwait(false);
            await WriteCurrentPointerAsync(snapshot.ProjectId, snapshot.Version, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private Task WriteSnapshotAsync(ProjectSnapshot snapshot, CancellationToken cancellationToken) =>
        WriteJsonAtomicallyAsync(VersionPath(snapshot.ProjectId, snapshot.Version), snapshot, cancellationToken);

    private Task WriteCurrentPointerAsync(Guid projectId, int version, CancellationToken cancellationToken) =>
        WriteJsonAtomicallyAsync(CurrentPath(projectId), new CurrentPointer(version), cancellationToken, overwrite: true);

    private async Task<ProjectSnapshot> LoadVersionAsync(
        Guid projectId,
        int version,
        CancellationToken cancellationToken) =>
        await ReadAsync<ProjectSnapshot>(VersionPath(projectId, version), cancellationToken).ConfigureAwait(false);

    private static async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException($"Project state file is empty: {Path.GetFileName(path)}");
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken,
        bool overwrite = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.partial";
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private SemaphoreSlim GetLock(Guid projectId) => Locks.GetOrAdd(
        Path.Combine(Path.GetFullPath(workspaceRoot), projectId.ToString("D")),
        _ => new SemaphoreSlim(1, 1));

    private string ProjectRoot(Guid projectId) =>
        Path.Combine(Path.GetFullPath(workspaceRoot), projectId.ToString("D"));

    private string CurrentPath(Guid projectId) => Path.Combine(ProjectRoot(projectId), "state", "current.json");

    private string VersionDirectory(Guid projectId) => Path.Combine(ProjectRoot(projectId), "state", "versions");

    private string VersionPath(Guid projectId, int version) =>
        Path.Combine(VersionDirectory(projectId), $"{version:D8}.json");

    private string MutationPath(Guid projectId, int version) =>
        Path.Combine(ProjectRoot(projectId), "audit", "mutations", $"{version:D8}.json");

    private sealed record CurrentPointer(int Version);
}
