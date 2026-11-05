using System.Text.Json;
using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass, DoNotParallelize]
public sealed partial class InjectedScrollUiTests
{
    public TestContext TestContext { get; set; } = null!;
    private static async Task WaitFor(Func<bool> complete, string message, int milliseconds = 6000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (!complete() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.IsTrue(complete(), message);
    }
    private static async Task<string> HistoryAsync(int count, bool tools = false)
    {
        var path = Path.Combine(Path.GetTempPath(), "CodexMonitor-injected-" + Guid.NewGuid() + ".jsonl");
        await Task.Run(async () =>
        {
            await using var writer = new StreamWriter(path);
            for (int i = 0; i < count; i++)
                await writer.WriteLineAsync(tools
                    ? JsonSerializer.Serialize(new { type = "response_item", payload = new { type = "function_call", call_id = "tool-" + i, name = "exec_command", arguments = "{\"cmd\":\"echo " + i + "\"}" } })
                    : JsonSerializer.Serialize(new { type = "response_item", payload = new { type = "message", id = "message-" + i, role = "assistant", content = new[] { new { type = "output_text", text = "Message " + i + ". A readable response for a real mouse-wheel paging test." } } } }));
        });
        return path;
    }
    [UITestMethod]
    public async Task WheelAtTheTopRecoversAPendingLayoutWithoutRequiringAnotherLayoutEvent()
    {
        var path = await HistoryAsync(1800);
        try
        {
            var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 700, "Dark", "zh-CN");
            window.ReviewTranscriptFile(path); window.ReviewUsePaging(); await window.ReviewReloadTranscript(true); await MainWindow.SettleAsync();
            var input = new InjectedInput(window); await input.ActivateAsync();
            window.ReviewBlockHistoryPaging(true);
            await input.WheelAsync(window.ReviewTranscriptSurface, 1920, 60); await MainWindow.SettleAsync();
            Assert.AreEqual(0, window.ReviewTranscriptSurface.Scroll.VerticalOffset, 1);
            window.ReviewUnacknowledgedHistoryLayout();
            await input.WheelAsync(window.ReviewTranscriptSurface, 120, 1, positionPointer: false);
            await WaitFor(() => window.ReviewLoadedCount > 300, "An upward wheel at offset zero must not be stranded waiting for a layout acknowledgement");
            TestContext.AddResultFile(await window.CaptureReviewAsync("injected-history-layout-recovery"));
        }
        finally { File.Delete(path); }
    }
    [UITestMethod]
    public async Task WheelAtTheSidebarEndRecoversItsPendingLayoutAndLoadsTheNextPage()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Light", "zh-CN");
        var sessions = Sessions(1000); await window.ReviewSessionCatalogAsync(sessions);
        var input = new InjectedInput(window); await input.ActivateAsync();
        var list = window.ReviewSessionSurface;
        var keys = new List<string>(); Microsoft.UI.Xaml.Input.KeyEventHandler trace = (_, e) => keys.Add(e.Key.ToString());
        list.AddHandler(UIElement.KeyDownEvent, trace, true);
        window.ReviewBlockSessionPaging(true);
        var visible = list.RealizedElements.First(e => { var y = e.TransformToVisual(list.Scroll).TransformPoint(new(0,0)).Y; return y >= 0 && y + e.ActualHeight < list.Scroll.ViewportHeight; });
        await input.ClickAsync(visible);
        Assert.IsTrue(visible.Focus(FocusState.Programmatic));
        await input.KeyAsync(0x22, 20); await MainWindow.SettleAsync();
        TestContext.WriteLine(input.KeyboardDiagnostic);
        TestContext.WriteLine("Keys=" + string.Join(",", keys) + "; focus=" + FocusManager.GetFocusedElement(window.ReviewRoot.XamlRoot)?.GetType().Name + "; " + window.ReviewPagingState);
        list.RemoveHandler(UIElement.KeyDownEvent, trace);
        Assert.AreEqual(window.ReviewSessionSurface.Scroll.ScrollableHeight, window.ReviewSessionSurface.Scroll.VerticalOffset, 2, "Injected PageDown must reach the loaded sidebar boundary before testing its paging latch");
        window.ReviewUnacknowledgedSessionLayout();
        await input.WheelAsync(window.ReviewSessionSurface, -120, 1);
        await WaitFor(() => window.ReviewSessionRows.Count > 50, "A downward wheel at the end must recover layout debt and continue paging: " + window.ReviewPagingState);
        TestContext.AddResultFile(await window.CaptureReviewAsync("injected-session-layout-recovery"));
    }
    private static SessionInfo[] Sessions(int count) => Enumerable.Range(0, count).Select(i => new SessionInfo
    { Id = "injected-" + i, Title = "Conversation " + i, Cwd = "D:\\Example", UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-i), CreatedAt = DateTimeOffset.UtcNow.AddDays(-i), State = SessionState.Completed }).ToArray();

    [UITestMethod]
    public async Task ReorderingDuringContinuousWheelInputDoesNotLeaveBlankOrStaleSessionContainers()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        var sessions = Sessions(1000); await window.ReviewSessionCatalogAsync(sessions);
        var input = new InjectedInput(window); await input.ActivateAsync();
        for (int batch = 0; batch < 14; batch++)
        {
            await input.WheelAsync(window.ReviewSessionSurface, batch % 4 < 2 ? -480 : 480, 12);
            var moved = sessions.Select((s, i) => i % 11 == batch % 11 ? s with { UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(batch) } : s).ToArray();
            await window.ReviewSessionCatalogAsync(moved); await MainWindow.SettleAsync();
            try { AssertCovered(window.ReviewSessionSurface); }
            catch
            {
                TestContext.WriteLine("After wheel and reorder: " + window.ReviewPagingState);
                TestContext.AddResultFile(await window.CaptureReviewAsync("injected-session-blank-reproduction"));
                throw;
            }
        }
        TestContext.AddResultFile(await window.CaptureReviewAsync("injected-session-reorder-no-gaps"));
    }
    internal static void AssertCovered(RepeaterList list, double spacing = 8)
    {
        var visible = list.RealizedElements.Select(e => (Element: e, Y: e.TransformToVisual(list.Scroll).TransformPoint(new(0, 0)).Y))
            .Where(p => p.Y + p.Element.ActualHeight > 0 && p.Y < list.Scroll.ViewportHeight).OrderBy(p => p.Y).ToArray();
        Assert.IsTrue(visible.Length > 0, "A nonempty viewport must have visible containers");
        double end = 0;
        foreach (var (element, y) in visible)
        {
            Assert.IsLessThanOrEqualTo(spacing, y - end, "Blank viewport interval before index " + element.Index);
            Assert.IsNotNull(element.Content, "A visible recycled container must have content");
            if (element.Content is SessionRow row)
                Assert.IsTrue(Descendants(element).OfType<TextBlock>().Any(t => t.Text == row.Title), "Visible title binding must match index " + element.Index);
            end = Math.Max(end, y + element.ActualHeight);
        }
        Assert.IsLessThanOrEqualTo(Math.Max(16d, spacing), Math.Min(list.Scroll.ViewportHeight, list.Scroll.ExtentHeight - list.Scroll.VerticalOffset) - end, "Unrealized blank tail in viewport");
    }
    [UITestMethod]
    public async Task AShortSidebarReturnsToTheTopAfterADeeplyScrolledCatalogIsReplaced()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        await window.ReviewSessionCatalogAsync(Sessions(1000));
        var input = new InjectedInput(window); await input.ActivateAsync();
        await input.WheelAsync(window.ReviewSessionSurface, -1920, 35); await MainWindow.SettleAsync();
        await window.ReviewSessionCatalogAsync(Sessions(2)); await MainWindow.SettleAsync();
        TestContext.AddResultFile(await window.CaptureReviewAsync("injected-short-sidebar"));
        AssertCovered(window.ReviewSessionSurface);
        var first = window.ReviewSessionSurface.ContainerFromIndex(0)!;
        var y = first.TransformToVisual(window.ReviewSessionSurface.Scroll).TransformPoint(new(0,0)).Y;
        Assert.AreEqual(0d, y, 4d, "A short catalog must start at the top rather than retain a blank prefix");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var descendant in Descendants(child)) yield return descendant; }
    }
}
