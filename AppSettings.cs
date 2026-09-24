using System.Text.Json;
using System.Text.Json.Serialization;

namespace aBookPlayer;

enum SubtitlePosition { Center, Bottom }

sealed class SubtitleStyle
{
    public string FontFamily { get; set; } = "Segoe UI";
    public float FontSize { get; set; } = 22f;
    public bool Bold { get; set; }
    public string TextColor { get; set; } = "#FAFAFA";
    public bool ShowBackground { get; set; } = true;
    public string BackgroundColor { get; set; } = "#000000";
    public int BackgroundOpacity { get; set; } = 55;
    public SubtitlePosition Position { get; set; } = SubtitlePosition.Center;

    public SubtitleStyle Clone() => (SubtitleStyle)MemberwiseClone();

    public static Color ParseColor(string? hex, Color fallback)
    {
        try { return string.IsNullOrWhiteSpace(hex) ? fallback : ColorTranslator.FromHtml(hex); }
        catch { return fallback; }
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}

/// <summary>Settings persisted to %APPDATA%\aBookPlayer\settings.json.</summary>
sealed class AppSettings
{
    public const string AppName = "aBookPlayer";
    const string LegacyFolderName = "Mp3ChapterPlayer";

    /// <summary>
    /// Moves data saved by earlier builds (settings in %APPDATA%, Whisper models in %LOCALAPPDATA%)
    /// from the old "Mp3ChapterPlayer" folders, so nothing is lost or downloaded again.
    /// </summary>
    public static void MigrateLegacyFolders()
    {
        foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
        {
            try
            {
                var basePath = Environment.GetFolderPath(root);
                var legacy = Path.Combine(basePath, LegacyFolderName);
                var current = Path.Combine(basePath, AppName);
                if (Directory.Exists(legacy) && !Directory.Exists(current)) Directory.Move(legacy, current);
            }
            catch { /* best effort: at worst the app starts with defaults */ }
        }
    }

    public string? LastFile { get; set; }
    public double LastPositionSeconds { get; set; }
    public string? LastSubtitleFile { get; set; }
    public double SubtitleOffsetMs { get; set; }
    public float Volume { get; set; } = 0.8f;
    public SubtitleStyle Subtitles { get; set; } = new();
    /// <summary>GgmlType of the model (e.g. "BaseEn"), independent of the UI language.</summary>
    public string WhisperModel { get; set; } = "BaseEn";
    public string WhisperLanguage { get; set; } = "en";
    public bool WhisperSaveText { get; set; } = true;
    public int[]? WindowBounds { get; set; }
    public bool WindowMaximized { get; set; }

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName, "settings.json");

    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
                if (settings != null)
                {
                    settings.Subtitles ??= new();
                    return settings;
                }
            }
        }
        catch { /* corrupt file: fall back to defaults */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* never block the app if saving fails */ }
    }
}
