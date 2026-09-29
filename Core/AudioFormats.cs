namespace aBookPlayer;

/// <summary>The audio files a book can be made of (each app decodes them in its own way).</summary>
static class AudioFormats
{
    public static readonly string[] Extensions =
        [".mp3", ".m4a", ".m4b", ".aac", ".mp4", ".wma", ".wav", ".flac", ".aiff", ".aif", ".ogg"];

    /// <summary>Filter for a Windows open-file dialog.</summary>
    public static string DialogFilter =>
        $"Audio files ({string.Join(";", Extensions.Select(e => "*" + e))})|{string.Join(";", Extensions.Select(e => "*" + e))}|All files (*.*)|*.*";

    public static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
}
