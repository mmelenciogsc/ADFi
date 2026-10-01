using System.Security.Cryptography;
using System.Text;
using Npgsql;
using NpgsqlTypes;

namespace ADFi.Infrastructure.Postgres;

public sealed record AccountRegistration(Guid UserId, string Email, string DisplayName, string PasswordHash);

public sealed record AuthenticatedAccount(Guid UserId, string Email, string DisplayName, bool EmailVerified);

public sealed record SessionToken(string Token, DateTimeOffset ExpiresAtUtc);

public sealed record EmailVerificationChallenge(string Token, DateTimeOffset ExpiresAtUtc);

public sealed record PasswordResetChallenge(
    string Email,
    string DisplayName,
    string Token,
    DateTimeOffset ExpiresAtUtc);

public sealed class AccountRepository(AdfiDatabase database, TimeProvider timeProvider)
{
    public async Task CreateAsync(AccountRegistration registration, CancellationToken cancellationToken)
    {
        await using var command = database.DataSource.CreateCommand(
            """
            INSERT INTO users (id, email_normalized, email_display, display_name, password_hash, created_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6)
            """);
        command.Parameters.AddWithValue(registration.UserId);
        command.Parameters.AddWithValue(registration.Email.Trim().ToUpperInvariant());
        command.Parameters.AddWithValue(registration.Email.Trim());
        command.Parameters.AddWithValue(registration.DisplayName.Trim());
        command.Parameters.AddWithValue(registration.PasswordHash);
        command.Parameters.AddWithValue(timeProvider.GetUtcNow());
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<EmailVerificationChallenge> CreateEmailVerificationChallengeAsync(
        Guid userId,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var rawToken = CreateOpaqueToken();
        var expires = timeProvider.GetUtcNow().Add(lifetime);
        await using var command = database.DataSource.CreateCommand(
            """
            INSERT INTO email_verification_tokens (id, user_id, token_sha256, created_at_utc, expires_at_utc)
            VALUES ($1, $2, $3, $4, $5)
            """);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(HashToken(rawToken));
        command.Parameters.AddWithValue(timeProvider.GetUtcNow());
        command.Parameters.AddWithValue(expires);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new(rawToken, expires);
    }

    public async Task<bool> VerifyEmailAsync(string rawToken, CancellationToken cancellationToken)
    {
        await using var command = database.DataSource.CreateCommand(
            """
            WITH consumed AS (
                UPDATE email_verification_tokens
                SET consumed_at_utc = now()
                WHERE token_sha256 = $1
                  AND consumed_at_utc IS NULL
                  AND expires_at_utc > now()
                RETURNING user_id
            )
            UPDATE users
            SET email_verified_at_utc = COALESCE(email_verified_at_utc, now())
            WHERE id = (SELECT user_id FROM consumed)
            """);
        command.Parameters.AddWithValue(HashToken(rawToken));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<(AuthenticatedAccount Account, string PasswordHash)?> FindByEmailAsync(
        string email,
        CancellationToken cancellationToken)
    {
        await using var command = database.DataSource.CreateCommand(
            """
            SELECT id, email_display, display_name, email_verified_at_utc IS NOT NULL, password_hash
            FROM users
            WHERE email_normalized = $1 AND disabled_at_utc IS NULL
            """);
        command.Parameters.AddWithValue(email.Trim().ToUpperInvariant());
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return (
            new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3)),
            reader.GetString(4));
    }

    public async Task<SessionToken> CreateSessionAsync(
        Guid userId,
        string deviceLabel,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var rawToken = CreateOpaqueToken();
        var tokenHash = HashToken(rawToken);
        var expires = timeProvider.GetUtcNow().Add(lifetime);
        await using var command = database.DataSource.CreateCommand(
            """
            INSERT INTO auth_sessions (id, user_id, token_sha256, device_label, created_at_utc, expires_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6)
            """);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(userId);
        command.Parameters.AddWithValue(tokenHash);
        command.Parameters.AddWithValue(deviceLabel);
        command.Parameters.AddWithValue(timeProvider.GetUtcNow());
        command.Parameters.AddWithValue(expires);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new(rawToken, expires);
    }

    public async Task<AuthenticatedAccount?> ValidateSessionAsync(
        string rawToken,
        CancellationToken cancellationToken)
    {
        await using var command = database.DataSource.CreateCommand(
            """
            SELECT u.id, u.email_display, u.display_name, u.email_verified_at_utc IS NOT NULL
            FROM auth_sessions s
            JOIN users u ON u.id = s.user_id
            WHERE s.token_sha256 = $1
              AND s.expires_at_utc > now()
              AND s.revoked_at_utc IS NULL
              AND u.disabled_at_utc IS NULL
            """);
        command.Parameters.AddWithValue(HashToken(rawToken));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3))
            : null;
    }

    public async Task RevokeSessionAsync(string rawToken, CancellationToken cancellationToken)
    {
        await using var command = database.DataSource.CreateCommand(
            "UPDATE auth_sessions SET revoked_at_utc = now() WHERE token_sha256 = $1 AND revoked_at_utc IS NULL");
        command.Parameters.AddWithValue(HashToken(rawToken));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<PasswordResetChallenge?> CreatePasswordResetChallengeAsync(
        string email,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var account = await FindByEmailAsync(email, cancellationToken).ConfigureAwait(false);
        if (account is null || !account.Value.Account.EmailVerified)
        {
            return null;
        }

        var token = CreateOpaqueToken();
        var expires = timeProvider.GetUtcNow().Add(lifetime);
        await using var command = database.DataSource.CreateCommand(
            """
            INSERT INTO password_reset_tokens (id, user_id, token_sha256, created_at_utc, expires_at_utc)
            VALUES ($1, $2, $3, $4, $5)
            """);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(account.Value.Account.UserId);
        command.Parameters.AddWithValue(HashToken(token));
        command.Parameters.AddWithValue(timeProvider.GetUtcNow());
        command.Parameters.AddWithValue(expires);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return new(account.Value.Account.Email, account.Value.Account.DisplayName, token, expires);
    }

    public async Task<bool> ResetPasswordAsync(
        string rawToken,
        string newPasswordHash,
        CancellationToken cancellationToken)
    {
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var consume = new NpgsqlCommand(
            """
            UPDATE password_reset_tokens
            SET consumed_at_utc = now()
            WHERE token_sha256 = $1
              AND consumed_at_utc IS NULL
              AND expires_at_utc > now()
            RETURNING user_id
            """,
            connection,
            transaction);
        consume.Parameters.AddWithValue(HashToken(rawToken));
        var user = await consume.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (user is not Guid userId)
        {
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        await using var update = new NpgsqlCommand(
            "UPDATE users SET password_hash = $1 WHERE id = $2",
            connection,
            transaction);
        update.Parameters.AddWithValue(newPasswordHash);
        update.Parameters.AddWithValue(userId);
        await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using var revoke = new NpgsqlCommand(
            "UPDATE auth_sessions SET revoked_at_utc = now() WHERE user_id = $1 AND revoked_at_utc IS NULL",
            connection,
            transaction);
        revoke.Parameters.AddWithValue(userId);
        await revoke.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<SessionToken?> RotateSessionAsync(
        string rawToken,
        TimeSpan lifetime,
        CancellationToken cancellationToken)
    {
        var newToken = CreateOpaqueToken();
        var expires = timeProvider.GetUtcNow().Add(lifetime);
        await using var connection = await database.DataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var rotate = new NpgsqlCommand(
            """
            UPDATE auth_sessions
            SET rotated_at_utc = now(), revoked_at_utc = now()
            WHERE token_sha256 = $1
              AND expires_at_utc > now()
              AND revoked_at_utc IS NULL
            RETURNING user_id, device_id, device_label
            """,
            connection,
            transaction);
        rotate.Parameters.AddWithValue(HashToken(rawToken));
        await using var reader = await rotate.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            await reader.DisposeAsync().ConfigureAwait(false);
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }

        var userId = reader.GetGuid(0);
        var deviceId = reader.IsDBNull(1) ? (Guid?)null : reader.GetGuid(1);
        var deviceLabel = reader.GetString(2);
        await reader.DisposeAsync().ConfigureAwait(false);

        await using var create = new NpgsqlCommand(
            """
            INSERT INTO auth_sessions
                (id, user_id, device_id, token_sha256, device_label, created_at_utc, expires_at_utc)
            VALUES ($1, $2, $3, $4, $5, $6, $7)
            """,
            connection,
            transaction);
        create.Parameters.AddWithValue(Guid.NewGuid());
        create.Parameters.AddWithValue(userId);
        create.Parameters.Add(new NpgsqlParameter
        {
            NpgsqlDbType = NpgsqlDbType.Uuid,
            Value = deviceId is null ? DBNull.Value : deviceId.Value,
        });
        create.Parameters.AddWithValue(HashToken(newToken));
        create.Parameters.AddWithValue(deviceLabel);
        create.Parameters.AddWithValue(timeProvider.GetUtcNow());
        create.Parameters.AddWithValue(expires);
        await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(newToken, expires);
    }

    private static string CreateOpaqueToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(48))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static string HashToken(string rawToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rawToken);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
    }
}
