using ADFi.Api;
using ADFi.Core;
using ADFi.Infrastructure.Notifications;
using ADFi.Infrastructure.Payments;
using ADFi.Infrastructure.Postgres;
using ADFi.Web;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using System.Net;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 2 * 1024 * 1024);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Add(IPAddress.Loopback);
    options.KnownProxies.Add(IPAddress.IPv6Loopback);
    options.ForwardLimit = 1;
});
var connectionString = builder.Configuration.GetConnectionString("ADFi");
var smtpOptions = ReadSmtpOptions(builder.Configuration);
var persistenceConfigured = !string.IsNullOrWhiteSpace(connectionString);
var registrationConfigured = persistenceConfigured && smtpOptions is not null;
var payPalOptions = ReadPayPalOptions(builder.Configuration);
var paymentsConfigured = persistenceConfigured && payPalOptions is not null;
var publicBaseConfigured = Uri.TryCreate(
    builder.Configuration["PublicBaseUrl"], UriKind.Absolute, out var configuredPublicBaseUri);

builder.Services.AddSingleton(TimeProvider.System);

if (persistenceConfigured)
{
    builder.Services.AddSingleton(new AdfiDatabase(connectionString!));
    builder.Services.AddSingleton<AccountRepository>();
    builder.Services.AddSingleton<SubscriptionRepository>();
    if (publicBaseConfigured)
    {
        builder.Services.AddSingleton(services => new DiscoverRepository(
            services.GetRequiredService<AdfiDatabase>(),
            configuredPublicBaseUri!));
    }
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
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("public", context => RateLimitPartition.GetFixedWindowLimiter(
        ClientPartition(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 60,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
    options.AddPolicy("authentication", context => RateLimitPartition.GetFixedWindowLimiter(
        ClientPartition(context),
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        }));
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

app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseExceptionHandler();
app.UseHttpsRedirection();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    context.Response.Headers["Content-Security-Policy"] =
        "default-src 'self'; frame-src https://www.youtube-nocookie.com; style-src 'self' 'unsafe-inline'; img-src 'self' data:; object-src 'none'; base-uri 'self'; frame-ancestors 'self'";
    await next().ConfigureAwait(false);
});
app.UseRateLimiter();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapGet("/api/v1/plans", () => Results.Ok(PlanCatalog.PaidPlans))
    .RequireRateLimiting("public")
    .AllowAnonymous();
if (persistenceConfigured && publicBaseConfigured)
{
    app.MapGet("/api/v1/discover", async (
            DiscoverRepository repository,
            CancellationToken cancellationToken) =>
        Results.Ok(await repository.ListAsync(50, cancellationToken).ConfigureAwait(false)))
        .RequireRateLimiting("public")
        .AllowAnonymous();
    app.MapGet("/discover", async (
            DiscoverRepository repository,
            CancellationToken cancellationToken) =>
        Results.Content(
            DiscoverHtmlRenderer.RenderIndex(
                await repository.ListAsync(50, cancellationToken).ConfigureAwait(false)),
            "text/html; charset=utf-8"))
        .RequireRateLimiting("public")
        .AllowAnonymous();
}
else
{
    app.MapGet("/api/v1/discover", () => Results.Problem(
            "DISCOVER is unavailable until PostgreSQL and PublicBaseUrl are configured.",
            statusCode: StatusCodes.Status503ServiceUnavailable))
        .RequireRateLimiting("public")
        .AllowAnonymous();
    app.MapGet("/discover", () => Results.Content(
            DiscoverHtmlRenderer.RenderUnavailable(),
            "text/html; charset=utf-8",
            statusCode: StatusCodes.Status503ServiceUnavailable))
        .RequireRateLimiting("public")
        .AllowAnonymous();
}

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

static string ClientPartition(HttpContext context) =>
    context.Connection.RemoteIpAddress?.ToString() ?? "unknown-client";

public partial class Program;
