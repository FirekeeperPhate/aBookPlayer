using System.Runtime.InteropServices;

namespace aBookPlayer.Tests;

/// <summary>The shared library (aBookPlayer.Core) must behave on Android as the Windows app does.</summary>
public class CoreTests
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int StrCmpLogicalW(string a, string b);

    [Fact]
    public void Natural_order_matches_Explorer_for_book_parts()
    {
        // Names the way audiobook rips and exports name their files and folders
        string[] names =
        [
            "Track 10.mp3", "Track 2.mp3", "track 1.mp3", "Track 02b.mp3", "Chapter 100.mp3", "Chapter 99.mp3",
            @"CD 10\01 - Intro.mp3", @"CD 2\01 - Intro.mp3", @"CD 2\10 - End.mp3", @"CD 2\9 - Middle.mp3",
            "Book_ Series, Book 2 [B0TEST] - 07 - Chapter 6.mp3", "Book_ Series, Book 2 [B0TEST] - 10 - Chapter 9.mp3",
            "Book_ Series, Book 2 [B0TEST] - 08 - Chapter 7.mp3", "001.mp3", "01.mp3", "1.mp3", "Part 1a.mp3", "Part 1.mp3",
            "Épilogue.mp3", "epilogue 2.mp3", "Prologue.mp3", "a.mp3", "B.mp3",
        ];
        var windows = names.ToList();
        windows.Sort((a, b) => Math.Sign(StrCmpLogicalW(a, b)));
        var shared = names.ToList();
        shared.Sort(NaturalOrder.Compare);
        Assert.Equal(windows, shared);
    }
}
