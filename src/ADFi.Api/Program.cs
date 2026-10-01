using ADFi.Core;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddRateLimiter(options =>
{
    options.AddFixedWindowLimiter("public", limiter =>
    {
        limiter.PermitLimit = 60;
        limiter.Window = TimeSpan.FromMinutes(1);
        limiter.QueueLimit = 0;
    });
});

var app = builder.Build();
app.UseExceptionHandler();
app.UseHttpsRedirection();
app.UseRateLimiter();

app.MapHealthChecks("/health/live");
app.MapHealthChecks("/health/ready");
app.MapGet("/api/v1/plans", () => Results.Ok(PlanCatalog.PaidPlans))
    .RequireRateLimiting("public")
    .AllowAnonymous();
app.MapGet("/api/v1/discover", () => Results.Ok(Array.Empty<object>()))
    .RequireRateLimiting("public")
    .AllowAnonymous();

app.Run();

public partial class Program;
