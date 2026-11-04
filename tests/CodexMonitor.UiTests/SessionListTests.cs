using System.Collections.Specialized;
using System.Text.Json;
using CodexMonitor.Core;
using CodexMonitor.App;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass]
[DoNotParallelize]
public sealed class SessionListTests
{
    public TestContext TestContext { get; set; } = null!;
    [UITestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ImportingTwoHundredServerSessionsKeepsTheOriginalVisibleRows(bool immediateStatusUpdate)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Light", "zh-CN");
        var sessions = Enumerable.Range(0, 50).Select(i => new SessionInfo { Id = "local" + i, Title = "Local session " + i, State = SessionState.Completed }).ToArray();
        window.ApplySessionReviewSnapshot(sessions); window.ReadReviewSession("local0");
        var before = await window.SessionAnchorReviewAsync("local0", true);
        var imported = Enumerable.Range(0, 200).Select(i => new SessionInfo { Id = "server" + i, Title = "Server session " + i, State = SessionState.Idle });
        var merged = imported.Concat(sessions).ToArray();
        window.ApplySessionReviewSnapshot(merged);
        if (immediateStatusUpdate) window.ApplySessionReviewSnapshot(merged);
        await MainWindow.SettleAsync();
        var after = await window.SessionAnchorReviewAsync("local0"); Assert.AreEqual(before, after, 2d);
        TestContext.AddResultFile(await window.CaptureReviewAsync("bulk-server-session-anchor-" + immediateStatusUpdate));
    }

    [UITestMethod]
    public async Task TranscriptUpdatesPreserveRowsSelectionOrderAndCollectionWithoutAnimations()
    {
        var window = TestApp.ReviewWindow;
        await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        var rows = window.ReviewSessionRows.ToArray(); var source = window.ReviewSessionSource; var selected = window.ReviewSelectedRow;
        var changes = new List<NotifyCollectionChangedAction>();
        NotifyCollectionChangedEventHandler onChange = (_, e) => changes.Add(e.Action);
        window.ReviewSessionRows.CollectionChanged += onChange;
        try
        {
            for (int i = 1; i <= 20; i++)
            {
                var updated = rows[1].Session with { UpdatedAt = rows[1].Session.UpdatedAt.AddSeconds(1), FileVersion = new(200 + i, DateTime.UtcNow) };
                window.ApplySessionReviewSnapshot(updated, rows[0].Session);
            }
            Assert.AreSame(source, window.ReviewSessionSource);
            Assert.AreSame(selected, window.ReviewSelectedRow);
            Assert.AreSame(rows[1], window.ReviewSessionRows[0]); Assert.AreSame(rows[0], window.ReviewSessionRows[1]);
            CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Move }, changes, "A real content update moves the existing row once, without Reset or duplicate insertion");
            Assert.AreEqual(0, window.ReviewListTransitions);
            Assert.IsTrue(rows[1].HasUnread); Assert.IsFalse(rows[0].HasUnread);
            StringAssert.Contains(rows[1].AccessibleName, new LocalizationService("zh-CN")["SessionUpdated"]);
            window.ReadReviewSession("child");
            Assert.IsFalse(rows[1].HasUnread, "Opening a session clears its activity dot");
            window.ApplySessionReviewSnapshot(rows[1].Session with { FileVersion = new(999, DateTime.UtcNow) }, rows[0].Session);
            Assert.IsFalse(rows[1].HasUnread, "The session being read does not accumulate unread activity");
        }
        finally { window.ReviewSessionRows.CollectionChanged -= onChange; }
    }

    [UITestMethod]
    public async Task AdditionsAndDeletionsStayLocalWithoutReanimatingExistingRows()
    {
        var window = TestApp.ReviewWindow;
        await window.SetReviewAsync(1180, 800, "Light", "zh-CN");
        var original = window.ReviewSessionRows.ToArray(); var source = window.ReviewSessionSource;
        var changes = new List<NotifyCollectionChangedAction>();
        NotifyCollectionChangedEventHandler onChange = (_, e) => changes.Add(e.Action);
        window.ReviewSessionRows.CollectionChanged += onChange;
        try
        {
            var added = new SessionInfo { Id = "new", Title = "New session", UpdatedAt = DateTimeOffset.Now };
            window.ApplySessionReviewSnapshot(added, original[1].Session, original[0].Session);
            CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Add }, changes);
            Assert.AreEqual(0, window.ReviewListTransitions);
            Assert.IsTrue(window.ReviewSessionRows[0].HasUnread);
            Assert.AreEqual("new", window.ReviewSessionRows[0].Session.Id);
            Assert.AreSame(original[0], window.ReviewSessionRows[1]); Assert.AreSame(original[1], window.ReviewSessionRows[2]);
            changes.Clear();
            window.ApplySessionReviewSnapshot(added, original[0].Session);
            CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Remove }, changes);
            Assert.AreEqual(0, window.ReviewListTransitions);
            Assert.AreSame(source, window.ReviewSessionSource); Assert.AreSame(original[0], window.ReviewSessionRows[1]);
        }
        finally { window.ReviewSessionRows.CollectionChanged -= onChange; }
    }

    [UITestMethod]
    public async Task NewSessionsPreserveTheVisibleAnchorAndSelectionInAScrolledList()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        var sessions = Enumerable.Range(0, 40).Select(i => new SessionInfo { Id = "row" + i, Title = "Session " + i, State = SessionState.Completed }).ToArray();
        window.ApplySessionReviewSnapshot(sessions); window.ReadReviewSession("row18");
        var selected = window.ReviewSelectedRow; var before = await window.SessionAnchorReviewAsync("row18", true);
        window.ApplySessionReviewSnapshot([new SessionInfo { Id = "added", Title = "New session" }, .. sessions]);
        await MainWindow.SettleAsync(); var after = await window.SessionAnchorReviewAsync("row18");
        Assert.AreSame(selected, window.ReviewSelectedRow); Assert.AreEqual(before, after, 2d, "A new conversation must not move the row being read");
        Assert.AreEqual(0, window.ReviewListTransitions);
        TestContext.AddResultFile(await window.CaptureReviewAsync("session-new-anchor-dark"));
    }
    [UITestMethod]
    public async Task TranscriptAndNestedActivityListsKeepTheirReadingPositionsAcrossUpdates()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        var before = await window.TranscriptAnchorReviewAsync(false); var after = await window.TranscriptAnchorReviewAsync(true);
        Assert.AreEqual(before, after, 2d, "An earlier message update must not move the transcript being read");
        await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        var activityBefore = await window.ActivityScrollReviewAsync(false); var activityAfter = await window.ActivityScrollReviewAsync(true);
        Assert.IsGreaterThan(0d, activityBefore); Assert.AreEqual(activityBefore, activityAfter, 2d, "Streaming activity must keep its internal scroll position");
    }
    [UITestMethod]
    public async Task NewRepliesDoNotStealAnActivityReaderAndTheEndStillFollowsNewCalls()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        var reading = await window.NewReplyWhileReadingActivityReviewAsync();
        Assert.IsTrue(reading.Paused); Assert.IsTrue(reading.SameList); Assert.AreEqual(reading.Before, reading.After, 2d);
        await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        Assert.IsTrue(await window.ActivityAtEndFollowsNewItemsReviewAsync(), "A reader already at the end should see appended calls naturally");
    }
    [UITestMethod]
    [DataRow(1180, 800)]
    [DataRow(760, 650)]
    [DataRow(560, 700)]
    public async Task ExpandedOutputKeepsScrollAndKeyboardFocusWhenItsContentStreams(int width, int height)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(width, height, "Dark", "zh-CN");
        var result = await window.ExpandedOutputUpdateReviewAsync();
        Assert.IsGreaterThan(0d, result.Before); Assert.AreEqual(result.Before, result.After, 2d);
        Assert.IsTrue(result.Expanded); Assert.IsTrue(result.Focused);
        Assert.IsTrue(window.ReviewVisualElements<Microsoft.UI.Xaml.Controls.ScrollViewer>().All(s => s.IsVerticalScrollChainingEnabled));
        TestContext.AddResultFile(await window.CaptureReviewAsync($"streaming-output-reading-position-{width}"));
    }
    [UITestMethod]
    [DataRow(SessionState.Active, true, false, true)]
    [DataRow(SessionState.Waiting, true, false, true)]
    [DataRow(SessionState.RecordedActive, false, false, false)]
    [DataRow(SessionState.Idle, true, false, false)]
    [DataRow(SessionState.Active, true, true, false)]
    public async Task RunningIndicatorRequiresConnectedServerConfirmedActiveSession(SessionState state, bool live, bool archived, bool expected)
    {
        var window = TestApp.ReviewWindow;
        await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        var rows = window.ReviewSessionRows.ToArray();
        var update = rows[1].Session with { State = state, IsLive = live, IsArchived = archived };
        window.ApplySessionReviewSnapshot(rows[0].Session, update);
        Assert.IsFalse(rows[1].IsRunning);
        await window.ConnectReviewServerAsync();
        Assert.AreEqual(expected, rows[1].IsRunning); Assert.IsFalse(rows[1].HasUnread, "A runtime status change alone is not unread content");
        await window.DisconnectReviewServerAsync();
        Assert.IsFalse(rows[1].IsRunning, "Disconnect must immediately remove confirmed running badges");
    }

    [UITestMethod]
    public async Task NonSelectedRuntimeNotificationsUpdateRunningStateAndActivityDotWithoutRebuildingList()
    {
        var window = TestApp.ReviewWindow;
        await window.SetReviewAsync(1180, 800, "Dark", "zh-CN"); await window.ConnectReviewServerAsync();
        var rows = window.ReviewSessionRows.ToArray(); var source = window.ReviewSessionSource;
        using var started = JsonDocument.Parse("""{"threadId":"child","turn":{"id":"t1"}}""");
        window.NotifyReviewSession(new("turn/started", started.RootElement));
        Assert.IsTrue(rows[1].IsRunning); Assert.IsFalse(rows[1].HasUnread);
        using var delta = JsonDocument.Parse("""{"threadId":"child","itemId":"a1","delta":"Hello"}""");
        window.NotifyReviewSession(new("item/agentMessage/delta", delta.RootElement));
        Assert.IsTrue(rows[1].HasUnread); Assert.AreSame(source, window.ReviewSessionSource);
        TestContext.AddResultFile(await window.CaptureReviewAsync("session-activity-dark-zh"));
        await window.SwitchReviewThemeAsync("Light");
        TestContext.AddResultFile(await window.CaptureReviewAsync("session-activity-light-zh"));
        using var completed = JsonDocument.Parse("""{"threadId":"child","turn":{"id":"t1","status":"completed"}}""");
        window.NotifyReviewSession(new("turn/completed", completed.RootElement));
        Assert.IsFalse(rows[1].IsRunning); Assert.IsTrue(rows[1].HasUnread, "Unread activity remains after completion");
    }
}
