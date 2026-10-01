using System.ComponentModel.DataAnnotations;
using System.Net.Mail;
using ADFi.Infrastructure.Notifications;
using ADFi.Infrastructure.Postgres;
using ADFi.Infrastructure.Security;
using Npgsql;

namespace ADFi.Api;

public static class AccountEndpoints
{
    public static RouteGroupBuilder MapAccountEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/account")
            .RequireRateLimiting("authentication");
        group.MapPost("/register", RegisterAsync).AllowAnonymous();
        group.MapGet("/verify-email", VerifyEmailAsync).AllowAnonymous();
        group.MapPost("/login", LoginAsync).AllowAnonymous();
        group.MapPost("/logout", LogoutAsync).AllowAnonymous();
        group.MapPost("/session/rotate", RotateSessionAsync).AllowAnonymous();
        group.MapPost("/password-reset/request", RequestPasswordResetAsync).AllowAnonymous();
        group.MapPost("/password-reset/complete", CompletePasswordResetAsync).AllowAnonymous();
        return group;
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        AccountRepository accounts,
        IAccountEmailSender emailSender,
        CancellationToken cancellationToken)
    {
        var errors = ValidateRegistration(request);
        if (errors.Count > 0)
        {
            return Results.ValidationProblem(errors);
        }

        var userId = Guid.NewGuid();
        try
        {
            await accounts.CreateAsync(
                new(userId, request.Email, request.DisplayName, PasswordCredentialHasher.Hash(request.Password)),
                cancellationToken).ConfigureAwait(false);
            var challenge = await accounts.CreateEmailVerificationChallengeAsync(
                userId,
                TimeSpan.FromHours(1),
                cancellationToken).ConfigureAwait(false);
            await emailSender.SendVerificationAsync(
                request.Email,
                request.DisplayName,
                challenge.Token,
                cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return Results.Conflict(new { message = "An account already uses that email address." });
        }

        return Results.Accepted(value: new
        {
            userId,
            message = "Registration received. Verify the email address before signing in.",
        });
    }

    private static async Task<IResult> VerifyEmailAsync(
        string token,
        AccountRepository accounts,
        CancellationToken cancellationToken)
    {
        var verified = await accounts.VerifyEmailAsync(token, cancellationToken).ConfigureAwait(false);
        return verified
            ? Results.Ok(new { message = "Email verified." })
            : Results.BadRequest(new { message = "The verification link is invalid or expired." });
    }

    private static async Task<IResult> LoginAsync(
        LoginRequest request,
        AccountRepository accounts,
        CancellationToken cancellationToken)
    {
        var found = await accounts.FindByEmailAsync(request.Email, cancellationToken).ConfigureAwait(false);
        if (found is null || !PasswordCredentialHasher.Verify(request.Password, found.Value.PasswordHash))
        {
            return Results.Unauthorized();
        }

        if (!found.Value.Account.EmailVerified)
        {
            return Results.Json(
                new { message = "Verify the account email before signing in." },
                statusCode: StatusCodes.Status403Forbidden);
        }

        var session = await accounts.CreateSessionAsync(
            found.Value.Account.UserId,
            request.DeviceLabel,
            TimeSpan.FromDays(30),
            cancellationToken).ConfigureAwait(false);
        return Results.Ok(new
        {
            session.Token,
            session.ExpiresAtUtc,
            account = found.Value.Account,
        });
    }

    private static async Task<IResult> LogoutAsync(
        HttpRequest request,
        AccountRepository accounts,
        CancellationToken cancellationToken)
    {
        if (!TryReadBearer(request, out var token))
        {
            return Results.Unauthorized();
        }

        await accounts.RevokeSessionAsync(token, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> RotateSessionAsync(
        HttpRequest request,
        AccountRepository accounts,
        CancellationToken cancellationToken)
    {
        if (!TryReadBearer(request, out var token))
        {
            return Results.Unauthorized();
        }

        var rotated = await accounts.RotateSessionAsync(token, TimeSpan.FromDays(30), cancellationToken)
            .ConfigureAwait(false);
        return rotated is null ? Results.Unauthorized() : Results.Ok(rotated);
    }

    private static async Task<IResult> RequestPasswordResetAsync(
        PasswordResetRequest request,
        AccountRepository accounts,
        IAccountEmailSender emailSender,
        CancellationToken cancellationToken)
    {
        if (new EmailAddressAttribute().IsValid(request.Email))
        {
            var challenge = await accounts.CreatePasswordResetChallengeAsync(
                request.Email,
                TimeSpan.FromMinutes(30),
                cancellationToken).ConfigureAwait(false);
            if (challenge is not null)
            {
                await emailSender.SendPasswordResetAsync(
                    challenge.Email,
                    challenge.DisplayName,
                    challenge.Token,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        return Results.Accepted(value: new
        {
            message = "If the address belongs to a verified account, a password-reset link has been sent.",
        });
    }

    private static async Task<IResult> CompletePasswordResetAsync(
        CompletePasswordResetRequest request,
        AccountRepository accounts,
        CancellationToken cancellationToken)
    {
        try
        {
            PasswordCredentialHasher.ValidatePassword(request.NewPassword);
        }
        catch (ArgumentException exception)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(request.NewPassword)] = [exception.Message],
            });
        }

        var reset = await accounts.ResetPasswordAsync(
            request.Token,
            PasswordCredentialHasher.Hash(request.NewPassword),
            cancellationToken).ConfigureAwait(false);
        return reset
            ? Results.Ok(new { message = "Password reset. Sign in again on every device." })
            : Results.BadRequest(new { message = "The password-reset link is invalid or expired." });
    }

    internal static Dictionary<string, string[]> ValidateRegistration(RegisterRequest request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        try
        {
            _ = new MailAddress(request.Email);
        }
        catch (FormatException)
        {
            errors[nameof(request.Email)] = ["Enter a valid email address."];
        }

        if (string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName.Trim().Length is < 2 or > 80)
        {
            errors[nameof(request.DisplayName)] = ["Display name must be between 2 and 80 characters."];
        }

        try
        {
            PasswordCredentialHasher.ValidatePassword(request.Password);
        }
        catch (ArgumentException exception)
        {
            errors[nameof(request.Password)] = [exception.Message];
        }

        return errors;
    }

    private static bool TryReadBearer(HttpRequest request, out string token)
    {
        const string prefix = "Bearer ";
        var authorization = request.Headers.Authorization.ToString();
        if (authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            token = authorization[prefix.Length..].Trim();
            return token.Length >= 32;
        }

        token = string.Empty;
        return false;
    }
}

public sealed record RegisterRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string DisplayName,
    [property: Required] string Password);

public sealed record LoginRequest(
    [property: Required, EmailAddress] string Email,
    [property: Required] string Password,
    [property: Required] string DeviceLabel);

public sealed record PasswordResetRequest([property: Required, EmailAddress] string Email);

public sealed record CompletePasswordResetRequest(
    [property: Required] string Token,
    [property: Required] string NewPassword);
