using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using System.Text.RegularExpressions;
using System.ComponentModel;
using System.Collections.ObjectModel;
using Microsoft.UI.Xaml.Input;

namespace CodexMonitor.App;

[WinRT.GeneratedBindableCustomProperty]
public sealed partial class SessionRow : INotifyPropertyChanged
{
    private LocalizationService _text;
    private bool _connected;
    public SessionInfo Session { get; private set; }
    public bool HasUnread { get; private set; }
    public string Title { get; private set; }
    public SessionRow(SessionInfo session, LocalizationService text, bool connected = false, bool hasUnread = false)
    { Session = session; _text = text; _connected = connected; HasUnread = hasUnread; Title = DisplayTitle(session.Title, text); }
    // TextTrimming still shapes its entire input. Metadata can contain a full, multi-megabyte prompt.
    // Keep the original on Session for search; never hand it to single-line XAML or UIA.
    internal static string DisplayTitle(string? title, LocalizationService text)
    {
        if (string.IsNullOrEmpty(title)) return text["UntitledSession"];
        var length = Math.Min(180, title.Length);
        if (length < title.Length && char.IsHighSurrogate(title[length - 1])) length--;
        var buffer = new System.Text.StringBuilder(length + 1);
        foreach (var character in title.AsSpan(0, length))
            if (!char.IsWhiteSpace(character)) buffer.Append(character);
            else if (buffer.Length > 0 && buffer[^1] != ' ') buffer.Append(' ');
        var value = buffer.ToString().TrimEnd();
        return value.Length == 0 ? text["UntitledSession"] : value + (length < title.Length ? "…" : "");
    }
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

    public void Update(SessionInfo session, LocalizationService text, bool connected, bool selected, bool observeContent = true)
    {
        if (Session == session && _text.LanguageTag == text.LanguageTag && _connected == connected && (!selected || !HasUnread)) return;
        var old = Values();
        var changedContent = observeContent && SessionMetadata.ContentChanged(Session, session);
        if (changedContent && !selected) HasUnread = true;
        if (selected) HasUnread = false;
        if (!ReferenceEquals(Session.Title, session.Title) || _text.LanguageTag != text.LanguageTag) Title = DisplayTitle(session.Title, text);
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
    public void MarkRead() { if (!HasUnread) return; var old = Values(); HasUnread = false; NotifyChanges(old); }
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
[WinRT.GeneratedBindableCustomProperty]
public sealed partial class EntryRow(TranscriptEntry entry, LocalizationService text, IReadOnlyList<TranscriptEntry>? activities = null, HashSet<string>? expanded = null, bool live = false, Dictionary<string, double>? scrollOffsets = null, Action<bool>? historyChanged = null, Func<bool>? canFollow = null, Action? earlier = null) : INotifyPropertyChanged
{
    public TurnInfo? Turn { get; private set; }
    public Func<TurnInfo, CancellationToken, Task<TurnInfo?>>? ReadTurn { get; private set; }
    private DateOnly _displayDay = DateOnly.FromDateTime(DateTime.Now);
    public TranscriptEntry Entry { get; private set; } = entry;
    public LocalizationService Text { get; private set; } = text;
    public IReadOnlyList<TranscriptEntry> Activities { get; private set; } = activities ?? new TranscriptEntry[] { entry };
    public HashSet<string> Expanded { get; } = expanded ?? [];
    public bool IsLive { get; private set; } = live;
    public Dictionary<string, double> ScrollOffsets { get; } = scrollOffsets ?? [];
    public Action<bool>? HistoryChanged { get; private set; } = historyChanged;
    public Func<bool>? CanFollow { get; private set; } = canFollow;
    public Action? Earlier { get; private set; } = earlier;
    public bool IsActivityGroup => Entry.Kind is EntryKind.Tool or EntryKind.Reasoning or EntryKind.Event;
    public bool IsWorkProcess { get; private set; }
    public bool AutoCollapseWork { get; private set; }
    public bool IsCurrentWork { get; private set; }
    public string ActivityTitle => Text.Format("Activities", Text.Number(Activities.Count));
    public bool IsGroupCollapsed => Expanded.Contains(Entry.Id + ":group:collapsed") || AutoCollapseWork && !IsGroupBusy && !Expanded.Contains(Entry.Id + ":group:manual");
    public bool IsGroupBusy => IsLive && (IsCurrentWork || Activities.Any(e => e.Tool?.State is ToolActivityState.Running or ToolActivityState.Generating));
    public string ActivitySummary => string.Join(" · ", Activities.GroupBy(e => e.Kind == EntryKind.Reasoning ? "Reasoning" : e.Tool?.State == ToolActivityState.Generating ? "Working" : e.Tool?.CategoryKey ?? "Tool")
        .Take(4).Select(g => Text[g.Key] + " " + Text.Number(g.Count()))) + (Activities.Select(e => e.Kind == EntryKind.Reasoning ? "Reasoning" : e.Tool?.CategoryKey ?? "Tool").Distinct().Count() > 4 ? " · …" : "");
    public string Role => Text[Entry.Kind switch { EntryKind.User => "User", EntryKind.Assistant => "Assistant", EntryKind.Tool => "Tool", EntryKind.Reasoning => "Reasoning", _ => "SystemMessage" }];
    public string Time => Text.MessageTime(Entry.Timestamp);
    public string AccessibleName => Activities.Count > 1 ? ActivityTitle : Entry.Tool?.State == ToolActivityState.Generating ? Text["Working"] : Entry.Reasoning is { Summary.Length: 0, FullText.Length: > 0 } ? Text["Thinking"] : Entry.Tool is { } tool ?
        (tool.CategoryKey == "ToolDetails" ? tool.Name : Text[tool.CategoryKey]) + ", " + tool.Summary + ", " + Text[tool.State switch
        { ToolActivityState.Failed => "ToolFailed", ToolActivityState.Interrupted => "EventInterrupted", ToolActivityState.Completed => "ToolCompleted", ToolActivityState.Running when IsLive => "ToolRunning", _ => "Tool" }] :
        Role + (Entry.Kind == EntryKind.Assistant && Entry.Model is { Length: > 0 } model ? ", " + Text["FieldModel"] + " " + model : "") + ", " + Time + ", " + Entry.Text[..Math.Min(160, Entry.Text.Length)].Replace('\n', ' ');
    public event PropertyChangedEventHandler? PropertyChanged;
    internal void UpdateNavigation(EntryRow next)
    { HistoryChanged = next.HistoryChanged; CanFollow = next.CanFollow; Earlier = next.Earlier; ReadTurn = next.ReadTurn; }
    public void Update(EntryRow next)
    { Entry = next.Entry; Text = next.Text; Activities = next.Activities; IsLive = next.IsLive; HistoryChanged = next.HistoryChanged; CanFollow = next.CanFollow; Earlier = next.Earlier; Turn = next.Turn; ReadTurn = next.ReadTurn; IsWorkProcess = next.IsWorkProcess; AutoCollapseWork = next.AutoCollapseWork; IsCurrentWork = next.IsCurrentWork; _displayDay = next._displayDay; PropertyChanged?.Invoke(this, new(null)); }
    public bool Matches(EntryRow row) => Text.LanguageTag == row.Text.LanguageTag && IsLive == row.IsLive && Activities.SequenceEqual(row.Activities) && Turn == row.Turn && AutoCollapseWork == row.AutoCollapseWork && IsCurrentWork == row.IsCurrentWork && _displayDay == row._displayDay;
    public static IReadOnlyList<EntryRow> Project(IEnumerable<TranscriptEntry> entries, LocalizationService text, HashSet<string> expanded, bool live = false, Dictionary<string, double>? scrollOffsets = null, Action<bool>? historyChanged = null, Func<bool>? canFollow = null, string? liveTurnId = null, Dictionary<string, string>? groupIdentities = null, Action? earlier = null,
        Func<string, TurnInfo?>? turnInfo = null, Func<TurnInfo, CancellationToken, Task<TurnInfo?>>? readTurn = null, bool autoCollapseWork = false, bool pendingWork = false)
    {
        var rows = new List<EntryRow>(); var tools = new List<TranscriptEntry>();
        var source = entries as IReadOnlyList<TranscriptEntry> ?? entries.ToArray();
        if (pendingWork) source = WorkProcessState.WithPending(source, live, liveTurnId);
        var boundary = "start";
        var usedIds = new HashSet<string>(StringComparer.Ordinal);
        void Flush()
        {
            if (tools.Count == 0) return;
            var segment = tools[0].TurnId is { Length: > 0 } turn ? "work-segment:" + turn + ":" + boundary : null;
            var remembered = segment is null ? null : groupIdentities?.GetValueOrDefault(segment);
            var identity = tools.Select(e => groupIdentities?.GetValueOrDefault(e.Id)).FirstOrDefault(id => id is not null && !usedIds.Contains(id)) ??
                (remembered is not null && !usedIds.Contains(remembered) ? remembered : tools[0].Id);
            usedIds.Add(identity);
            if (groupIdentities is not null) foreach (var tool in tools) groupIdentities[tool.Id] = identity;
            if (groupIdentities is not null && segment is not null) groupIdentities[segment] = identity;
            rows.Add(new(tools[0] with { Id = identity }, text, tools.ToArray(), expanded, live, scrollOffsets, historyChanged, canFollow, rows.Count == 0 ? earlier : null) { IsWorkProcess = true }); tools.Clear();
        }
        foreach (var item in source)
        {
            if (tools.Count > 0 && tools[^1].TurnId is { Length: > 0 } previousTurn && item.TurnId is { Length: > 0 } nextTurn && previousTurn != nextTurn) Flush();
            if (item.Kind == EntryKind.Reasoning && string.IsNullOrWhiteSpace(item.Text)) continue;
            if (item.Kind is EntryKind.Tool or EntryKind.Reasoning or EntryKind.Event)
            {
                if (item.Tool is { } tool && tool.CategoryKey == "ToolFileChanges")
                {
                    var parts = tool.DisplayParts();
                    for (int i = 0; i < parts.Count; i++) tools.Add(item with { Id = item.Id + ":edit:" + i, Tool = parts[i] });
                }
                else tools.Add(item);
            }
            else { Flush(); rows.Add(new(item, text, expanded: expanded, live: live, scrollOffsets: scrollOffsets, historyChanged: historyChanged, canFollow: canFollow)); boundary = item.Id; }
        }
        Flush();
        var lastActivity = rows.LastOrDefault(r => r.IsActivityGroup);
        if (live) foreach (var row in rows.Where(r => r.IsActivityGroup))
            row.IsLive = liveTurnId is { Length: > 0 } && row.Activities.Any(e => e.TurnId == liveTurnId) || ReferenceEquals(row, lastActivity) && row.Activities.All(e => string.IsNullOrEmpty(e.TurnId));
        foreach (var row in rows.Where(r => r.IsActivityGroup))
        { row.AutoCollapseWork = autoCollapseWork; row.IsCurrentWork = row.IsLive && ReferenceEquals(rows.LastOrDefault(), row); }
        var groups = new Dictionary<string, List<EntryRow>>(StringComparer.Ordinal); string observed = "observed:first";
        foreach (var row in rows)
        {
            if (row.Entry.Kind == EntryKind.User) observed = "observed:" + row.Entry.Id;
            var key = string.IsNullOrEmpty(row.Entry.TurnId) ? observed : row.Entry.TurnId;
            if (!groups.TryGetValue(key, out var members)) groups[key] = members = [];
            members.Add(row);
        }
        foreach (var (key, members) in groups)
        {
            var parts = members.SelectMany(r => r.Activities).Where(e => !WorkProcessState.IsPending(e)).ToArray();
            var knownId = key.StartsWith("observed:", StringComparison.Ordinal) ? "" : key;
            var details = turnInfo?.Invoke(knownId) ?? new TurnInfo(knownId);
            var times = parts.Where(e => e.Timestamp != default).Select(e => e.Timestamp).ToArray();
            var models = parts.Select(e => e.Model).Append(details.Model).Where(m => !string.IsNullOrEmpty(m)).Distinct(StringComparer.Ordinal).ToArray();
            members[^1].Turn = details with { Model = models.Length > 0 ? string.Join(" → ", models) : null, LoadedItems = parts.Length,
                FirstRecord = times.Length > 0 ? times.Min() : null, LastRecord = times.Length > 0 ? times.Max() : null };
            members[^1].ReadTurn = knownId.Length > 0 ? readTurn : null;
        }
        return rows;
    }
    public override string ToString() => AccessibleName;
}

public sealed partial class ActivityDisclosure : Button
{
    public bool IsExpanded { get; private set; }
    public event Action? ExpansionChanged;
    public ActivityDisclosure()
    {
        UseSystemFocusVisuals = true;
        Click += (_, _) => SetExpanded(!IsExpanded);
        KeyDown += (_, e) =>
        {
            var expand = FlowDirection == FlowDirection.RightToLeft ? Windows.System.VirtualKey.Left : Windows.System.VirtualKey.Right;
            var collapse = FlowDirection == FlowDirection.RightToLeft ? Windows.System.VirtualKey.Right : Windows.System.VirtualKey.Left;
            if (e.Key == expand) { SetExpanded(true); e.Handled = true; }
            else if (e.Key == collapse) { SetExpanded(false); e.Handled = true; }
        };
    }
    public void SetExpanded(bool expanded)
    {
        if (IsExpanded == expanded) return;
        var before = IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        IsExpanded = expanded; ExpansionChanged?.Invoke();
        if (FrameworkElementAutomationPeer.FromElement(this) is ActivityDisclosurePeer peer)
            peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, before, expanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new ActivityDisclosurePeer(this);
    private sealed partial class ActivityDisclosurePeer(ActivityDisclosure owner) : ButtonAutomationPeer(owner), IExpandCollapseProvider
    {
        public ExpandCollapseState ExpandCollapseState => owner.IsExpanded ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        public void Collapse() { if (!owner.IsEnabled) throw new ElementNotEnabledException(); owner.SetExpanded(false); }
        public void Expand() { if (!owner.IsEnabled) throw new ElementNotEnabledException(); owner.SetExpanded(true); }
        protected override object GetPatternCore(PatternInterface patternInterface) => patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPatternCore(patternInterface);
    }
}

public sealed partial class TranscriptCard : UserControl
{
    private static readonly Regex InlineMarkup = new(@"(`[^`]+`|\*\*[^*]+\*\*|\[[^\]]+\]\(https?://[^\s)]+\))", RegexOptions.NonBacktracking);
    public static readonly DependencyProperty RowProperty = DependencyProperty.Register(nameof(Row), typeof(EntryRow), typeof(TranscriptCard), new PropertyMetadata(null, Changed));
    public EntryRow? Row { get => (EntryRow?)GetValue(RowProperty); set => SetValue(RowProperty, value); }
    private EntryRow? _observed;
    private EntryRow? _rendered;
    private TranscriptEntry? _renderedEntry;
    private IReadOnlyList<TranscriptEntry>? _renderedActivities;
    private string? _renderedLanguage;
    private bool _renderedLive;
    private TurnInfo? _renderedTurn;
    public TranscriptCard()
    {
        Loaded += (_, _) =>
        {
            Observe();
            if (Row is { } row && (!ReferenceEquals(_rendered, row) || !ReferenceEquals(_renderedEntry, row.Entry) ||
                !ReferenceEquals(_renderedActivities, row.Activities) || _renderedLanguage != row.Text.LanguageTag || _renderedLive != row.IsLive || _renderedTurn != row.Turn)) RefreshContent();
            AccessibilitySupport.NormalizeIconsAfterLayout(this);
        };
        Unloaded += (_, _) => { if (_observed is not null) _observed.PropertyChanged -= RowUpdated; _observed = null; };
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e)
    { var card = (TranscriptCard)d; card.Observe(); card.RefreshContent(); }
    private void Observe()
    {
        if (ReferenceEquals(_observed, Row)) return;
        if (_observed is not null) _observed.PropertyChanged -= RowUpdated;
        _observed = Row;
        if (_observed is not null) _observed.PropertyChanged += RowUpdated;
    }
    private void RowUpdated(object? sender, PropertyChangedEventArgs args) => RefreshContent();
    private void RefreshContent()
    {
        var sameOwner = ReferenceEquals(_rendered, Row);
        _rendered = Row;
        _renderedEntry = Row?.Entry; _renderedActivities = Row?.Activities; _renderedLanguage = Row?.Text.LanguageTag; _renderedLive = Row?.IsLive == true;
        _renderedTurn = Row?.Turn;
        if (Row is { IsWorkProcess: true } next && Content is Border { Child: ActivityGroupView group } && group.List is RepeaterList list && list.ItemsSource is ObservableCollection<EntryRow> children &&
            children.Count > 0 && ReferenceEquals(group.OwnerRow, next) && group.Identity == next.Entry.Id && children[0].Text.LanguageTag == next.Text.LanguageTag)
        {
            var scroll = ScrollViewerIn(list); var follow = next.CanFollow?.Invoke() == true && (scroll is null || scroll.ScrollableHeight - scroll.VerticalOffset < 24);
            var incoming = next.Activities.Select(entry => new EntryRow(entry, next.Text, expanded: next.Expanded, live: next.IsLive, scrollOffsets: next.ScrollOffsets, historyChanged: next.HistoryChanged, canFollow: next.CanFollow)).ToArray();
            var anchor = !follow && incoming.Length > children.Count ? ScrollAnchor.Capture(list) : null;
            ScrollAnchor.Diagnostics?.Invoke($"group update {children.Count}->{incoming.Length} follow={follow} loaded={list.IsLoaded} visible={list.FirstVisibleIndex} anchor={anchor is not null}");
            var restoringPosition = anchor is not null ? list.BeginRestoration() : 0;
            using (list.DeferUpdates()) EntryRows.Synchronize(children, incoming);
            group.Update(next);
            if (restoringPosition != 0) list.EndRestoration(restoringPosition);
            anchor?.Restore();
            var followInput = list.InputVersion;
            if (follow && !next.IsGroupCollapsed && scroll is not null) list.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            { if (list.IsLoaded && list.InputVersion == followInput && next.CanFollow?.Invoke() == true) { list.UpdateLayout(); scroll.ChangeView(null, scroll.ScrollableHeight, null, true); } });
            return;
        }
        var hadFocus = sameOwner && IsLoaded && AccessibilitySupport.ContainsFocus(this);
        var focused = hadFocus ? FocusManager.GetFocusedElement(XamlRoot) as Control : null;
        var focusId = focused is null ? "" : AutomationProperties.GetAutomationId(focused);
        var focusName = focused is null ? "" : AutomationProperties.GetName(focused);
        var positions = sameOwner && Content is DependencyObject previous ? VisualChildren(previous).OfType<ScrollViewer>()
            .Select(scroll => (scroll.VerticalOffset, AtEnd: scroll.ScrollableHeight - scroll.VerticalOffset < 24)).ToArray() : [];
        Render();
        if ((positions.Length > 0 || focused is not null) && Content is FrameworkElement replacement)
        {
            var restoring = false; var focusRestored = false;
            void Detach() { replacement.LayoutUpdated -= Restore; replacement.Loaded -= Loaded; replacement.Unloaded -= Unloaded; }
            void Loaded(object sender, RoutedEventArgs args) => Restore(null, new object());
            void Unloaded(object sender, RoutedEventArgs args) => Detach();
            void Restore(object? sender, object args)
            {
                if (restoring) return;
                if (!ReferenceEquals(Content, replacement)) { Detach(); return; }
                if (!IsLoaded || !replacement.IsLoaded) return;
                restoring = true;
                try
                {
                    replacement.UpdateLayout(); var children = VisualChildren(replacement).ToArray();
                    if (focused is not null && !focusRestored)
                    {
                        var target = children.OfType<Control>().FirstOrDefault(control => focusId.Length > 0 ? AutomationProperties.GetAutomationId(control) == focusId : control.GetType() == focused.GetType() && AutomationProperties.GetName(control) == focusName);
                        target?.Focus(FocusState.Programmatic); focusRestored = true;
                    }
                    var scrolls = children.OfType<ScrollViewer>().ToArray();
                    // A replacement ScrollPresenter can still have zero extent on the first dispatcher callback.
                    // Wait for its loaded layout; otherwise ChangeView silently clamps a saved offset to zero.
                    if (scrolls.Any(scroll => !scroll.IsLoaded || scroll.ViewportHeight <= 0 || scroll.ExtentHeight <= 0)) return;
                    for (int i = 0; i < Math.Min(positions.Length, scrolls.Length); i++)
                        scrolls[i].ChangeView(null, positions[i].AtEnd && Row?.CanFollow?.Invoke() == true ? scrolls[i].ScrollableHeight : positions[i].VerticalOffset, null, true);
                    Detach();
                }
                finally { restoring = false; }
            }
            replacement.LayoutUpdated += Restore; replacement.Loaded += Loaded; replacement.Unloaded += Unloaded;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Restore(null, new object()));
        }
    }
    internal static void PreserveScroll(DependencyObject container, EntryRow row)
    {
        if (row.IsWorkProcess && ScrollViewerIn(container) is { ScrollableHeight: > 0 } scroll)
            row.ScrollOffsets[row.Entry.Id] = scroll.VerticalOffset;
    }
    internal static void ScrollGroupToEnd(DependencyObject container, EntryRow row)
    {
        if (row.IsGroupCollapsed) return;
        if (row.IsWorkProcess && ScrollViewerIn(container) is { } scroll)
        { scroll.UpdateLayout(); scroll.ChangeView(null, scroll.ScrollableHeight, null, true); }
    }
    private void Render()
    {
        if (Row is not { } row) { Content = null; return; }
        var entry = row.Entry;
        if (row.IsActivityGroup)
        {
            var activity = ToolGroup(row);
            if (activity is not ActivityGroupView && row.Turn is not null) { var activities = new StackPanel { Spacing = 4 }; activities.Children.Add(activity); activities.Children.Add(TurnDetailsPanel.Button(row)); activity = activities; }
            Content = new Border { Margin = new Thickness(12, 0, 12, 0), Child = activity }; return;
        }
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(12, 10, 12, 8) };
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var role = new TextBlock { Text = row.Role, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        var identity = new Grid { ColumnSpacing = 8 }; identity.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); identity.ColumnDefinitions.Add(new()); identity.Children.Add(role);
        if (entry.Kind == EntryKind.Assistant && entry.Model is { Length: > 0 } model)
        {
            var modelLabel = new TextBlock { Text = model, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FlowDirection = FlowDirection.LeftToRight, TextAlignment = row.Text.IsRightToLeft ? TextAlignment.Right : TextAlignment.Left };
            modelLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush"); ToolTipService.SetToolTip(modelLabel, model);
            AutomationProperties.SetName(modelLabel, row.Text["FieldModel"] + ": " + model); Grid.SetColumn(modelLabel, 1); identity.Children.Add(modelLabel);
        }
        header.Children.Add(identity);
        var time = new TextBlock { Text = row.Time, VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        time.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        Grid.SetColumn(time, 1); header.Children.Add(time);
        var copy = new Button { Content = new FontIcon { IsTextScaleFactorEnabled = false, Glyph = "\uE8C8", FontSize = 16 }, MinHeight = 40, MinWidth = 40, Padding = new Thickness(6), Style = (Style)Application.Current.Resources["IconButtonStyle"] };
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
        if (row.Turn is null) Content = border;
        else { var body = new StackPanel { Spacing = 2 }; body.Children.Add(border); body.Children.Add(TurnDetailsPanel.Button(row)); Content = body; }
    }
    private static DataTemplate? _workItemTemplate;
    private static Style? _workItemStyle;
    private static FrameworkElement ToolGroup(EntryRow row)
    {
        if (!row.IsWorkProcess) return ToolRow(row.Activities[0], row);
        // One inline disclosure per operation, without nested group cards or repeated role/time headers.
        var template = _workItemTemplate ??= (DataTemplate)Microsoft.UI.Xaml.Markup.XamlReader.Load("<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='using:CodexMonitor.App'><local:TranscriptCard Row='{Binding}'/></DataTemplate>");
        var itemStyle = _workItemStyle ??= (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load("<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:local='using:CodexMonitor.App' TargetType='local:RepeaterListItem'><Setter Property='HorizontalContentAlignment' Value='Stretch'/><Setter Property='Padding' Value='0'/><Setter Property='Margin' Value='0'/><Setter Property='MinHeight' Value='40'/></Style>");
        if (itemStyle.BasedOn is null) itemStyle.BasedOn = (Style)Application.Current.Resources["RepeaterRowStyle"];
        var list = new RepeaterList { MaxHeight = 480, SelectionMode = ListViewSelectionMode.None, IsTabStop = false, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Padding = new Thickness(0), ItemTemplate = template, ItemContainerStyle = itemStyle,
            Transitions = [], ItemsSource = new ObservableCollection<EntryRow>(row.Activities.Select(entry => new EntryRow(entry, row.Text, expanded: row.Expanded, live: row.IsLive, scrollOffsets: row.ScrollOffsets, historyChanged: row.HistoryChanged, canFollow: row.CanFollow))) };
        list.Repeater.Layout = new ViewportStackLayout(list);
        list.Scroll.ViewChanged += (_, _) => list.Repeater.InvalidateMeasure();
        ScrollViewer.SetIsVerticalScrollChainingEnabled(list, true);
        var userScrolling = false; var pointerActive = false;
        list.NavigationRequested += forward => { userScrolling = true; if (!forward) row.Earlier?.Invoke(); };
        list.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => userScrolling = true), true);
        list.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => { pointerActive = true; userScrolling = true; }), true);
        list.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => { pointerActive = false; if (userScrolling && list.Scroll.VerticalOffset < 160) row.Earlier?.Invoke(); }), true);
        list.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, _) => pointerActive = false), true);
        list.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => userScrolling = true), true);
        list.Loaded += (_, _) =>
        {
            var scroll = ScrollViewerIn(list); if (scroll is null) return;
            var savedOffset = row.ScrollOffsets.GetValueOrDefault(row.Entry.Id);
            var restoring = true; var previousOffset = scroll.VerticalOffset; var initialInput = list.InputVersion; var retired = false;
            EventHandler<ScrollViewerViewChangedEventArgs> viewChanged = (_, args) =>
            {
                if (list.InputVersion != initialInput) restoring = false;
                if (!restoring && list.IsLoaded && scroll.ScrollableHeight > 0)
                {
                    if (!args.IsIntermediate) row.ScrollOffsets[row.Entry.Id] = scroll.VerticalOffset;
                    if (userScrolling && !list.IsRestoringPosition) row.HistoryChanged?.Invoke(scroll.ScrollableHeight - scroll.VerticalOffset > 24);
                    if (userScrolling && !list.IsRestoringPosition && scroll.VerticalOffset < previousOffset && scroll.VerticalOffset < 160) row.Earlier?.Invoke();
                }
                previousOffset = scroll.VerticalOffset;
                if (!args.IsIntermediate && !pointerActive) userScrolling = false;
            };
            scroll.ViewChanged += viewChanged;
            list.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
            {
                try
                {
                    int stable = 0;
                    for (int attempt = 0; attempt < 24 && !retired && list.IsLoaded && list.InputVersion == initialInput; attempt++)
                    {
                        list.UpdateLayout();
                        if (scroll.ViewportHeight > 0 && scroll.ExtentHeight > 0)
                        {
                            var target = row.CanFollow?.Invoke() == true ? scroll.ScrollableHeight : Math.Min(savedOffset, scroll.ScrollableHeight);
                            if (Math.Abs(scroll.VerticalOffset - target) < .5) { if (++stable >= 2) break; }
                            else { stable = 0; scroll.ChangeView(null, target, null, true); }
                        }
                        await Task.Delay(16);
                    }
                }
                finally { restoring = false; }
            });
            RoutedEventHandler? unloaded = null;
            unloaded = (_, _) => { retired = true; scroll.ViewChanged -= viewChanged; list.Unloaded -= unloaded; };
            list.Unloaded += unloaded;
        };
        return new ActivityGroupView(row, list);
    }
    private static ScrollViewer? ScrollViewerIn(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is ScrollViewer scroll) return scroll; if (ScrollViewerIn(child) is { } nested) return nested; }
        return null;
    }
    private static IEnumerable<DependencyObject> VisualChildren(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in VisualChildren(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
    private static FrameworkElement ToolRow(TranscriptEntry entry, EntryRow row)
    {
        if (entry.Tool?.State == ToolActivityState.Generating)
        {
            var working = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Padding = new Thickness(10, 8, 10, 8) };
            var ring = new ProgressRing { Width = 16, Height = 16, IsActive = row.IsLive, Visibility = row.IsLive ? Visibility.Visible : Visibility.Collapsed };
            AutomationProperties.SetName(ring, row.Text["Working"]); working.Children.Add(ring);
            working.Children.Add(new TextBlock { Text = row.Text[row.IsLive ? "Working" : "IncompleteActivity"], VerticalAlignment = VerticalAlignment.Center });
            AutomationProperties.SetName(working, row.Text[row.IsLive ? "Working" : "IncompleteActivity"]); AutomationProperties.SetAutomationId(working, "Working-" + entry.Id);
            return working;
        }
        if (entry.Kind == EntryKind.Reasoning)
        {
            var panel = new StackPanel { Spacing = 4 };
            var readableSummary = entry.Reasoning?.Summary ?? entry.Text;
            if (!string.IsNullOrWhiteSpace(readableSummary)) panel.Children.Add(new Border { Padding = new Thickness(10, 6, 10, 8), Child = Markdown(readableSummary) });
            if (entry.Reasoning?.FullText is { Length: > 0 } full)
                panel.Children.Add(Expandable(entry.Id + ":thinking", ActivityHeader(row.Text["Thinking"], ToolActivityState.Unknown, row.Text, null, "Thinking"), row, () =>
                {
                    var scroll = new ScrollViewer { Content = Markdown(full), MaxHeight = 320, IsVerticalScrollChainingEnabled = true, HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
                    ScrollAnchor.EnableWheelChaining(scroll);
                    AutomationProperties.SetName(scroll, row.Text["Thinking"]);
                    scroll.ViewChanged += (_, _) => { if (scroll.ScrollableHeight - scroll.VerticalOffset > 24 && AccessibilitySupport.ContainsFocus(scroll)) row.HistoryChanged?.Invoke(true); };
                    return scroll;
                }, row.Text["Thinking"]));
            return panel;
        }
        var tool = entry.Tool;
        if (tool is { State: ToolActivityState.Running } && !row.IsLive) tool = tool with { State = ToolActivityState.Unknown };
        if (tool?.WaitDurationMs is { } wait) return WaitRow(entry, tool, wait, row);
        var label = tool is null ? entry.Label ?? row.Text["Tool"] : tool.CategoryKey == "ToolDetails" ? tool.Name : row.Text[tool.CategoryKey];
        if (tool is { State: ToolActivityState.Completed or ToolActivityState.Running })
            label = row.Text[(tool.CategoryKey, tool.State) switch
            {
                ("ToolFileChanges", ToolActivityState.Completed) => "ToolEdited", ("ToolFileChanges", _) => "ToolEditing",
                ("ToolCommand", ToolActivityState.Completed) => "ToolRan", ("ToolCommand", _) => "ToolExecuting",
                ("ToolSearch", ToolActivityState.Completed) => "ToolSearched", ("ToolSearch", _) => "ToolSearching", _ => tool.CategoryKey
            }];
        if (tool?.State == ToolActivityState.Failed && tool.CategoryKey != "EventError") label = row.Text["ToolFailed"] + " · " + label;
        if (tool?.State == ToolActivityState.Interrupted && tool.CategoryKey != "AgentInterrupted") label = row.Text["EventInterrupted"] + " · " + label;
        var summary = tool?.Summary ?? "";
        var title = label + (summary.Length > 0 ? " " + summary : "");
        if (tool is { AddedLines: { } added, RemovedLines: { } removed }) title += " +" + row.Text.Number(added) + " −" + row.Text.Number(removed);
        return Expandable(entry.Id, ActivityHeader(title, tool?.State ?? ToolActivityState.Unknown, row.Text, tool?.DurationMs, tool?.CategoryKey,
            tool?.CategoryKey is "AgentMessage" or "ToolCollaboration" ? label : null, summary), row, () =>
        {
            if (tool is not null) return ActivityDetails.Render(tool, row.Text, row.HistoryChanged);
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(Code(entry.Text));
            content.Children.Add(CopyButton(entry.Text));
            return content;
            Button CopyButton(string value)
            {
                var copy = new Button { Content = new FontIcon { IsTextScaleFactorEnabled = false, Glyph = "\uE8C8", FontSize = 16 }, HorizontalAlignment = HorizontalAlignment.Right, MinHeight = 40, MinWidth = 40, Padding = new Thickness(6), Style = (Style)Application.Current.Resources["IconButtonStyle"] };
                AutomationProperties.SetName(copy, row.Text["Copy"]); ToolTipService.SetToolTip(copy, row.Text["Copy"]);
                copy.Click += (_, _) => { var data = new Windows.ApplicationModel.DataTransfer.DataPackage(); data.SetText(value); Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(data); };
                return copy;
            }
        }, title);
    }
    private static FrameworkElement WaitRow(TranscriptEntry entry, ToolActivity tool, long durationMs, EntryRow row)
    {
        var text = row.Text;
        var duration = text.Format("ToolDuration", (durationMs / 1000d).ToString("0.#", text.Culture));
        var waiting = row.IsLive && tool.State == ToolActivityState.Running;
        var completed = tool.State == ToolActivityState.Completed;
        var title = completed ? text["ToolWaitCompleted"] + " · " + duration : waiting ? text["ToolWaiting"] + " · " + duration : text.Format("ToolWaitDuration", duration);
        if (tool.State == ToolActivityState.Failed) title = text["ToolFailed"] + " · " + title;
        if (tool.State == ToolActivityState.Interrupted) title = text["EventInterrupted"] + " · " + title;
        var header = (Grid)ActivityHeader(title, waiting ? ToolActivityState.Running : tool.State == ToolActivityState.Running ? ToolActivityState.Unknown : tool.State, text, categoryKey: "ToolWaiting");
        if (waiting)
        {
            header.Children.RemoveAt(0);
            var ring = new ProgressRing { IsActive = true, Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(ring, text["ToolWaiting"]); header.Children.Insert(0, ring);
        }
        return Expandable(entry.Id, header, row, () =>
        {
            var panel = new StackPanel { Spacing = 12 };
            panel.Children.Add(new TextBlock { Text = text["ToolWaitDurationLabel"] + " · " + duration, FontSize = 13, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(Expandable(entry.Id + ":details", new TextBlock { Text = text["ToolDetails"], FontSize = 13 }, row, () => ActivityDetails.Render(tool, text, row.HistoryChanged), text["ToolDetails"]));
            return panel;
        }, title);
    }
    private static FrameworkElement ActivityHeader(string title, ToolActivityState state, LocalizationService text, long? durationMs = null, string? categoryKey = null, string? prefix = null, string? technicalSummary = null)
    {
        var header = new Grid { ColumnSpacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var icon = ActivityIcon.Create(categoryKey ?? "ToolDetails", state, text);
        header.Children.Add(icon);
        var label = new TextBlock { Text = title, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        FrameworkElement caption = label;
        if (prefix is not null && !string.IsNullOrEmpty(technicalSummary))
        {
            var parts = new Grid { ColumnSpacing = 6 };
            parts.ColumnDefinitions.Add(new() { Width = GridLength.Auto, MaxWidth = 160 }); parts.ColumnDefinitions.Add(new());
            label.Text = prefix; parts.Children.Add(label);
            var summary = new TextBlock { Text = technicalSummary, FlowDirection = FlowDirection.LeftToRight, TextAlignment = text.IsRightToLeft ? TextAlignment.Right : TextAlignment.Left,
                FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center };
            summary.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            Grid.SetColumn(summary, 1); parts.Children.Add(summary); caption = parts;
        }
        Grid.SetColumn(caption, 1); header.Children.Add(caption);
        var metadata = state == ToolActivityState.Running ? text["ToolRunning"] : "";
        if (durationMs is { } duration) metadata += (metadata.Length > 0 ? " · " : "") + text.Format("ToolDuration", (duration / 1000d).ToString("0.#", text.Culture));
        if (metadata.Length > 0)
        {
            var elapsed = new TextBlock { Text = metadata, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
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
        var chevron = new FontIcon { IsTextScaleFactorEnabled = false, Glyph = "\uE70D", FontSize = 16, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetAccessibilityView(chevron, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        Grid.SetColumn(chevron, 1); toggleContent.Children.Add(chevron);
        var toggle = new ActivityDisclosure { Content = toggleContent, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            MinHeight = 40, Padding = new Thickness(10, 6, 10, 6), Style = (Style)Application.Current.Resources["IconButtonStyle"] };
        AutomationProperties.SetName(toggle, name); AutomationProperties.SetAutomationId(toggle, "ActivityToggle-" + id);
        AutomationProperties.SetHelpText(toggle, row.Text["ActivityDetailsHelp"]);
        if (row.Entry.Tool is { } activity && (activity.State != ToolActivityState.Running || row.IsLive))
            AutomationProperties.SetItemStatus(toggle, row.Text[activity.State switch { ToolActivityState.Completed => "ToolCompleted", ToolActivityState.Failed => "ToolFailed", ToolActivityState.Interrupted => "EventInterrupted", ToolActivityState.Running => "ToolRunning", _ => "Tool" }]);
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
            IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.LeftToRight };
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
            var block = new RichTextBlock { IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontSize = 14 };
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
        foreach (Match match in InlineMarkup.Matches(text))
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
    private static readonly Dictionary<(Type Type, string Property, string Key, Style? BasedOn), Style> ResourceStyles = [];
    private static readonly Dictionary<Style, string> ResourceStyleProperties = [];
    // ThemeResource bindings keep native contrast themes and runtime light/dark changes intact.
    public static void SetResourceReference(this FrameworkElement element, DependencyProperty property, string key)
    {
        var name = property == Border.BackgroundProperty ? "Background" : property == Border.BorderBrushProperty ? "BorderBrush" : "Foreground";
        var basis = element.Style;
        if (basis is not null && ResourceStyleProperties.GetValueOrDefault(basis) == name) basis = basis.BasedOn;
        var identity = (element.GetType(), name, key, basis);
        if (!ResourceStyles.TryGetValue(identity, out var style))
        {
            style = (Style)Microsoft.UI.Xaml.Markup.XamlReader.Load($"<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='{element.GetType().Name}'><Setter Property='{name}' Value='{{ThemeResource {key}}}'/></Style>");
            style.BasedOn = basis; ResourceStyles.Add(identity, style); ResourceStyleProperties.Add(style, name);
        }
        element.Style = style;
    }
}
