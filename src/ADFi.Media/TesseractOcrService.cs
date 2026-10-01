using System.Globalization;
using ADFi.Core;
using OpenCvSharp;

namespace ADFi.Media;

public readonly record struct ImageRegion(int X, int Y, int Width, int Height);

public sealed record OcrProcessResult(int ExitCode, string StandardOutput, string StandardError);

public interface IOcrProcessRunner
{
    Task<OcrProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed class TesseractProcessRunner : IOcrProcessRunner
{
    public async Task<OcrProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await ExternalProcess.RunAsync(executable, arguments, cancellationToken).ConfigureAwait(false);
        return new(result.ExitCode, result.StandardOutput, result.StandardError);
    }
}

public sealed class OcrCropService
{
    public static string ExtractNormalizedCrop(
        string framePath,
        ImageRegion region,
        string outputPath)
    {
        if (!File.Exists(framePath))
        {
            throw new FileNotFoundException("OCR source frame is missing.", framePath);
        }

        using var source = Cv2.ImRead(framePath, ImreadModes.Color);
        if (source.Empty()
            || region.X < 0
            || region.Y < 0
            || region.Width <= 0
            || region.Height <= 0
            || region.X + region.Width > source.Width
            || region.Y + region.Height > source.Height)
        {
            throw new ArgumentOutOfRangeException(nameof(region), "OCR crop must be inside the source frame.");
        }

        using var crop = new Mat(source, new Rect(region.X, region.Y, region.Width, region.Height));
        using var gray = new Mat();
        using var enlarged = new Mat();
        using var normalized = new Mat();
        Cv2.CvtColor(crop, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.Resize(gray, enlarged, new Size(), 2, 2, InterpolationFlags.Cubic);
        Cv2.Threshold(enlarged, normalized, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
        var fullOutput = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        if (!Cv2.ImWrite(fullOutput, normalized))
        {
            throw new InvalidOperationException("OpenCV could not write the normalized OCR crop.");
        }

        return fullOutput;
    }
}

public sealed class TesseractOcrService(
    string tesseractPath,
    IOcrProcessRunner processRunner)
{
    public async Task<OcrCandidate> ReadExactAsync(
        string evidenceId,
        string cropPath,
        IReadOnlyList<string> languages,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(evidenceId);
        if (!File.Exists(cropPath))
        {
            throw new FileNotFoundException("OCR crop is missing.", cropPath);
        }

        if (languages.Count == 0 || languages.Any(language => !IsSafeLanguageCode(language)))
        {
            throw new ArgumentException("At least one valid Tesseract language code is required.", nameof(languages));
        }

        var result = await processRunner.RunAsync(
            tesseractPath,
            [Path.GetFullPath(cropPath), "stdout", "-l", string.Join('+', languages), "--psm", "6", "tsv"],
            cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"TESSERACT_OCR_FAILED: {Sanitize(result.StandardError)}");
        }

        var parsed = ParseTsv(result.StandardOutput);
        return new(evidenceId, OcrEvidenceSource.TesseractLiteral, parsed.Text, parsed.Confidence, true);
    }

    internal static (string Text, decimal Confidence) ParseTsv(string tsv)
    {
        var words = new List<(string Line, string Text, decimal Confidence)>();
        foreach (var row in tsv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Skip(1))
        {
            var columns = row.Split('\t');
            if (columns.Length < 12
                || string.IsNullOrWhiteSpace(columns[11])
                || !decimal.TryParse(columns[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var confidence)
                || confidence < 0)
            {
                continue;
            }

            words.Add(($"{columns[2]}:{columns[3]}:{columns[4]}", columns[11].Trim(), confidence / 100m));
        }

        if (words.Count == 0)
        {
            return (string.Empty, 0);
        }

        var text = string.Join(
            '\n',
            words.GroupBy(word => word.Line)
                .Select(line => string.Join(' ', line.Select(word => word.Text))));
        return (text, decimal.Round(words.Average(word => word.Confidence), 4));
    }

    private static bool IsSafeLanguageCode(string value) =>
        value.Length is >= 2 and <= 16 && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static string Sanitize(string value)
    {
        var result = string.Join(' ', value.Split(default(string[]), StringSplitOptions.RemoveEmptyEntries));
        return result[..Math.Min(result.Length, 500)];
    }
}

public sealed class OcrVerificationService(TesseractOcrService tesseract)
{
    public async Task<ReconciledOcr> ReconcileAsync(
        IReadOnlyList<OcrCandidate> vlmCandidates,
        string focusedCropPath,
        string tesseractEvidenceId,
        IReadOnlyList<string> languages,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(vlmCandidates);
        var initial = OcrReconciler.Reconcile(vlmCandidates);
        var exactRequired = vlmCandidates.Any(candidate => candidate.ExactCharactersRequired);
        if (!initial.RequiresAdditionalEvidence && !exactRequired)
        {
            return initial;
        }

        var literal = await tesseract.ReadExactAsync(
            tesseractEvidenceId,
            focusedCropPath,
            languages,
            cancellationToken).ConfigureAwait(false);
        return OcrReconciler.Reconcile([.. vlmCandidates, literal]);
    }
}
