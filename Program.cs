using System.Globalization;

namespace aBookPlayer;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var startupFile = args.FirstOrDefault();

        // One player per session: hand the file to the running instance. If it cannot be reached
        // (still starting up, or hung), fall back to starting normally.
        using var instance = SingleInstance.TryAcquire();
        if (instance == null && SingleInstance.SendToRunningInstance(startupFile)) return;

        AppSettings.MigrateLegacyFolders();
        // English UI: numbers and percentages in English format, regardless of Windows regional settings
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        ApplicationConfiguration.Initialize();

        var form = new MainForm(startupFile);
        // Listen only once the window exists, so forwarded files can be marshalled to the UI thread
        form.Shown += (_, _) => instance?.Listen(path => form.BeginInvoke(() => form.OpenFromOtherInstance(path)));
        Application.Run(form);
    }
}
