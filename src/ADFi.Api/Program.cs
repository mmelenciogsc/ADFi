using ADFi.Api;
using ADFi.Core;
using ADFi.Infrastructure.Notifications;
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

if (persistenceConfigured)
{
    builder.Services.AddSingleton(new AdfiDatabase(connectionString!));
    builder.Services.AddSingleton<AccountRepository>();
}

if (smtpOptions is not null)
{
    builder.Services.AddSingleton(smtpOptions);
    builder.Services.AddSingleton<IAccountEmailSender, SmtpAccountEmailSender>();
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

public partial class Program;
