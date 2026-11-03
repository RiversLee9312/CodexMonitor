using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass]
[DoNotParallelize]
public sealed class ServerDiscoveryUiTests
{
    public TestContext TestContext { get; set; } = null!;
    private sealed class DiscoveryStub(Func<CancellationToken, Task<IReadOnlyList<DiscoveredServer>>> result) : IServerDiscovery
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<DiscoveredServer>> FindAsync(string home, string executable, CancellationToken ct = default) { Calls++; return result(ct); }
    }
    [UITestMethod]
    [DataRow("Dark", "zh-CN")]
    [DataRow("Light", "de-DE")]
    [DataRow("Dark", "ar-SA")]
    public async Task SelectingExistingServerAutomaticallyScansAndFitsNarrowDialog(string theme, string language)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 650, theme, language);
        var stub = new DiscoveryStub(_ => Task.FromResult<IReadOnlyList<DiscoveredServer>>([
            new(new("unix://", SocketPath: "C:\\Users\\Example\\.codex\\app-server-control\\app-server-control.sock")), new(new("ws://127.0.0.1:45123", 12345))]));
        var review = await window.OpenServerDiscoveryReviewAsync(stub);
        try
        {
            Assert.AreEqual(1, stub.Calls, "Opening existing-server settings should begin exactly one scan");
            Assert.HasCount(2, review.Panel.Results); Assert.IsFalse(review.Panel.IsScanning);
            Assert.IsTrue(review.Dialog.ActualWidth <= window.ReviewRoot.ActualWidth + 1);
            Assert.IsTrue(review.Dialog.ActualHeight <= window.ReviewRoot.ActualHeight + 1);
            TestContext.AddResultFile(await window.CaptureReviewAsync($"discovery-560-{theme}-{language}", review.Dialog));
        }
        finally { review.Dialog.Hide(); await review.Closing; }
    }
    [UITestMethod]
    public async Task SingleAvailableDaemonCanFillAnUntouchedExampleAddress()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 650, "Light", "zh-CN");
        var stub = new DiscoveryStub(_ => Task.FromResult<IReadOnlyList<DiscoveredServer>>([new(new("unix://", SocketPath: "C:\\example.sock"))]));
        var review = await window.OpenServerDiscoveryReviewAsync(stub);
        try { Assert.AreEqual("unix://", review.Panel.Endpoint); }
        finally { review.Dialog.Hide(); await review.Closing; }
    }
    [UITestMethod]
    public async Task ScanDoesNotOverwriteAnAddressTypedWhileItIsRunning()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 650, "Dark", "zh-CN");
        var result = new TaskCompletionSource<IReadOnlyList<DiscoveredServer>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var review = await window.OpenServerDiscoveryReviewAsync(new DiscoveryStub(_ => result.Task));
        try
        {
            Assert.IsTrue(review.Panel.IsScanning);
            review.Panel.Endpoint = "ws://127.0.0.1:49000";
            result.SetResult([new(new("unix://", SocketPath: "C:\\example.sock"))]);
            await CodexMonitor.App.MainWindow.SettleAsync();
            Assert.AreEqual("ws://127.0.0.1:49000", review.Panel.Endpoint);
        }
        finally { review.Dialog.Hide(); await review.Closing; }
    }
    [UITestMethod]
    public async Task ClosingSettingsCancelsAnInProgressScan()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 650, "Dark", "zh-CN");
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stub = new DiscoveryStub(async ct =>
        {
            using var registration = ct.Register(() => cancelled.TrySetResult());
            await Task.Delay(Timeout.Infinite, ct); return [];
        });
        var review = await window.OpenServerDiscoveryReviewAsync(stub);
        Assert.IsTrue(review.Panel.IsScanning); review.Dialog.Hide(); await review.Closing;
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }
    [UITestMethod]
    public async Task EmptyScanKeepsManualAddressAndShowsAUsableSettingsDialog()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 600, "Light", "zh-CN");
        var review = await window.OpenServerDiscoveryReviewAsync(new DiscoveryStub(_ => Task.FromResult<IReadOnlyList<DiscoveredServer>>([])));
        try
        {
            Assert.HasCount(0, review.Panel.Results); Assert.AreEqual("ws://127.0.0.1:4500", review.Panel.Endpoint);
            TestContext.AddResultFile(await window.CaptureReviewAsync("discovery-empty-560-light-zh", review.Dialog));
        }
        finally { review.Dialog.Hide(); await review.Closing; }
    }
}
