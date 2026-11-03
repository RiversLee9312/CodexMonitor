using Microsoft.UI.Xaml;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

public partial class TestApp : Application
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _reviewTimer;
    internal static CodexMonitor.App.MainWindow ReviewWindow { get; private set; } = null!;
    public TestApp()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            var path = Environment.GetEnvironmentVariable("CODEX_MONITOR_MANUAL_STATE");
            if (!string.IsNullOrEmpty(path)) File.AppendAllText(path + ".error", args.Exception.ToString());
        };
    }
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        ReviewWindow = new CodexMonitor.App.MainWindow(reviewMode: true);
        ReviewWindow.Title = "Codex Monitor · MSTest";
        ReviewWindow.Activate();
        if (Environment.GetCommandLineArgs().Contains("--manual-review"))
        {
            ReviewWindow.Title = "Codex Monitor · Review";
            await CodexMonitor.App.MainWindow.SettleAsync();
            await ReviewWindow.SetReviewAsync(1180, 800, "Light", "zh-CN"); await ReviewWindow.ShowActivityReviewAsync(true);
            var statePath = Environment.GetEnvironmentVariable("CODEX_MONITOR_MANUAL_STATE");
            if (!string.IsNullOrEmpty(statePath))
            {
                _reviewTimer = ReviewWindow.DispatcherQueue.CreateTimer(); _reviewTimer.Interval = TimeSpan.FromSeconds(1);
                string? previous = null;
                _reviewTimer.Tick += (_, _) =>
                {
                    var state = System.Text.Json.JsonSerializer.Serialize(new { HighContrast = ReviewWindow.ReviewNativeHighContrast, TextScaleFactor = new Windows.UI.ViewManagement.UISettings().TextScaleFactor, Theme = ReviewWindow.ReviewTheme.ToString() });
                    if (state != previous) { File.WriteAllText(statePath, state); previous = state; }
                };
                ReviewWindow.Closed += (_, _) => _reviewTimer.Stop(); _reviewTimer.Start();
            }
            return;
        }
        UITestMethodAttribute.DispatcherQueue = ReviewWindow.DispatcherQueue;
        Environment.ExitCode = await MicrosoftTestingPlatformApplication.RunAsync(Environment.GetCommandLineArgs()[1..]);
        ReviewWindow.Close();
        Exit();
    }
}
