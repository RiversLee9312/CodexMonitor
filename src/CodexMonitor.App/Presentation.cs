using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System.Text.RegularExpressions;
using System.ComponentModel;

namespace CodexMonitor.App;

public sealed class SessionRow(SessionInfo session, LocalizationService text, bool connected = false, bool hasUnread = false) : INotifyPropertyChanged
{
    private LocalizationService _text = text;
    private bool _connected = connected;
    public SessionInfo Session { get; private set; } = session;
    public bool HasUnread { get; private set; } = hasUnread;
    public string Title => string.IsNullOrWhiteSpace(Session.Title) ? _text["UntitledSession"] : Session.Title.Replace('\n', ' ');
    public string Folder => (Session.ParentId is not null ? _text["ChildAgent"] + " · " : "") + Session.FolderName;
    public bool IsRunning => _connected && Session.IsLive && !Session.IsArchived && Session.State is SessionState.Active or SessionState.Waiting;
    public string State => IsRunning && Session.State == SessionState.Active ? RunningText : _text.State(Session.State);
    public string Time => Session.UpdatedAt.ToLocalTime().ToString("d", _text.Culture);
    public string RunningText => _text["RunningSession"];
    public string UpdatedText => _text["SessionUpdated"];
    public Visibility RunningVisibility => IsRunning ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UpdatedVisibility => HasUnread ? Visibility.Visible : Visibility.Collapsed;
    public string AccessibleName => $"{Title}, {Folder}, {State}, {_text.DateTime(Session.UpdatedAt)}" +
        (IsRunning && Session.State == SessionState.Waiting ? ", " + RunningText : "") + (HasUnread ? ", " + UpdatedText : "");
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(SessionInfo session, LocalizationService text, bool connected, bool selected)
    {
        var old = Values();
        var changedContent = Session.FileVersion is { } before && session.FileVersion is { } after ? before != after :
            Session.FileVersion is null && session.FileVersion is null && session.UpdatedAt > Session.UpdatedAt;
        if (changedContent && !selected) HasUnread = true;
        if (selected) HasUnread = false;
        Session = session; _text = text; _connected = connected;
        NotifyChanges(old);
    }
    public void MarkUpdated(bool selected)
    {
        var old = Values(); HasUnread = !selected; NotifyChanges(old);
    }
    public void SetConnection(bool connected)
    {
        if (_connected == connected) return;
        var old = Values(); _connected = connected; NotifyChanges(old);
    }
    public void MarkRead() { var old = Values(); HasUnread = false; NotifyChanges(old); }
    private Dictionary<string, object> Values() => new()
    {
        [nameof(Session)] = Session, [nameof(Title)] = Title, [nameof(Folder)] = Folder, [nameof(State)] = State,
        [nameof(Time)] = Time, [nameof(IsRunning)] = IsRunning, [nameof(HasUnread)] = HasUnread,
        [nameof(RunningText)] = RunningText, [nameof(UpdatedText)] = UpdatedText, [nameof(RunningVisibility)] = RunningVisibility,
        [nameof(UpdatedVisibility)] = UpdatedVisibility, [nameof(AccessibleName)] = AccessibleName
    };
    private void NotifyChanges(Dictionary<string, object> old)
    {
        foreach (var (property, value) in Values()) if (!Equals(old[property], value)) PropertyChanged?.Invoke(this, new(property));
    }
    public override string ToString() => AccessibleName;
}
public sealed class EntryRow(TranscriptEntry entry, LocalizationService text, IReadOnlyList<TranscriptEntry>? activities = null, HashSet<string>? expanded = null)
{
    public TranscriptEntry Entry { get; } = entry;
    public LocalizationService Text { get; } = text;
    public IReadOnlyList<TranscriptEntry> Activities { get; } = activities ?? [entry];
    public HashSet<string> Expanded { get; } = expanded ?? [];
    public bool IsActivityGroup => Entry.Kind is EntryKind.Tool or EntryKind.Reasoning;
    public string ActivityTitle { get { var count = Activities.Count(e => e.Kind == EntryKind.Tool); return count > 0 ? Text.Format("ToolActivities", Text.Number(count)) : Text["Reasoning"]; } }
    public string Role => Text[Entry.Kind switch { EntryKind.User => "User", EntryKind.Assistant => "Assistant", EntryKind.Tool => "Tool", EntryKind.Reasoning => "Reasoning", _ => "SystemMessage" }];
    public string Time => Entry.Timestamp == default ? "" : Entry.Timestamp.ToLocalTime().ToString("t", Text.Culture);
    public string AccessibleName => Activities.Count > 1 ? ActivityTitle : Role + ", " + Time + ", " + Entry.Text[..Math.Min(160, Entry.Text.Length)].Replace('\n', ' ');
    public bool Matches(EntryRow row) => Text.LanguageTag == row.Text.LanguageTag && Activities.SequenceEqual(row.Activities);
    public static IReadOnlyList<EntryRow> Project(IEnumerable<TranscriptEntry> entries, LocalizationService text, HashSet<string> expanded)
    {
        var rows = new List<EntryRow>(); var tools = new List<TranscriptEntry>();
        void Flush() { if (tools.Count > 0) { rows.Add(new(tools[0], text, tools.ToArray(), expanded)); tools.Clear(); } }
        foreach (var item in entries)
        {
            if (item.Kind is EntryKind.Tool or EntryKind.Reasoning)
            {
                if (item.Tool is { } tool && tool.CategoryKey == "ToolFileChanges")
                {
                    var parts = tool.DisplayParts();
                    for (int i = 0; i < parts.Count; i++) tools.Add(item with { Id = item.Id + ":edit:" + i, Tool = parts[i] });
                }
                else tools.Add(item);
            }
            else { Flush(); rows.Add(new(item, text, expanded: expanded)); }
        }
        Flush(); return rows;
    }
    public override string ToString() => AccessibleName;
}

public sealed class ActivityDisclosure : Button
{
    public bool IsExpanded { get; private set; }
    public event Action? ExpansionChanged;
    public ActivityDisclosure() => Click += (_, _) => SetExpanded(!IsExpanded);
    public void SetExpanded(bool expanded)
    {
        if (IsExpanded == expanded) return;
        var before = IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        IsExpanded = expanded; ExpansionChanged?.Invoke();
        if (FrameworkElementAutomationPeer.FromElement(this) is ActivityDisclosurePeer peer)
            peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, before, expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new ActivityDisclosurePeer(this);
    private sealed class ActivityDisclosurePeer(ActivityDisclosure owner) : ButtonAutomationPeer(owner), IExpandCollapseProvider
    {
        public ExpandCollapseState ExpandCollapseState => owner.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        public void Collapse() => owner.SetExpanded(false);
        public void Expand() => owner.SetExpanded(true);
        protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPatternCore(patternInterface);
    }
}

public sealed class TranscriptCard : UserControl
{
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(nameof(Row), typeof(EntryRow), typeof(TranscriptCard), new PropertyMetadata(null, Changed));
    public EntryRow? Row { get => (EntryRow?)GetValue(RowProperty); set => SetValue(RowProperty, value); }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((TranscriptCard)d).Render();
    private void Render()
    {
        if (Row is not { } row) return;
        var entry = row.Entry;
        if (row.IsActivityGroup)
        {
            Content = new Border { Margin = new Thickness(12, 0, 12, 0), Child = ToolGroup(row) }; return;
        }
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(12, 10, 12, 8) };
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var role = new TextBlock { Text = row.Role, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        header.Children.Add(role);
        var time = new TextBlock { Text = row.Time, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        time.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Grid.SetColumn(time, 1); header.Children.Add(time);
        var copy = new Button { Content = new FontIcon { Glyph = "\uE8C8", FontSize = 14 }, MinHeight = 32, MinWidth = 36, Padding = new Thickness(6), Style = (Style)Application.Current.Resources["IconButtonStyle"] };
        ToolTipService.SetToolTip(copy, row.Text["Copy"]); AutomationProperties.SetName(copy, row.Text["Copy"]);
        copy.Click += (_, _) => { var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(entry.Text); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); ToolTipService.SetToolTip(copy, row.Text["Copied"]); };
        Grid.SetColumn(copy, 2); header.Children.Add(copy);
        panel.Children.Add(header);
        if (entry.Kind is EntryKind.Tool or EntryKind.Reasoning or EntryKind.Event)
        {
            var body = Code(entry.Text);
            var label = entry.Kind == EntryKind.Reasoning ? row.Role : entry.Label ?? row.Role;
            var expander = new Expander { Header = label, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(expander, row.Role + " " + label);
            panel.Children.Add(expander);
        }
        else panel.Children.Add(Markdown(entry.Text));
        var border = new Border { Child = panel, CornerRadius = new CornerRadius(8) };
        if (entry.Kind == EntryKind.User)
        {
            border.SetResourceReference(Border.BackgroundProperty, "ControlFillColorSecondaryBrush");
            border.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
            border.BorderThickness = new Thickness(1);
        }
        Content = border;
    }
    private static FrameworkElement ToolGroup(EntryRow row)
    {
        if (row.Activities.Count == 1) return ToolRow(row.Activities[0], row);
        // One inline disclosure per operation, without nested group cards or repeated role/time headers.
        var template = (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='using:CodexMonitor.App'><local:TranscriptCard Row='{Binding}'/></DataTemplate>");
        var itemStyle = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ListViewItem'><Setter Property='HorizontalContentAlignment' Value='Stretch'/><Setter Property='Padding' Value='0'/><Setter Property='Margin' Value='0'/><Setter Property='MinHeight' Value='36'/></Style>");
        return new ListView { MaxHeight = 480, SelectionMode = ListViewSelectionMode.None, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Padding = new Thickness(0), ItemTemplate = template, ItemContainerStyle = itemStyle,
            ItemsSource = row.Activities.Select(entry => new EntryRow(entry, row.Text, expanded: row.Expanded)).ToArray() };
    }
    private static FrameworkElement ToolRow(TranscriptEntry entry, EntryRow row)
    {
        var tool = entry.Tool;
        var reasoning = entry.Kind == EntryKind.Reasoning;
        var label = reasoning ? row.Text["Reasoning"] : tool is null ? entry.Label ?? row.Text["Tool"] : tool.CategoryKey == "ToolDetails" ? tool.Name : row.Text[tool.CategoryKey];
        if (tool is { State: ToolActivityState.Completed or ToolActivityState.Running })
            label = row.Text[(tool.CategoryKey, tool.State) switch
            {
                ("ToolFileChanges", ToolActivityState.Completed) => "ToolEdited", ("ToolFileChanges", _) => "ToolEditing",
                ("ToolCommand", ToolActivityState.Completed) => "ToolRan", ("ToolCommand", _) => "ToolExecuting",
                ("ToolSearch", ToolActivityState.Completed) => "ToolSearched", ("ToolSearch", _) => "ToolSearching", _ => tool.CategoryKey
            }];
        if (tool?.State == ToolActivityState.Failed) label = row.Text["ToolFailed"] + " · " + label;
        var reasoningPreview = reasoning ? Regex.Replace(entry.Text, @"\s+", " ") : "";
        var summary = tool?.Summary ?? reasoningPreview[..Math.Min(100, reasoningPreview.Length)];
        var title = label + (summary.Length > 0 ? " " + summary : "");
        if (tool is { AddedLines: { } added, RemovedLines: { } removed }) title += " +" + row.Text.Number(added) + " −" + row.Text.Number(removed);
        var categoryIcon = reasoning ? "\uE946" : tool?.CategoryKey switch { "ToolFileChanges" => "\uE70F", "ToolSearch" => "\uE721", "ToolCommand" => "\uE756", _ => "\uE8A5" };
        return Expandable(entry.Id, ActivityHeader(title, tool?.State ?? ToolActivityState.Unknown, row.Text, tool?.DurationMs, categoryIcon), row, () =>
        {
            var content = new StackPanel { Spacing = 12 };
            if (tool is null) content.Children.Add(reasoning ? Markdown(entry.Text) : Code(entry.Text));
            else
            {
                AddSection(row.Text[tool.CategoryKey == "ToolCommand" ? "ToolCommand" : tool.CategoryKey == "ToolFileChanges" ? "ToolFileChanges" : "ToolParameters"], tool.DecodedInput);
                if (tool.Output is { Length: > 0 } output) AddSection(row.Text["ToolOutput"], output);
            }
            if (tool is null) content.Children.Add(CopyButton(entry.Text));
            return content;
            Button CopyButton(string value)
            {
                var copy = new Button { Content = new FontIcon { Glyph = "\uE8C8", FontSize = 13 }, HorizontalAlignment = HorizontalAlignment.Right, MinHeight = 32, MinWidth = 32, Padding = new Thickness(6), Style = (Style)Application.Current.Resources["IconButtonStyle"] };
                AutomationProperties.SetName(copy, row.Text["Copy"]); ToolTipService.SetToolTip(copy, row.Text["Copy"]);
                copy.Click += (_, _) => { var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(value); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); };
                return copy;
            }
            void AddSection(string heading, string value)
            {
                var section = new Grid { ColumnSpacing = 8 };
                section.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); section.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
                section.Children.Add(new TextBlock { Text = heading, FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
                var copy = CopyButton(value); Grid.SetColumn(copy, 1); section.Children.Add(copy); content.Children.Add(section);
                content.Children.Add(new ScrollViewer { Content = Code(value), MaxHeight = 300, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
            }
        }, title);
    }
    private static FrameworkElement ActivityHeader(string title, ToolActivityState state, LocalizationService text, long? durationMs = null, string? categoryIcon = null)
    {
        var header = new Grid { ColumnSpacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var icon = new FontIcon { Glyph = state == ToolActivityState.Failed ? "\uE783" : categoryIcon ?? "\uE756", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        icon.SetResourceReference(FontIcon.ForegroundProperty, state == ToolActivityState.Failed ? "SystemFillColorCriticalBrush" : state == ToolActivityState.Running ? "AccentTextFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
        AutomationProperties.SetName(icon, text[state switch { ToolActivityState.Failed => "ToolFailed", ToolActivityState.Completed => "ToolCompleted", ToolActivityState.Running => "ToolRunning", _ => "Tool" }]);
        header.Children.Add(icon);
        var label = new TextBlock { Text = title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Grid.SetColumn(label, 1); header.Children.Add(label);
        if (durationMs is { } duration)
        {
            var elapsed = new TextBlock { Text = text.Format("ToolDuration", (duration / 1000d).ToString("0.#", text.Culture)), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            elapsed.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); Grid.SetColumn(elapsed, 2); header.Children.Add(elapsed);
        }
        ToolTipService.SetToolTip(header, title); return header;
    }
    private static FrameworkElement Expandable(string id, FrameworkElement header, EntryRow row, Func<FrameworkElement> body, string name)
    {
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var toggleContent = new Grid { ColumnSpacing = 12 };
        toggleContent.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); toggleContent.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        toggleContent.Children.Add(header);
        var chevron = new FontIcon { Glyph = "\uE70D", FontSize = 10, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(chevron, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Grid.SetColumn(chevron, 1); toggleContent.Children.Add(chevron);
        var toggle = new ActivityDisclosure { Content = toggleContent, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 36, Padding = new Thickness(10, 6, 10, 6), Style = (Style)Application.Current.Resources["IconButtonStyle"] };
        AutomationProperties.SetName(toggle, name); AutomationProperties.SetAutomationId(toggle, "ActivityToggle-" + id);
        var content = new Border { Padding = new Thickness(12), Margin = new Thickness(0, 4, 0, 8), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Visibility = Visibility.Collapsed };
        content.SetResourceReference(Border.BackgroundProperty, "ControlFillColorTertiaryBrush"); content.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        Grid.SetRow(content, 1); grid.Children.Add(toggle); grid.Children.Add(content);
        void Update()
        {
            var open = toggle.IsExpanded; chevron.Glyph = open ? "\uE70E" : "\uE70D";
            if (open) { row.Expanded.Add(id); content.Child ??= body(); } else row.Expanded.Remove(id);
            content.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        }
        toggle.ExpansionChanged += Update;
        if (row.Expanded.Contains(id)) toggle.SetExpanded(true);
        return grid;
    }
    private static FrameworkElement Code(string text)
    {
        var block = new TextBlock { Text = text, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13,
            IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.LeftToRight, LineHeight = 20 };
        return block;
    }
    // A deliberately small native renderer: selectable prose, code fences, headings, links and inline emphasis.
    // No HTML/WebView or execution of links with custom schemes.
    private static FrameworkElement Markdown(string source)
    {
        var stack = new StackPanel { Spacing = 10 };
        var prose = new List<string>(); var code = new List<string>(); string? fence = null;
        void Flush()
        {
            if (prose.Count == 0) return;
            var block = new RichTextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 23 };
            var paragraph = new Paragraph();
            var content = string.Join("\n", prose); prose.Clear();
            foreach (var line in content.Split('\n'))
            {
                var heading = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
                var text = heading.Success ? heading.Groups[2].Value : Regex.Replace(line, @"^\s*[-*]\s+", "• ");
                if (heading.Success) paragraph.Inlines.Add(new Run { Text = text, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 18 });
                else AddInline(paragraph, text);
                paragraph.Inlines.Add(new LineBreak());
            }
            block.Blocks.Add(paragraph); stack.Children.Add(block);
        }
        foreach (var line in source.Replace("\r", "").Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*(`{3,}|~{3,})");
            if (match.Success && fence is null) { Flush(); fence = match.Groups[1].Value; continue; }
            if (fence is not null && line.Trim().StartsWith(fence, StringComparison.Ordinal))
            {
                var border = new Border { Child = Code(string.Join("\n", code)), Padding = new Thickness(14), CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
                border.SetResourceReference(Border.BackgroundProperty, "ControlFillColorTertiaryBrush");
                border.SetResourceReference(Border.BorderBrushProperty, "ControlStrokeColorDefaultBrush");
                stack.Children.Add(border); code.Clear(); fence = null; continue;
            }
            if (fence is not null) code.Add(line);
            else if (string.IsNullOrWhiteSpace(line)) Flush();
            else prose.Add(line);
        }
        if (fence is not null) stack.Children.Add(Code(string.Join("\n", code)));
        Flush();
        return stack;
    }
    private static void AddInline(Paragraph paragraph, string text)
    {
        int end = 0;
        foreach (Match match in Regex.Matches(text, @"(`[^`]+`|\*\*[^*]+\*\*|\[[^\]]+\]\(https?://[^\s)]+\))"))
        {
            if (match.Index > end) paragraph.Inlines.Add(new Run { Text = text[end..match.Index] });
            var s = match.Value;
            if (s.StartsWith('`')) paragraph.Inlines.Add(new Run { Text = s[1..^1], FontFamily = new FontFamily("Cascadia Mono, Consolas") });
            else if (s.StartsWith("**")) paragraph.Inlines.Add(new Run { Text = s[2..^2], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            else
            {
                var split = s.IndexOf("](", StringComparison.Ordinal);
                if (Uri.TryCreate(s[(split + 2)..^1], UriKind.Absolute, out var uri))
                {
                    var link = new Hyperlink { NavigateUri = uri }; link.Inlines.Add(new Run { Text = s[1..split] }); paragraph.Inlines.Add(link);
                }
                else paragraph.Inlines.Add(new Run { Text = s });
            }
            end = match.Index + match.Length;
        }
        if (end < text.Length) paragraph.Inlines.Add(new Run { Text = text[end..] });
    }
}

internal static class ThemeExtensions
{
    // ThemeResource bindings keep native contrast themes and runtime light/dark changes intact.
    public static void SetResourceReference(this FrameworkElement element, DependencyProperty property, string key)
    {
        var name = property == Border.BackgroundProperty ? "Background" : property == Border.BorderBrushProperty ? "BorderBrush" : "Foreground";
        var style = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='{element.GetType().Name}'><Setter Property='{name}' Value='{{ThemeResource {key}}}'/></Style>");
        style.BasedOn = element.Style;
        element.Style = style;
    }
}
