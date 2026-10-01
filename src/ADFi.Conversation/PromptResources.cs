using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ADFi.Conversation;

public sealed record PromptResource(
    string Id,
    Version Version,
    string OutputSchema,
    string Content,
    string Sha256);

public static class PromptResourceLoader
{
    public static async Task<PromptResource> LoadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        var normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        if (lines.Length < 6 || lines[0] != "---")
        {
            throw new InvalidDataException("Prompt resource is missing YAML-style metadata.");
        }

        var closingIndex = Array.IndexOf(lines, "---", 1);
        if (closingIndex < 2)
        {
            throw new InvalidDataException("Prompt resource metadata is not terminated.");
        }

        var metadata = lines[1..closingIndex]
            .Select(line => line.Split(':', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2)
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        var body = string.Join('\n', lines[(closingIndex + 1)..]).Trim();
        if (body.Length == 0)
        {
            throw new InvalidDataException("Prompt resource body is empty.");
        }

        return new(
            Required(metadata, "id"),
            Version.Parse(Required(metadata, "version")),
            Required(metadata, "output_schema"),
            body,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))));
    }

    private static string Required(Dictionary<string, string> metadata, string key) =>
        metadata.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new InvalidDataException($"Prompt metadata is missing {key}.");
}

public sealed record SemanticToolContractDocument(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("rules")] IReadOnlyDictionary<string, string> Rules,
    [property: JsonPropertyName("tools")] IReadOnlyList<SemanticToolContract> Tools);

public sealed record SemanticToolContract(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("when")] string When,
    [property: JsonPropertyName("mutates")] bool Mutates,
    [property: JsonPropertyName("input")] string Input,
    [property: JsonPropertyName("output")] string Output,
    [property: JsonPropertyName("validation")] string Validation,
    [property: JsonPropertyName("errors")] IReadOnlyList<string> Errors);

public static class SemanticToolContractLoader
{
    public static async Task<SemanticToolContractDocument> LoadAndValidateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var document = await JsonSerializer.DeserializeAsync<SemanticToolContractDocument>(
            stream,
            cancellationToken: cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("Semantic tool contract file is empty.");

        if (document.SchemaVersion != "1.0" || document.Tools.Count == 0)
        {
            throw new InvalidDataException("Unsupported or empty semantic tool contract document.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in document.Tools)
        {
            if (string.IsNullOrWhiteSpace(tool.Name)
                || string.IsNullOrWhiteSpace(tool.When)
                || string.IsNullOrWhiteSpace(tool.Input)
                || string.IsNullOrWhiteSpace(tool.Output)
                || string.IsNullOrWhiteSpace(tool.Validation)
                || tool.Errors.Count == 0
                || !names.Add(tool.Name))
            {
                throw new InvalidDataException($"Invalid or duplicate semantic tool contract: {tool.Name}");
            }
        }

        return document;
    }
}

public sealed record PromptDecisionProvenance(
    string PromptId,
    string PromptVersion,
    string PromptSha256,
    string ModelRuntime,
    string ModelName,
    DateTimeOffset CreatedAtUtc);
