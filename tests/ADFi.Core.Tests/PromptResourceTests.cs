using ADFi.Conversation;

namespace ADFi.Core.Tests;

public sealed class PromptResourceTests
{
    [Fact]
    public async Task EveryRequiredPromptHasVersionedMetadataAndContentHash()
    {
        var expected = new[]
        {
            "ADFI_VISUAL_ANALYST_SYSTEM",
            "ADFI_PLANNER_SYSTEM",
            "ADFI_TASK_EXECUTOR_SYSTEM",
            "ADFI_VQA_SYSTEM",
            "ADFI_MOMENT_VQA_SYSTEM",
            "ADFI_OCR_RECONCILER_SYSTEM",
            "ADFI_AD_WRITER_SYSTEM",
            "ADFI_EDIT_VALIDATOR_SYSTEM",
            "ADFI_RESPONSE_SYNTHESIZER_SYSTEM",
        };

        foreach (var id in expected)
        {
            var prompt = await PromptResourceLoader.LoadAsync(
                RepositoryPath("prompts", $"{id}.md"),
                CancellationToken.None);
            Assert.Equal(id, prompt.Id);
            Assert.NotEmpty(prompt.Content);
            Assert.Equal(64, prompt.Sha256.Length);
        }
    }

    [Fact]
    public async Task EverySemanticToolHasACompleteUniqueContract()
    {
        var document = await SemanticToolContractLoader.LoadAndValidateAsync(
            RepositoryPath("prompts", "semantic-tools.json"),
            CancellationToken.None);

        Assert.Equal(53, document.Tools.Count);
        Assert.Contains(document.Tools, tool => tool.Name == "ApplyProjectMutation" && tool.Mutates);
        Assert.Contains(document.Tools, tool => tool.Name == "RewriteAdSegment" && !tool.Mutates);
        Assert.Contains(document.Tools, tool => tool.Name == "RenderFinalVideo");
        Assert.Contains(document.Tools, tool => tool.Name == "AnswerMomentQuestion" && !tool.Mutates);
    }

    private static string RepositoryPath(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ADFi.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return Path.Combine([directory.FullName, .. parts]);
    }
}
