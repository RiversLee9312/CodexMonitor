using Microsoft.UI.Xaml;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

public partial class TestApp : Application
{
    internal static CodexMonitor.App.MainWindow ReviewWindow { get; private set; } = null!;
    public TestApp() => InitializeComponent();
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        ReviewWindow = new CodexMonitor.App.MainWindow(reviewMode: true);
        ReviewWindow.Title = "Codex Monitor · MSTest";
        ReviewWindow.Activate();
        UITestMethodAttribute.DispatcherQueue = ReviewWindow.DispatcherQueue;
        Environment.ExitCode = await MicrosoftTestingPlatformApplication.RunAsync(Environment.GetCommandLineArgs()[1..]);
        ReviewWindow.Close();
        Exit();
    }
}
