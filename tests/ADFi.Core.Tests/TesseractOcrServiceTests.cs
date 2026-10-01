using ADFi.Core;
using ADFi.Media;

namespace ADFi.Core.Tests;

public sealed class TesseractOcrServiceTests
{
    private const string Tsv =
        "level\tpage_num\tblock_num\tpar_num\tline_num\tword_num\tleft\ttop\twidth\theight\tconf\ttext\n" +
        "5\t1\t1\t1\t1\t1\t0\t0\t10\t10\t96.0\tGate\n" +
        "5\t1\t1\t1\t1\t2\t11\t0\t10\t10\t94.0\t17\n" +
        "5\t1\t1\t1\t2\t1\t0\t12\t10\t10\t90.0\tCLOSED\n";

    [Fact]
    public async Task ExactFallbackPreservesLinesAndReconcilesAgreement()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adfi-crop-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, [1], TestContext.Current.CancellationToken);
        try
        {
            var runner = new FixedOcrRunner();
            var service = new OcrVerificationService(new TesseractOcrService("tesseract", runner));
            var result = await service.ReconcileAsync(
                [new("vlm", OcrEvidenceSource.VlmContext, "Gate 17 CLOSED", 0.7m, true)],
                path,
                "tesseract-crop-1",
                ["eng"],
                TestContext.Current.CancellationToken);

            Assert.Equal("Gate 17\nCLOSED", result.SupportedText);
            Assert.False(result.RequiresAdditionalEvidence);
            Assert.Equal(1, runner.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ConflictingLiteralReadingRemainsUncertain()
    {
        var path = Path.Combine(Path.GetTempPath(), $"adfi-crop-{Guid.NewGuid():N}.png");
        await File.WriteAllBytesAsync(path, [1], TestContext.Current.CancellationToken);
        try
        {
            var service = new OcrVerificationService(new TesseractOcrService("tesseract", new FixedOcrRunner()));
            var result = await service.ReconcileAsync(
                [new("vlm", OcrEvidenceSource.VlmContext, "Gate 12", 0.8m, true)],
                path,
                "tesseract",
                ["eng"],
                TestContext.Current.CancellationToken);

            Assert.Null(result.SupportedText);
            Assert.True(result.RequiresAdditionalEvidence);
            Assert.Equal(2, result.CompetingReadings.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private sealed class FixedOcrRunner : IOcrProcessRunner
    {
        public int Count { get; private set; }

        public Task<OcrProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(new OcrProcessResult(0, Tsv, string.Empty));
        }
    }
}
