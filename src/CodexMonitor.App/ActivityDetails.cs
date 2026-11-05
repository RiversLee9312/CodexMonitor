using System.Text.Json;
using System.Text.RegularExpressions;
using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Input;

namespace CodexMonitor.App;

/// <summary>Native, selectable details. Known structured payloads stay readable without JSON punctuation.</summary>
internal static class ActivityDetails
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["path"] = "FieldPath", ["cwd"] = "FieldDirectory", ["query"] = "FieldQuery", ["queries"] = "FieldQuery",
        ["url"] = "FieldUrl", ["pattern"] = "FieldPattern", ["server"] = "FieldServer", ["serverName"] = "FieldServer",
        ["tool"] = "FieldTool", ["status"] = "FieldStatus", ["message"] = "FieldMessage", ["summary"] = "FieldMessage",
        ["reason"] = "FieldReason", ["explanation"] = "FieldReason", ["model"] = "FieldModel", ["fromModel"] = "FieldFromModel", ["toModel"] = "FieldToModel",
        ["receiverThreadId"] = "FieldAgent", ["newThreadId"] = "FieldAgent", ["senderThreadId"] = "FieldAgent", ["prompt"] = "FieldPrompt",
        ["exitCode"] = "FieldExitCode", ["name"] = "FieldName", ["text"] = "FieldText", ["review"] = "ToolReview",
        ["totalTokens"] = "FieldTotalTokens", ["inputTokens"] = "FieldInputTokens", ["outputTokens"] = "FieldOutputTokens", ["cachedInputTokens"] = "FieldCachedTokens",
        ["usedPercent"] = "FieldUsedPercent", ["resetsAt"] = "FieldResetsAt", ["arguments"] = "FieldArguments", ["result"] = "FieldResult",
        ["durationMs"] = "FieldDuration", ["duration_ms"] = "ToolWaitDurationLabel", ["command"] = "ToolCommand", ["cmd"] = "ToolCommand",
        ["error"] = "EventError", ["details"] = "ToolDetails", ["additionalDetails"] = "ToolDetails", ["output"] = "ToolOutput", ["delta"] = "ToolOutput",
        ["agentStatus"] = "FieldStatus", ["content"] = "ToolOutput", ["contentItems"] = "ToolOutput", ["total"] = "FieldTotalTokens", ["last"] = "Tokens",
        ["code"] = "FieldText", ["input"] = "FieldText",
        ["author"] = "FieldAuthor", ["recipient"] = "FieldRecipient", ["receiverThreadIds"] = "FieldRecipient", ["agentThreadId"] = "FieldAgent", ["agentPath"] = "FieldPath", ["savedPath"] = "FieldPath", ["revisedPrompt"] = "FieldPrompt", ["revised_prompt"] = "FieldPrompt", ["reasoningEffort"] = "Thinking",
        ["authMode"] = "FieldAuthMode", ["planType"] = "FieldPlan", ["count"] = "FieldCount", ["threadName"] = "FieldName", ["startedAtMs"] = "Created", ["completedAtMs"] = "Updated", ["stdin"] = "ToolStdin"
    };
    internal static FrameworkElement Render(ToolActivity tool, LocalizationService text, Action<bool>? historyChanged = null)
    {
        var panel = new StackPanel { Spacing = 14 };
        panel.Loaded += (_, _) => AccessibilitySupport.NormalizeIconsAfterLayout(panel);
        if (!string.IsNullOrWhiteSpace(tool.Progress)) Add(text["FieldMessage"], Prose(tool.Progress), tool.Progress);
        if (!string.IsNullOrEmpty(tool.StandardInput)) Add(text["ToolStdin"], Prose(tool.StandardInput, true), tool.StandardInput);
        if (tool.Program is { } parsed && (parsed.Calls.Count > 1 || !parsed.Complete))
        {
            var calls = tool.DisplayCalls ?? parsed.Calls.Select(call => ToolActivity.FromCall(call.Name, call.Input, state: ToolActivityState.Unknown)).ToArray();
            foreach (var child in calls)
            {
                var section = new StackPanel { Spacing = 6 };
                if (child.CategoryKey != "ToolCommand")
                { var title = Prose(text[child.CategoryKey] + " · " + child.Summary); title.FontSize = 12; section.Children.Add(title); }
                section.Children.Add(Render(child, text, historyChanged)); panel.Children.Add(section);
            }
            if (!parsed.Complete)
            {
                var original = new Expander { Header = text["RawData"], Content = Prose(tool.Input, true), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                AutomationProperties.SetName(original, text["RawData"]); AutomationProperties.SetHelpText(original, text["RawDataHelp"]); panel.Children.Add(original);
            }
        }
        else if (tool.CategoryKey == "ToolFileChanges") Add(text["ToolFileChanges"], Diff(tool.DecodedInput));
        else if (tool.CategoryKey == "ToolDetails" && TryJson(tool.Input, out var unknown)) panel.Children.Add(Original(unknown, text));
        else if (!string.IsNullOrWhiteSpace(tool.Input) && (tool.CategoryKey == "ToolCommand" || tool.Name == "commandExecution")) Add(text["ToolCommand"], Prose(tool.DecodedInput, true), tool.DecodedInput);
        else if (tool.CategoryKey == "ToolPlan" && TryJson(tool.Input, out var plan) && plan.Field("plan").ValueKind == JsonValueKind.Array)
        {
            if (plan.Str("explanation").Length > 0) panel.Children.Add(Prose(plan.Str("explanation")));
            foreach (var step in plan.Field("plan").Items())
            {
                var state = step.Str("status");
                var stateText = text[state switch { "completed" => "PlanCompleted", "inProgress" => "PlanInProgress", _ => "PlanPending" }];
                var row = new Grid { ColumnSpacing = 10 }; row.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); row.ColumnDefinitions.Add(new()); row.ColumnDefinitions.Add(new() { Width = GridLength.Auto, MaxWidth = 120 });
                row.Children.Add(new FontIcon { IsTextScaleFactorEnabled = false, Glyph = state == "completed" ? "\uE73E" : state == "inProgress" ? "\uE768" : "\uEA3A", FontSize = 16, VerticalAlignment = VerticalAlignment.Top, Margin = new(0, 3, 0, 0) });
                var label = Prose(step.Str("step")); Grid.SetColumn(label, 1); row.Children.Add(label);
                var status = Prose(stateText); status.FontSize = 12; status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); Grid.SetColumn(status, 2); row.Children.Add(status);
                AutomationProperties.SetName(row, stateText + ": " + step.Str("step")); ToolTipService.SetToolTip(row, stateText); panel.Children.Add(row);
            }
        }
        else if (!string.IsNullOrWhiteSpace(tool.Input)) Add(text[tool.CategoryKey is "ToolCommand" or "ToolReadFile" or "ToolListFiles" ? "ToolCommand" : tool.CategoryKey is "ToolPlan" or "ToolReview" ? tool.CategoryKey : "ToolParameters"], Value(tool.DisplayInput, text, tool.CategoryKey is "ToolCommand" or "ToolImageView"), tool.DisplayInput);
        if ((tool.DisplayOutput ?? tool.Output) is { Length: > 0 } output) Add(text["ToolOutput"], Value(output, text, tool.CategoryKey is "ToolCommand" or "ToolReadOutput"), output);
        if (panel.Children.Count == 0) panel.Children.Add(Prose(text["NoDetails"]));
        return panel;
        void Add(string heading, FrameworkElement body, string? copied = null)
        {
            var section = new StackPanel { Spacing = 7 };
            var title = new Grid(); title.ColumnDefinitions.Add(new()); title.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            var caption = Prose(heading); caption.FontSize = 12; caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); title.Children.Add(caption);
            var copy = new Button { Content = new FontIcon { IsTextScaleFactorEnabled = false, Glyph = "\uE8C8", FontSize = 16 }, Style = (Style)Application.Current.Resources["IconButtonStyle"], MinHeight = 40, MinWidth = 40, Padding = new(6) };
            AutomationProperties.SetName(copy, text["Copy"] + " · " + heading); ToolTipService.SetToolTip(copy, text["Copy"]);
            copy.Click += (_, _) => { var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(copied ?? (heading == text["ToolOutput"] ? tool.Output ?? "" : tool.Input)); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); };
            Grid.SetColumn(copy, 1); title.Children.Add(copy); section.Children.Add(title);
            var scroll = new ScrollViewer { Content = body, MaxHeight = 320, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, IsVerticalScrollChainingEnabled = true, UseSystemFocusVisuals = true };
            AutomationProperties.SetName(scroll, heading);
            ScrollAnchor.EnableWheelChaining(scroll);
            var userScrolling = false;
            scroll.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => userScrolling = true), true);
            scroll.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => userScrolling = true), true);
            scroll.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => userScrolling = true), true);
            scroll.ViewChanged += (_, args) =>
            { scroll.IsTabStop = scroll.ScrollableHeight > 0; if (userScrolling && !args.IsIntermediate && scroll.ScrollableHeight - scroll.VerticalOffset > 24) historyChanged?.Invoke(true); };
            scroll.Loaded += (_, _) => scroll.IsTabStop = scroll.ScrollableHeight > 0;
            section.Children.Add(scroll); panel.Children.Add(section);
        }
    }
    private static FrameworkElement Value(string value, LocalizationService text, bool code = false) => TryJson(value, out var root) ? Json(root, text, 0) : Prose(value, code);
    private static bool TryJson(string value, out JsonElement element)
    {
        try { using var doc = JsonDocument.Parse(value); element = doc.RootElement.Clone(); return true; }
        catch (JsonException) { element = default; return false; }
    }
    private static FrameworkElement Json(JsonElement value, LocalizationService text, int depth)
    {
        if (depth > 8) return Original(value, text);
        if (value.ValueKind == JsonValueKind.String) return Prose(value.GetString() ?? "");
        if (value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) return Prose(value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var n) ? text.Number(n) : value.TextValue());
        // MCP content blocks carry user-facing text; transport metadata need not become UI.
        if (value.Str("type") is "text" or "input_text" or "output_text" && value.Field("text").ValueKind == JsonValueKind.String) return Value(value.Str("text"), text);
        if (value.Str("type") is "image" or "input_image" or "audio" or "resource") return Prose(value.Str("mimeType", value.Str("type")));
        if (value.ValueKind == JsonValueKind.Object && value.Field("content").ValueKind == JsonValueKind.Array && value.EnumerateObject().All(p => p.Name is "content" or "isError"))
            return Json(value.Field("content"), text, depth + 1);
        var stack = new StackPanel { Spacing = 10 };
        if (value.ValueKind == JsonValueKind.Array)
        {
            var items = value.EnumerateArray().ToArray();
            foreach (var child in items.Take(100)) stack.Children.Add(Json(child, text, depth + 1));
            if (items.Length > 100) { stack.Children.Add(Prose(text.Format("MoreItems", text.Number(items.Length - 100)))); stack.Children.Add(Original(value, text)); }
        }
        else
        {
            foreach (var property in value.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined || property.Name is "threadId" or "turnId" or "itemId") continue;
                if (property.Name == "type" && value.EnumerateObject().Count() > 1) continue;
                var label = Prose(Labels.TryGetValue(property.Name, out var key) ? text[key] : Regex.Replace(property.Name.Replace('_', ' '), "([a-z])([A-Z])", "$1 $2"));
                label.FontSize = 12; label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
                var child = property.Name is "durationMs" or "duration_ms" && property.Value.TryNumber(out var ms) ? Prose(text.Format("ToolDuration", (ms / 1000d).ToString("0.#", text.Culture))) :
                    property.Name == "status" ? Prose(text[property.Value.TextValue() switch { "completed" => "ToolCompleted", "inProgress" or "running" => "ToolRunning", "interrupted" => "EventInterrupted", "failed" or "declined" => "ToolFailed", _ => "FieldStatus" }] + (property.Value.TextValue() is "completed" or "inProgress" or "running" or "interrupted" or "failed" or "declined" ? "" : " · " + property.Value.TextValue())) : Json(property.Value, text, depth + 1);
                if (property.Name is "path" or "cwd" or "url" or "command" or "cmd" or "receiverThreadId" or "newThreadId" or "author" or "recipient" or "agentThreadId" or "agentPath" or "savedPath") child.FlowDirection = FlowDirection.LeftToRight;
                if (property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                { var nested = new StackPanel { Spacing = 5 }; nested.Children.Add(label); nested.Children.Add(child); stack.Children.Add(nested); }
                else
                {
                    var field = new Grid { ColumnSpacing = 12 }; field.ColumnDefinitions.Add(new() { Width = GridLength.Auto, MinWidth = 80, MaxWidth = 128 }); field.ColumnDefinitions.Add(new());
                    field.Children.Add(label); Grid.SetColumn(child, 1); field.Children.Add(child); stack.Children.Add(field);
                }
            }
        }
        if (stack.Children.Count == 0) stack.Children.Add(Prose(text["NoDetails"]));
        return stack;
    }
    private static FrameworkElement Original(JsonElement value, LocalizationService text)
    {
        var control = new Expander { Header = text["RawData"], Content = Prose(value.GetRawText(), true), HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(control, text["RawData"]); AutomationProperties.SetHelpText(control, text["RawDataHelp"]); return control;
    }
    private static TextBlock Prose(string value, bool code = false)
    {
        var block = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 13 };
        if (code) { block.FontFamily = new FontFamily("Cascadia Mono, Consolas"); block.FlowDirection = FlowDirection.LeftToRight; }
        return block;
    }
    private static FrameworkElement Diff(string value)
    {
        var stack = new StackPanel();
        foreach (var line in value.Replace("\r", "").Split('\n'))
        {
            var block = Prose(line, true);
            if (line.StartsWith('+')) block.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextFillColorPrimaryBrush");
            else if (line.StartsWith('-')) block.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            stack.Children.Add(block);
        }
        return stack;
    }
}
