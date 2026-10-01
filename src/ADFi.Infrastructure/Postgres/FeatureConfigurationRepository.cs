using System.Text.Json;

namespace ADFi.Infrastructure.Postgres;

public sealed class FeatureConfigurationRepository(AdfiDatabase database)
{
    public async Task<bool?> GetBooleanAsync(string key, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        await using var command = database.DataSource.CreateCommand(
            "SELECT value_json::text FROM feature_configuration WHERE key = $1");
        command.Parameters.AddWithValue(key);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
        if (value is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<bool>(value);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"Feature configuration {key} is not a JSON boolean.", exception);
        }
    }
}
