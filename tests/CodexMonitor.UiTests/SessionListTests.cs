using System.Collections.Specialized;
using System.Text.Json;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass]
[DoNotParallelize]
public sealed class SessionListTests
{
    public TestContext TestContext { get; set; } = null!;

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
            Assert.AreSame(rows[0], window.ReviewSessionRows[0]); Assert.AreSame(rows[1], window.ReviewSessionRows[1]);
            Assert.HasCount(0, changes, "Ordinary transcript updates must not emit collection changes or Reset");
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
    public async Task OnlyActualSessionAdditionsAndDeletionsAnimateAndNeverResetExistingRows()
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
            Assert.IsGreaterThan(0, window.ReviewListTransitions);
            Assert.AreEqual("new", window.ReviewSessionRows[0].Session.Id);
            Assert.AreSame(original[0], window.ReviewSessionRows[1]); Assert.AreSame(original[1], window.ReviewSessionRows[2]);
            changes.Clear();
            window.ApplySessionReviewSnapshot(added, original[0].Session);
            CollectionAssert.AreEqual(new[] { NotifyCollectionChangedAction.Remove }, changes);
            Assert.IsGreaterThan(0, window.ReviewListTransitions);
            Assert.AreSame(source, window.ReviewSessionSource); Assert.AreSame(original[0], window.ReviewSessionRows[1]);
        }
        finally { window.ReviewSessionRows.CollectionChanged -= onChange; }
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
