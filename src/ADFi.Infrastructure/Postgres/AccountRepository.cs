using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace ADFi.Infrastructure.Postgres;

public sealed record AccountRegistration(Guid UserId, string Email, string DisplayName, string PasswordHash);

public sealed record AuthenticatedAccount(Guid UserId, string Email, string DisplayName, bool EmailVerified);

public sealed record SessionToken(string Token, DateTimeOffset ExpiresAtUtc);

public sealed record EmailVerificationChallenge(string Token, DateTimeOffset ExpiresAtUtc);

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
