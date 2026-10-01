using System.Net;
using System.Net.Mail;

namespace ADFi.Infrastructure.Notifications;

public sealed record SmtpOptions(
    string Host,
    int Port,
    bool EnableSsl,
    string FromAddress,
    string? UserName,
    string? Password,
    Uri PublicBaseUri);

public interface IAccountEmailSender
{
    Task SendVerificationAsync(
        string recipient,
        string displayName,
        string rawVerificationToken,
        CancellationToken cancellationToken);

    Task SendPasswordResetAsync(
        string recipient,
        string displayName,
        string rawResetToken,
        CancellationToken cancellationToken);
}

public sealed class SmtpAccountEmailSender(SmtpOptions options) : IAccountEmailSender
{
    public async Task SendVerificationAsync(
        string recipient,
        string displayName,
        string rawVerificationToken,
        CancellationToken cancellationToken)
    {
        var verificationUri = new UriBuilder(new Uri(options.PublicBaseUri, "/verify-email"))
        {
            Query = $"token={Uri.EscapeDataString(rawVerificationToken)}",
        }.Uri;
        using var message = new MailMessage(
            options.FromAddress,
            recipient,
            "Verify your ADFi email",
            $"Hello {displayName},\r\n\r\nVerify your ADFi email by opening this link:\r\n{verificationUri}\r\n\r\nIf you did not create this account, ignore this message.");
        using var client = CreateClient();
        cancellationToken.ThrowIfCancellationRequested();
        await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendPasswordResetAsync(
        string recipient,
        string displayName,
        string rawResetToken,
        CancellationToken cancellationToken)
    {
        var resetUri = new UriBuilder(new Uri(options.PublicBaseUri, "/reset-password"))
        {
            Query = $"token={Uri.EscapeDataString(rawResetToken)}",
        }.Uri;
        using var message = new MailMessage(
            options.FromAddress,
            recipient,
            "Reset your ADFi password",
            $"Hello {displayName},\r\n\r\nReset your ADFi password by opening this link:\r\n{resetUri}\r\n\r\nThis short-lived link can be used once. If you did not request it, ignore this message.");
        using var client = CreateClient();
        cancellationToken.ThrowIfCancellationRequested();
        await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);
    }

    private SmtpClient CreateClient()
    {
        var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.EnableSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (!string.IsNullOrWhiteSpace(options.UserName))
        {
            client.Credentials = new NetworkCredential(options.UserName, options.Password);
        }

        return client;
    }
}
