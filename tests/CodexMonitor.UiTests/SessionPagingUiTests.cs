using System.Collections.Specialized;
using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass, DoNotParallelize]
public sealed class SessionPagingUiTests
{
    public TestContext TestContext { get; set; } = null!;
    private static SessionInfo[] Sessions(int count) => Enumerable.Range(0, count).Select(i => new SessionInfo
    { Id = $"catalog-{i:D6}", Title = $"Conversation {i}", State = SessionState.Completed, CreatedAt = DateTimeOffset.UtcNow.AddDays(-i), UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-i) }).ToArray();
    [UITestMethod]
    public async Task TenThousandConversationsCreateFiftyRowsAndFastScrollAutomaticallyLoadsDistinctPages()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        await window.ReviewSessionCatalogAsync(Sessions(10000)); Assert.HasCount(50, window.ReviewSessionRows); Assert.IsLessThanOrEqualTo(51, window.ReviewSessionRowModels);
        var beats = 0; var timer = window.DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(16); timer.Tick += (_, _) => beats++; timer.Start();
        try
        {
            for (int i = 0; i < 20; i++) await window.ReviewScrollSessionsEndAsync();
            await window.ReviewWaitSessionQueryAsync(); Assert.IsGreaterThan(50, window.ReviewSessionRows.Count); Assert.IsLessThan(1200, window.ReviewSessionRows.Count);
            Assert.AreEqual(window.ReviewSessionRows.Count, window.ReviewSessionRows.Select(r => r.Session.Id).Distinct().Count());
            Assert.IsGreaterThan(5, beats, "The native dispatcher must keep processing input while pages load");
            Assert.IsTrue(window.ReviewSessionRows.Select(r => r.Session.UpdatedAt).SequenceEqual(window.ReviewSessionRows.Select(r => r.Session.UpdatedAt).OrderDescending()));
        }
        finally { timer.Stop(); }
        TestContext.AddResultFile(await window.CaptureReviewAsync("session-paging-10000-dark"));
    }
    [UITestMethod]
    public async Task ImportingHistoricalServerPagesPreservesTheVisibleRowWithoutUnreadDotsOrAutomaticDrain()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Light", "zh-CN");
        var local = Sessions(150); await window.ReviewSessionCatalogAsync(local); window.ReadReviewSession(local[0].Id);
        var before = await window.SessionAnchorReviewAsync(local[0].Id, true); var original = window.ReviewSessionRows[0];
        var remote = Sessions(10000).Select((s,i) => s with { Id = "remote-" + i, UpdatedAt = DateTimeOffset.UtcNow.AddDays(1).AddMinutes(-i) }).ToArray();
        var calls = 0;
        await window.ReviewSessionCatalogAsync(local, (a, c, _) =>
        { calls++; var n = c is null ? 0 : int.Parse(c); return Task.FromResult(new SessionPage(a ? [] : remote.Skip(n).Take(50).ToArray(), a || n >= 9950 ? null : (n + 50).ToString())); });
        await Task.Delay(350);
        Assert.AreEqual(before, await window.SessionAnchorReviewAsync(local[0].Id), 2); Assert.IsTrue(window.ReviewSessionRows.Contains(original));
        Assert.IsFalse(window.ReviewSessionRows.Any(r => r.HasUnread)); Assert.IsLessThanOrEqualTo(3, calls, "Restoring an anchor at the bottom must not trigger a pagination loop");
        Assert.IsLessThan(100, window.ReviewSessionRows.Count);
        TestContext.AddResultFile(await window.CaptureReviewAsync("server-import-anchor-light"));
    }
    [UITestMethod]
    public async Task RapidSearchCancelsASlowSourceAndOldResultsCannotOverwriteTheNewQuery()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 700, "Dark", "zh-CN");
        var late = new TaskCompletionSource<SessionPage>(TaskCreationOptions.RunContinuationsAsynchronously); var calls = 0;
        var initializing = window.ReviewSessionCatalogAsync([], (a,c,ct) => Interlocked.Increment(ref calls) == 1 ? late.Task : Task.FromResult(new SessionPage(a ? [] : Sessions(100), null)));
        for (int i = 0; i < 100 && calls == 0; i++) await Task.Delay(5);
        await window.ReviewSessionQueryAsync("Conversation 42"); await initializing;
        Assert.HasCount(1, window.ReviewSessionRows); Assert.AreEqual("Conversation 42", window.ReviewSessionRows[0].Title);
        late.TrySetResult(new([new() { Id = "stale", Title = "Old query" }], null)); await MainWindow.SettleAsync();
        Assert.HasCount(1, window.ReviewSessionRows); Assert.AreEqual("Conversation 42", window.ReviewSessionRows[0].Title); Assert.IsFalse(window.ReviewSessionsLoading);
    }
    [UITestMethod]
    [DataRow("Light", "zh-CN")]
    [DataRow("Dark", "de-DE")]
    [DataRow("Dark", "ar-SA")]
    public async Task DateSearchCombinesWithTextAndTheCustomDateFlyoutFitsNarrowWindows(string theme, string language)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 700, theme, language, true);
        var local = Sessions(300); await window.ReviewSessionCatalogAsync(local);
        var date = DateOnly.FromDateTime(local[42].CreatedAt.ToLocalTime().DateTime);
        await window.ReviewSessionQueryAsync("Conversation 42", SessionDateField.Created, date, date); Assert.HasCount(1, window.ReviewSessionRows);
        var panel = await window.ReviewOpenDateFilterAsync();
        var fields = Descendants(panel).OfType<CalendarDatePicker>().ToArray(); Assert.HasCount(2, fields);
        fields[0].Date = DateTimeOffset.Now; fields[1].Date = DateTimeOffset.Now.AddDays(-1);
        var apply = Descendants(panel).OfType<Button>().Single(b => AutomationProperties.GetAutomationId(b) == "ApplySessionDates"); Assert.IsFalse(apply.IsEnabled);
        fields[1].Date = DateTimeOffset.Now; Assert.IsTrue(apply.IsEnabled);
        Assert.IsTrue(panel.ActualWidth >= 240 && panel.ActualHeight > 100, $"The native date panel must actually be laid out: {panel.ActualWidth} x {panel.ActualHeight}");
        var bounds = panel.TransformToVisual(window.ReviewRoot).TransformBounds(new(0,0,panel.ActualWidth,panel.ActualHeight)); Assert.IsTrue(bounds.Left >= -1 && bounds.Right <= window.ReviewRoot.ActualWidth + 1);
        window.ReviewCloseDateFilter(); TestContext.AddResultFile(await window.CaptureReviewAsync($"session-date-results-{theme}-{language}"));
        await window.ReviewSessionQueryAsync("no-match"); Assert.HasCount(0, window.ReviewSessionRows);
        await window.ReviewSessionQueryAsync(""); Assert.HasCount(50, window.ReviewSessionRows); window.ReviewCloseDateFilter();
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    { yield return root; for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) foreach(var child in Descendants(VisualTreeHelper.GetChild(root,i))) yield return child; }
}
