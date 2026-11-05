using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CodexMonitor.App;

internal sealed partial class TurnDetailsPanel : StackPanel
{
    private readonly LocalizationService _text;
    internal TurnDetailsPanel(TurnInfo info, LocalizationService text)
    { _text = text; Spacing = 12; MinWidth = 240; MaxWidth = 380; Paint(info); }
    internal void Paint(TurnInfo info, bool loading = false)
    {
        Children.Clear();
        var heading = new TextBlock { Text = _text["TurnDetails"], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        AutomationProperties.SetHeadingLevel(heading, Microsoft.UI.Xaml.Automation.Peers.AutomationHeadingLevel.Level2); Children.Add(heading);
        Add("FieldTurn", info.Id);
        Add("FieldStatus", info.Status switch { "completed" => _text["StatusCompleted"], "inProgress" => _text["ToolRunning"], "interrupted" => _text["StatusInterrupted"], "failed" => _text["StatusError"], _ => null });
        Add(info.ModelSource, info.Model ?? _text["NotAvailable"]);
        Add("FieldProvider", info.Provider); Add("Thinking", info.Effort); Add("FieldServiceTier", info.ServiceTier);
        Add("TurnStarted", info.StartedAt is { } start ? _text.MessageTime(start) : null);
        Add("TurnCompleted", info.CompletedAt is { } end ? _text.MessageTime(end) : null);
        var elapsed = info.DurationMs ?? (info.StartedAt is { } a && info.CompletedAt is { } b ? (long?)(b - a).TotalMilliseconds : null);
        Add("FieldDuration", elapsed is >= 0 ? _text.Format("ToolDuration", (elapsed.Value / 1000d).ToString("0.###", _text.Culture)) : null);
        Add("FirstRecord", info.FirstRecord is { } first ? _text.MessageTime(first) : null);
        Add("LastRecord", info.LastRecord is { } last ? _text.MessageTime(last) : null);
        Add("LoadedItems", _text.Number(info.LoadedItems));
        Usage("LastRequestUsage", info.LastRequest); Usage("ThreadUsage", info.ThreadTotal);
        Add("ContextWindow", info.ContextWindow is { } window ? _text.Number(window) : null);
        Add("EventError", info.Error);
        if (loading)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var ring = new ProgressRing { IsActive = true, Width = 16, Height = 16 }; AutomationProperties.SetName(ring, _text["Loading"]);
            row.Children.Add(ring); row.Children.Add(new TextBlock { Text = _text["Loading"], VerticalAlignment = VerticalAlignment.Center }); Children.Add(row);
        }
    }
    private void Add(string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var row = new StackPanel { Spacing = 3 };
        var label = new TextBlock { Text = _text[key], FontSize = 12, TextWrapping = TextWrapping.Wrap };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); row.Children.Add(label);
        var body = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        AutomationProperties.SetLabeledBy(body, label); row.Children.Add(body); Children.Add(row);
    }
    private void Usage(string key, TokenBreakdown? tokens)
    {
        if (tokens is null) return;
        var title = new TextBlock { Text = _text[key], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) };
        Children.Add(title);
        foreach (var (label, number) in new[] { ("FieldInputTokens", tokens.Input), ("FieldCachedTokens", tokens.CachedInput), ("FieldCacheWriteTokens", tokens.CacheWriteInput),
            ("FieldOutputTokens", tokens.Output), ("FieldReasoningTokens", tokens.Reasoning), ("FieldTotalTokens", tokens.Total) })
            if (number is { } n) Add(label, _text.Number(n));
    }
    internal static Button Button(EntryRow row)
    {
        var info = row.Turn!;
        var button = new Button { Content = new FontIcon { Glyph = "\uE946", FontSize = 16, IsTextScaleFactorEnabled = false }, HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.Resources["IconButtonStyle"], MinWidth = 40, MinHeight = 40, Padding = new(8), Margin = new(0, 0, 8, 2) };
        AutomationProperties.SetName(button, row.Text["TurnDetails"]); AutomationProperties.SetAutomationId(button, "TurnInfo-" + (info.Id.Length > 0 ? info.Id : row.Entry.Id));
        ToolTipService.SetToolTip(button, row.Text["TurnDetails"]);
        TurnDetailsPanel? panel = null;
        ScrollViewer? scroll = null;
        var flyout = new Flyout { Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.TopEdgeAlignedRight };
        button.Flyout = flyout; CancellationTokenSource? cancellation = null; bool open = false;
        void Updated(object? sender, System.ComponentModel.PropertyChangedEventArgs args) { if (open && row.Turn is { } current) { info = current; panel?.Paint(info); } }
        flyout.Opened += async (_, _) =>
        {
            open = true; info = row.Turn ?? info;
            panel ??= new TurnDetailsPanel(info, row.Text);
            panel.FlowDirection = button.FlowDirection; panel.Language = row.Text.LanguageTag; panel.RequestedTheme = button.ActualTheme;
            panel.MaxWidth = Math.Max(240, Math.Min(380, (button.XamlRoot?.Size.Width ?? 480) - 64));
            scroll ??= new ScrollViewer { Content = panel, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            scroll.MaxHeight = Math.Max(160, Math.Min(500, (button.XamlRoot?.Size.Height ?? 700) - 100));
            AutomationProperties.SetName(scroll, row.Text["TurnDetails"]); flyout.Content = scroll;
            row.PropertyChanged += Updated;
            cancellation?.Cancel(); cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10)); var current = cancellation;
            if (row.ReadTurn is null) { panel.Paint(info); return; }
            panel.Paint(info, true);
            try
            {
                var latest = await row.ReadTurn(info, current.Token);
                if (!current.IsCancellationRequested) panel.Paint(latest ?? info);
            }
            catch (Exception e) when (e is OperationCanceledException or IOException or RpcException or ObjectDisposedException)
            { if (open && ReferenceEquals(cancellation, current)) panel.Paint(info); }
        };
        flyout.Closed += (_, _) => { open = false; row.PropertyChanged -= Updated; cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null; };
        button.Unloaded += (_, _) => { flyout.Hide(); cancellation?.Cancel(); };
        return button;
    }
}
