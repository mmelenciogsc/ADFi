using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ADFi.Core;

namespace ADFi.Infrastructure.CloudAI;

public sealed record ChatGptPlanCredential(
    string AccessToken,
    IReadOnlySet<string> GrantedScopes,
    DateTimeOffset ExpiresAtUtc,
    string AccountDisplayName);

public interface IChatGptPlanCredentialSource
{
    Task<ChatGptPlanCredential?> GetSelectedAsync(CancellationToken cancellationToken);

    VqaProviderAvailabilityState ConnectionState { get; }
}

public sealed record ChatGptPlanModel(string Slug, string DisplayName);

public sealed class ChatGptPlanUsageException(
    string message,
    string? providerCode,
    VqaProviderAvailabilityState state,
    HttpStatusCode? statusCode = null,
    Exception? innerException = null) : Exception(message, innerException)
{
    public string? ProviderCode { get; } = providerCode;

    public VqaProviderAvailabilityState State { get; } = state;

    public HttpStatusCode? StatusCode { get; } = statusCode;
}

/// <summary>
/// Calls only the public OpenAI model-list and Responses endpoints with a user-authorized OAuth
/// token. Requests are non-stored and streamed; text is released only after response.completed.
/// </summary>
public sealed class ChatGptPlanResponsesClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task<IReadOnlyList<ChatGptPlanModel>> ListModelsAsync(
        string accessToken,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        using var request = CreateRequest(HttpMethod.Get, "v1/models", accessToken);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateFailureAsync(response, cancellationToken).ConfigureAwait(false);
        }

        var catalog = await response.Content.ReadFromJsonAsync<ModelCatalog>(JsonOptions, cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidDataException("ChatGPT returned an empty model catalog.");
        return catalog.Models
            .Where(model => string.Equals(model.Visibility, "list", StringComparison.Ordinal))
            .Select(model => new ChatGptPlanModel(model.Slug, model.DisplayName))
            .ToArray();
    }

    public async Task<string> CompleteAsync(
        string accessToken,
        string model,
        string instruction,
        IReadOnlyList<string> privateImagePaths,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentException.ThrowIfNullOrWhiteSpace(instruction);
        var content = new List<object> { new { type = "input_text", text = instruction } };
        foreach (var path in privateImagePaths)
        {
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            content.Add(new
            {
                type = "input_image",
                image_url = "data:image/png;base64," + Convert.ToBase64String(bytes),
            });
        }

        var payload = new
        {
            model,
            input = new[] { new { role = "user", content } },
            store = false,
            stream = true,
        };
        using var request = CreateRequest(HttpMethod.Post, "v1/responses", accessToken);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw await CreateFailureAsync(response, cancellationToken).ConfigureAwait(false);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var output = new StringBuilder();
        var completed = false;
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            if (!line.StartsWith("data:", StringComparison.Ordinal))
            {
                continue;
            }

            var data = line[5..].TrimStart();
            if (data.Length == 0 || data == "[DONE]")
            {
                continue;
            }

            using var document = JsonDocument.Parse(data);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeNode) ? typeNode.GetString() : null;
            switch (type)
            {
                case "response.output_text.delta":
                    if (root.TryGetProperty("delta", out var delta))
                    {
                        output.Append(delta.GetString());
                    }

                    break;
                case "response.completed":
                    completed = true;
                    break;
                case "response.failed":
                case "error":
                    throw CreateStreamFailure(root);
                case "response.incomplete":
                    throw new ChatGptPlanUsageException(
                        "ChatGPT stopped before completing the answer.",
                        "response_incomplete",
                        VqaProviderAvailabilityState.Unavailable);
            }
        }

        if (!completed)
        {
            throw new ChatGptPlanUsageException(
                "The ChatGPT response stream ended before completion.",
                "stream_ended_without_completion",
                VqaProviderAvailabilityState.Unavailable);
        }

        var result = output.ToString().Trim();
        return result.Length > 0
            ? result
            : throw new ChatGptPlanUsageException(
                "ChatGPT completed without an answer.",
                "empty_completed_response",
                VqaProviderAvailabilityState.Unavailable);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string accessToken)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return request;
    }

    private static async Task<ChatGptPlanUsageException> CreateFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var code = ReadErrorCode(body);
        return new(
            AccessibleError(response.StatusCode, code),
            code,
            StateFor(response.StatusCode, code),
            response.StatusCode);
    }

    private static ChatGptPlanUsageException CreateStreamFailure(JsonElement root)
    {
        var error = root.TryGetProperty("response", out var response)
                    && response.TryGetProperty("error", out var responseError)
            ? responseError
            : root.TryGetProperty("error", out var directError)
                ? directError
                : default;
        var code = error.ValueKind == JsonValueKind.Object
                   && error.TryGetProperty("code", out var codeNode)
            ? codeNode.GetString()
            : null;
        return new(
            AccessibleError(null, code),
            code,
            StateFor(null, code));
    }

    private static string? ReadErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            return root.TryGetProperty("error", out var error)
                   && error.TryGetProperty("code", out var code)
                ? code.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static VqaProviderAvailabilityState StateFor(HttpStatusCode? status, string? code) => code switch
    {
        "subscription_sharing_usage_limit_exceeded" => VqaProviderAvailabilityState.LimitReached,
        "subscription_sharing_invalid_user" or "chatpass_v2_invalid_authorization_context" =>
            VqaProviderAvailabilityState.ReauthorizationRequired,
        "subscription_sharing_user_not_eligible" => VqaProviderAvailabilityState.Unavailable,
        _ when status == HttpStatusCode.Unauthorized => VqaProviderAvailabilityState.ReauthorizationRequired,
        _ => VqaProviderAvailabilityState.Unavailable,
    };

    private static string AccessibleError(HttpStatusCode? status, string? code) => code switch
    {
        "subscription_sharing_usage_limit_exceeded" =>
            "ChatGPT plan usage limit reached. Open Manage usage to review the applicable limit.",
        "subscription_sharing_usage_unavailable" =>
            "ChatGPT plan usage availability cannot be checked right now. Try again later.",
        "subscription_sharing_user_not_eligible" =>
            "ChatGPT plan usage is unavailable for this account or workspace.",
        "subscription_sharing_unsupported_capability" =>
            "This ChatGPT plan connection does not support the requested visual capability.",
        "subscription_sharing_invalid_user" or "chatpass_v2_invalid_authorization_context" =>
            "The ChatGPT connection must be authorized again.",
        _ when status == HttpStatusCode.Unauthorized => "The ChatGPT connection must be authorized again.",
        _ when status == HttpStatusCode.Forbidden => "ChatGPT did not permit this request.",
        _ when status == HttpStatusCode.ServiceUnavailable => "ChatGPT plan usage is temporarily unavailable.",
        _ => "ChatGPT could not complete the request.",
    };

    private sealed record ModelCatalog([property: JsonPropertyName("models")] IReadOnlyList<ModelItem> Models);

    private sealed record ModelItem(
        [property: JsonPropertyName("slug")] string Slug,
        [property: JsonPropertyName("display_name")] string DisplayName,
        [property: JsonPropertyName("visibility")] string Visibility);
}

public sealed class ChatGptMomentVqaProvider(
    IChatGptPlanCredentialSource credentialSource,
    ChatGptPlanResponsesClient responsesClient,
    TimeProvider timeProvider) : IVqaProvider
{
    private static readonly Uri ManageUsageUri = new("https://chatgpt.com/settings/usage");
    private VqaProviderAvailabilityState? lastFailureState;

    public VqaProviderKind Kind => VqaProviderKind.ChatGpt;

    public async Task<VqaProviderAvailability> GetAvailabilityAsync(CancellationToken cancellationToken)
    {
        if (lastFailureState == VqaProviderAvailabilityState.LimitReached)
        {
            return new(
                Kind,
                lastFailureState.Value,
                false,
                "ChatGPT plan usage limit reached. Use Manage usage or choose another configured provider.",
                ManageUsageUri);
        }

        var credential = await credentialSource.GetSelectedAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null)
        {
            return new(
                Kind,
                credentialSource.ConnectionState,
                false,
                StatusFor(credentialSource.ConnectionState),
                null);
        }

        if (!credential.GrantedScopes.Contains(ChatGptAuthorization.PlanUsageScope))
        {
            return new(
                Kind,
                VqaProviderAvailabilityState.ConnectedWithoutPlanUsage,
                false,
                "ChatGPT is connected, but plan usage was not granted.",
                null);
        }

        if (credential.ExpiresAtUtc <= timeProvider.GetUtcNow())
        {
            return new(
                Kind,
                VqaProviderAvailabilityState.ReauthorizationRequired,
                false,
                "The ChatGPT connection must be authorized again.",
                null);
        }

        return new(
            Kind,
            VqaProviderAvailabilityState.PlanUsageAvailable,
            true,
            $"Connected as {credential.AccountDisplayName}. ChatGPT plan usage available.",
            ManageUsageUri);
    }

    public async Task<MomentVqaResult> AskAsync(
        MomentVqaRequest request,
        CancellationToken cancellationToken)
    {
        MomentVqaRequestValidator.Validate(request);
        if (RequiresVisualEvidence(request.QuestionIntent)
            && !request.Evidence.Any(item => item.Kind is MomentEvidenceKind.EphemeralImage
                                             or MomentEvidenceKind.PersistedSceneAnalysis
                                             or MomentEvidenceKind.PersistedOcr
                                             or MomentEvidenceKind.PersistedEntity
                                             or MomentEvidenceKind.PersistedAudioDescription))
        {
            return new(
                "I can’t answer that visual question because direct visual evidence is unavailable for this moment.",
                [MomentEvidenceBasis.NoDirectVisualEvidence],
                0m,
                ["No approved visual evidence was available."],
                "You can ask about officially available video metadata or try a local file.",
                Kind,
                timeProvider.GetUtcNow());
        }

        var credential = await credentialSource.GetSelectedAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException(StatusFor(credentialSource.ConnectionState));
        if (!credential.GrantedScopes.Contains(ChatGptAuthorization.PlanUsageScope))
        {
            throw new InvalidOperationException("ChatGPT is connected, but plan usage was not granted.");
        }

        try
        {
            var models = await responsesClient.ListModelsAsync(credential.AccessToken, cancellationToken)
                .ConfigureAwait(false);
            var model = models.Count > 0
                ? models[0]
                : throw new InvalidOperationException("No ChatGPT plan-usage model is available for this connection.");
            var answer = await responsesClient.CompleteAsync(
                credential.AccessToken,
                model.Slug,
                BuildInstruction(request),
                request.Evidence
                    .Where(item => item.Kind == MomentEvidenceKind.EphemeralImage)
                    .Select(item => item.PrivateEphemeralPath)
                    .OfType<string>()
                    .ToArray(),
                cancellationToken).ConfigureAwait(false);
            lastFailureState = null;
            return new(
                answer,
                request.Evidence.Select(item => item.Basis).DefaultIfEmpty(MomentEvidenceBasis.OfficialVideoMetadata).Distinct().ToArray(),
                request.Evidence.Count == 0 ? 0.4m : request.Evidence.Min(item => item.Confidence),
                [],
                null,
                Kind,
                timeProvider.GetUtcNow());
        }
        catch (ChatGptPlanUsageException exception)
        {
            lastFailureState = exception.State;
            throw;
        }
    }

    private static string BuildInstruction(MomentVqaRequest request)
    {
        var context = request.Evidence
            .Where(item => !string.IsNullOrWhiteSpace(item.Text))
            .Select(item => new { item.EvidenceId, item.Kind, item.Text, item.Confidence });
        return JsonSerializer.Serialize(new
        {
            instruction = "Answer one moment-specific accessibility question concisely. Use only supplied evidence. Never identify a person from appearance or outside knowledge. State uncertainty; do not guess. Return only the answer text.",
            timestampMs = request.CurrentTimestampMs,
            question = request.Question,
            intent = request.QuestionIntent,
            evidence = context,
            establishedEntities = request.EstablishedEntities,
            ocr = request.OcrContext,
            dialogue = request.DialogueContext,
            captureCompliance = request.CaptureComplianceState,
        });
    }

    private static bool RequiresVisualEvidence(MomentQuestionIntent intent) =>
        intent is not MomentQuestionIntent.Speaker;

    private static string StatusFor(VqaProviderAvailabilityState state) => state switch
    {
        VqaProviderAvailabilityState.NotConnected =>
            "ChatGPT is not connected. Use Continue with ChatGPT to connect an eligible account.",
        VqaProviderAvailabilityState.ConnectedWithoutPlanUsage =>
            "ChatGPT is connected, but plan usage was not granted.",
        VqaProviderAvailabilityState.ProviderApprovalRequired =>
            "ChatGPT plan usage is not available until OpenAI approves this paid application integration.",
        VqaProviderAvailabilityState.LimitReached =>
            "ChatGPT plan usage limit reached. Use Manage usage or choose another configured provider.",
        VqaProviderAvailabilityState.ReauthorizationRequired =>
            "The ChatGPT connection must be authorized again.",
        _ => "ChatGPT plan usage is unavailable.",
    };
}
