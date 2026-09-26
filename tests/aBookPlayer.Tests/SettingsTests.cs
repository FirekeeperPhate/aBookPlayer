namespace aBookPlayer.Tests;

public class SettingsTests
{
    [Fact]
    public void Books_are_remembered_per_path_case_insensitively()
    {
        var settings = new AppSettings();
        settings.RememberBook(@"C:\Books\One.m4b", 120, @"C:\Books\One.srt", 300);
        settings.RememberBook(@"C:\Books\Two.mp3", 45, null, 0);

        var one = settings.GetBook(@"c:\books\ONE.M4B");
        Assert.NotNull(one);
        Assert.Equal(120, one.PositionSeconds);
        Assert.Equal(@"C:\Books\One.srt", one.SubtitleFile);
        Assert.Equal(300, one.SubtitleOffsetMs);
        Assert.Equal([@"C:\Books\Two.mp3", @"C:\Books\One.m4b"], settings.RecentBooks(10));
    }

    [Fact]
    public void History_keeps_only_the_most_recent_books()
    {
        var settings = new AppSettings();
        for (int i = 0; i < AppSettings.MaxBooks + 5; i++)
        {
            settings.RememberBook($@"C:\Books\{i}.mp3", i, null, 0);
            settings.Books[$@"C:\Books\{i}.mp3"].LastOpened = new DateTime(2026, 1, 1).AddMinutes(i); // deterministic order
        }
        settings.RememberBook(@"C:\Books\new.mp3", 1, null, 0);

        Assert.Equal(AppSettings.MaxBooks, settings.Books.Count);
        Assert.Null(settings.GetBook(@"C:\Books\0.mp3"));
        Assert.NotNull(settings.GetBook(@"C:\Books\new.mp3"));
    }

    [Fact]
    public void Legacy_last_position_is_migrated_into_the_history()
    {
        var settings = new AppSettings
        {
            LastFile = @"C:\Books\Old.m4b",
            LastPositionSeconds = 3600,
            LastSubtitleFile = @"C:\Books\Old.srt",
            SubtitleOffsetMs = -200,
        };
        settings.MigrateLegacyPosition();

        var book = settings.GetBook(@"C:\Books\Old.m4b");
        Assert.NotNull(book);
        Assert.Equal(3600, book.PositionSeconds);
        Assert.Equal(-200, book.SubtitleOffsetMs);
        Assert.Equal(0, settings.LastPositionSeconds); // no longer written
        Assert.Null(settings.LastSubtitleFile);
    }

    [Fact]
    public void Normalize_merges_keys_differing_only_by_case()
    {
        var settings = new AppSettings
        {
            Books = new Dictionary<string, BookState>
            {
                [@"C:\A.mp3"] = new() { PositionSeconds = 1, LastOpened = new DateTime(2026, 1, 1) },
                [@"c:\a.MP3"] = new() { PositionSeconds = 2, LastOpened = new DateTime(2026, 2, 1) },
            },
        };
        settings.NormalizeBooks();

        Assert.Single(settings.Books);
        Assert.Equal(2, settings.GetBook(@"C:\A.mp3")!.PositionSeconds); // the most recent wins
    }

    [Fact]
    public void Model_ids_and_files_are_unique_and_full_models_keep_their_old_names()
    {
        var models = WhisperModels.All;
        Assert.Equal(models.Length, models.Select(m => m.Id).Distinct().Count());
        Assert.Equal(models.Length, models.Select(m => m.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Settings and already downloaded models from earlier versions must still match
        var medium = models.Single(m => m.Id == "Medium");
        Assert.EndsWith("ggml-medium.bin", medium.FilePath);
        Assert.EndsWith("ggml-largev3turbo-q5_0.bin", models.Single(m => m.Id == "LargeV3Turbo-Q5_0").FilePath);
    }
}
