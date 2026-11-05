using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass, DoNotParallelize]
public sealed class WorkProcessUiTests
{
    [UITestMethod]
    public async Task RecycledCardsDoNotReuseWorkViewportsFromADifferentRowOwner()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        var entries = Enumerable.Range(0, 30).Select(i => new TranscriptEntry("owner-work-" + i, EntryKind.Tool, "echo " + i, default, Tool: new("shell", "echo " + i))).ToArray();
        var text = new LocalizationService("zh-CN");
        var card = new TranscriptCard { Row = EntryRow.Project(entries, text, [], canFollow: () => false).Single() };
        var root = (Grid)window.ReviewRoot;
        try
        {
            root.Children.Add(card); await MainWindow.SettleAsync();
            var previous = window.ReviewVisualElements<ActivityGroupView>().Single().List;
            card.Row = EntryRow.Project(entries, text, [], canFollow: () => true).Single(); await MainWindow.SettleAsync();
            var current = window.ReviewVisualElements<ActivityGroupView>().Single().List;
            for (int i = 0; i < 80 && current.Scroll.ScrollableHeight - current.Scroll.VerticalOffset >= 2; i++) await Task.Delay(25);
            Assert.AreNotSame(previous, current, "Identical item IDs do not make an old row's callbacks and scroll state valid for a new owner");
            Assert.IsLessThan(2d, current.Scroll.ScrollableHeight - current.Scroll.VerticalOffset);
        }
        finally { root.Children.Remove(card); }
    }
    [TestMethod]
    public void MatchedRowsRefreshNavigationOwnershipWithoutRebuildingTheirPresentation()
    {
        var text = new LocalizationService("zh-CN"); var entry = new TranscriptEntry("same-content", EntryKind.Assistant, "Stable response", default);
        var old = new EntryRow(entry, text, canFollow: () => false); int changed = 0, navigated = 0;
        old.PropertyChanged += (_, _) => changed++;
        var rows = new System.Collections.ObjectModel.ObservableCollection<EntryRow> { old };
        EntryRows.Synchronize(rows, new[] { new EntryRow(entry, text, canFollow: () => true, historyChanged: _ => navigated++) });
        Assert.AreSame(old, rows.Single()); Assert.IsTrue(old.CanFollow!()); old.HistoryChanged!(true);
        Assert.AreEqual(1, navigated); Assert.AreEqual(0, changed);
    }
    [UITestMethod]
    public async Task OldSourceScrollCallbacksCannotChangeTheCurrentReadingMode()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 700, "Dark", "zh-CN");
        await window.ShowConversationPresentationReviewAsync(new[] { new TranscriptEntry("owned-work", EntryKind.Tool, "echo work", default, Tool: new("shell", "echo work")) }, false);
        var row = window.ReviewMessageRows.Single(); var stale = row.HistoryChanged!;
        await window.ReviewAdvanceTranscriptSourceAsync(); stale(true);
        Assert.IsTrue(window.ReviewFollowing); Assert.AreSame(row, window.ReviewMessageRows.Single()); Assert.IsTrue(row.CanFollow!());
    }
    [TestMethod]
    public void VariableHeightViewportIndexMatchesTheVisibleRowsAfterStreamingAndPrepending()
    {
        var random = new Random(7391);
        var values = Enumerable.Range(0, 1800).Select(_ => 40d + random.Next(250)).ToArray();
        var index = new ViewportStackLayout.HeightIndex(); index.Reset(values.ToArray());
        for (int update = 0; update < 250; update++)
        {
            var changed = random.Next(values.Length); values[changed] = 40 + random.Next(400); index.Set(changed, values[changed]);
            var offset = random.NextDouble() * values.Sum(); double sum = 0; int expected = 0;
            while (expected < values.Length - 1 && sum + values[expected] <= offset) sum += values[expected++];
            Assert.AreEqual(expected, index.At(offset)); Assert.AreEqual(sum, index.Before(expected), .001);
        }
        values = Enumerable.Repeat(40d, 300).Concat(values).ToArray(); index.Reset(values);
        Assert.AreEqual(values.Sum(), index.Total, .001); Assert.AreEqual(300, index.At(12000));
        Assert.AreEqual(values.Length - 1, index.At(index.Total));
    }
    public TestContext TestContext { get; set; } = null!;
    [UITestMethod, DataRow("Dark", "zh-CN"), DataRow("Light", "ar-SA")]
    public async Task AutomaticCollapseKeepsManualChoicesAndNeverFoldsOngoingWork(string theme, string language)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(760, 700, theme, language);
        TranscriptEntry Entry(string id, string turn, ToolActivityState state) => new(id, EntryKind.Tool, "", DateTimeOffset.Now,
            Tool: new("shell", "echo " + id, state == ToolActivityState.Completed ? "Done" : null, state), TurnId: turn);
        var entries = new[] { Entry("old-work", "old", ToolActivityState.Completed), new TranscriptEntry("reply", EntryKind.Assistant, "Finished", default, TurnId: "old"), Entry("current-work", "turn", ToolActivityState.Running) };
        await window.ShowConversationPresentationReviewAsync(entries, true); window.ReviewAutoCollapse(true); await MainWindow.SettleAsync();
        ActivityDisclosure Group(string id) => window.ReviewVisualElements<ActivityDisclosure>().Single(t => AutomationProperties.GetAutomationId(t) == "ActivityGroup-" + id);
        Assert.IsFalse(Group("old-work").IsExpanded); Assert.IsTrue(Group("current-work").IsExpanded);
        var manual = Group("old-work"); manual.SetExpanded(true);
        Group("current-work").SetExpanded(false); Assert.IsTrue(Group("current-work").IsExpanded);
        await window.UpdateConversationPresentationReviewAsync(entries.Select(e => e.Id == "current-work" ? e with { Tool = e.Tool! with { Output = "Done", State = ToolActivityState.Completed } } : e).Append(new("end", EntryKind.Assistant, "Finished", default, TurnId: "turn")).ToArray());
        Assert.AreSame(manual, Group("old-work")); Assert.IsTrue(manual.IsExpanded); Assert.IsFalse(Group("current-work").IsExpanded);
        manual.SetExpanded(false); window.ReviewAutoCollapse(false); window.ReviewAutoCollapse(true); await MainWindow.SettleAsync(); Assert.IsFalse(Group("old-work").IsExpanded);
        TestContext.AddResultFile(await window.CaptureReviewAsync($"automatic-work-process-{theme}-{language}"));
    }
    [UITestMethod]
    public async Task TurnCompletionWithoutNewContentUpdatesTheWorkProcessImmediately()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Dark", "zh-CN");
        await window.ShowConversationPresentationReviewAsync(new[] { new TranscriptEntry("live-work", EntryKind.Tool, "", default,
            Tool: new("shell", "echo work", State: ToolActivityState.Running), TurnId: "turn") }, true);
        window.ReviewAutoCollapse(true); await MainWindow.SettleAsync();
        var group = window.ReviewVisualElements<ActivityDisclosure>().Single(t => AutomationProperties.GetAutomationId(t) == "ActivityGroup-live-work");
        Assert.IsTrue(group.IsExpanded);
        using var message = System.Text.Json.JsonDocument.Parse("""{"threadId":"review-session","turn":{"id":"turn","status":"completed"}}""");
        window.NotifyTranscriptReview(new("turn/completed", message.RootElement.Clone())); await MainWindow.SettleAsync();
        Assert.IsFalse(group.IsExpanded); Assert.IsFalse(window.ReviewMessageRows[0].IsGroupBusy);
    }
    [UITestMethod]
    public async Task ThreeSurfacesUseVirtualizedRepeatersWithAccessibleSingleSelection()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 800, "Light", "zh-CN"); await window.ShowActivityReviewAsync();
        var lists = window.ReviewVisualElements<RepeaterList>(); Assert.HasCount(3, lists);
        Assert.IsFalse(window.ReviewVisualElements<ListView>().Any());
        foreach (var list in lists) { Assert.IsInstanceOfType<VirtualizingLayout>(list.Repeater.Layout); Assert.IsTrue(list.RealizedElements.Count() < 30); }
        var sessions = lists.Single(l => l.SelectionMode == ListViewSelectionMode.Single);
        Assert.IsTrue(window.ReviewVisualElements<TextBlock>().Any(t => t.Text == "CodexMonitor"), "Folder bindings must run outside ListView's phased binding mechanism");
        var peer = FrameworkElementAutomationPeer.CreatePeerForElement(sessions);
        Assert.AreEqual(AutomationControlType.List, peer.GetAutomationControlType());
        Assert.IsInstanceOfType<ISelectionProvider>(peer.GetPattern(PatternInterface.Selection));
        Assert.IsInstanceOfType<IScrollProvider>(peer.GetPattern(PatternInterface.Scroll));
        var item = sessions.RealizedElements.First(); var selection = (ISelectionItemProvider)FrameworkElementAutomationPeer.CreatePeerForElement(item).GetPattern(PatternInterface.SelectionItem);
        selection.Select(); Assert.AreSame(item.Content, sessions.SelectedItem); Assert.IsTrue(selection.IsSelected);
        Assert.IsGreaterThan(0, AutomationProperties.GetPositionInSet(item));
    }
}
