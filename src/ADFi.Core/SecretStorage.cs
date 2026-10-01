namespace ADFi.Core;

public interface IProtectedSecretStore
{
    Task SaveAsync(string key, ReadOnlyMemory<byte> secret, CancellationToken cancellationToken);

    Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);
}
