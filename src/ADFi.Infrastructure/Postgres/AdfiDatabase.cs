using System.Reflection;
using Npgsql;

namespace ADFi.Infrastructure.Postgres;

public sealed class AdfiDatabase : IAsyncDisposable
{
    private const long MigrationLockId = 4_114_446_900;
    private readonly NpgsqlDataSource dataSource;

    public AdfiDatabase(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        dataSource = builder.Build();
    }

    public NpgsqlDataSource DataSource => dataSource;

    public async Task ApplyMigrationsAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock($1)",
            connection,
            transaction))
        {
            lockCommand.Parameters.AddWithValue(MigrationLockId);
            await lockCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using (var createHistory = new NpgsqlCommand(
            "CREATE TABLE IF NOT EXISTS adfi_schema_migrations (version text PRIMARY KEY, applied_at_utc timestamptz NOT NULL)",
            connection,
            transaction))
        {
            await createHistory.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        foreach (var migration in LoadMigrations())
        {
            await using var check = new NpgsqlCommand(
                "SELECT EXISTS (SELECT 1 FROM adfi_schema_migrations WHERE version = $1)",
                connection,
                transaction);
            check.Parameters.AddWithValue(migration.Version);
            var alreadyApplied = (bool)(await check.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) ?? false);
            if (alreadyApplied)
            {
                continue;
            }

            await using var apply = new NpgsqlCommand(migration.Sql, connection, transaction)
            {
                CommandTimeout = 120,
            };
            await apply.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            await using var record = new NpgsqlCommand(
                "INSERT INTO adfi_schema_migrations(version, applied_at_utc) VALUES ($1, now())",
                connection,
                transaction);
            record.Parameters.AddWithValue(migration.Version);
            await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken) =>
        await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false) is { } connection
            && await DisposeConnectionAndReturnTrueAsync(connection).ConfigureAwait(false);

    public ValueTask DisposeAsync() => dataSource.DisposeAsync();

    private static async Task<bool> DisposeConnectionAndReturnTrueAsync(NpgsqlConnection connection)
    {
        await connection.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    private static EmbeddedMigration[] LoadMigrations()
    {
        var assembly = typeof(AdfiDatabase).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"Missing embedded migration: {name}");
                using var reader = new StreamReader(stream);
                var fileName = name[(name.LastIndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
                return new EmbeddedMigration(fileName[..^4], reader.ReadToEnd());
            })
            .ToArray();
    }

    private sealed record EmbeddedMigration(string Version, string Sql);
}
