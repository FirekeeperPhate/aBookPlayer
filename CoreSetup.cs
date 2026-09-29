using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace aBookPlayer;

/// <summary>How the Windows app sets up the shared code (aBookPlayer.Core) as soon as it is loaded.</summary>
static class CoreSetup
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    static extern int StrCmpLogicalW(string a, string b);

    [ModuleInitializer]
    internal static void Initialize()
    {
        // The parts of a folder book are ordered exactly as Explorer shows them
        BookSource.NaturalCompare = StrCmpLogicalW;
        // Details of books never opened are kept next to the cover thumbnails
        LibraryDetailsCache.FilePath = Path.Combine(AppPaths.Local, "covers", "details.json");
    }
}
