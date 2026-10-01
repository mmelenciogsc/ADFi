using System.Security.Cryptography;
using System.Text.Json;
using ADFi.Core;

namespace ADFi.Media;

public sealed record SourceIntakeResult(
    ImmutableSourceMetadata Source,
    RightsAttestation Attestation,
    PipelineCheckpoint Checkpoint);

public sealed class SourceIntakeService(IMediaProbe mediaProbe, TimeProvider timeProvider)
{
    private const decimal TemporarySpaceMultiplier = 2.5m;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public async Task<SourceIntakeResult> ValidateAsync(
        string localVideoPath,
        string projectDirectory,
        Guid userId,
        Guid projectId,
        bool rightsAccepted,
        CancellationToken cancellationToken)
    {
        var sourcePath = LocalSourcePolicy.ValidateExistingLocalVideo(localVideoPath);
        var projectPath = Path.GetFullPath(projectDirectory);
        Directory.CreateDirectory(projectPath);

        var sourceInfo = new FileInfo(sourcePath);
        var estimatedTemporaryBytes = checked((long)Math.Ceiling(sourceInfo.Length * TemporarySpaceMultiplier));
        var drive = new DriveInfo(Path.GetPathRoot(projectPath)!);
        if (drive.AvailableFreeSpace < estimatedTemporaryBytes)
        {
            throw new IOException(
                $"Insufficient temporary disk space. Required: {estimatedTemporaryBytes} bytes; available: {drive.AvailableFreeSpace} bytes.");
        }

        var hash = await ComputeSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        var attestation = RightsAttestation.Create(
            userId,
            projectId,
            timeProvider.GetUtcNow(),
            hash,
            rightsAccepted);
        var probe = await mediaProbe.ProbeAsync(sourcePath, cancellationToken).ConfigureAwait(false);
        var source = new ImmutableSourceMetadata(
            sourcePath,
            hash,
            sourceInfo.Length,
            sourceInfo.LastWriteTimeUtc,
            probe,
            estimatedTemporaryBytes);
        var relativeManifestPath = "manifests/source-validated.json";
        var manifestPath = Path.Combine(projectPath, relativeManifestPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);

        var checkpoint = new PipelineCheckpoint(
            projectId,
            AdficationPhase.SourceIntakeAndAuthorization,
            CheckpointStatus.Complete,
            timeProvider.GetUtcNow(),
            relativeManifestPath,
            hash,
            null);
        var result = new SourceIntakeResult(source, attestation, checkpoint);
        await WriteAtomicallyAsync(manifestPath, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    private static async Task WriteAtomicallyAsync<T>(
        string destination,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = destination + ".partial";
        await using (var stream = new FileStream(
            temporary,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                value,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(temporary, destination, true);
    }
}
