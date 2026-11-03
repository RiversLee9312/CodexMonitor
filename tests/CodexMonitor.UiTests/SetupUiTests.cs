using CodexMonitor.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass]
[DoNotParallelize]
public sealed class SetupUiTests
{
    public TestContext TestContext { get; set; } = null!;
    private sealed class HomesStub(Func<CancellationToken, Task<IReadOnlyList<string>>> find) : ICodexHomeDiscovery
    {
        public Task<IReadOnlyList<string>> FindAsync(string home, CancellationToken ct = default) => find(ct);
    }
    [UITestMethod]
    [DataRow("Light", "zh-CN")]
    [DataRow("Dark", "de-DE")]
    [DataRow("Dark", "ar-SA")]
    public async Task MissingCliAndMultipleHomesFitNarrowSettings(string theme, string language)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 650, theme, language);
        var homes = new HomesStub(_ => Task.FromResult<IReadOnlyList<string>>(["C:\\Users\\Example\\.codex", "D:\\Codex State"]));
        var review = await window.OpenSetupReviewAsync(homes, _ => throw new FileNotFoundException(), "C:\\Users\\Example\\.codex");
        try
        {
            Assert.HasCount(2, review.Home.Results); Assert.IsFalse(review.Cli.IsAvailable);
            Assert.IsTrue(review.Dialog.ActualWidth <= window.ReviewRoot.ActualWidth + 1);
            Assert.IsTrue(review.Home.ActualWidth <= review.Dialog.ActualWidth);
            ((ScrollViewer)review.Dialog.Content).ChangeView(null, 250, null, true); await CodexMonitor.App.MainWindow.SettleAsync();
            TestContext.AddResultFile(await window.CaptureReviewAsync($"setup-560-{theme}-{language}", review.Dialog));
        }
        finally { review.Dialog.Hide(); await review.Closing; }
    }
    [UITestMethod]
    public async Task HomeScanFillsMissingPathButPreservesTypingAndValidCustomPath()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 650, "Dark", "zh-CN");
        var source = new TaskCompletionSource<IReadOnlyList<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var review = await window.OpenSetupReviewAsync(new HomesStub(_ => source.Task), _ => "C:\\codex.exe", "C:\\missing-home");
        try
        {
            review.Home.Home = "D:\\typing"; source.SetResult(["D:\\found"]); await CodexMonitor.App.MainWindow.SettleAsync();
            Assert.AreEqual("D:\\typing", review.Home.Home);
            review.Home.Home = "C:\\missing-home"; await review.Home.ScanAsync(); Assert.AreEqual("D:\\found", review.Home.Home);
            review.Home.Home = Path.GetTempPath(); await review.Home.ScanAsync(); Assert.AreEqual(Path.GetTempPath().Trim(), review.Home.Home);
            Assert.IsTrue(review.Cli.IsAvailable);
        }
        finally { review.Dialog.Hide(); await review.Closing; }
    }
    [UITestMethod]
    public async Task MissingCliConnectShowsSetupAndLeavesTranscriptReadable()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 600, "Light", "zh-CN");
        var review = await window.OpenMissingCliReviewAsync();
        try
        {
            Assert.IsNotNull(review.Dialog); Assert.IsTrue(review.Dialog.ActualHeight <= window.ReviewRoot.ActualHeight + 1);
            TestContext.AddResultFile(await window.CaptureReviewAsync("missing-cli-560-light-zh", review.Dialog));
        }
        finally { review.Dialog.Hide(); await review.Closing; }
        Assert.IsTrue(window.ReviewBounds()["messages"].Height > 0);
    }
    [UITestMethod]
    public async Task MissingCliWelcomeOffersSetupAndClearsAfterInstallationIsDetected()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 600, "Dark", "zh-CN");
        Assert.IsTrue(await window.SetMissingCliWelcomeReviewAsync(true));
        TestContext.AddResultFile(await window.CaptureReviewAsync("missing-cli-welcome-560-dark-zh"));
        Assert.IsFalse(await window.SetMissingCliWelcomeReviewAsync(false));
    }
}
