using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace CodexMonitor.App;

/// <summary>One stable disclosure around the existing virtualized activity list.</summary>
internal sealed partial class ActivityGroupView : Grid
{
    internal ListView List { get; }
    internal string Identity => _row.Entry.Id;
    private EntryRow _row;
    private readonly ActivityDisclosure _toggle = new();
    private readonly TextBlock _caption = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly ProgressRing _ring = new() { Width = 16, Height = 16, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _chevron = new() { IsTextScaleFactorEnabled = false, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    internal ActivityGroupView(EntryRow row, ListView list)
    {
        _row = row; List = list;
        RowDefinitions.Add(new() { Height = GridLength.Auto }); RowDefinitions.Add(new() { Height = GridLength.Auto });
        var header = new Grid { ColumnSpacing = 8 }; header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(_ring); Grid.SetColumn(_caption, 1); header.Children.Add(_caption); Grid.SetColumn(_chevron, 2); header.Children.Add(_chevron);
        _caption.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetAccessibilityView(_chevron, AccessibilityView.Raw);
        _toggle.Content = header; _toggle.Style = (Style)Application.Current.Resources["IconButtonStyle"];
        _toggle.HorizontalAlignment = HorizontalAlignment.Stretch; _toggle.HorizontalContentAlignment = HorizontalAlignment.Stretch;
        _toggle.MinHeight = 40; _toggle.Padding = new(10, 6, 10, 6);
        AutomationProperties.SetAutomationId(_toggle, "ActivityGroup-" + row.Entry.Id);
        _toggle.ExpansionChanged += () =>
        {
            var key = _row.Entry.Id + ":group:collapsed";
            if (_toggle.IsExpanded) _row.Expanded.Remove(key); else _row.Expanded.Add(key);
            Paint();
        };
        Children.Add(_toggle); SetRow(list, 1); Children.Add(list);
        Update(row);
    }
    internal void Update(EntryRow row)
    { _row = row; _toggle.SetExpanded(!row.IsGroupCollapsed); Paint(); }
    private void Paint()
    {
        var open = _toggle.IsExpanded;
        _caption.Text = open ? _row.Text["ActivityGroup"] : _row.ActivitySummary;
        _chevron.Glyph = open ? "\uE70E" : "\uE70D";
        _ring.IsActive = !open && _row.IsGroupBusy; _ring.Visibility = _ring.IsActive ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(_ring, _row.Text["Working"]);
        AutomationProperties.SetName(_toggle, _row.Text["ActivityGroup"] + ", " + _row.ActivitySummary);
        AutomationProperties.SetHelpText(_toggle, _row.Text["ActivityGroupHelp"]);
        AutomationProperties.SetItemStatus(_toggle, _row.IsGroupBusy ? _row.Text["Working"] : "");
        List.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }
}
