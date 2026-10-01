using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using ADFi.Core;

namespace ADFi.Infrastructure.Security;

/// <summary>
/// Encrypts desktop OAuth tokens for the current Windows user with DPAPI. Secret names are
/// hashed before becoming filenames, and writes are atomic. Secret values are never logged.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiFileSecretStore(string directory, string applicationPurpose)
    : IProtectedSecretStore
{
    private static readonly byte[] Header = "ADFI-DPAPI-1\n"u8.ToArray();
    private readonly byte[] entropy = SHA256.HashData(Encoding.UTF8.GetBytes(applicationPurpose));

    public async Task SaveAsync(
        string key,
        ReadOnlyMemory<byte> secret,
        CancellationToken cancellationToken)
    {
        EnsureWindows();
        ValidateKey(key);
        if (secret.IsEmpty)
        {
            throw new ArgumentException("A protected secret cannot be empty.", nameof(secret));
        }

        Directory.CreateDirectory(directory);
        var protectedBytes = ProtectedData.Protect(secret.ToArray(), entropy, DataProtectionScope.CurrentUser);
        var payload = new byte[Header.Length + protectedBytes.Length];
        Header.CopyTo(payload, 0);
        protectedBytes.CopyTo(payload, Header.Length);
        var path = ResolvePath(key);
        var temporary = path + ".partial";
        try
        {
            await File.WriteAllBytesAsync(temporary, payload, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(protectedBytes);
            CryptographicOperations.ZeroMemory(payload);
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    public async Task<byte[]?> LoadAsync(string key, CancellationToken cancellationToken)
    {
        EnsureWindows();
        ValidateKey(key);
        var path = ResolvePath(key);
        if (!File.Exists(path))
        {
            return null;
        }

        var payload = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            if (payload.Length <= Header.Length || !payload.AsSpan(0, Header.Length).SequenceEqual(Header))
            {
                throw new InvalidDataException("Protected credential file format is invalid.");
            }

            return ProtectedData.Unprotect(
                payload.AsSpan(Header.Length).ToArray(),
                entropy,
                DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new InvalidDataException(
                "The credential cannot be decrypted by the current Windows user.",
                exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        EnsureWindows();
        ValidateKey(key);
        cancellationToken.ThrowIfCancellationRequested();
        var path = ResolvePath(key);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string ResolvePath(string key)
    {
        var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(Path.GetFullPath(directory), name + ".secret");
    }

    private static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (key.Length > 256)
        {
            throw new ArgumentException("Secret key is too long.", nameof(key));
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI credential storage is available only on Windows.");
        }
    }
}
