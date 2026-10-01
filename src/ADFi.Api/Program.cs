using ADFi.Api;
using ADFi.Core;
using ADFi.Infrastructure.Notifications;
using ADFi.Infrastructure.Payments;
using ADFi.Infrastructure.Postgres;
using ADFi.Web;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
var connectionString = builder.Configuration.GetConnectionString("ADFi");
var smtpOptions = ReadSmtpOptions(builder.Configuration);
var persistenceConfigured = !string.IsNullOrWhiteSpace(connectionString);
var registrationConfigured = persistenceConfigured && smtpOptions is not null;
var payPalOptions = ReadPayPalOptions(builder.Configuration);
var paymentsConfigured = persistenceConfigured && payPalOptions is not null;

builder.Services.AddSingleton(TimeProvider.System);

if (persistenceConfigured)
{
    builder.Services.AddSingleton(new AdfiDatabase(connectionString!));
    builder.Services.AddSingleton<AccountRepository>();
    builder.Services.AddSingleton<SubscriptionRepository>();
}

if (smtpOptions is not null)
{
    builder.Services.AddSingleton(smtpOptions);
    builder.Services.AddSingleton<IAccountEmailSender, SmtpAccountEmailSender>();
}

if (paymentsConfigured)
{
    builder.Services.AddSingleton(payPalOptions!);
    builder.Services.AddHttpClient("paypal");
    builder.Services.AddTransient<IPayPalProvider>(services => new PayPalClient(
        services.GetRequiredService<IHttpClientFactory>().CreateClient("paypal"),
        services.GetRequiredService<PayPalConfiguration>(),
        services.GetRequiredService<TimeProvider>()));
    builder.Services.AddSingleton<IPayPalWebhookEventStore, PostgresPayPalWebhookEventStore>();
    builder.Services.AddTransient<PayPalWebhookProcessor>();
}

var healthChecks = builder.Services.AddHealthChecks();
if (persistenceConfigured)
{
    healthChecks.AddCheck<PostgresHealthCheck>("postgres", tags: ["ready"]);
}
else
{
    healthChecks.AddCheck(
        "postgres-configuration",
        () => HealthCheckResult.Unhealthy("ConnectionStrings:ADFi is not configured."),
        tags: ["ready"]);
}
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("public", limiter =>
    {
        limiter.PermitLimit = 60;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
    options.AddFixedWindowLimiter("authentication", limiter =>
    {
        limiter.PermitLimit = 10;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});

var app = builder.Build();

if (args.Contains("--migrate-only", StringComparer.Ordinal))
{
    if (!persistenceConfigured)
    {
        throw new InvalidOperationException("ConnectionStrings:ADFi is required for migration.");
    }

    await app.Services.GetRequiredService<AdfiDatabase>()
        .ApplyMigrationsAsync(CancellationToken.None)
        .ConfigureAwait(false);
    return;
}

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseRateLimiter();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapGet("/api/v1/plans", () => Results.Ok(PlanCatalog.PaidPlans))
    .RequireRateLimiting("public")
    .AllowAnonymous();
app.MapGet("/api/v1/discover", () => Results.Ok(Array.Empty<object>()))
    .RequireRateLimiting("public")
    .AllowAnonymous();
app.MapGet("/discover", () => Results.Content(
        DiscoverHtmlRenderer.RenderIndex([]),
        "text/html; charset=utf-8"))
    .RequireRateLimiting("public")
    .AllowAnonymous();

if (registrationConfigured)
{
    app.MapAccountEndpoints();
}
else
{
    app.MapMethods(
            "/api/v1/account/{**path}",
            ["GET", "POST"],
            () => Results.Problem(
                "Account services are unavailable until PostgreSQL and SMTP are configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable))
        .RequireRateLimiting("authentication")
        .AllowAnonymous();
}

if (paymentsConfigured && Uri.TryCreate(builder.Configuration["PublicBaseUrl"], UriKind.Absolute, out var paymentPublicBaseUri))
{
    app.MapPaymentEndpoints(paymentPublicBaseUri);
}
else
{
    app.MapMethods(
            "/api/v1/subscriptions/{**path}",
            ["GET", "POST"],
            () => Results.Problem(
                "Subscription services are unavailable until PostgreSQL and PayPal are configured.",
                statusCode: StatusCodes.Status503ServiceUnavailable))
        .RequireRateLimiting("authentication")
        .AllowAnonymous();
    app.MapPost(
            "/api/v1/paypal/webhook",
            () => Results.Problem(
                "PayPal webhook processing is unavailable until server configuration is complete.",
                statusCode: StatusCodes.Status503ServiceUnavailable))
        .RequireRateLimiting("authentication")
        .AllowAnonymous();
}

if (persistenceConfigured && builder.Configuration.GetValue<bool>("Database:ApplyMigrationsAtStartup"))
{
    await app.Services.GetRequiredService<AdfiDatabase>()
        .ApplyMigrationsAsync(CancellationToken.None)
        .ConfigureAwait(false);
}

app.Run();

static SmtpOptions? ReadSmtpOptions(IConfiguration configuration)
{
    var host = configuration["Smtp:Host"];
    var fromAddress = configuration["Smtp:FromAddress"];
    var publicBaseUrl = configuration["PublicBaseUrl"];
    return string.IsNullOrWhiteSpace(host)
           || string.IsNullOrWhiteSpace(fromAddress)
           || !Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var publicBaseUri)
        ? null
        : new(
            host,
            configuration.GetValue("Smtp:Port", 587),
            configuration.GetValue("Smtp:EnableSsl", true),
            fromAddress,
            configuration["Smtp:UserName"],
            configuration["Smtp:Password"],
            publicBaseUri);
}

static PayPalConfiguration? ReadPayPalOptions(IConfiguration configuration)
{
    var environment = configuration["PayPal:Environment"];
    var clientId = configuration["PayPal:ClientId"];
    var clientSecret = configuration["PayPal:ClientSecret"];
    var webhookId = configuration["PayPal:WebhookId"];
    var communityPlan = configuration["PayPal:CommunityPlanId"];
    var creatorPlan = configuration["PayPal:CreatorPlanId"];
    if (new[] { environment, clientId, clientSecret, webhookId, communityPlan, creatorPlan }
        .Any(string.IsNullOrWhiteSpace))
    {
        return null;
    }

    var apiBaseUri = string.Equals(environment, "Production", StringComparison.OrdinalIgnoreCase)
        ? new Uri("https://api-m.paypal.com/")
        : string.Equals(environment, "Sandbox", StringComparison.OrdinalIgnoreCase)
            ? new Uri("https://api-m.sandbox.paypal.com/")
            : throw new InvalidOperationException("PayPal:Environment must be Sandbox or Production.");
    return new(apiBaseUri, clientId!, clientSecret!, webhookId!, communityPlan!, creatorPlan!);
}

public partial class Program;
