using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
public sealed class EntryRow(TranscriptEntry entry, LocalizationService text)
{
    public TranscriptEntry Entry { get; } = entry;
    public LocalizationService Text { get; } = text;
    public string Role => Text[Entry.Kind switch { EntryKind.User => "User", EntryKind.Assistant => "Assistant", EntryKind.Tool => "Tool", EntryKind.Reasoning => "Reasoning", _ => "SystemMessage" }];
    public string Time => Entry.Timestamp == default ? "" : Entry.Timestamp.ToLocalTime().ToString("t", Text.Culture);
    public string AccessibleName => Role + ", " + Time + ", " + Entry.Text[..Math.Min(160, Entry.Text.Length)].Replace('\n', ' ');
    public override string ToString() => AccessibleName;
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
