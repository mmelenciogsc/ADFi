using ADFi.Infrastructure.Postgres;

namespace ADFi.Api;

public sealed record CreateViewerFeedbackRequest(long TimestampMs, string Body);

public static class ViewerFeedbackEndpoints
{
    public static IEndpointRouteBuilder MapViewerFeedbackEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost(
                "/api/v1/discover/{projectId:guid}/feedback",
                CreateAsync)
            .RequireRateLimiting("authentication")
            .AllowAnonymous();
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        Guid projectId,
        CreateViewerFeedbackRequest feedback,
        HttpRequest request,
        AccountRepository accounts,
        DiscoverFeedbackRepository repository,
        CancellationToken cancellationToken)
    {
        var token = ReadBearer(request);
        var account = token is null
            ? null
            : await accounts.ValidateSessionAsync(token, cancellationToken).ConfigureAwait(false);
        if (account is null)
        {
            return Results.Unauthorized();
        }

        var body = feedback.Body?.Trim() ?? string.Empty;
        if (feedback.TimestampMs < 0 || body.Length is < 20 or > 2000)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(feedback.Body)] = ["Feedback must contain 20 through 2,000 characters and use a nonnegative timestamp."],
            });
        }

        var receipt = await repository.CreatePendingAsync(
            projectId,
            account.UserId,
            feedback.TimestampMs,
            body,
            cancellationToken).ConfigureAwait(false);
        return receipt is null ? Results.NotFound() : Results.Accepted(value: receipt);
    }

    private static string? ReadBearer(HttpRequest request)
    {
        const string prefix = "Bearer ";
        var value = request.Headers.Authorization.ToString();
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = value[prefix.Length..].Trim();
        return token.Length >= 32 ? token : null;
    }
}
