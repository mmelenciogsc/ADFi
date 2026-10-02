using System.Text.Json;
using ADFi.Core;

namespace ADFi.Conversation;

public sealed record CreatorConversationProgress(
    int CompletedTasks,
    int TotalTasks,
    string Announcement);

public sealed record CreatorConversationTurnManifest(
    Guid TurnId,
    Guid ProjectId,
    int StartingProjectVersion,
    int ResultProjectVersion,
    string UserRequest,
    ConversationIntent Intent,
    TimeSpan? ReferencedTime,
    string? SelectedSegmentId,
    TaskPlan Plan,
    PromptDecisionProvenance? PlannerProvenance,
    IReadOnlyList<TaskResult> Results,
    string Response,
    MediaInterval? PreviewWindow,
    bool PreviewRequested,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc);

public sealed record CreatorConversationTurnResult(
    CreatorConversationTurnManifest Manifest,
    ProjectSnapshot Snapshot,
    string? SelectedSegmentId);

public sealed class FileConversationTaskResultStore : ITaskResultStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly string taskDirectory;
    private int sequence;

    public FileConversationTaskResultStore(string projectDirectory, Guid turnId)
    {
        var root = Path.GetFullPath(projectDirectory);
        taskDirectory = Path.Combine(root, "conversation", "turns", turnId.ToString("D"), "tasks");
    }

    public Task PersistAsync(TaskResult result, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        var current = Interlocked.Increment(ref sequence);
        return WriteAtomicallyAsync(
            Path.Combine(taskDirectory, $"{current:D3}.json"),
            result,
            cancellationToken);
    }

    public static Task PersistTurnAsync(
        string projectDirectory,
        CreatorConversationTurnManifest manifest,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var root = Path.GetFullPath(projectDirectory);
        return WriteAtomicallyAsync(
            Path.Combine(root, "conversation", "turns", manifest.TurnId.ToString("D"), "turn.json"),
            manifest,
            cancellationToken);
    }

    private static async Task WriteAtomicallyAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
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
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: false);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }
}
