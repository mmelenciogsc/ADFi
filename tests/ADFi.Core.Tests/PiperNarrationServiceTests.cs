using System.Text;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class PiperNarrationServiceTests
{
    [Fact]
    public async Task SynthesizesMeasuresAndReusesUnchangedSegment()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-piper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new WaveWritingRunner(TimeSpan.FromSeconds(1.5));
            var service = new PiperNarrationService(
                new PiperConfiguration("python", root),
                Path.Combine(root, "cache"),
                runner);
            var request = new NarrationSynthesisRequest(
                "segment-1", "He opens the red door.", "voice-1",
                TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(300), 1);

            var first = await service.SynthesizeAsync(request, TestContext.Current.CancellationToken);
            var second = await service.SynthesizeAsync(request, TestContext.Current.CancellationToken);

            Assert.True(first.Fit.Fits);
            Assert.Equal(TimeSpan.FromSeconds(1.5), first.MeasuredDuration);
            Assert.False(first.ReusedCachedAudio);
            Assert.True(second.ReusedCachedAudio);
            Assert.Equal(1, runner.InvocationCount);
            Assert.Contains("--data-dir", runner.LastArguments);
            Assert.Contains("en_GB-cori-high", runner.LastArguments);
            Assert.Contains("--input-file", runner.LastArguments);
            Assert.Equal(request.Text, runner.LastInputText);
            Assert.DoesNotContain(request.Text, runner.LastArguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ThirdOverrunIsFlaggedForUserReviewWithoutTimeCompression()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-piper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var service = new PiperNarrationService(
                new PiperConfiguration("python", root),
                Path.Combine(root, "cache"),
                new WaveWritingRunner(TimeSpan.FromSeconds(4)));
            var result = await service.SynthesizeAsync(
                new NarrationSynthesisRequest(
                    "segment-1", "A deliberately long description.", "voice-2",
                    TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(250), 3),
                TestContext.Current.CancellationToken);

            Assert.False(result.Fit.Fits);
            Assert.True(result.Fit.RequiresUserReview);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptCachedAudioIsRegeneratedInsteadOfTrusted()
    {
        var root = Path.Combine(Path.GetTempPath(), $"adfi-piper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var runner = new WaveWritingRunner(TimeSpan.FromSeconds(1));
            var service = new PiperNarrationService(
                new PiperConfiguration("python", root),
                Path.Combine(root, "cache"),
                runner);
            var request = new NarrationSynthesisRequest(
                "segment-1", "A concise description.", "voice-1",
                TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(250), 1);
            var first = await service.SynthesizeAsync(request, TestContext.Current.CancellationToken);
            await File.WriteAllBytesAsync(first.AudioPath, [1, 2, 3], TestContext.Current.CancellationToken);

            var repaired = await service.SynthesizeAsync(request, TestContext.Current.CancellationToken);

            Assert.False(repaired.ReusedCachedAudio);
            Assert.Equal(TimeSpan.FromSeconds(1), repaired.MeasuredDuration);
            Assert.Equal(2, runner.InvocationCount);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CatalogContainsFiveReviewedVoicesAndNoNonCommercialLicense()
    {
        Assert.Equal(5, PiperVoiceCatalog.All.Count);
        Assert.DoesNotContain(
            PiperVoiceCatalog.All,
            voice => voice.ModelLicense.Contains("NC", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class WaveWritingRunner(TimeSpan duration) : INarrationProcessRunner
    {
        public int InvocationCount { get; private set; }
        public IReadOnlyList<string> LastArguments { get; private set; } = [];
        public string? LastInputText { get; private set; }

        public Task<NarrationProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            InvocationCount++;
            LastArguments = arguments;
            var inputIndex = arguments.IndexOf("--input-file") + 1;
            LastInputText = File.ReadAllText(arguments[inputIndex]);
            var outputIndex = arguments.IndexOf("-f") + 1;
            WriteWave(arguments[outputIndex], duration);
            return Task.FromResult(new NarrationProcessResult(0, string.Empty, string.Empty));
        }

        private static void WriteWave(string path, TimeSpan duration)
        {
            const int sampleRate = 16_000;
            const short channels = 1;
            const short bits = 16;
            var bytesPerSecond = sampleRate * channels * bits / 8;
            var dataSize = checked((int)(duration.TotalSeconds * bytesPerSecond));
            using var stream = File.Create(path);
            using var writer = new BinaryWriter(stream, Encoding.ASCII);
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + dataSize);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write(channels);
            writer.Write(sampleRate);
            writer.Write(bytesPerSecond);
            writer.Write((short)(channels * bits / 8));
            writer.Write(bits);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(dataSize);
            writer.Write(new byte[dataSize]);
        }
    }
}

internal static class ReadOnlyListExtensions
{
    public static int IndexOf<T>(this IReadOnlyList<T> values, T expected)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(values[index], expected))
            {
                return index;
            }
        }

        return -1;
    }
}
