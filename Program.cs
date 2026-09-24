using System.Globalization;
using System.Text;

namespace aBookPlayer;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Windows-1252 for non-UTF-8 .srt files
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        AppSettings.MigrateLegacyFolders();
        // English UI: numbers and percentages in English format, regardless of Windows regional settings
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm(args.FirstOrDefault()));
    }
}
