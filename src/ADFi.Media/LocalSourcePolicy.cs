namespace ADFi.Media;

public static class LocalSourcePolicy
{
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".mkv", ".webm", ".avi", ".m4v",
    };

    public static string ValidateExistingLocalVideo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (Uri.TryCreate(path, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            throw new ArgumentException("ADFi source intake accepts local video files only.", nameof(path));
        }

        var fullPath = Path.GetFullPath(path);
        if (!SupportedExtensions.Contains(Path.GetExtension(fullPath)))
        {
            throw new NotSupportedException("The selected local file is not a supported video format.");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("The selected local video does not exist.", fullPath);
        }

        return fullPath;
    }
}

