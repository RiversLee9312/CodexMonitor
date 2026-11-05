using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using CodexMonitor.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Storage.Pickers;
using Windows.System;

namespace CodexMonitor.App;

public sealed partial class MainWindow : Window
{
    private AppSettings _settings;
    private LocalizationService _text;
    private LocalSessionStore _store;
    private IServerDiscovery _serverDiscovery = new LocalServerDiscovery();
    private ICodexHomeDiscovery _homeDiscovery = new CodexHomeDiscovery();
    private Func<string, string> _resolveExecutable = StdioTransport.ResolveExecutable;
    private ISessionServerLocator _sessionServerLocator = new SessionServerLocator();
    private Func<SessionServerMatch, CancellationToken, Task<CodexClient>> _openSessionServer = null!;
    private readonly HashSet<string> _expandedActivities = [];
    private readonly Dictionary<string, double> _activityScrollOffsets = [];
    private readonly Dictionary<string, string> _sessionConnectionFailures = [];
    private readonly Dictionary<string, DateTimeOffset> _sessionServerCheckedAt = [];
    private CancellationTokenSource? _sessionAvailabilityCancellation;
    private string? _checkingSessionServerId;
    private Func<SessionInfo, CancellationToken, Task<SessionInfo>> _checkResumeState = null!;
    private bool _findingSessionServer;
    private string? _connectionEndpoint;
    private CancellationTokenSource? _sessionConnectCancellation;
    private CodexClient? _client;
    private IReadOnlyList<SessionInfo> _sessions = [];
    private SessionInfo? _selected;
    private TranscriptSnapshot? _snapshot;
    private readonly ObservableCollection<EntryRow> _messages = [];
    private readonly ObservableCollection<SessionRow> _visibleSessionRows = [];
    private readonly Dictionary<string, SessionRow> _sessionRows = [];
    private readonly List<string> _sessionOrder = [];
    private readonly TransitionCollection _noSessionTransitions = [];
    private bool _sessionBaselineLoaded;
    private readonly Dictionary<string, string> _drafts = [];
    private readonly HashSet<string> _attached = [];
    private readonly SemaphoreSlim _refreshLock = new(1);
    private readonly SemaphoreSlim _dialogs = new(1);
    private readonly Dictionary<(CodexClient Client, string Id), PendingInput> _pendingInputs = [];
    private sealed record PendingInput(CodexClient Client, UserInputRequest Request, CancellationTokenSource Cancellation);
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _selectionCts;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private ScrollViewer? _messageScroll;
    private bool _ready, _syncing, _connecting, _sending, _following = true, _closing;
    private readonly bool _reviewMode;
    private bool _cliMissing;
    private string? _availabilityAnnouncement;
    private bool _readingActivityHistory;
    private bool _latestVisible;
    private Storyboard? _latestTransition;
    private bool _jumpingLatest;
    private bool _transcriptScrollingFromInput;
    private long _latestScrollGeneration;
    private long _transcriptViewportInput;
    private long _selectedLength = -1;
    private long _transcriptReadGeneration;
    private ServerRead? _serverRead;
    private string? _historySessionId, _historyCursor;
    private CodexClient? _historyClient;
    private long? _historyStart;
    private bool _hasEarlier, _loadingEarlier, _pagedReview = false;
    private CancellationTokenSource? _earlierCancellation;
    private LocalTranscriptPager? _localPager;
    private string? _localPagerSession;
    private readonly Dictionary<string, string> _activityGroupIdentities = [];
    private double _previousTranscriptOffset;
    private readonly Dictionary<string, TranscriptEntry> _serverEvents = [];
    private long _sessionAnchorGeneration, _sessionAnchorInputVersion;
    private SessionRow? _pendingSessionAnchor;
    private double _pendingSessionAnchorY;
    private long _pendingSessionAnchorInput;
    private sealed record ServerRead(CodexClient Client, string SessionId, long Generation, List<RpcMessage> Messages);
    private Func<SessionInfo, CancellationToken, Task<TranscriptSnapshot>> _readLocalTranscript = (session, ct) => new TranscriptParser().ReadAsync(session, ct);
    private readonly Microsoft.UI.System.ThemeSettings _themeSettings;

    public MainWindow() : this(false, new()) { }
    public MainWindow(AppSettings settings) : this(false, settings) { }
    internal MainWindow(bool reviewMode) : this(reviewMode, new()) { }
    private MainWindow(bool reviewMode, AppSettings settings)
    {
        _settings = settings;
        _reviewMode = reviewMode;
        _checkResumeState = CheckResumeStateAsync;
        _openSessionServer = (match, ct) => SessionServerLocator.OpenAsync(match.Server, _settings.CodexHome, _settings.Executable, ct);
        _text = new(_settings.Language); _store = new(_settings.CodexHome, Path.Combine(Path.GetDirectoryName(AppSettings.SettingsPath)!, "session-index.json"));
        InitializeComponent();
        SessionList.LayoutUpdated += (_, _) => { if (_sessionAwaitingLayout) { _sessionAwaitingLayout = false; QueueSessionPageCheck(); } };
        SessionList.NavigationRequested += forward => { OnSessionNavigationInput(); if (forward) { ++_sessionPageIntent; QueueSessionPageCheck(); } };
        SessionList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) => { OnSessionNavigationInput(); if (e.GetCurrentPoint(SessionList).Properties.MouseWheelDelta < 0) { ++_sessionPageIntent; QueueSessionPageCheck(); } }), true);
        SessionList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => OnSessionNavigationInput()), true);
        SessionList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) => { OnSessionNavigationInput(); if (e.Key is VirtualKey.PageDown or VirtualKey.End or VirtualKey.Down) { ++_sessionPageIntent; QueueSessionPageCheck(); } }), true);
        _themeSettings = Microsoft.UI.System.ThemeSettings.CreateForWindowId(AppWindow.Id);
        _themeSettings.Changed += OnSystemThemeChanged;
        Root.GotFocus += (_, _) => AccessibilitySupport.NormalizeIconsAfterLayout(Root);
        Root.ActualThemeChanged += (_, _) => { UpdateCaptionTheme(); ApplyThemeSurface(); };
        Title = "Codex Monitor";
        ExtendsContentIntoTitleBar = true; SetTitleBar(TitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        MessageList.ItemsSource = _messages;
        MessageList.NavigationRequested += forward =>
        {
            _transcriptScrollingFromInput = true;
            ++_transcriptViewportInput; _jumpingLatest = false; ++_latestScrollGeneration;
            _following = false;
            if (forward) _earlierRequested = false; else { _following = false; RequestEarlier(); }
        };
        MessageList.Transitions = [];
        MessageList.LayoutUpdated += (_, _) => { if (_historyAwaitingLayout) { _historyAwaitingLayout = false; QueueEarlierCheck(); } };
        MessageList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, e) =>
        {
            _transcriptScrollingFromInput = true;
            ++_transcriptViewportInput;
            _jumpingLatest = false; ++_latestScrollGeneration;
            if (e.GetCurrentPoint(MessageList).Properties.MouseWheelDelta <= 0) { _earlierRequested = false; return; }
            _following = false;
            for (var node = e.OriginalSource as DependencyObject; node is not null && node != MessageList; node = VisualTreeHelper.GetParent(node))
                if (node is ScrollViewer inner && !ReferenceEquals(inner, MessageList.Scroll) && inner.VerticalOffset > 160) return;
            RequestEarlier();
        }), true);
        MessageList.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, e) =>
        { _transcriptScrollingFromInput = true; ++_transcriptViewportInput; if (e.Key is VirtualKey.PageUp or VirtualKey.Home or VirtualKey.Up) { _jumpingLatest = false; ++_latestScrollGeneration; _following = false; RequestEarlier(); } }), true);
        MessageList.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        { _transcriptScrollingFromInput = true; ++_transcriptViewportInput; _following = false; _jumpingLatest = false; ++_latestScrollGeneration; }), true);
        SessionList.Loaded += (_, _) => TrackSessionListScroll();
        SessionList.ItemsSource = _visibleSessionRows;
        SessionList.Transitions = _noSessionTransitions;
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick += async (_, _) => await RefreshAsync();
        Closed += async (_, _) =>
        {
            _closing = true; CancelEarlierRead(); RetireLocalPager(); _sessionQueryCancellation?.Cancel(); _sessionReorderTimer?.Stop(); _timer.Stop(); _lifetime.Cancel(); _selectionCts?.Cancel(); _sessionAvailabilityCancellation?.Cancel();
            _themeSettings.Changed -= OnSystemThemeChanged;
            if (_client is not null) await _client.DisposeAsync();
        };
        AddShortcut(VirtualKey.F, VirtualKeyModifiers.Control, () => ToggleFind(true));
        AddShortcut(VirtualKey.L, VirtualKeyModifiers.Control, () => { Shell.IsPaneOpen = true; SessionSearch.Focus(FocusState.Keyboard); });
        AddShortcut(VirtualKey.Enter, VirtualKeyModifiers.Control, () => _ = SendAsync());
        AddShortcut(VirtualKey.F5, VirtualKeyModifiers.None, () => _ = RefreshAsync(true));
        AddShortcut((VirtualKey)188, VirtualKeyModifiers.Control, () => _ = SettingsAsync());
        AddShortcut(VirtualKey.Escape, VirtualKeyModifiers.None, () => { ToggleFind(false); if (Shell.DisplayMode == SplitViewDisplayMode.Overlay) Shell.IsPaneOpen = false; });
#if DEBUG
        // Repeatable visual review shortcuts, excluded from Release builds and never persisted.
        var reviewModifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Menu;
        AddShortcut(VirtualKey.Number1, reviewModifiers, () => ResizeForReview(560, 600));
        AddShortcut(VirtualKey.Number2, reviewModifiers, () => ResizeForReview(760, 650));
        AddShortcut(VirtualKey.Number3, reviewModifiers, () => ResizeForReview(1180, 800));
        AddShortcut(VirtualKey.Number4, reviewModifiers, () => ResizeForReview(1440, 850));
        AddShortcut(VirtualKey.Number5, reviewModifiers, () => { _settings = _settings with { Theme = "Dark" }; Localize(); });
        AddShortcut(VirtualKey.Number6, reviewModifiers, () => { _settings = _settings with { Theme = "Light" }; Localize(); });
        AddShortcut(VirtualKey.Number7, reviewModifiers, () => { _text = new("ar-SA"); Localize(); });
        AddShortcut(VirtualKey.Number8, reviewModifiers, () => { _text = new("de-DE"); Localize(); });
        AddShortcut(VirtualKey.Number9, reviewModifiers, () => { _text = new("zh-CN"); Localize(); });
#endif
        Localize();
    }
#if DEBUG
    private void ResizeForReview(double width, double height)
    {
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var scale = Root.XamlRoot.RasterizationScale;
        var w = Math.Min((int)(width * scale), area.Width - 32); var h = Math.Min((int)(height * scale), area.Height - 32);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }
#endif
    private void AddShortcut(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var shortcut = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        shortcut.Invoked += (_, e) => { if (_dialogs.CurrentCount == 0) return; action(); e.Handled = true; };
        Root.KeyboardAccelerators.Add(shortcut);
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        AccessibilitySupport.NormalizeIconsAfterLayout(Root);
        if (_reviewMode) { _ready = true; return; }
        var scale = Root.XamlRoot.RasterizationScale;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1180 * scale), area.Width - 48);
        var height = Math.Min((int)(800 * scale), area.Height - 48);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        _ready = true; await RefreshAsync(true); _timer.Start(); await CheckCliWelcomeAsync();
    }
    private void Localize()
    {
        Root.FlowDirection = _text.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Root.Language = _text.LanguageTag;
        Root.RequestedTheme = _settings.Theme switch { "Dark" => ElementTheme.Dark, "Light" => ElementTheme.Light, _ => ElementTheme.Default };
        ApplyThemeSurface();
        AppTitle.Text = _text["AppName"]; SessionsHeading.Text = _text["Sessions"];
        SessionSearch.PlaceholderText = _text["SearchSessions"];
        AutomationProperties.SetName(SessionSearch, _text["SearchSessions"]);
        var filter = Math.Max(0, SessionFilter.SelectedIndex);
        _syncing = true; SessionFilter.Items.Clear();
        foreach (var key in new[] { "AllSessions", "Active", "Archived" }) SessionFilter.Items.Add(_text[key]);
        SessionFilter.SelectedIndex = filter; _syncing = false;
        AutomationProperties.SetName(SessionFilter, _text["SessionFilterLabel"]);
        AutomationProperties.SetName(SessionList, _text["Sessions"]); AutomationProperties.SetName(MessageList, _text["Transcript"]);
        Label(NewButton, "NewSession"); Label(RefreshButton, "Refresh"); Label(SettingsButton, "Settings"); Label(PaneButton, "ShowSessions");
        Label(FindButton, "SearchTranscript"); Label(MoreButton, "SessionActions"); Label(CloseFindButton, "Close");
        AutomationProperties.SetAcceleratorKey(FindButton, "Ctrl+F"); AutomationProperties.SetAcceleratorKey(SessionSearch, "Ctrl+L");
        AutomationProperties.SetAcceleratorKey(SettingsButton, "Ctrl+,"); AutomationProperties.SetAcceleratorKey(RefreshButton, "F5");
        AutomationProperties.SetAcceleratorKey(SendButton, "Ctrl+Enter");
        DetailsMenu.Text = _text["Details"]; ExportMenu.Text = _text["Export"]; OpenFolderMenu.Text = _text["OpenFolder"];
        UpdateDateFilterChrome();
        Label(LatestButton, "JumpToLatest"); AutomationProperties.SetName(EarlierRing, _text["LoadingEarlier"]);
        Composer.PlaceholderText = _text["MessagePlaceholder"]; AutomationProperties.SetName(Composer, _text["MessageInput"]);
        TranscriptSearch.PlaceholderText = _text["SearchTranscript"]; AutomationProperties.SetName(TranscriptSearch, _text["SearchTranscript"]);
        NoSessionsTitle.Text = _text["NoSessions"]; NoSessionsHint.Text = _text["NoSessionsHint"];
        WelcomeTitle.Text = _text["SelectSession"]; ApplyCliWelcomeState();
        NoMessagesText.Text = _text["NoMessages"]; ResumeLabel.Text = _text["Resume"];
        RestoreExplanation.Text = _text["RestoreSessionExplanation"]; ConnectExplanation.Text = _text["ConnectSessionExplanation"]; ArchiveExplanation.Text = _text["ArchivedReadOnly"];
        StopButton.Content = _text["Interrupt"];
        UpdateChrome(); UpdateTitle(); RenderMessages(); ApplyFilter(true);
        UpdateCaptionTheme();
    }
    private void UpdateCaptionTheme()
    {
        var dark = Root.ActualTheme == ElementTheme.Dark;
        var titleBar = AppWindow.TitleBar;
        titleBar.PreferredTheme = dark ? Microsoft.UI.Windowing.TitleBarTheme.Dark : Microsoft.UI.Windowing.TitleBarTheme.Light;
        // Let Windows own hover, pressed and disabled states, including contrast themes.
        titleBar.ButtonForegroundColor = null;
        titleBar.ButtonInactiveForegroundColor = null;
        titleBar.ButtonHoverForegroundColor = null;
        titleBar.ButtonHoverBackgroundColor = null;
        titleBar.ButtonPressedForegroundColor = null;
        titleBar.ButtonPressedBackgroundColor = null;
        AppWindow.TitleBar.ButtonBackgroundColor = _themeSettings.HighContrast ? null : Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = _themeSettings.HighContrast ? null : Microsoft.UI.Colors.Transparent;
    }
    private void OnSystemThemeChanged(Microsoft.UI.System.ThemeSettings sender, object args) => DispatcherQueue.TryEnqueue(() =>
    { if (!_closing) { ApplyThemeSurface(); UpdateCaptionTheme(); } });
    private void ApplyThemeSurface()
    {
        if (_themeSettings.HighContrast)
        {
            SystemBackdrop = null;
            var color = new Windows.UI.ViewManagement.UISettings().GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            Root.Background = new SolidColorBrush(color);
        }
        else
        {
            if (SystemBackdrop is not MicaBackdrop) SystemBackdrop = new MicaBackdrop();
            Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
    }
    private void Label(DependencyObject control, string key)
    {
        AutomationProperties.SetName(control, _text[key]);
        ToolTipService.SetToolTip(control, _text[key]);
    }
    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 900;
        var changed = Shell.DisplayMode != (compact ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline);
        Shell.DisplayMode = compact ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        if (changed) Shell.IsPaneOpen = !compact;
        PaneButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
    }
    private void NoticeText(string key, InfoBarSeverity severity = InfoBarSeverity.Informational, string? detail = null)
    {
        Notice.Message = _text[key].Contains("{0}", StringComparison.Ordinal) ? _text.Format(key, detail ?? "0") :
            _text[key] + (string.IsNullOrEmpty(detail) ? "" : "\n" + detail);
        Notice.Severity = severity; Notice.IsOpen = true;
    }
    private async Task RefreshAsync(bool force = false)
    {
        if (!_ready || _closing || !await _refreshLock.WaitAsync(0)) return;
        try
        {
            if (_sessionCatalog is null) SetSessionLoading(true);
            var previousLocal = _knownLocalSessionIds; var scanningStore = _store;
            var scanned = await Task.Run(async () =>
            {
                var items = await scanningStore.ListAsync(_lifetime.Token).ConfigureAwait(false);
                var ids = items.Select(x => x.Id).ToHashSet(StringComparer.Ordinal);
                var added = previousLocal is null ? Array.Empty<string>() : items.Where(x => !previousLocal.Contains(x.Id) && x.CreatedAt >= _sessionStartup).Select(x => x.Id).ToArray();
                return (Items: items, Ids: ids, Added: added);
            }, _lifetime.Token);
            if (!ReferenceEquals(scanningStore, _store)) return;
            _localSessionSnapshot = scanned.Items; _knownLocalSessionIds = scanned.Ids; foreach (var id in scanned.Added) _unreadSessionIds.Add(id);
            var client = _client is { IsConnected: true } connected ? connected : null;
            if (_sessionCatalog is null || !ReferenceEquals(_sessionCatalogClient, client))
            {
                _sessionQueryCancellation?.Cancel(); _sessionCatalogClient = client; _sessionCatalogBaseline = true;
                if (!ReferenceEquals(_sessionEventClient, client)) { _sessionEventMetadata.Clear(); _sessionEventClient = client; }
                _sessionCatalog = new(scanned.Items, client is null ? null : (archived, cursor, ct) => client.ListPageAsync(archived, cursor, ct));
                foreach (var observed in _sessionEventMetadata.Values) _sessionCatalog.Observe(observed);
            }
            else _sessionCatalog.SetLocal(scanned.Items);
            if (!await ReloadSessionQueryAsync(refresh: true)) return;
            if (_selected is { } selected)
            {
                var updated = _sessions.FirstOrDefault(x => x.Id == selected.Id);
                if (updated is not null)
                {
                    _selected = updated with { ActiveTurnId = updated.ActiveTurnId ?? (_attached.Contains(updated.Id) && updated.State is SessionState.Active or SessionState.Waiting ? selected.ActiveTurnId : null) };
                    UpdateTitle(); UpdateChrome();
                    var length = updated.FileVersion?.Length ?? await Task.Run(() => RolloutFiles.Find(updated.FilePath)?.Length ?? -1, _lifetime.Token);
                    if (_client is { IsConnected: true } && _attached.Contains(updated.Id))
                    { if (force) await LoadTranscriptAsync(_selected, false); }
                    else if (!string.IsNullOrEmpty(updated.FilePath) && (length != _selectedLength || force)) await LoadTranscriptAsync(_selected, false);
                    await CheckSessionAvailabilityAsync(force);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { NoticeText("ReadFailed", InfoBarSeverity.Error); }
        finally { if (_sessionQueryCancellation is null) SetSessionLoading(false); _refreshLock.Release(); }
    }
    private void ApplyFilter(bool force = false)
    {
        if (!_ready) return;
        if (_sessionCatalog is not null) { _ = ReloadSessionQueryAsync(); return; }
        var query = CurrentSessionQuery();
        var visible = _sessions.Where(s => query.Matches(s, TimeZoneInfo.Local)).OrderByDescending(s => s.UpdatedAt).ThenByDescending(s => s.CreatedAt).ThenBy(s => s.Id, StringComparer.Ordinal).ToArray();
        ApplySessionRows(visible, true);
        SessionCount.Text = _text.Format("SessionCount", _text.Number(visible.Length));
    }
    private void ApplySessionRows(IReadOnlyList<SessionInfo> sessions, bool observeAdditions, bool observeContent = true)
    {
        var anchorGeneration = ++_sessionAnchorGeneration; var inputVersion = _sessionAnchorInputVersion;
        SessionRow? anchor = null; double anchorY = 0;
        if (!_resetSessionViewport && _pendingSessionAnchor is { } pending && _pendingSessionAnchorInput == inputVersion && _visibleSessionRows.Contains(pending))
        { anchor = pending; anchorY = _pendingSessionAnchorY; }
        else if (!_resetSessionViewport && SessionList.FirstVisibleIndex is var firstIndex && firstIndex >= 0 && firstIndex < _visibleSessionRows.Count && SessionList.ContainerFromIndex(firstIndex) is FrameworkElement first)
        { anchor = _visibleSessionRows[firstIndex]; anchorY = first.TransformToVisual(SessionList).TransformPoint(new(0, 0)).Y; }
        var connected = _client is { IsConnected: true }; var ids = sessions.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var added = sessions.Where(s => !_sessionRows.ContainsKey(s.Id)).ToArray();
        var announceNew = observeAdditions && _sessionBaselineLoaded && added.Length > 0;
        var visible = sessions.Select(session =>
        {
            if (_sessionRows.TryGetValue(session.Id, out var row)) row.Update(session, _text, connected, _selected?.Id == session.Id, observeContent);
            else _sessionRows[session.Id] = row = new(session, _text, connected, _selected?.Id != session.Id && (_unreadSessionIds.Contains(session.Id) || observeAdditions && _sessionBaselineLoaded));
            return row;
        }).ToArray();
        // Native KeepItemsInView handles tail appends. Re-anchoring those pages forces realization
        // and fights an in-flight wheel gesture. Correct only changes before an existing row.
        var appendOnly = visible.Length >= _visibleSessionRows.Count && _visibleSessionRows.Select((row, i) => ReferenceEquals(row, visible[i])).All(same => same);
        var structureChanged = !appendOnly;
        if (structureChanged || visible.Length != _visibleSessionRows.Count) _sessionAwaitingLayout = true;
        _sessionBaselineLoaded = true; _syncing = true;
        try
        {
            SessionList.Transitions = _noSessionTransitions;
            for (int i = _visibleSessionRows.Count - 1; i >= 0; i--) if (!ids.Contains(_visibleSessionRows[i].Session.Id)) _visibleSessionRows.RemoveAt(i);
            for (int i = 0; i < visible.Length; i++)
            {
                if (i < _visibleSessionRows.Count && ReferenceEquals(_visibleSessionRows[i], visible[i])) continue;
                var oldIndex = _visibleSessionRows.IndexOf(visible[i]);
                if (oldIndex >= 0) _visibleSessionRows.Move(oldIndex, i); else _visibleSessionRows.Insert(i, visible[i]);
            }
            if (_selected is not null && visible.FirstOrDefault(row => row.Session.Id == _selected.Id) is { } selected) SessionList.SelectedItem = selected;
        }
        finally { _syncing = false; }
        // Row objects only cover loaded matches; the lightweight metadata index remains in the worker catalog.
        foreach (var id in _sessionRows.Keys.Where(id => !ids.Contains(id) && id != _selected?.Id).ToArray()) _sessionRows.Remove(id);
        if (anchor is not null && (structureChanged || ReferenceEquals(anchor, _pendingSessionAnchor)) && _visibleSessionRows.Contains(anchor))
        {
            var captured = anchor;
            _restoringSessionAnchor = anchorGeneration;
            _pendingSessionAnchor = anchor; _pendingSessionAnchorY = anchorY; _pendingSessionAnchorInput = inputVersion;
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, async () =>
            {
                try
                {
                if (_closing || anchorGeneration != _sessionAnchorGeneration || inputVersion != _sessionAnchorInputVersion) return;
                SessionList.ScrollIntoView(captured, ScrollIntoViewAlignment.Leading);
                for (int attempt = 0; attempt < 16; attempt++)
                {
                    if (_closing || anchorGeneration != _sessionAnchorGeneration || inputVersion != _sessionAnchorInputVersion || !_visibleSessionRows.Contains(captured)) return;
                    SessionList.UpdateLayout();
                    if (SessionList.ContainerFromItem(captured) is FrameworkElement current)
                    {
                        var y = current.TransformToVisual(SessionList).TransformPoint(new(0, 0)).Y;
                        DependencyObject? parent = SessionList.Repeater;
                        while (parent is not null && parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
                        if (parent is ScrollViewer scroll)
                        {
                            if (attempt >= 2 && Math.Abs(y - anchorY) < 1) { _pendingSessionAnchor = null; return; }
                            scroll.ChangeView(null, scroll.VerticalOffset + y - anchorY, null, true);
                        }
                    }
                    await Task.Delay(16);
                }
                }
                finally
                {
                    if (_restoringSessionAnchor == anchorGeneration)
                    { _sessionPreviousOffset = _sessionScroll?.VerticalOffset ?? _sessionPreviousOffset; _restoringSessionAnchor = 0; }
                }
            });
        }
        else { _pendingSessionAnchor = null; _restoringSessionAnchor = 0; }
        NoSessionsPanel.Visibility = visible.Length == 0 && !_sessionQueryLoading ? Visibility.Visible : Visibility.Collapsed;
        if (announceNew) AccessibilitySupport.Announce(SessionCount, _text.Format("NewSessionsAvailable", _text.Number(added.Length)), "new-sessions");
    }
    private void OnSessionSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    { if (!_syncing) ScheduleSessionQuery(true, args.Reason == AutoSuggestionBoxTextChangeReason.UserInput); }
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) { if (!_syncing) ScheduleSessionQuery(true); }
    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync(true);
    private async void OnSessionSelected(object sender, EventArgs e)
    {
        if (_syncing || SessionList.SelectedItem is not SessionRow row) return;
        _sessionConnectCancellation?.Cancel();
        _sessionAvailabilityCancellation?.Cancel();
        row.MarkRead(); _unreadSessionIds.Remove(row.Session.Id);
        if (_selected is not null) _drafts[_selected.Id] = Composer.Text;
        _selected = row.Session; _snapshot = null; _selectedLength = -1; _following = true;
        _readingActivityHistory = false;
        _availabilityAnnouncement = null;
        Composer.Text = _drafts.GetValueOrDefault(_selected.Id, "");
        TranscriptSearch.Text = ""; Notice.IsOpen = false;
        WelcomePanel.Visibility = Visibility.Collapsed; Conversation.Visibility = Visibility.Visible;
        UpdateTitle(); UpdateChrome();
        if (Shell.DisplayMode == SplitViewDisplayMode.Overlay) Shell.IsPaneOpen = false;
        var availability = CheckSessionAvailabilityAsync();
        await LoadTranscriptAsync(_selected, true);
        await availability;
    }
    private async Task LoadTranscriptAsync(SessionInfo session, bool showLoading, IReadOnlyList<RpcMessage>? initialNotifications = null)
    {
        _transcriptScrollingFromInput = false;
        var generation = ++_transcriptReadGeneration;
        CancelEarlierRead();
        if (_localPagerSession != session.Id) RetireLocalPager();
        _selectionCts?.Cancel(); _selectionCts?.Dispose();
        var cts = _selectionCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (showLoading) { _messages.Clear(); LoadingRing.IsActive = true; LoadingRing.Visibility = Visibility.Visible; }
        try
        {
            TranscriptSnapshot snapshot;
            var connected = _client is { IsConnected: true } current ? current : null;
            var file = await Task.Run(() => RolloutFiles.Find(session.FilePath), cts.Token);
            if (_selected?.Id != session.Id || generation != _transcriptReadGeneration || cts.IsCancellationRequested) return;
            var sourceClient = connected is not null && (_attached.Contains(session.Id) || file is null) ? connected : null;
            ServerRead? reading = null; ServerTranscript? serverSnapshot = null;
            if (sourceClient is not null)
            {
                reading = _serverRead = new(sourceClient, session.Id, generation, initialNotifications?.ToList() ?? new List<RpcMessage>());
                serverSnapshot = await sourceClient.ReadTranscriptPageAsync(session.Id, ct: cts.Token);
                var sameSource = _historySessionId == session.Id && ReferenceEquals(_historyClient, sourceClient);
                snapshot = new(session, sameSource && _snapshot is not null ? ServerTranscript.Merge(_snapshot.Entries, serverSnapshot.Entries, false) : serverSnapshot.Entries);
                if (!ReferenceEquals(sourceClient, _client) || !sourceClient.IsConnected || generation != _transcriptReadGeneration || _selected?.Id != session.Id) return;
                if (!sameSource) { ResetHistory(session.Id, sourceClient); _historyCursor = serverSnapshot.NextCursor; _hasEarlier = _historyCursor is not null; }
            }
            else if (file is not null)
            {
                var initialLength = file.Length;
                LocalTranscriptPage? localPage = null;
                if (!_reviewMode || _pagedReview)
                {
                    var start = _historySessionId == session.Id && _historyClient is null ? _historyStart : null;
                    localPage = await GetLocalPager(session.Id).ReadAsync(session, start, ct: cts.Token);
                    snapshot = localPage.Snapshot;
                }
                else snapshot = await Task.Run(() => _readLocalTranscript(session, cts.Token), cts.Token);
                if (_selected?.Id != session.Id || cts.IsCancellationRequested || generation != _transcriptReadGeneration || _client is { IsConnected: true } && _attached.Contains(session.Id)) return;
                if (_historySessionId != session.Id || _historyClient is not null) ResetHistory(session.Id, null);
                if (localPage is not null) { _historyStart = localPage.StartOffset; _hasEarlier = localPage.HasEarlier; }
                _selectedLength = initialLength;
                if (session.IsLive && _client is { IsConnected: true }) snapshot = snapshot with { Session = session };
            }
            else throw new IOException("Rollout file unavailable");
            if (_selected?.Id != session.Id || cts.IsCancellationRequested || generation != _transcriptReadGeneration) return;
            _snapshot = snapshot; _selected = snapshot.Session;
            if (reading is not null && ReferenceEquals(_serverRead, reading))
            {
                _serverRead = null;
                foreach (var notification in reading.Messages.Where(serverSnapshot!.ShouldReplay)) HandleMessage(reading.Client, notification);
            }
            RenderMessages(showLoading); UpdateTitle(); UpdateChrome();
            if (snapshot.InvalidRecords > 0) NoticeText("ReadWarnings", InfoBarSeverity.Warning, _text.Number(snapshot.InvalidRecords));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RpcException)
        { if (_selected?.Id == session.Id && generation == _transcriptReadGeneration && !cts.IsCancellationRequested) NoticeText("ReadFailed", InfoBarSeverity.Error); }
        finally
        {
            if (_serverRead?.Generation == generation) _serverRead = null;
            if (_selectionCts == cts) { LoadingRing.IsActive = false; LoadingRing.Visibility = Visibility.Collapsed; }
        }
    }
    private void RenderMessages(bool reset = false)
    {
        if (_snapshot is null) { _messages.Clear(); return; }
        var query = TranscriptSearch.Text.Trim();
        var entries = _snapshot.Entries.Where(e => query.Length == 0 || e.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        var viewingId = _selected?.Id;
        var authority = _historyClient;
        var rows = EntryRow.Project(entries, _text, _expandedActivities, _client is { IsConnected: true } && _selected is { IsLive: true, State: SessionState.Active or SessionState.Waiting }, _activityScrollOffsets,
            reading =>
            {
                if (_selected?.Id != viewingId) return;
                _readingActivityHistory = reading;
                var atEnd = !reading && _messageScroll is { } viewport && viewport.ScrollableHeight - viewport.VerticalOffset < 24;
                _following = atEnd; SetLatestVisible(!atEnd);
            }, () => _following, _selected?.ActiveTurnId, _activityGroupIdentities, RequestEarlier,
            id => authority is null ? _snapshot.Turns.GetValueOrDefault(id) : authority.Models.Details(viewingId!, id),
            authority is null ? null : async (info, ct) =>
            {
                if (!ReferenceEquals(authority, _historyClient) || _selected?.Id != viewingId || !authority.IsConnected) return info;
                var detail = await authority.ReadTurnInfoAsync(viewingId!, info.Id, info.FirstRecord, ct);
                if (!ReferenceEquals(authority, _historyClient) || _selected?.Id != viewingId) return info;
                return detail is null ? info : detail with { FirstRecord = info.FirstRecord, LastRecord = info.LastRecord, LoadedItems = info.LoadedItems, Model = detail.Model ?? info.Model };
            }, _settings.AutoCollapseWork);
        if (reset) _messages.Clear();
        var anchor = !_following && !reset ? ScrollAnchor.Capture(MessageList) : null;
        var viewportInput = _transcriptViewportInput; var sourceGeneration = _transcriptReadGeneration;
        foreach (var container in MessageList.RealizedElements)
            if (container.Content is EntryRow row) TranscriptCard.PreserveScroll(container, row);
        if (_messages.Count != rows.Count || !_messages.Select(e => e.Entry.Id).SequenceEqual(rows.Select(e => e.Entry.Id))) _historyAwaitingLayout = true;
        var restoring = MessageList.BeginRestoration();
        EntryRows.Synchronize(_messages, rows);
        MessageList.EndRestoration(restoring);
        anchor?.Restore(() => viewportInput == _transcriptViewportInput && sourceGeneration == _transcriptReadGeneration && _selected?.Id == viewingId);
        NoMessagesText.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_following && query.Length == 0) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ScrollLatest);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            TrackTranscriptScroll();
            QueueEarlierCheck();
            if (_messageScroll is not null) SetLatestVisible(_readingActivityHistory || !_following && _messageScroll.ScrollableHeight - _messageScroll.VerticalOffset >= 24);
        });
    }
    private void ResetHistory(string id, CodexClient? client)
    {
        _historySessionId = id; _historyClient = client; _historyStart = null; _historyCursor = null; _hasEarlier = false;
        _activityGroupIdentities.Clear();
    }
    private void RequestEarlier()
    {
        if (!_hasEarlier || _snapshot is null) return;
        _earlierRequested = true; QueueEarlierCheck();
    }
    private async Task LoadEarlierAsync()
    {
        if (_selected is not { } session || _loadingEarlier || !_hasEarlier) return;
        _loadingEarlier = true; EarlierRing.IsActive = true; EarlierRing.Visibility = Visibility.Visible;
        var generation = _transcriptReadGeneration; var client = _historyClient;
        var start = _historyStart; var cursor = _historyCursor;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_selectionCts?.Token ?? _lifetime.Token);
        _earlierCancellation = cancellation;
        var ct = cancellation.Token;
        try
        {
            if (client is not null)
            {
                var page = await client.ReadTranscriptPageAsync(session.Id, cursor, ct);
                if (ct.IsCancellationRequested || generation != _transcriptReadGeneration || _selected?.Id != session.Id || !ReferenceEquals(client, _historyClient) || !client.IsConnected) return;
                _snapshot = _snapshot! with { Entries = ServerTranscript.Merge(_snapshot!.Entries, page.Entries, true) };
                _historyCursor = page.NextCursor; _hasEarlier = page.NextCursor is not null;
            }
            else
            {
                var page = await GetLocalPager(session.Id).ReadAsync(session, start, older: true, ct: ct);
                if (ct.IsCancellationRequested || generation != _transcriptReadGeneration || _selected?.Id != session.Id || _historyClient is not null) return;
                _historyStart = page.StartOffset; _hasEarlier = page.HasEarlier; _snapshot = page.Snapshot;
            }
            RenderMessages();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RpcException)
        { if (!ct.IsCancellationRequested && generation == _transcriptReadGeneration && _selected?.Id == session.Id) NoticeText("ReadFailed", InfoBarSeverity.Warning); }
        finally { if (ReferenceEquals(_earlierCancellation, cancellation)) { _earlierCancellation = null; _loadingEarlier = false; EarlierRing.IsActive = false; EarlierRing.Visibility = Visibility.Collapsed; QueueEarlierCheck(); } }
    }
    private LocalTranscriptPager GetLocalPager(string sessionId)
    {
        if (_localPagerSession != sessionId) RetireLocalPager();
        _localPagerSession = sessionId; return _localPager ??= new(retainSnapshot: true);
    }
    private void RetireLocalPager()
    { if (_localPager is { } old) { _localPager = null; _localPagerSession = null; _ = old.DisposeAsync(); } }
    private void CancelEarlierRead()
    { _earlierRequested = false; _historyAwaitingLayout = false; _earlierCancellation?.Cancel(); _earlierCancellation = null; _loadingEarlier = false; EarlierRing.IsActive = false; EarlierRing.Visibility = Visibility.Collapsed; }
    private void UpdateTitle()
    {
        SessionTitle.Text = _selected is null ? _text["Transcript"] : new SessionRow(_selected, _text).Title;
        SessionSubtitle.Text = _selected is null ? "" : $"{_selected.FolderName} · {_text.State(_selected.State)}";
        ToolTipService.SetToolTip(SessionTitle, SessionTitle.Text); ToolTipService.SetToolTip(SessionSubtitle, SessionSubtitle.Text);
        FindButton.IsEnabled = MoreButton.IsEnabled = _selected is not null;
    }
    private void UpdateChrome()
    {
        var actionHadFocus = AccessibilitySupport.ContainsFocus(SessionSetupActions) || AccessibilitySupport.ContainsFocus(ComposerActions) || AccessibilitySupport.ContainsFocus(Composer);
        var connected = _client is { IsConnected: true };
        foreach (var row in _sessionRows.Values) row.SetConnection(connected);
        DisconnectMenu.Text = _text["Disconnect"]; DisconnectMenu.IsEnabled = connected && !_connecting && !_findingSessionServer;
        DisconnectMenu.IsEnabled &= !_sending; SettingsButton.IsEnabled = !_connecting && !_findingSessionServer && !_sending;
        NewButton.IsEnabled = !_connecting && !_findingSessionServer && !_sending;
        var attached = connected && _selected is not null && _attached.Contains(_selected.Id) && !_selected.IsArchived;
        var archived = _selected?.IsArchived == true;
        var showResume = _selected is not null && SessionConnectionPolicy.CanResume(_selected);
        var showConnect = _selected is not null && !archived && !showResume;
        var failure = _selected is not null ? _sessionConnectionFailures.GetValueOrDefault(_selected.Id) : null;
        var checking = showConnect && failure is null && !_sessionServerCheckedAt.ContainsKey(_selected!.Id) && !_findingSessionServer;
        var needsTurn = attached && (_selected?.IsLive != true || _selected.State is SessionState.Active or SessionState.Waiting && string.IsNullOrEmpty(_selected.ActiveTurnId));
        if (needsTurn) { showResume = false; showConnect = true; }
        if (needsTurn) checking = false;
        SessionSetupHelp.Visibility = (!attached || needsTurn) && !archived && failure is null && !checking ? Visibility.Visible : Visibility.Collapsed;
        SessionSetupActions.Visibility = (!attached || needsTurn) && !archived ? Visibility.Visible : Visibility.Collapsed;
        Composer.Visibility = ComposerActions.Visibility = attached && !needsTurn ? Visibility.Visible : Visibility.Collapsed;
        ArchiveExplanation.Visibility = archived ? Visibility.Visible : Visibility.Collapsed;
        ResumeButton.Visibility = RestoreExplanation.Visibility = showResume && failure is null ? Visibility.Visible : Visibility.Collapsed;
        SessionConnectButton.Visibility = ConnectExplanation.Visibility = showConnect && failure is null && !checking ? Visibility.Visible : Visibility.Collapsed;
        SessionServerChecking.Visibility = !attached && checking ? Visibility.Visible : Visibility.Collapsed;
        SessionServerCheckingRing.IsActive = !attached && checking;
        SessionServerCheckingText.Text = _text["FindingSessionServer"];
        SessionConnectionUnavailable.Visibility = !archived && failure is not null ? Visibility.Visible : Visibility.Collapsed;
        SessionConnectionReason.Text = failure is not null ? _text[failure] : "";
        SessionConnectLabel.Text = _text[_findingSessionServer ? "FindingSessionServer" : _connecting ? "SessionConnecting" : needsTurn ? "ReconnectSession" : "ConnectSession"];
        ConnectExplanation.Text = _text[needsTurn ? "ReconnectSessionExplanation" : "ConnectSessionExplanation"];
        ResumeLabel.Text = _text[_sending && !attached ? "Loading" : "Resume"];
        RetrySessionConnectionMenu.Text = _text["CheckAgain"];
        RetrySessionConnectionMenu.Visibility = failure is not null || needsTurn ? Visibility.Visible : Visibility.Collapsed;
        RetrySessionConnectionMenu.IsEnabled = !_findingSessionServer && !_connecting && !_sending && _checkingSessionServerId != _selected?.Id;
        SessionConnectButton.IsEnabled = !_findingSessionServer && !_connecting && !_sending && _selected is { IsArchived: false } && (!attached || needsTurn);
        AutomationProperties.SetName(SessionConnectButton, _text[needsTurn ? "ReconnectSession" : "ConnectSessionHint"]);
        AutomationProperties.SetHelpText(SessionConnectButton, _text[needsTurn ? "ReconnectSessionExplanation" : "ConnectSessionExplanation"]);
        AutomationProperties.SetHelpText(ResumeButton, _text["RestoreSessionExplanation"]);
        ToolTipService.SetToolTip(SessionConnectButton, needsTurn ? _text["ReconnectSessionExplanation"] : attached ? _connectionEndpoint ?? _text["SessionConnected"] : _text["ConnectSessionHint"]);
        var active = _selected?.State is SessionState.Active or SessionState.Waiting;
        var canSteer = active && !string.IsNullOrEmpty(_selected?.ActiveTurnId);
        Composer.IsEnabled = attached && _selected?.IsLive == true && (!active || canSteer) && !_sending && !_pendingInputs.Values.Any(p => p.Request.ThreadId == _selected?.Id);
        SendButton.IsEnabled = Composer.IsEnabled && !string.IsNullOrWhiteSpace(Composer.Text);
        SendButton.Content = _text[canSteer ? "Steer" : "Send"];
        StopButton.Visibility = attached && canSteer ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = !_sending;
        ResumeButton.IsEnabled = !_sending && !_connecting && !_findingSessionServer && showResume;
        ToolTipService.SetToolTip(ResumeButton, _text["RestoreSessionExplanation"]);
        ComposerHint.Text = _text[attached && _selected?.IsLive != true || active && !canSteer ? "RuntimeTurnUnavailable" : canSteer ? "SteerHint" : "SendHint"];
        AutomationProperties.SetHelpText(Composer, ComposerHint.Text);
        if (actionHadFocus && !Composer.IsEnabled && !ResumeButton.IsEnabled && SessionConnectButton.Visibility != Visibility.Visible)
            MoreButton.Focus(FocusState.Programmatic);
        var announcement = failure is null ? null : _selected?.Id + ":" + failure;
        if (announcement != _availabilityAnnouncement && failure is not null)
            AccessibilitySupport.Announce(SessionConnectionReason, _text[failure], "session-connection");
        _availabilityAnnouncement = announcement;
    }
    private void OnComposerChanged(object sender, TextChangedEventArgs e) { if (_ready) UpdateChrome(); }
    private async void OnConnect(object sender, RoutedEventArgs e) => await ConnectAsync();
    private async Task ConnectAsync(string? sessionActionId = null)
    {
        if (_connecting) return;
        _connecting = true; UpdateChrome();
        try
        {
            var wasConnected = _client is { IsConnected: true };
            if (_client is not null) { var old = _client; _client = null; _attached.Clear(); ResetSessionSource(); InvalidateTranscriptRead(); await old.DisposeAsync(); }
            _serverEvents.Clear();
            if (!wasConnected)
            {
                if (!_settings.UseSharedServer) _resolveExecutable(_settings.Executable);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
                IRpcTransport transport = _settings.UseSharedServer ? await SharedServerEndpoint.ConnectAsync(_settings.Endpoint, _settings.CodexHome, _settings.Executable, timeout.Token) : StdioTransport.Start(_settings.Executable, _settings.CodexHome);
                var client = new CodexClient(new JsonRpcClient(transport));
                ObserveClient(client);
                try { await client.InitializeAsync(timeout.Token); _client = client; }
                catch { await client.DisposeAsync(); throw; }
            }
            _connectionEndpoint = _settings.UseSharedServer ? _settings.Endpoint : null;
            Notice.IsOpen = false; await RefreshAsync(true);
            if (wasConnected && _selected is { FilePath.Length: > 0 } localSession) await LoadTranscriptAsync(localSession, false);
        }
        catch (FileNotFoundException) when (!_settings.UseSharedServer)
        { await ShowCliSetupAsync(); }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.Net.WebSockets.WebSocketException or RpcException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { if (sessionActionId is not null) SetSessionConnectionFailure(sessionActionId, "ConnectionFailed"); else NoticeText("ConnectionFailed", InfoBarSeverity.Error); }
        finally { _connecting = false; UpdateChrome(); }
    }
    private async void OnResume(object sender, RoutedEventArgs e) => await ResumeAsync();
    private async void OnSessionConnect(object sender, RoutedEventArgs e) => await ConnectSessionAsync();
    private async void OnRetrySessionAction(object sender, RoutedEventArgs e) => await RetrySessionActionAsync();
    private async Task RetrySessionActionAsync()
    {
        if (_selected is not { } session || _findingSessionServer || _connecting || _sending) return;
        _sessionConnectionFailures.Remove(session.Id); UpdateChrome();
        if (SessionConnectionPolicy.CanResume(session)) await ResumeAsync();
        else if (_attached.Contains(session.Id)) await ConnectSessionAsync();
        else await CheckSessionAvailabilityAsync(true);
    }
    private async Task CheckSessionAvailabilityAsync(bool force = false)
    {
        if (_selected is { } history && SessionConnectionPolicy.CanResume(history))
        {
            if (_sessionConnectionFailures.GetValueOrDefault(history.Id) is "SessionServerNotFound" or "SessionServerLookupFailed")
            { _sessionConnectionFailures.Remove(history.Id); _sessionServerCheckedAt.Remove(history.Id); UpdateChrome(); }
            return;
        }
        if (_closing || _selected is not { IsArchived: false } session ||
            _client is { IsConnected: true } && _attached.Contains(session.Id) || _findingSessionServer || _connecting || _sending) return;
        if (_checkingSessionServerId == session.Id && _sessionAvailabilityCancellation is { IsCancellationRequested: false }) return;
        if (!force && _sessionServerCheckedAt.TryGetValue(session.Id, out var checkedAt) && DateTimeOffset.UtcNow - checkedAt < TimeSpan.FromSeconds(20)) return;
        _sessionAvailabilityCancellation?.Cancel();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
        _sessionAvailabilityCancellation = cancellation; _checkingSessionServerId = session.Id;
        timeout.CancelAfter(TimeSpan.FromSeconds(25));
        if (force) { _sessionServerCheckedAt.Remove(session.Id); _sessionConnectionFailures.Remove(session.Id); }
        UpdateChrome();
        try
        {
            var ct = timeout.Token;
            var available = false;
            if (_client is { IsConnected: true } client)
            {
                try { available = (await client.LoadedThreadIdsAsync(ct)).Contains(session.Id) && (await client.RuntimeAsync(session.Id, ct)).IsLive; }
                catch (Exception e) when (e is IOException or RpcException or System.Net.WebSockets.WebSocketException) { }
            }
            if (!available) available = (await _sessionServerLocator.FindAsync(session.Id, _settings.CodexHome, _settings.Executable,
                _connectionEndpoint ?? (_settings.UseSharedServer ? _settings.Endpoint : null), ct)).Count > 0;
            // Probing never subscribes, resumes, or starts a turn. A late result must not replace another selection.
            ct.ThrowIfCancellationRequested();
            if (_selected?.Id != session.Id || _selected.IsArchived || SessionConnectionPolicy.CanResume(_selected)) return;
            _sessionServerCheckedAt[session.Id] = DateTimeOffset.UtcNow;
            if (available) _sessionConnectionFailures.Remove(session.Id);
            else SetSessionConnectionFailure(session.Id, "SessionServerNotFound");
        }
        catch (Exception e) when (e is IOException or RpcException or OperationCanceledException or ArgumentException or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException)
        {
            if (!cancellation.IsCancellationRequested && ReferenceEquals(_sessionAvailabilityCancellation, cancellation) && _selected?.Id == session.Id)
            { _sessionServerCheckedAt[session.Id] = DateTimeOffset.UtcNow; SetSessionConnectionFailure(session.Id, "SessionServerLookupFailed"); }
        }
        finally
        {
            if (ReferenceEquals(_sessionAvailabilityCancellation, cancellation)) { _sessionAvailabilityCancellation = null; _checkingSessionServerId = null; UpdateChrome(); }
        }
    }
    private void ObserveClient(CodexClient client)
    {
        client.Rpc.Message += message => DispatcherQueue.TryEnqueue(() => HandleMessage(client, message));
        client.Rpc.Disconnected += _ => DispatcherQueue.TryEnqueue(async () =>
        {
            if (!_closing && ReferenceEquals(_client, client))
            {
                _attached.Clear(); ResetSessionSource(); InvalidateTranscriptRead(); UpdateChrome(); NoticeText("DisconnectedHint", InfoBarSeverity.Warning);
                if (_selected is { FilePath.Length: > 0 } session) await LoadTranscriptAsync(session, false);
            }
        });
    }
    private void InvalidateTranscriptRead()
    { ++_transcriptReadGeneration; CancelEarlierRead(); RetireLocalPager(); _jumpingLatest = false; ++_latestScrollGeneration; _selectionCts?.Cancel(); _serverRead = null; foreach (var pending in _pendingInputs.Values) pending.Cancellation.Cancel(); if (_client is not { IsConnected: true }) _serverEvents.Clear(); }
    private async Task ConnectSessionAsync()
    {
        if (_selected is not { IsArchived: false } session || _findingSessionServer || _connecting || _sending) return;
        _sessionAvailabilityCancellation?.Cancel();
        using var cancellation = _sessionConnectCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(25));
        var ct = cancellation.Token; _findingSessionServer = true; UpdateChrome();
        _sessionConnectionFailures.Remove(session.Id); Notice.IsOpen = false; UpdateChrome();
        CodexClient? next = null;
        var joiningMessages = new List<RpcMessage>();
        try
        {
            var client = _client;
            if (client is not { IsConnected: true } || !(await client.LoadedThreadIdsAsync(ct)).Contains(session.Id))
            {
                var matches = await _sessionServerLocator.FindAsync(session.Id, _settings.CodexHome, _settings.Executable,
                    _connectionEndpoint ?? (_settings.UseSharedServer ? _settings.Endpoint : null), ct);
                if (matches.Count == 0) { SetSessionConnectionFailure(session.Id, "SessionServerNotFound"); return; }
                var match = matches[0];
                if (matches.Count > 1)
                {
                    var choices = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
                    foreach (var candidate in matches) choices.Items.Add(candidate.Server.Endpoint);
                    choices.SelectedIndex = 0; AutomationProperties.SetName(choices, _text["ChooseSessionServer"]);
                    if (await ShowDialogAsync(new ContentDialog { Title = _text["ChooseSessionServer"], Content = choices, PrimaryButtonText = _text["ConnectSession"], CloseButtonText = _text["Cancel"] }) != ContentDialogResult.Primary) return;
                    match = matches[choices.SelectedIndex];
                }
                if (_selected?.Id != session.Id) return;
                next = await _openSessionServer(match, ct); client = next;
                // Recheck after opening the actual connection. Never resume on the strength of stored history alone.
                if (!(await client.LoadedThreadIdsAsync(ct)).Contains(session.Id)) { SetSessionConnectionFailure(session.Id, "SessionServerNotFound"); return; }
                var runtime = await client.RuntimeAsync(session.Id, ct);
                if (!runtime.IsLive) { SetSessionConnectionFailure(session.Id, "SessionServerNotFound"); return; }
                Action<RpcMessage> duringJoin = message => DispatcherQueue.TryEnqueue(() =>
                {
                    if (ReferenceEquals(_client, client)) HandleMessage(client, message); else joiningMessages.Add(message);
                });
                client.Rpc.Message += duringJoin;
                var joined = await client.WithActiveTurnAsync(await client.ResumeAsync(session.Id, ct), ct);
                if (_selected?.Id != session.Id) return;
                var old = _client; _client = next; next = null; _attached.Clear(); _serverEvents.Clear(); ObserveClient(client);
                client.Rpc.Message -= duringJoin;
                _connectionEndpoint = match.Server.Endpoint;
                if (old is not null) await old.DisposeAsync();
                ApplyAttachedSession(session, joined);
            }
            else
            {
                var runtime = await client.RuntimeAsync(session.Id, ct);
                if (!runtime.IsLive) { SetSessionConnectionFailure(session.Id, "SessionServerNotFound"); return; }
                ApplyAttachedSession(session, await client.WithActiveTurnAsync(await client.ResumeAsync(session.Id, ct), ct));
            }
            Notice.IsOpen = false;
            if (_selected?.Id == session.Id) { await LoadTranscriptAsync(_selected, false, joiningMessages); Composer.Focus(FocusState.Programmatic); }
        }
        catch (OperationCanceledException) { if (!_lifetime.IsCancellationRequested && _selected?.Id == session.Id) SetSessionConnectionFailure(session.Id, "SessionServerLookupFailed"); }
        catch (Exception e) when (e is IOException or RpcException or ArgumentException or System.Net.WebSockets.WebSocketException or System.Net.Sockets.SocketException)
        { SetSessionConnectionFailure(session.Id, "SessionServerLookupFailed"); }
        finally
        {
            if (next is not null) await next.DisposeAsync();
            _sessionConnectCancellation = null; _findingSessionServer = false; UpdateTitle(); UpdateChrome();
        }
    }
    private void SetSessionConnectionFailure(string id, string key) => _sessionConnectionFailures[id] = key;
    private void ApplyAttachedSession(SessionInfo original, SessionInfo runtime)
    {
        InvalidateTranscriptRead();
        _sessionConnectionFailures.Remove(original.Id);
        _sessionServerCheckedAt[original.Id] = DateTimeOffset.UtcNow;
        _attached.Add(original.Id);
        var joined = runtime with { CreatedAt = original.CreatedAt, UpdatedAt = original.UpdatedAt, ContentRevision = original.ContentRevision, FilePath = original.FilePath, Title = original.Title, ParentId = original.ParentId, Model = runtime.Model.Length > 0 ? runtime.Model : original.Model, FileVersion = original.FileVersion };
        if (!ReferenceEquals(_sessionEventClient, _client)) { _sessionEventMetadata.Clear(); _sessionEventClient = _client; }
        _sessionCatalog?.Observe(joined); _sessionEventMetadata[joined.Id] = joined;
        _sessions = _sessions.Select(s => s.Id == original.Id ? joined : s).ToArray();
        if (_selected?.Id == original.Id) { _selected = joined; _snapshot = new(joined, Array.Empty<TranscriptEntry>()); }
        ApplyFilter();
    }
    private async Task ResumeAsync()
    {
        if (_selected is not { IsArchived: false } session || _sending || _findingSessionServer) return;
        if (!SessionConnectionPolicy.CanResume(session)) { await ConnectSessionAsync(); return; }
        var connectInstead = false;
        _sending = true; UpdateChrome();
        try
        {
            // Recheck before creating a local server, and again before resuming on a connected server.
            session = await _checkResumeState(session, _lifetime.Token);
            if (_selected?.Id != session.Id) return;
            if (!SessionConnectionPolicy.CanResume(session)) { UpdateCheckedSession(session); connectInstead = true; return; }
            if (_client is not { IsConnected: true }) await ConnectAsync(session.Id);
            if (_client is not { IsConnected: true } client || _selected?.Id != session.Id) return;
            session = await _checkResumeState(session, _lifetime.Token);
            if (_selected?.Id != session.Id) return;
            if (!SessionConnectionPolicy.CanResume(session)) { UpdateCheckedSession(session); connectInstead = true; return; }
            var result = await client.WithActiveTurnAsync(await client.ResumeAsync(session.Id, _lifetime.Token), _lifetime.Token);
            ApplyAttachedSession(session, result);
            if (_selected?.Id == session.Id)
            {
                await LoadTranscriptAsync(_selected, false); Composer.Focus(FocusState.Programmatic);
            }
        }
        catch (Exception e) when (e is IOException or RpcException or OperationCanceledException) { SetSessionConnectionFailure(session.Id, "ResumeFailed"); Notice.IsOpen = false; }
        finally
        {
            _sending = false; UpdateTitle(); UpdateChrome();
            if (connectInstead && _selected?.Id == session.Id && !_closing) await ConnectSessionAsync();
            else if (Composer.IsEnabled) Composer.Focus(FocusState.Programmatic);
        }
    }
    private async Task<SessionInfo> CheckResumeStateAsync(SessionInfo original, CancellationToken ct)
    {
        var local = (await Task.Run(() => _store.ListAsync(ct), ct)).FirstOrDefault(s => s.Id == original.Id) ?? original;
        if (_client is { IsConnected: true } client && (await client.LoadedThreadIdsAsync(ct)).Contains(original.Id))
            return (await client.RuntimeAsync(original.Id, ct)) with { FilePath = local.FilePath, Title = local.Title, ParentId = local.ParentId, FileVersion = local.FileVersion };
        return local;
    }
    private void UpdateCheckedSession(SessionInfo session)
    {
        _sessions = _sessions.Select(s => s.Id == session.Id ? session : s).ToArray();
        if (_selected?.Id == session.Id) _selected = session;
        ApplyFilter();
    }
    private async void OnSend(object sender, RoutedEventArgs e) => await SendAsync();
    private async Task SendAsync()
    {
        if (!SendButton.IsEnabled || _client is not { IsConnected: true } client || _selected is not { } session || _sending) return;
        var message = Composer.Text; _sending = true; UpdateChrome();
        try
        {
            var activeId = session.State is SessionState.Active or SessionState.Waiting ? session.ActiveTurnId : null;
            var turnId = await client.SendAsync(session.Id, message, activeId, _lifetime.Token);
            if (_selected?.Id == session.Id)
            {
                _selected = _selected with { State = SessionState.Active, ActiveTurnId = turnId };
                if (Composer.Text == message) Composer.Text = "";
                _following = true;
            }
            _drafts[session.Id] = "";
            await RefreshAsync(true);
        }
        catch (Exception e) when (e is IOException or RpcException or OperationCanceledException)
        { NoticeText("SendFailed", InfoBarSeverity.Error); }
        finally { _sending = false; UpdateTitle(); UpdateChrome(); Composer.Focus(FocusState.Programmatic); }
    }
    private async void OnStop(object sender, RoutedEventArgs e)
    {
        if (_client is not { IsConnected: true } client || _selected is not { ActiveTurnId: { Length: > 0 } turn } session) return;
        StopButton.IsEnabled = false;
        try { await client.InterruptAsync(session.Id, turn, _lifetime.Token); }
        catch (Exception ex) when (ex is IOException or RpcException or OperationCanceledException) { NoticeText("SendFailed", InfoBarSeverity.Error); }
        finally { UpdateChrome(); }
    }
    private void HandleMessage(CodexClient client, RpcMessage message)
    {
        if (!ReferenceEquals(client, _client) || _closing || !client.IsConnected) return;
        if (message.Id is not null) { _ = HandleRequestAsync(client, message); return; }
        message = RecordedEvents.Normalize(message);
        var p = message.Parameters;
        client.Models.Observe(message.Method, p);
        if (message.Method == "serverRequest/resolved" && _pendingInputs.TryGetValue((client, p.Field("requestId").GetRawText()), out var resolved)) resolved.Cancellation.Cancel();
        if (message.Method is "turn/completed" or "thread/closed" or "thread/reverted")
            foreach (var pending in _pendingInputs.Values.Where(x => x.Request.ThreadId == p.Str("threadId"))) pending.Cancellation.Cancel();
        if (ProtocolEventCatalog.Notifications.TryGetValue(message.Method, out var definition) && definition.Route == ProtocolEventRoute.Server)
        {
            if (TranscriptEvents.Parse(message.Method, p, DateTimeOffset.Now) is { } systemEvent) _serverEvents[systemEvent.Id] = systemEvent;
            return;
        }
        if (message.Method == "thread/reverted" && p.Str("threadId") == _selected?.Id)
        { ResetHistory(_selected.Id, null); _ = LoadTranscriptAsync(_selected, true); return; }
        UpdateSessionActivity(message);
        var threadId = p.Str("threadId");
        if (_selected is null || !_attached.Contains(_selected.Id) || threadId != _selected.Id && !(threadId.Length == 0 && TranscriptEvents.IsGlobal(message.Method))) return;
        if (_serverRead is { } reading && ReferenceEquals(reading.Client, client) && (reading.SessionId == threadId || threadId.Length == 0))
        { reading.Messages.Add(message); return; }
        var previous = _snapshot?.Entries ?? Array.Empty<TranscriptEntry>();
        var modelTurn = p.Str("turnId", p.Field("turn").Str("id", _selected.ActiveTurnId ?? ""));
        var entries = TranscriptUpdates.Apply(previous, message.Method, p, DateTimeOffset.Now, client.Models.ForTurn(_selected.Id, modelTurn.Length > 0 ? modelTurn : null), modelTurn);
        var modelItemId = p.Str("itemId", p.Str("item_id", p.Field("item").Str("id")));
        if (modelItemId.Length > 0) client.Models.Remember(_selected.Id, modelItemId, entries.FirstOrDefault(e => e.Id == modelItemId)?.Model);
        var contentChanged = !ReferenceEquals(previous, entries);
        if (contentChanged) _snapshot = _snapshot is null ? new(_selected, entries) : _snapshot with { Session = _selected, Entries = entries };
        switch (message.Method)
        {
            case "turn/started":
                _selected = _selected with { State = SessionState.Active, ActiveTurnId = p.Field("turn").Str("id") }; break;
            case "turn/completed":
                _selected = _selected with { ActiveTurnId = null, State = p.Field("turn").Str("status") switch { "failed" => SessionState.Error, "interrupted" => SessionState.Interrupted, _ => SessionState.Completed } }; break;
            case "thread/status/changed":
                var state = CodexClient.ParseState(p.Field("status"));
                _selected = _selected with { State = state, IsLive = state != SessionState.Unknown, ActiveTurnId = state is SessionState.Active or SessionState.Waiting ? _selected.ActiveTurnId : null }; break;
        }
        if (contentChanged || message.Method is "turn/started" or "turn/completed" or "model/rerouted" or "thread/settings/updated" or "thread/tokenUsage/updated" or "response.created" or "thread/status/changed") RenderMessages();
        UpdateTitle(); UpdateChrome();
    }
    private void UpdateSessionActivity(RpcMessage message)
    {
        var p = message.Parameters; var id = p.Str("threadId");
        if (!ReferenceEquals(_sessionEventClient, _client)) { _sessionEventMetadata.Clear(); _sessionEventClient = _client; }
        _sessionRows.TryGetValue(id, out var row);
        var session = _sessionEventMetadata.GetValueOrDefault(id) ?? row?.Session ?? _sessionMetadataIndex.GetValueOrDefault(id);
        if (session is null) return;
        var content = message.Method is "item/agentMessage/delta" or "item/reasoning/summaryTextDelta" or "item/reasoning/textDelta" or "item/plan/delta" or "item/started" or "item/completed" or "turn/plan/updated" or "turn/diff/updated" or "error" or "warning";
        var changed = message.Method switch
        {
            "thread/name/updated" => session with { Title = p.Str("threadName", session.Title) },
            "thread/archived" => session with { IsArchived = true },
            "thread/unarchived" => session with { IsArchived = false },
            "thread/closed" or "thread/deleted" => session with { IsLive = false, State = SessionState.Completed, ActiveTurnId = null },
            "turn/started" => session with { IsLive = true, State = SessionState.Active, ActiveTurnId = p.Field("turn").Str("id") },
            "turn/completed" => session with { IsLive = true, State = p.Field("turn").Str("status") switch
            { "failed" => SessionState.Error, "interrupted" => SessionState.Interrupted, _ => SessionState.Completed }, ActiveTurnId = null },
            "thread/status/changed" => session with { State = CodexClient.ParseState(p.Field("status")), IsLive = CodexClient.ParseState(p.Field("status")) != SessionState.Unknown },
            _ => session
        };
        if (content && DateTimeOffset.UtcNow - changed.UpdatedAt > TimeSpan.FromSeconds(1)) changed = changed with { UpdatedAt = DateTimeOffset.UtcNow };
        if (changed != session)
        {
            _sessionEventMetadata[id] = changed; _sessionCatalog?.Observe(changed);
            if (_sessionCatalog is null) _sessions = _sessions.Select(s => s.Id == id ? changed : s).ToArray();
            row?.Update(changed, _text, true, _selected?.Id == id);
            if (_selected?.Id == id) _selected = changed;
            QueueSessionReorder();
        }
        if (content)
        {
            row?.MarkUpdated(_selected?.Id == id);
            if (_selected?.Id != id) _unreadSessionIds.Add(id);
        }
    }
    private async Task HandleRequestAsync(CodexClient client, RpcMessage message)
    {
        try
        {
            if (message.Method is "item/tool/requestUserInput" or "tool/requestUserInput")
            {
                var key = (client, message.Id!.Value.GetRawText()); if (_pendingInputs.ContainsKey(key)) return;
                var request = UserInputRequest.Parse(message.Parameters);
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                var pending = new PendingInput(client, request, cancellation); _pendingInputs.Add(key, pending); UpdateChrome();
                try
                {
                    var panel = new UserInputPanel(request, _text, _sessions.FirstOrDefault(s => s.Id == request.ThreadId)?.Title ?? request.ThreadId);
                    var dialog = new ContentDialog { Title = _text["UserInputTitle"], Content = new ScrollViewer { Content = panel, MaxHeight = Math.Min(440, Math.Max(160, Root.ActualHeight - 240)) }, PrimaryButtonText = _text["SubmitAnswers"], CloseButtonText = _text["Cancel"], IsPrimaryButtonEnabled = false, DefaultButton = ContentDialogButton.None };
                    panel.AnswerChanged += () => dialog.IsPrimaryButtonEnabled = panel.Complete;
                    var result = await ShowDialogAsync(dialog, cancellation.Token);
                    if (!ReferenceEquals(client, _client) || !client.IsConnected) return;
                    await client.Rpc.RespondAsync(message.Id.Value, result == ContentDialogResult.Primary ? request.Respond(panel.Answers) : UserInputRequest.Cancelled(), cancellation.Token);
                }
                finally { if (_pendingInputs.GetValueOrDefault(key) == pending) _pendingInputs.Remove(key); UpdateChrome(); }
            }
            else if (message.Method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
            {
                var p = message.Parameters;
                var content = new StackPanel { Spacing = 12 };
                content.Children.Add(new TextBlock { Text = _text["ApprovalHint"], TextWrapping = TextWrapping.Wrap });
                content.Children.Add(new TextBlock { Text = _text["SessionId"] + ": " + p.Str("threadId"), IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap });
                var requestedItem = _selected?.Id == p.Str("threadId") ? _snapshot?.Entries.FirstOrDefault(x => x.Id == p.Str("itemId")) : null;
                content.Children.Add(new TextBlock { Text = (requestedItem is null ? "" : requestedItem.Text + "\n\n") + p.GetRawText(), IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FlowDirection = FlowDirection.LeftToRight });
                var result = await ShowDialogAsync(new ContentDialog { Title = _text["ApprovalTitle"], Content = new ScrollViewer { Content = content, MaxHeight = 400 }, PrimaryButtonText = _text["Approve"], CloseButtonText = _text["Decline"], DefaultButton = ContentDialogButton.Close });
                await client.Rpc.RespondAsync(message.Id!.Value, new System.Text.Json.Nodes.JsonObject { ["decision"] = result == ContentDialogResult.Primary ? "accept" : "decline" }, _lifetime.Token);
            }
            else
            {
                await client.Rpc.RejectAsync(message.Id!.Value, "Request is not supported by Codex Monitor", _lifetime.Token);
                NoticeText("UnsupportedRequest", InfoBarSeverity.Warning);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { }
    }
    private void OnMessageListLoaded(object sender, RoutedEventArgs e)
        => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, TrackTranscriptScroll);
    private void TrackTranscriptScroll()
    {
        var scroll = MessageList.Scroll;
        if (ReferenceEquals(scroll, _messageScroll)) return;
        if (_messageScroll is not null) _messageScroll.ViewChanged -= OnTranscriptViewChanged;
        _messageScroll = scroll;
        if (_messageScroll is not null) _messageScroll.ViewChanged += OnTranscriptViewChanged;
    }
    private void OnTranscriptViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (_messageScroll is null) return;
        var up = _messageScroll.VerticalOffset < _previousTranscriptOffset;
        _previousTranscriptOffset = _messageScroll.VerticalOffset;
        if (_jumpingLatest || MessageList.IsRestoringPosition) return;
        var atEnd = _messageScroll.ScrollableHeight - _messageScroll.VerticalOffset < 24;
        if (atEnd && !_readingActivityHistory) _following = true;
        SetLatestVisible(_readingActivityHistory || !_following && !atEnd);
        if (up && _transcriptScrollingFromInput && _messageScroll.VerticalOffset < Math.Max(200, _messageScroll.ViewportHeight * 2)) RequestEarlier();
        if (!args.IsIntermediate) _transcriptScrollingFromInput = false;
    }
    private void SetLatestVisible(bool visible)
    {
        if (_latestVisible == visible) return;
        var opacity = LatestButton.Opacity; var offset = LatestTranslation.Y;
        _latestVisible = visible; _latestTransition?.Stop(); LatestButton.Opacity = opacity; LatestTranslation.Y = offset;
        LatestButton.IsHitTestVisible = visible; LatestButton.IsTabStop = visible;
        if (!visible && AccessibilitySupport.ContainsFocus(LatestButton)) MessageList.Focus(FocusState.Programmatic);
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled || !Root.IsLoaded)
        { LatestButton.Opacity = visible ? 1 : 0; LatestTranslation.Y = visible ? 0 : 6; LatestButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed; return; }
        LatestButton.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation { From = LatestButton.Opacity, To = visible ? 1 : 0, Duration = TimeSpan.FromMilliseconds(120) };
        var slide = new DoubleAnimation { From = LatestTranslation.Y, To = visible ? 0 : 6, Duration = TimeSpan.FromMilliseconds(120), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, LatestButton); Storyboard.SetTargetProperty(fade, "Opacity");
        Storyboard.SetTarget(slide, LatestTranslation); Storyboard.SetTargetProperty(slide, "Y");
        var transition = _latestTransition = new Storyboard(); transition.Children.Add(fade); transition.Children.Add(slide);
        transition.Completed += (_, _) => { if (ReferenceEquals(_latestTransition, transition) && !_latestVisible) LatestButton.Visibility = Visibility.Collapsed; };
        transition.Begin();
    }
    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) return match; if (FindVisual<T>(child) is { } nested) return nested; }
        return null;
    }
    private async void ScrollLatest()
    {
        ScrollAnchor.Diagnostics?.Invoke($"latest start count={_messages.Count} following={_following} generation={_latestScrollGeneration}");
        if (_messages.Count == 0 || !_following || _closing) return;
        var generation = ++_latestScrollGeneration; _jumpingLatest = true;
        // Virtualized item heights become exact only after realization. Settle the end over a few frames,
        // while an explicit upward gesture cancels this follow immediately.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            if (_closing || generation != _latestScrollGeneration || !_following || _messages.Count == 0) return;
            MessageList.ScrollIntoView(_messages[^1], ScrollIntoViewAlignment.Leading); MessageList.UpdateLayout(); TrackTranscriptScroll();
            _messageScroll?.ChangeView(null, _messageScroll.ScrollableHeight, null, true);
            if (MessageList.ContainerFromIndex(_messages.Count - 1) is DependencyObject container) TranscriptCard.ScrollGroupToEnd(container, _messages[^1]);
            await Task.Delay(16);
        }
        if (generation == _latestScrollGeneration) { _jumpingLatest = false; _following = true; SetLatestVisible(false); }
    }
    private void OnLatest(object sender, RoutedEventArgs e) { _readingActivityHistory = false; _following = true; ScrollLatest(); SetLatestVisible(false); }
    private void OnTranscriptSearchChanged(object sender, TextChangedEventArgs e) { if (_ready) RenderMessages(true); }
    private void ToggleFind(bool open)
    {
        var wasOpen = FindPanel.Visibility == Visibility.Visible;
        FindPanel.Visibility = open && _selected is not null ? Visibility.Visible : Visibility.Collapsed;
        if (open) TranscriptSearch.Focus(FocusState.Keyboard);
        else { TranscriptSearch.Text = ""; if (wasOpen) FindButton.Focus(FocusState.Programmatic); }
        AccessibilitySupport.KeepIconsUnscaled(FindPanel);
    }
    private void OnFind(object sender, RoutedEventArgs e) => ToggleFind(true);
    private void OnCloseFind(object sender, RoutedEventArgs e) => ToggleFind(false);
    private void OnTogglePane(object sender, RoutedEventArgs e) => Shell.IsPaneOpen = !Shell.IsPaneOpen;
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog, CancellationToken cancellation = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, cancellation);
        await _dialogs.WaitAsync(linked.Token);
        var returnFocus = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        try
        {
            dialog.XamlRoot = Root.XamlRoot; dialog.RequestedTheme = Root.RequestedTheme;
            dialog.FlowDirection = Root.FlowDirection; dialog.Language = Root.Language;
            dialog.GotFocus += (_, _) => AccessibilitySupport.NormalizeIconsAfterLayout(dialog);
            linked.Token.ThrowIfCancellationRequested();
            using var registration = linked.Token.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
            var result = await dialog.ShowAsync(); linked.Token.ThrowIfCancellationRequested(); return result;
        }
        finally { _dialogs.Release(); if (!_closing) AccessibilitySupport.RestoreFocus(returnFocus, SettingsButton); }
    }
    private async void OnDetails(object sender, RoutedEventArgs e) => await DetailsAsync();
    private async Task DetailsAsync()
    {
        if (_selected is not { } s) return;
        var panel = new StackPanel { Spacing = 16, MaxWidth = 520 };
        foreach (var (key, value) in new[] { ("SessionId", s.Id), ("Folder", s.Cwd), ("Model", s.Model), ("Source", s.Source),
            ("ParentSession", s.ParentId ?? ""), ("Created", _text.DateTime(s.CreatedAt)), ("Updated", _text.DateTime(s.UpdatedAt)), ("Tokens", _text.Number(_snapshot?.Tokens ?? 0)) })
        {
            if (string.IsNullOrEmpty(value)) continue;
            var row = new StackPanel { Spacing = 4 };
            row.Children.Add(new TextBlock { Text = _text[key], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            row.Children.Add(new TextBlock { Text = value, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.LeftToRight });
            panel.Children.Add(row);
        }
        if (_serverEvents.Count > 0)
        {
            var statuses = new StackPanel { Spacing = 16 };
            foreach (var status in _serverEvents.Values)
            {
                var content = new StackPanel { Spacing = 8 };
                content.Children.Add(new TextBlock { Text = status.Label, TextWrapping = TextWrapping.Wrap, FlowDirection = FlowDirection.LeftToRight, IsTextSelectionEnabled = true });
                if (status.Tool is { } tool) content.Children.Add(ActivityDetails.Render(tool, _text));
                statuses.Children.Add(content);
            }
            var section = new Expander { Header = _text["ServerEvents"], Content = statuses, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
            AutomationProperties.SetName(section, _text["ServerEvents"]); panel.Children.Add(section);
        }
        await ShowDialogAsync(new ContentDialog { Title = _text["Details"], Content = new ScrollViewer { Content = panel, MaxHeight = 520 }, CloseButtonText = _text["Close"] });
    }
    private async void OnExport(object sender, RoutedEventArgs e)
    {
        if (_snapshot is not { } snapshot) return;
        try
        {
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "codex-" + snapshot.Session.Id };
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            picker.FileTypeChoices.Add(_text["TextFiles"], new List<string> { ".md" });
            if (await picker.PickSaveFileAsync() is { } file) { var text = _text; var markdown = await Task.Run(() => TranscriptExport.Markdown(snapshot, text)); await File.WriteAllTextAsync(file.Path, markdown); NoticeText("Exported", InfoBarSeverity.Success); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { NoticeText("ReadFailed", InfoBarSeverity.Error); }
    }
    private async void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_selected?.Cwd is { Length: > 0 } folder && await Task.Run(() => Directory.Exists(folder)))
        { try { await Launcher.LaunchFolderPathAsync(folder); } catch (Exception ex) when (ex is IOException or System.Runtime.InteropServices.COMException) { NoticeText("ReadFailed", InfoBarSeverity.Error); } }
    }
    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFolderAsync())?.Path;
    }
    private async void OnNewSession(object sender, RoutedEventArgs e)
    {
        if (_connecting || _findingSessionServer) return;
        var path = new TextBox { Header = _text["Folder"], Text = _selected?.Cwd ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), FlowDirection = FlowDirection.LeftToRight };
        var browse = new Button { Content = _text["Browse"] }; browse.Click += async (_, _) => { if (await PickFolderAsync() is { } selected) path.Text = selected; };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(path); panel.Children.Add(browse);
        if (await ShowDialogAsync(new ContentDialog { Title = _text["NewSessionTitle"], Content = panel, PrimaryButtonText = _text["Start"], CloseButtonText = _text["Cancel"], DefaultButton = ContentDialogButton.Primary }) != ContentDialogResult.Primary) return;
        var selectedPath = path.Text;
        if (!await Task.Run(() => Directory.Exists(selectedPath))) { NoticeText("InvalidHome", InfoBarSeverity.Error); return; }
        if (_client is not { IsConnected: true }) await ConnectAsync();
        if (_client is not { IsConnected: true } client) return;
        try
        {
            var session = await client.StartAsync(path.Text, _lifetime.Token); _attached.Add(session.Id);
            if (_selected is not null) _drafts[_selected.Id] = Composer.Text;
            _selected = session; _snapshot = new(session, []); Composer.Text = "";
            WelcomePanel.Visibility = Visibility.Collapsed; Conversation.Visibility = Visibility.Visible;
            RenderMessages(true); UpdateTitle(); UpdateChrome(); await RefreshAsync(true); Composer.Focus(FocusState.Programmatic);
        }
        catch (Exception ex) when (ex is IOException or RpcException or OperationCanceledException) { NoticeText("ConnectionFailed", InfoBarSeverity.Error); }
    }
    private async void OnSettings(object sender, RoutedEventArgs e) => await SettingsAsync();
    private void ApplyCliWelcomeState()
    {
        WelcomeHint.Text = _text[_cliMissing ? "CliNotInstalled" : "SelectSessionHint"];
        SetupCodexButton.Content = _text["CliSetupTitle"];
        SetupCodexButton.Visibility = _cliMissing ? Visibility.Visible : Visibility.Collapsed;
    }
    private async Task CheckCliWelcomeAsync()
    {
        _cliMissing = !_settings.UseSharedServer && !await Task.Run(() =>
        {
            try { _resolveExecutable(_settings.Executable); return true; }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or NotSupportedException or System.ComponentModel.Win32Exception) { return false; }
        });
        if (!_closing) ApplyCliWelcomeState();
    }
    private async void OnSetupCodex(object sender, RoutedEventArgs e) => await ShowCliSetupAsync();
    private async Task<string?> PickExecutableAsync()
    {
        var picker = new FileOpenPicker(); picker.FileTypeFilter.Add(".exe"); picker.FileTypeFilter.Add(".cmd");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return (await picker.PickSingleFileAsync())?.Path;
    }
    private async Task ShowCliSetupAsync()
    {
        using var setup = new CliSetupPanel(_settings.Executable, _text, PickExecutableAsync, _lifetime.Token, _resolveExecutable);
        var content = new StackPanel { Spacing = 20, MaxWidth = 520 };
        content.Children.Add(new TextBlock { Text = _text["CliSetupHelp"], TextWrapping = TextWrapping.Wrap }); content.Children.Add(setup);
        var dialog = new ContentDialog { Title = _text["CliSetupTitle"], Content = new ScrollViewer { Content = content, MaxHeight = 530 }, PrimaryButtonText = _text["Save"], CloseButtonText = _text["Cancel"] };
        dialog.Opened += async (_, _) => await setup.CheckAsync();
        if (await ShowDialogAsync(dialog) == ContentDialogResult.Primary)
        {
            _settings = _settings with { Executable = setup.Executable };
            try { await _settings.SaveAsync(); NoticeText("Saved", InfoBarSeverity.Success); }
            catch (IOException) { NoticeText("ReadFailed", InfoBarSeverity.Error); }
        }
        await CheckCliWelcomeAsync();
    }
    private async Task SettingsAsync()
    {
        if (_findingSessionServer || _connecting || _sending) return;
        var panel = new StackPanel { Spacing = 20, MaxWidth = 520 };
        var settingsOpen = true; var homeRevision = 0;
        using var home = new HomeDiscoveryPanel(_homeDiscovery, _settings.CodexHome, _text, PickFolderAsync, _lifetime.Token);
        using var exe = new CliSetupPanel(_settings.Executable, _text, PickExecutableAsync, _lifetime.Token, _resolveExecutable);
        var mode = new ComboBox { Header = _text["Connection"], HorizontalAlignment = HorizontalAlignment.Stretch };
        mode.Items.Add(_text["LocalServer"]); mode.Items.Add(_text["SharedServer"]); mode.SelectedIndex = _settings.UseSharedServer ? 1 : 0;
        using var discovery = new ServerDiscoveryPanel(_serverDiscovery, () => home.Home, () => exe.Executable, _settings.Endpoint, _text, _lifetime.Token)
        { Visibility = _settings.UseSharedServer ? Visibility.Visible : Visibility.Collapsed };
        mode.SelectionChanged += (_, _) =>
        {
            discovery.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (mode.SelectedIndex == 1) discovery.Activate(); else discovery.Deactivate();
        };
        home.HomeChanged += async () =>
        {
            var revision = ++homeRevision; discovery.Deactivate();
            await Task.Delay(400);
            if (settingsOpen && revision == homeRevision && mode.SelectedIndex == 1) discovery.Activate();
        };
        var theme = new ComboBox { Header = _text["Theme"], HorizontalAlignment = HorizontalAlignment.Stretch };
        var themes = new[] { "System", "Light", "Dark" }; foreach (var key in themes) theme.Items.Add(_text[key]); theme.SelectedIndex = Math.Max(0, Array.IndexOf(themes, _settings.Theme));
        var autoCollapse = new ToggleSwitch { Header = _text["AutoCollapseWork"], IsOn = _settings.AutoCollapseWork };
        AutomationProperties.SetAutomationId(autoCollapse, "AutoCollapseWork");
        AutomationProperties.SetName(autoCollapse, _text["AutoCollapseWork"]);
        AutomationProperties.SetHelpText(autoCollapse, _text["AutoCollapseWorkHelp"]);
        var collapseHelp = new TextBlock { Text = _text["AutoCollapseWorkHelp"], TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        collapseHelp.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var language = new ComboBox { Header = _text["Language"], HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(mode, _text["Connection"]); AutomationProperties.SetName(theme, _text["Theme"]); AutomationProperties.SetName(language, _text["Language"]);
        var languages = new[] { "" }.Concat(LocalizationService.AvailableLanguages).ToArray();
        foreach (var tag in languages) language.Items.Add(tag.Length == 0 ? _text["System"] : CultureInfo.GetCultureInfo(tag).NativeName);
        language.SelectedIndex = Math.Max(0, Array.IndexOf(languages, _settings.Language));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }; error.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        foreach (var item in new FrameworkElement[] { mode, discovery, new TextBlock { Text = _text["ConnectionHelp"], TextWrapping = TextWrapping.Wrap }, home, exe, theme, language, autoCollapse, collapseHelp, new TextBlock { Text = _text["PrivacyNote"], TextWrapping = TextWrapping.Wrap, FontSize = 12 }, error }) panel.Children.Add(item);
        var dialog = new ContentDialog { Title = _text["Settings"], Content = new ScrollViewer { Content = panel, MaxHeight = 530 }, PrimaryButtonText = _text["Save"], CloseButtonText = _text["Cancel"], DefaultButton = ContentDialogButton.Primary };
        var settingsContent = dialog.Content; var showingLicenses = false; double settingsOffset = 0;
        var licenses = new Button { Content = _text["OpenSourceLicenses"], HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(licenses, _text["OpenSourceLicenses"]); panel.Children.Add(licenses);
        licenses.Click += (_, _) =>
        {
            settingsOffset = ((ScrollViewer)settingsContent).VerticalOffset;
            showingLicenses = true; dialog.Title = _text["OpenSourceLicenses"]; dialog.Content = new LicensePanel(_text);
            dialog.PrimaryButtonText = ""; dialog.CloseButtonText = _text["Back"]; dialog.DefaultButton = ContentDialogButton.Close;
        };
        dialog.Opened += (_, _) => { if (mode.SelectedIndex == 1) discovery.Activate(); _ = home.ScanAsync(); _ = exe.CheckAsync(); };
        dialog.Closing += (_, args) =>
        {
            if (showingLicenses)
            {
                args.Cancel = true; showingLicenses = false; dialog.Title = _text["Settings"]; dialog.Content = settingsContent;
                dialog.PrimaryButtonText = _text["Save"]; dialog.CloseButtonText = _text["Cancel"]; dialog.DefaultButton = ContentDialogButton.Primary;
                DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => { ((ScrollViewer)settingsContent).ChangeView(null, settingsOffset, null, true); licenses.Focus(FocusState.Programmatic); });
                return;
            }
            settingsOpen = false; discovery.Deactivate(); home.Cancel();
        };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var errorKey = !CodexHomeDiscovery.IsValidPath(home.Home) ? "InvalidHome" : mode.SelectedIndex == 1 && !SharedServerEndpoint.IsAllowed(discovery.Endpoint, home.Home) ? "InvalidServerAddress" : "";
            if (errorKey.Length > 0) { error.Text = _text[errorKey]; error.Visibility = Visibility.Visible; args.Cancel = true; }
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var changedConnection = home.Home != _settings.CodexHome || exe.Executable != _settings.Executable || discovery.Endpoint != _settings.Endpoint || (mode.SelectedIndex == 1) != _settings.UseSharedServer;
        _settings = _settings with { CodexHome = Path.GetFullPath(home.Home), Executable = exe.Executable, UseSharedServer = mode.SelectedIndex == 1, Endpoint = discovery.Endpoint, Theme = themes[theme.SelectedIndex], Language = languages[language.SelectedIndex], AutoCollapseWork = autoCollapse.IsOn };
        try { await _settings.SaveAsync(); }
        catch (IOException) { NoticeText("ReadFailed", InfoBarSeverity.Error); return; }
        _store = new(_settings.CodexHome, Path.Combine(Path.GetDirectoryName(AppSettings.SettingsPath)!, "session-index.json")); _sessionCatalog = null; _knownLocalSessionIds = null; _text = new(_settings.Language);
        if (changedConnection) { _sessionAvailabilityCancellation?.Cancel(); _sessionServerCheckedAt.Clear(); _sessionConnectionFailures.Clear(); }
        Localize();
        if (changedConnection && _client is not null) { var old = _client; _client = null; _attached.Clear(); await old.DisposeAsync(); NoticeText("RestartConnection"); }
        else NoticeText("Saved", InfoBarSeverity.Success);
        await RefreshAsync(true); UpdateChrome(); await CheckCliWelcomeAsync();
    }
}
