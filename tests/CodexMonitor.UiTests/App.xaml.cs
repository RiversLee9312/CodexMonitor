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
        if (Environment.GetCommandLineArgs().Contains("--manual-review-navigation"))
        {
            ReviewWindow.Title = "Codex Monitor · Navigation review";
            await CodexMonitor.App.MainWindow.SettleAsync();
            await ReviewWindow.SetReviewAsync(1180, 800, "Dark", "zh-CN");
            var path = Path.Combine(Path.GetTempPath(), "CodexMonitor-wheel-" + Guid.NewGuid() + ".jsonl");
            await File.WriteAllLinesAsync(path, Enumerable.Range(0, 120).Select(i => System.Text.Json.JsonSerializer.Serialize(new
            { type = "event_msg", timestamp = DateTimeOffset.UtcNow, payload = new { type = "agent_message", message = "Response " + i + "\n\n" + string.Concat(Enumerable.Repeat("Selectable **message** with `code` and a [link](https://example.com).\n", 30)) } })));
            var prompt = string.Concat(Enumerable.Repeat("## Task\n请检查实现、注释和测试。 Preserve state and reject stale results.\n", 4100));
            var clock = System.Diagnostics.Stopwatch.StartNew(); double previous = 0, maxGap = 0; int beats = 0;
            _reviewTimer = ReviewWindow.DispatcherQueue.CreateTimer(); _reviewTimer.Interval = TimeSpan.FromMilliseconds(16);
            _reviewTimer.Tick += (_, _) => { var now = clock.Elapsed.TotalMilliseconds; maxGap = Math.Max(maxGap, now - previous); previous = now; beats++; };
            _reviewTimer.Start();
            await ReviewWindow.ReviewSessionCatalogAsync(Enumerable.Range(0, 1000).Select(i => new CodexMonitor.Core.SessionInfo
            { Id = "wheel-" + i, Title = "Conversation " + i + (i % 3 == 0 ? "\n" + prompt : ""), FilePath = path, State = CodexMonitor.Core.SessionState.Completed, CreatedAt = DateTimeOffset.UtcNow.AddDays(-i), UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-i) }).ToArray());
            var output = Environment.GetEnvironmentVariable("CODEX_MONITOR_MANUAL_STATE");
            var telemetry = ReviewWindow.DispatcherQueue.CreateTimer(); telemetry.Interval = TimeSpan.FromMilliseconds(250); bool writing = false;
            telemetry.Tick += async (_, _) =>
            {
                if (writing || string.IsNullOrEmpty(output)) return;
                writing = true;
                try
                {
                    var state = System.Text.Json.JsonSerializer.Serialize(new { MaxDispatcherGapMs = maxGap, Beats = beats, Rows = ReviewWindow.ReviewSessionRows.Count,
                        Selected = (ReviewWindow.ReviewSelectedRow as CodexMonitor.App.SessionRow)?.Session.Id, Loading = ReviewWindow.ReviewTranscriptLoading, Messages = ReviewWindow.ReviewMessageRows.Count });
                    await Task.Run(() => File.WriteAllTextAsync(output, state));
                }
                finally { writing = false; }
            };
            telemetry.Start();
            ReviewWindow.Closed += (_, _) => { _reviewTimer.Stop(); telemetry.Stop(); File.Delete(path); };
            return;
        }
        if (Environment.GetCommandLineArgs().Any(a => a is "--manual-review" or "--manual-review-sessions"))
        {
            ReviewWindow.Title = "Codex Monitor · Review";
            await CodexMonitor.App.MainWindow.SettleAsync();
            await ReviewWindow.SetReviewAsync(1180, 800, "Light", "zh-CN"); await ReviewWindow.ShowActivityReviewAsync(true);
            if (Environment.GetCommandLineArgs().Contains("--manual-review-sessions"))
                await ReviewWindow.ReviewSessionCatalogAsync(Enumerable.Range(0, 600).Select(i => new CodexMonitor.Core.SessionInfo { Id = "local-fixture-" + i, Title = "Conversation " + i, CreatedAt = DateTimeOffset.UtcNow.AddDays(-i), UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-i) }).ToArray());
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
