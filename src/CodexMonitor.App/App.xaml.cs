using Microsoft.UI.Xaml;
using CodexMonitor.Core;

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
        _ = Task.Run(async () =>
        {
            try
            {
                var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CodexMonitor");
                Directory.CreateDirectory(folder);
                await File.AppendAllTextAsync(Path.Combine(folder, "crash.log"), $"{DateTimeOffset.UtcNow:O} {message}\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        });
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args) { var settings = await AppSettings.LoadAsync(); Window = new MainWindow(settings); Window.Activate(); }
}
