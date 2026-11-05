using CodexMonitor.App;
using CodexMonitor.Core;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.VisualStudio.TestTools.UnitTesting.AppContainer;

namespace CodexMonitor.UiTests;

public sealed partial class InjectedScrollUiTests
{
    [UITestMethod]
    public async Task NestedOutputWheelChainsAtItsBoundaryAndStreamingDoesNotStealTheReader()
    {
        var window = TestApp.ReviewWindow; await window.SetReviewAsync(1180, 900, "Dark", "zh-CN");
        var output = string.Join("\n", Enumerable.Range(0, 300).Select(i => "Output line " + i));
        var entries = Enumerable.Range(0, 200).Select(i => new TranscriptEntry("nested-" + i, EntryKind.Tool, "", default,
            Tool: new("shell", "echo " + i, i == 0 ? output : "done", ToolActivityState.Running))).ToArray();
        await window.ShowConversationPresentationReviewAsync(entries, false);
        var input = new InjectedInput(window); await input.ActivateAsync();
        var toggle = window.ReviewVisualElements<ActivityDisclosure>().Single(d => AutomationProperties.GetAutomationId(d) == "ActivityToggle-nested-0");
        await input.ClickAsync(toggle); await MainWindow.SettleAsync();
        ScrollViewer Output() => window.ReviewVisualElements<ScrollViewer>().First(s => AutomationProperties.GetName(s) == new LocalizationService("zh-CN")["ToolOutput"]);
        var scroll = Output(); var group = window.ReviewVisualElements<ActivityGroupView>().Single();
        await input.WheelAsync(scroll, -120, 8, y: .3); await MainWindow.SettleAsync();
        Assert.IsTrue(scroll.VerticalOffset > 20, "Wheel input over expanded output must scroll that output");
        var before = scroll.VerticalOffset; var outer = window.ReviewTranscriptSurface.Scroll.VerticalOffset;
        await window.UpdateConversationPresentationReviewAsync(entries.Select(e => e.Id == "nested-0" ? e with { Tool = e.Tool! with { Output = output + "\nNew output" } } : e)
            .Append(new("new-reply", EntryKind.Assistant, "A new reply arrived while the output was being read.", default)).ToArray());
        Assert.AreEqual(before, Output().VerticalOffset, 2d, "Streaming and a new reply must preserve the nested reading position");
        Assert.AreEqual(outer, window.ReviewTranscriptSurface.Scroll.VerticalOffset, 2d);
        await input.WheelAsync(Output(), 1920, 30, y: .3); await MainWindow.SettleAsync();
        Assert.AreEqual(0, Output().VerticalOffset, 2d);
        await input.WheelAsync(Output(), -1920, 60, y: .3); await MainWindow.SettleAsync();
        TestContext.AddResultFile(await window.CaptureReviewAsync("injected-nested-output-chain"));
        Assert.IsTrue(group.List.Scroll.VerticalOffset > 20 || window.ReviewTranscriptSurface.Scroll.VerticalOffset > 20,
            "At the output boundary, sustained wheel input must chain to a parent instead of being trapped");
    }
}
