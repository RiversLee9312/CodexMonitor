using Microsoft.UI.Xaml;

namespace CodexMonitor.App;

public partial class App : Application
{
    public static MainWindow? Window { get; private set; }
    public App()
    {
        InitializeComponent();
        DebugSettings.XamlResourceReferenceFailed += (_, args) => RecordDiagnostic("XAML resource: " + args.Message);
        UnhandledException += (_, args) =>
        {
            // Preserve actionable crash diagnostics locally; never persist transcript text here.
            RecordDiagnostic(args.Message + "\n" + args.Exception);
        };
    }
    private static void RecordDiagnostic(string message)
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMonitor");
        Directory.CreateDirectory(folder);
        File.AppendAllText(Path.Combine(folder, "crash.log"), $"{DateTimeOffset.UtcNow:O} {message}\n");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args) { Window = new MainWindow(); Window.Activate(); }
}
