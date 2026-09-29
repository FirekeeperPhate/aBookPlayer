namespace aBookPlayer;

/// <summary>How the running copy got onto the PC: it decides where its data lives and how it can be updated.</summary>
enum InstallKind
{
    /// <summary>Installed for everyone, in Program Files.</summary>
    AllUsers,
    /// <summary>Installed just for this user, in %LOCALAPPDATA%\Programs.</summary>
    CurrentUser,
    /// <summary>The portable zip: a "portable.txt" next to the exe, data in a "Data" folder beside it.</summary>
    Portable,
    /// <summary>Anything else (a build run from the source folder…): updated by hand.</summary>
    Other,
}

/// <summary>
/// Where the app keeps its data: %APPDATA%\aBookPlayer (settings) and %LOCALAPPDATA%\aBookPlayer (models, GPU
/// support, cover thumbnails), or, for the portable version, a "Data" folder next to the exe (for a USB stick).
/// </summary>
static class AppPaths
{
    public const string PortableMarker = "portable.txt";

    static readonly string AppFolder = AppContext.BaseDirectory;

    public static bool Portable { get; } = File.Exists(Path.Combine(AppFolder, PortableMarker));

    static readonly string PortableData = Path.Combine(AppFolder, "Data");

    /// <summary>Settings (small, follow the user on a roaming profile).</summary>
    public static string Roaming { get; } = Portable ? PortableData
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppSettings.AppName);

    /// <summary>Large downloads and caches (models, GPU support, covers).</summary>
    public static string Local { get; } = Portable ? PortableData
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppSettings.AppName);

    public static InstallKind Install { get; } = Portable ? InstallKind.Portable : FromRegistry() ?? Kind(AppFolder, Portable,
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs"));

    /// <summary>The installer's AppId (installer\aBookPlayer.iss): Windows keeps its uninstall entry under it.</summary>
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{7E1C4A2B-3D5F-4B8E-9A61-2F0C8D4B7A13}_is1";

    /// <summary>
    /// Installed by the installer, also in a folder chosen in the wizard: its uninstall entry names this folder, in
    /// the machine's registry (for everyone) or the user's (just for them).
    /// </summary>
    static InstallKind? FromRegistry()
    {
        foreach (var (hive, kind) in new[] { (Microsoft.Win32.RegistryHive.LocalMachine, InstallKind.AllUsers), (Microsoft.Win32.RegistryHive.CurrentUser, InstallKind.CurrentUser) })
        {
            try
            {
                using var root = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, Microsoft.Win32.RegistryView.Registry64);
                using var key = root.OpenSubKey(UninstallKey);
                if (key?.GetValue("InstallLocation") is string location
                    && string.Equals(Path.TrimEndingDirectorySeparator(location), Path.TrimEndingDirectorySeparator(AppFolder), StringComparison.OrdinalIgnoreCase))
                    return kind;
            }
            catch { /* no access: the folder name decides */ }
        }
        return null;
    }

    /// <summary>The installer puts the app in "Program Files\aBookPlayer" or "%LOCALAPPDATA%\Programs\aBookPlayer".</summary>
    internal static InstallKind Kind(string appFolder, bool portable, string programFiles, string userPrograms)
    {
        if (portable) return InstallKind.Portable;
        var folder = Path.TrimEndingDirectorySeparator(appFolder);
        bool In(string root) => string.Equals(folder, Path.Combine(root, AppSettings.AppName), StringComparison.OrdinalIgnoreCase);
        return In(programFiles) ? InstallKind.AllUsers : In(userPrograms) ? InstallKind.CurrentUser : InstallKind.Other;
    }

    /// <summary>The self-contained build (full installer, portable zip) carries the .NET runtime next to the exe.</summary>
    public static bool SelfContained { get; } = File.Exists(Path.Combine(AppFolder, "coreclr.dll"));
}
