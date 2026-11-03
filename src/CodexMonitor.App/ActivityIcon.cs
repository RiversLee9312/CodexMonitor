using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CodexMonitor.App;

internal static class ActivityIcon
{
    internal static readonly IReadOnlyDictionary<string, string> Generated = new Dictionary<string, string>
    {
        ["Thinking"] = "Thinking", ["ToolMcp"] = "McpTool", ["EventMcpStatus"] = "McpTool", ["ToolDynamic"] = "CustomTool", ["ToolCollaboration"] = "AgentCollaboration",
        ["ToolCompaction"] = "ContextCompaction", ["EventHook"] = "LifecycleHook", ["ToolImageGeneration"] = "ImageGeneration",
        ["ToolFindInPage"] = "FindInPage", ["EventUsage"] = "TokenUsage", ["EventLimits"] = "TokenUsage"
    };
    internal static IconElement Create(string category, ToolActivityState state, LocalizationService text)
    {
        IconElement icon;
        if (state != ToolActivityState.Failed && Generated.TryGetValue(category, out var asset)) icon = Bitmap(asset);
        else icon = new FontIcon { IsTextScaleFactorEnabled = false, FontSize = 16, Glyph = state == ToolActivityState.Failed ? "\uE783" : category switch
        {
            "ToolFileChanges" => "\uE70F", "ToolSearch" => "\uE721", "ToolCommand" or "ToolReadOutput" => "\uE756",
            "ToolReadFile" => "\uE8A5", "ToolListFiles" => "\uE8B7", "ToolOpenPage" => "\uE774", "ToolImageView" => "\uEB9F",
            "ToolPlan" => "\uE9D5", "ToolReview" => "\uE73E", "ToolWaiting" or "EventBuffering" => "\uE917",
            "EventModelChanged" => "\uE8AB", "EventWarning" or "EventVerification" => "\uE7BA", "EventError" => "\uE783",
            "EventRetrying" => "\uE72C", "EventInterrupted" => "\uE71A", _ => "\uE8A5"
        } };
        icon.Width = icon.Height = 16; icon.VerticalAlignment = VerticalAlignment.Center;
        icon.SetResourceReference(IconElement.ForegroundProperty, state == ToolActivityState.Failed ? "SystemFillColorCriticalBrush" :
            category is "EventWarning" or "EventVerification" ? "SystemFillColorCautionBrush" : state == ToolActivityState.Running ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        AutomationProperties.SetName(icon, text[state switch { ToolActivityState.Failed => "ToolFailed", ToolActivityState.Completed => "ToolCompleted", ToolActivityState.Running => "ToolRunning", _ => "Tool" }]);
        return icon;
    }
    internal static BitmapIcon Bitmap(string name) => new()
    { UriSource = new Uri("ms-appx:///Assets/Icons/" + name + ".png"), ShowAsMonochrome = true, Width = 16, Height = 16 };
}
