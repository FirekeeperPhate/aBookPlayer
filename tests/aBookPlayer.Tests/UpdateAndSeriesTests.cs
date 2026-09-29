using System.Text.Json;

namespace aBookPlayer.Tests;

/// <summary>Updates, the portable copy, the next book of a series, library groups and the daily goal's streak.</summary>
public class UpdateAndSeriesTests
{
    static ReleaseInfo Release(string version, params string[] assets) =>
        new(Version.Parse(version), "", assets.Select(a => new ReleaseAsset(a, "https://example.invalid/" + a, 100, null)).ToList());

    [Fact]
    public void Update_picks_the_installer_of_the_same_kind_and_none_for_portable_or_unknown_copies()
    {
        var release = Release("1.10.0", "aBookPlayer-1.10.0-x64-setup.exe", "aBookPlayer-1.10.0-x64-light-setup.exe", "aBookPlayer-1.10.0-x64-portable.zip");
        Assert.Equal("aBookPlayer-1.10.0-x64-setup.exe", UpdateCheck.InstallerFor(release, InstallKind.AllUsers, selfContained: true)?.Name);
        Assert.Equal("aBookPlayer-1.10.0-x64-light-setup.exe", UpdateCheck.InstallerFor(release, InstallKind.CurrentUser, selfContained: false)?.Name);
        Assert.Null(UpdateCheck.InstallerFor(release, InstallKind.Portable, selfContained: true));
        Assert.Null(UpdateCheck.InstallerFor(release, InstallKind.Other, selfContained: true));
        // Just published, before the installers are attached
        Assert.Null(UpdateCheck.InstallerFor(Release("1.10.0"), InstallKind.AllUsers, selfContained: true));
    }

    [Fact]
    public void Update_reads_version_notes_and_asset_checksums_from_the_github_answer()
    {
        using var json = JsonDocument.Parse("""
            { "tag_name": "v1.10.0", "body": "## New\n\n**Update check**\n\n## Downloads\n| a | b |",
              "assets": [ { "name": "x.exe", "browser_download_url": "https://u/x.exe", "size": 42, "digest": "sha256:ABCD" } ] }
            """);
        var release = UpdateCheck.Parse(json.RootElement)!;
        Assert.Equal(new Version(1, 10, 0), release.Version);
        Assert.Equal(new ReleaseAsset("x.exe", "https://u/x.exe", 42, "ABCD"), release.Assets.Single());
        Assert.Equal($"New{Environment.NewLine}{Environment.NewLine}Update check", UpdateCheck.NotesForDisplay(release.Notes));
    }

    [Fact]
    public void Install_kind_comes_from_where_the_app_runs()
    {
        const string pf = @"C:\Program Files", user = @"C:\Users\x\AppData\Local\Programs";
        Assert.Equal(InstallKind.AllUsers, AppPaths.Kind(@"C:\Program Files\aBookPlayer\", false, pf, user));
        Assert.Equal(InstallKind.CurrentUser, AppPaths.Kind(@"C:\Users\x\AppData\Local\Programs\aBookPlayer\", false, pf, user));
        Assert.Equal(InstallKind.Portable, AppPaths.Kind(@"C:\Program Files\aBookPlayer\", true, pf, user));
        Assert.Equal(InstallKind.Other, AppPaths.Kind(@"D:\src\aBookPlayer\bin\Release\", false, pf, user));
    }

    [Fact]
    public void Next_in_series_is_the_lowest_unfinished_number_after_this_one()
    {
        SeriesCandidate Book(string title, string? series, int? number, bool finished = false) => new(title, title, series, number, finished);
        var books = new[]
        {
            Book("Book 1", "Harbor", 1, finished: true), Book("Book 3", "harbor ", 3), Book("Book 2", "Harbor", 2, finished: true),
            Book("Book 4", "Harbor", 4), Book("Other 2", "Other", 2), Book("Loose", null, null),
        };
        Assert.Equal("Book 3", SeriesOrder.Next("Harbor", 1, books)?.Title); // 2 is finished already
        Assert.Equal("Book 4", SeriesOrder.Next("Harbor", 3, books)?.Title);
        Assert.Null(SeriesOrder.Next("Harbor", 4, books));
    }

    [Fact]
    public void Series_candidates_are_the_books_stored_beside_the_finished_one()
    {
        var others = new[] { @"D:\Books\Reyes\Harbor 2", @"D:\Books\Reyes\Harbor 3.m4b", @"D:\Books\Dunmore\Winter", @"E:\Else\Harbor 4" };
        Assert.Equal([@"D:\Books\Reyes\Harbor 2", @"D:\Books\Reyes\Harbor 3.m4b", @"D:\Books\Dunmore\Winter"],
            SeriesOrder.Nearby(@"D:\Books\Reyes\Harbor 1", others).ToList());
    }

    [Fact]
    public void Library_groups_by_series_with_counts_and_collapsed_groups_hidden()
    {
        LibraryEntry Book(string title, string? series, int? number, bool finished = false) =>
            new() { Path = @"C:\b\" + title, State = new BookState { Title = title, Series = series, SeriesNumber = number, Finished = finished } };
        var sorted = LibraryOrder.Sort([Book("B", "Harbor", 2), Book("A", "Harbor", 1, finished: true), Book("Solo", null, null), Book("W", "Winter", 1)], LibrarySort.Series);

        var open = LibraryOrder.Group(sorted, LibrarySort.Series, []);
        Assert.Equal(["Harbor", "A", "B", "Winter", "W", "Not in a series", "Solo"],
            open.Select(i => i is LibraryGroup g ? g.Name : ((LibraryEntry)i).Title).ToList());
        Assert.Equal(new LibraryGroup("series:harbor", "Harbor", 2, 1, false), open[0]);

        var collapsed = LibraryOrder.Group(sorted, LibrarySort.Series, ["series:harbor"]);
        Assert.Equal(["Harbor", "Winter", "W", "Not in a series", "Solo"],
            collapsed.Select(i => i is LibraryGroup g ? g.Name : ((LibraryEntry)i).Title).ToList());
        Assert.Equal(4, LibraryOrder.Group(sorted, LibrarySort.Recent, []).Count); // no headers
    }

    [Fact]
    public void Streak_counts_days_reaching_the_goal_and_today_does_not_break_it_yet()
    {
        var settings = new AppSettings();
        var today = new DateTime(2026, 9, 29, 20, 0, 0);
        void Listen(int daysAgo, int minutes) => ListeningStats.Add(settings, today.AddDays(-daysAgo), minutes * 60);
        Listen(1, 35); Listen(2, 30); Listen(3, 10); Listen(4, 40); Listen(5, 45); Listen(6, 50);

        Assert.Equal(2, ListeningStats.Streak(settings, today, 30));  // today not reached yet: up to yesterday
        Listen(0, 30);
        Assert.Equal(3, ListeningStats.Streak(settings, today, 30));  // today reached
        Assert.Equal(7, ListeningStats.Streak(settings, today, 0));   // no goal: any listening counts
        Assert.Equal(3, ListeningStats.BestStreak(settings, 30));
    }
}
