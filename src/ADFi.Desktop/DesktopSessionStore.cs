using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using ADFi.Core;

namespace ADFi.Desktop;

public sealed class DesktopSessionStore(IProtectedSecretStore protectedSecrets)
{
    private const string SessionKey = "adfi-api-session-v1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task SaveAsync(DesktopSession session, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        try
        {
            await protectedSecrets.SaveAsync(SessionKey, payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public async Task<DesktopSession?> LoadAsync(CancellationToken cancellationToken)
    {
        var payload = await protectedSecrets.LoadAsync(SessionKey, cancellationToken).ConfigureAwait(false);
        if (payload is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DesktopSession>(payload, JsonOptions)
                   ?? throw new InvalidDataException("The protected ADFi session is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("The protected ADFi session is invalid.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(payload);
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken) =>
        protectedSecrets.DeleteAsync(SessionKey, cancellationToken);
}
