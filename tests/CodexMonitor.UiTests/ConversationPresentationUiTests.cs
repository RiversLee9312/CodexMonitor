using System.Text.Json;
using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

[TestClass, DoNotParallelize]
public sealed class ConversationPresentationUiTests
{
    public TestContext TestContext { get; set; } = null!;
    private static TranscriptEntry[] Entries(bool pending) =>
    [
        new("reply-a", EntryKind.Assistant, "First response", default, TurnId: "old", Model: "model-a"),
        new("summary", EntryKind.Reasoning, "Checking the result.", default, Reasoning: new("Checking the result.", ""), TurnId: "turn"),
        new("command", EntryKind.Tool, "", default, Tool: ToolActivity.FromCall("exec_command", "{\"cmd\":\"dotnet build\"}", "Build succeeded", ToolActivityState.Completed), TurnId: "turn"),
        new("pending", EntryKind.Tool, "", default, Tool: pending ? new("hidden", "DO_NOT_RENDER_PARTIAL", State: ToolActivityState.Generating, CategoryOverride: "Working") : ToolActivity.FromCall("clock.sleep", "{\"duration_ms\":30000}", "Done", ToolActivityState.Completed), TurnId: "turn"),
        new("reply-b", EntryKind.Assistant, "Second response", default, TurnId: "turn", Model: "model-b-long-model-identifier-for-narrow-window-layout")
    ];
    [UITestMethod]
    [DataRow("Light", "zh-CN")]
    [DataRow("Dark", "de-DE")]
    [DataRow("Dark", "ar-SA")]
    public async Task ModelLabelsAndCollapsibleGroupsKeepTheirStateDuringGeneration(string theme, string language)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 700, theme, language);
        await window.ShowConversationPresentationReviewAsync(Entries(true), true);
        var text = new LocalizationService(language);
        Assert.IsTrue(window.ReviewVisualElements<TextBlock>().Any(t => t.Text == "model-a"));
        Assert.IsTrue(window.ReviewVisualElements<TextBlock>().Any(t => t.Text.StartsWith("model-b-long", StringComparison.Ordinal)));
        var group = window.ReviewVisualElements<ActivityDisclosure>().Single(t => AutomationProperties.GetAutomationId(t) == "ActivityGroup-summary");
        Assert.IsTrue(group.IsExpanded); Assert.IsTrue(window.ReviewVisualElements<FrameworkElement>().Any(e => AutomationProperties.GetAutomationId(e) == "Working-pending"));
        Assert.IsFalse(window.ReviewVisualElements<TextBlock>().Any(t => t.Text.Contains("DO_NOT_RENDER_PARTIAL", StringComparison.Ordinal)));
        group.SetExpanded(false); await MainWindow.SettleAsync();
        var list = window.ReviewVisualElements<RepeaterList>().Single(l => l.ItemsSource is System.Collections.ObjectModel.ObservableCollection<EntryRow> rows && rows.All(r => r.Entry.Kind != EntryKind.Assistant) && rows.Any(r => r.Entry.Id == "summary"));
        Assert.AreEqual(Visibility.Collapsed, list.Visibility);
        Assert.IsTrue(window.ReviewVisualElements<ProgressRing>().Any(r => r.IsActive && r.Visibility == Visibility.Visible));
        TestContext.AddResultFile(await window.CaptureReviewAsync($"group-collapsed-{theme}-{language}"));
        await window.UpdateConversationPresentationReviewAsync(Entries(false));
        Assert.AreSame(group, window.ReviewVisualElements<ActivityDisclosure>().Single(t => AutomationProperties.GetAutomationId(t) == "ActivityGroup-summary"));
        Assert.IsFalse(group.IsExpanded); Assert.AreEqual(Visibility.Collapsed, list.Visibility);
        Assert.IsFalse(window.ReviewVisualElements<ProgressRing>().Any(r => r.IsActive && r.Visibility == Visibility.Visible));
        group.SetExpanded(true); await MainWindow.SettleAsync(); Assert.AreEqual(Visibility.Visible, list.Visibility);
        foreach (var (_, bounds) in window.ReviewBounds()) Assert.IsTrue(bounds.Right <= window.ReviewRoot.ActualWidth + 1);
        TestContext.AddResultFile(await window.CaptureReviewAsync($"model-group-expanded-{theme}-{language}"));
    }
    [UITestMethod]
    [DataRow("Light", "zh-CN")]
    [DataRow("Dark", "ar-SA")]
    public async Task MissingRuntimeTurnOffersAnExplainedReconnectInsteadOfADisabledComposer(string theme, string language)
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(560, 650, theme, language); await window.ConnectReviewServerAsync();
        window.SetSessionStateReview(SessionState.Active, true); await MainWindow.SettleAsync();
        var text = new LocalizationService(language);
        Assert.IsFalse(window.ReviewComposerVisible); Assert.IsTrue(window.ReviewConnectionButtonVisible); Assert.IsTrue(window.ReviewConnectionHelpVisible); Assert.IsFalse(window.ReviewResumeButtonVisible);
        Assert.AreEqual(text["ReconnectSession"], window.ReviewSessionConnectionLabel); Assert.AreEqual(text["ReconnectSessionExplanation"], window.ReviewConnectionExplanation);
        Assert.IsFalse(window.ReviewBannerOpen);
        TestContext.AddResultFile(await window.CaptureReviewAsync($"runtime-reconnect-{theme}-{language}"));
        using var ready = JsonDocument.Parse("""{"threadId":"review-session","turn":{"id":"available","status":"inProgress"}}""");
        window.NotifyTranscriptReview(new("turn/started", ready.RootElement.Clone())); await MainWindow.SettleAsync();
        Assert.IsTrue(window.ReviewComposerVisible); Assert.IsFalse(window.ReviewConnectionButtonVisible);
    }
}
