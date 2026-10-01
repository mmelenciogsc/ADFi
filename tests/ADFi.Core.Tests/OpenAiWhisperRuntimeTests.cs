using System.Text;
using ADFi.Core;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class OpenAiWhisperRuntimeTests
{
    [Fact]
    public async Task PersistentWorkerMapsChunkRelativeTimesToAbsoluteSourceTimes()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-whisper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        var worker = Path.Combine(root, "worker.py");
        var audio = Path.Combine(root, "chunk.wav");
        await File.WriteAllBytesAsync(audio, [1], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            worker,
            """
            import sys,json
            for line in sys.stdin:
              c=json.loads(line)
              if c['type']=='transcribe':
                print(json.dumps({'ok':True,'segments':[{'text':'Hello','startSeconds':1.0,'endSeconds':2.0,'confidence':0.9,'words':[{'text':'Hello','startSeconds':1.0,'endSeconds':1.8,'confidence':0.95}]}]}),flush=True)
              else: print(json.dumps({'ok':True}),flush=True)
              if c['type']=='exit': break
            """,
            Encoding.UTF8,
            TestContext.Current.CancellationToken);
        try
        {
            await using var runtime = new OpenAiWhisperRuntime(new(
                "python", worker, Path.Combine(root, "models")));
            await runtime.LoadAsync("tiny", TestContext.Current.CancellationToken);
            var result = await runtime.TranscribeAsync(
                new("chunk-1", new(TimeSpan.FromSeconds(29), TimeSpan.FromSeconds(40)), "audio.wav", 16_000, 1),
                audio,
                TestContext.Current.CancellationToken);
            await runtime.UnloadAsync(TestContext.Current.CancellationToken);

            var segment = Assert.Single(result);
            Assert.Equal(TimeSpan.FromSeconds(30), segment.SourceInterval.Start);
            Assert.Equal(TimeSpan.FromSeconds(31), segment.SourceInterval.End);
            Assert.Equal(TimeSpan.FromSeconds(30), segment.Words[0].Start);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(4, "tiny")]
    [InlineData(8, "base")]
    [InlineData(16, "small")]
    public void WhisperModelSelectionUsesTotalMemory(int gibibytes, string expected)
    {
        Assert.Equal(expected, WhisperModelPolicy.Select(gibibytes * 1024L * 1024 * 1024));
    }
}
