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
    private AppSettings _settings = AppSettings.Load();
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
    private long _selectedLength = -1;
    private long _transcriptReadGeneration;
    private ServerRead? _serverRead;
    private readonly Dictionary<string, TranscriptEntry> _serverEvents = [];
    private sealed record ServerRead(CodexClient Client, string SessionId, long Generation, List<RpcMessage> Messages);
    private Func<SessionInfo, CancellationToken, Task<TranscriptSnapshot>> _readLocalTranscript = (session, ct) => new TranscriptParser().ReadAsync(session, ct);
    private readonly Microsoft.UI.System.ThemeSettings _themeSettings;

    public MainWindow() : this(false) { }
    internal MainWindow(bool reviewMode)
    {
        _reviewMode = reviewMode;
        _checkResumeState = CheckResumeStateAsync;
        _openSessionServer = (match, ct) => SessionServerLocator.OpenAsync(match.Server, _settings.CodexHome, _settings.Executable, ct);
        _text = new(_settings.Language); _store = new(_settings.CodexHome);
        InitializeComponent();
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
        MessageList.ItemContainerTransitions = [];
        SessionList.ItemsSource = _visibleSessionRows;
        SessionList.ItemContainerTransitions = _noSessionTransitions;
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick += async (_, _) => await RefreshAsync();
        Closed += async (_, _) =>
        {
            _closing = true; _timer.Stop(); _lifetime.Cancel(); _selectionCts?.Cancel(); _sessionAvailabilityCancellation?.Cancel();
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
        Label(LatestButton, "JumpToLatest");
        Composer.PlaceholderText = _text["MessagePlaceholder"]; AutomationProperties.SetName(Composer, _text["MessageInput"]);
        TranscriptSearch.PlaceholderText = _text["SearchTranscript"]; AutomationProperties.SetName(TranscriptSearch, _text["SearchTranscript"]);
        NoSessionsTitle.Text = _text["NoSessions"]; NoSessionsHint.Text = _text["NoSessionsHint"];
        WelcomeTitle.Text = _text["SelectSession"]; ApplyCliWelcomeState();
        NoMessagesText.Text = _text["NoMessages"]; ResumeLabel.Text = _text["Resume"];
        RestoreExplanation.Text = _text["RestoreSessionExplanation"]; ConnectExplanation.Text = _text["ConnectSessionExplanation"]; ArchiveExplanation.Text = _text["ArchivedReadOnly"];
        StopButton.Content = _text["Interrupt"];
        UpdateChrome(); UpdateTitle(); RenderMessages(true); ApplyFilter(true);
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
            var local = await _store.ListAsync(_lifetime.Token);
            if (_client is { IsConnected: true } client)
            {
                try
                {
                    var live = await client.ListAsync(_lifetime.Token);
                    if (!ReferenceEquals(client, _client) || !client.IsConnected) return;
                    var merged = local.ToDictionary(x => x.Id);
                    foreach (var item in live)
                    {
                        if (item.IsLive) _sessionConnectionFailures.Remove(item.Id);
                        if (merged.TryGetValue(item.Id, out var disk)) merged[item.Id] = item with
                        { FilePath = disk.FilePath, Title = string.IsNullOrEmpty(item.Title) ? disk.Title : item.Title, ParentId = item.ParentId ?? disk.ParentId, Model = disk.Model,
                            State = item.State == SessionState.Unknown && !_attached.Contains(item.Id) ? disk.State : item.State, FileVersion = disk.FileVersion };
                        else merged[item.Id] = item;
                    }
                    _sessions = merged.Values.OrderByDescending(x => x.UpdatedAt).ToArray();
                }
                catch (Exception e) when (e is IOException or RpcException or OperationCanceledException)
                {
                    var retained = local.ToDictionary(s => s.Id);
                    foreach (var attached in _sessions.Where(s => _attached.Contains(s.Id))) retained[attached.Id] = attached;
                    _sessions = retained.Values.OrderByDescending(s => s.UpdatedAt).ToArray();
                    if (force) NoticeText("ReadFailed", InfoBarSeverity.Warning);
                }
            }
            else _sessions = local;
            ApplyFilter(force);
            if (_selected is { } selected)
            {
                var updated = _sessions.FirstOrDefault(x => x.Id == selected.Id);
                if (updated is not null)
                {
                    _selected = updated with { ActiveTurnId = updated.ActiveTurnId ?? (_attached.Contains(updated.Id) && updated.State is SessionState.Active or SessionState.Waiting ? selected.ActiveTurnId : null) };
                    UpdateTitle(); UpdateChrome();
                    var length = File.Exists(updated.FilePath) ? new FileInfo(updated.FilePath).Length : -1;
                    if (_client is { IsConnected: true } && _attached.Contains(updated.Id))
                    { if (force) await LoadTranscriptAsync(_selected, false); }
                    else if (!string.IsNullOrEmpty(updated.FilePath) && (length != _selectedLength || force)) await LoadTranscriptAsync(_selected, false);
                    await CheckSessionAvailabilityAsync(force);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { NoticeText("ReadFailed", InfoBarSeverity.Error); }
        finally { _refreshLock.Release(); }
    }
    private void ApplyFilter(bool force = false)
    {
        if (!_ready) return;
        var incoming = _sessions.ToDictionary(s => s.Id);
        var removed = _sessionOrder.Where(id => !incoming.ContainsKey(id)).ToArray();
        var added = _sessions.Where(s => !_sessionRows.ContainsKey(s.Id)).ToArray();
        var announceNew = _sessionBaselineLoaded && added.Length > 0;
        var connected = _client is { IsConnected: true };
        foreach (var id in removed) { _sessionRows.Remove(id); _sessionOrder.Remove(id); }
        foreach (var session in _sessions)
        {
            if (_sessionRows.TryGetValue(session.Id, out var row)) row.Update(session, _text, connected, _selected?.Id == session.Id);
            else _sessionRows.Add(session.Id, new(session, _text, connected, _sessionBaselineLoaded && _selected?.Id != session.Id));
        }
        // Existing rows retain their relative order while transcripts stream. Only new sessions enter at the top.
        _sessionOrder.InsertRange(0, added.Select(s => s.Id));
        _sessionBaselineLoaded = true;
        var query = SessionSearch.Text.Trim(); var filter = SessionFilter.SelectedIndex;
        var visible = _sessionOrder.Select(id => _sessionRows[id]).Where(row =>
        {
            var s = row.Session;
            return (filter != 1 || s.State is SessionState.Active or SessionState.RecordedActive or SessionState.Waiting)
            && (filter != 2 || s.IsArchived) && (query.Length == 0 || $"{s.Title} {s.Cwd} {s.Id} {s.ParentId} {s.Source}".Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }).ToArray();
        _syncing = true;
        try
        {
            // Changing list-wide transitions reanimates recycled existing rows and flashes the entire pane.
            SessionList.ItemContainerTransitions = _noSessionTransitions;
            var visibleIds = visible.Select(row => row.Session.Id).ToHashSet();
            for (int i = _visibleSessionRows.Count - 1; i >= 0; i--) if (!visibleIds.Contains(_visibleSessionRows[i].Session.Id)) _visibleSessionRows.RemoveAt(i);
            for (int i = 0; i < visible.Length; i++)
                if (i >= _visibleSessionRows.Count || !ReferenceEquals(_visibleSessionRows[i], visible[i])) _visibleSessionRows.Insert(i, visible[i]);
            if (_selected is not null) SessionList.SelectedItem = visible.FirstOrDefault(x => x.Session.Id == _selected.Id);
        }
        finally { _syncing = false; }
        NoSessionsPanel.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionCount.Text = _text.Format("SessionCount", visible.Length);
        if (announceNew) AccessibilitySupport.Announce(SessionCount, _text.Format("NewSessionsAvailable", _text.Number(added.Length)), "new-sessions");
    }
    private void OnSessionSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) { if (!_syncing) ApplyFilter(); }
    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync(true);
    private async void OnSessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SessionList.SelectedItem is not SessionRow row) return;
        _sessionConnectCancellation?.Cancel();
        _sessionAvailabilityCancellation?.Cancel();
        row.MarkRead();
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
        var generation = ++_transcriptReadGeneration;
        _selectionCts?.Cancel(); _selectionCts?.Dispose();
        var cts = _selectionCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (showLoading) { _messages.Clear(); LoadingRing.IsActive = true; LoadingRing.Visibility = Visibility.Visible; }
        try
        {
            TranscriptSnapshot snapshot;
            var sourceClient = _client is { IsConnected: true } connected && (_attached.Contains(session.Id) || !File.Exists(session.FilePath)) ? connected : null;
            ServerRead? reading = null; ServerTranscript? serverSnapshot = null;
            if (sourceClient is not null)
            {
                reading = _serverRead = new(sourceClient, session.Id, generation, initialNotifications?.ToList() ?? new List<RpcMessage>());
                serverSnapshot = await sourceClient.ReadTranscriptStateAsync(session.Id, cts.Token);
                snapshot = new(session, serverSnapshot.Entries);
                if (!ReferenceEquals(sourceClient, _client) || !sourceClient.IsConnected) return;
            }
            else if (File.Exists(session.FilePath))
            {
                var initialLength = new FileInfo(session.FilePath).Length;
                snapshot = await Task.Run(() => _readLocalTranscript(session, cts.Token), cts.Token);
                if (_selected?.Id != session.Id || cts.IsCancellationRequested || generation != _transcriptReadGeneration || _client is { IsConnected: true } && _attached.Contains(session.Id)) return;
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
        var rows = EntryRow.Project(entries, _text, _expandedActivities, _client is { IsConnected: true } && _selected is { IsLive: true, State: SessionState.Active or SessionState.Waiting }, _activityScrollOffsets,
            reading => { if (_selected?.Id == viewingId && reading) { _readingActivityHistory = true; _following = false; SetLatestVisible(true); } }, () => _following);
        // Virtualized ListView keeps the entire transcript accessible without an artificial history cutoff.
        if (reset) _messages.Clear();
        // Replace only changed rows. Rebuilding the tail discards virtualized scroll anchors.
        var common = Math.Min(_messages.Count, rows.Count);
        for (int i = 0; i < common; i++) if (!_messages[i].Matches(rows[i]))
        { if (MessageList.ContainerFromIndex(i) is DependencyObject container) TranscriptCard.PreserveScroll(container, _messages[i]); _messages[i].Update(rows[i]); }
        while (_messages.Count > rows.Count) _messages.RemoveAt(_messages.Count - 1);
        for (int i = common; i < rows.Count; i++) _messages.Add(rows[i]);
        NoMessagesText.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_following && query.Length == 0) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ScrollLatest);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            TrackTranscriptScroll();
            if (_messageScroll is not null) SetLatestVisible(_readingActivityHistory || !_following && _messageScroll.ScrollableHeight - _messageScroll.VerticalOffset >= 24);
        });
    }
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
        var needsTurn = attached && _selected?.State is SessionState.Active or SessionState.Waiting && string.IsNullOrEmpty(_selected.ActiveTurnId);
        SessionSetupHelp.Visibility = !attached && !archived && failure is null && !checking ? Visibility.Visible : Visibility.Collapsed;
        SessionSetupActions.Visibility = !attached && !archived ? Visibility.Visible : Visibility.Collapsed;
        Composer.Visibility = ComposerActions.Visibility = attached ? Visibility.Visible : Visibility.Collapsed;
        ArchiveExplanation.Visibility = archived ? Visibility.Visible : Visibility.Collapsed;
        ResumeButton.Visibility = RestoreExplanation.Visibility = showResume && failure is null ? Visibility.Visible : Visibility.Collapsed;
        SessionConnectButton.Visibility = ConnectExplanation.Visibility = showConnect && failure is null && !checking ? Visibility.Visible : Visibility.Collapsed;
        SessionServerChecking.Visibility = !attached && checking ? Visibility.Visible : Visibility.Collapsed;
        SessionServerCheckingRing.IsActive = !attached && checking;
        SessionServerCheckingText.Text = _text["FindingSessionServer"];
        SessionConnectionUnavailable.Visibility = !archived && failure is not null ? Visibility.Visible : Visibility.Collapsed;
        SessionConnectionReason.Text = failure is not null ? _text[failure] : "";
        SessionConnectLabel.Text = _text[_findingSessionServer ? "FindingSessionServer" : _connecting ? "SessionConnecting" : "ConnectSession"];
        ResumeLabel.Text = _text[_sending && !attached ? "Loading" : "Resume"];
        RetrySessionConnectionMenu.Text = _text["CheckAgain"];
        RetrySessionConnectionMenu.Visibility = failure is not null || needsTurn ? Visibility.Visible : Visibility.Collapsed;
        RetrySessionConnectionMenu.IsEnabled = !_findingSessionServer && !_connecting && !_sending && _checkingSessionServerId != _selected?.Id;
        SessionConnectButton.IsEnabled = !_findingSessionServer && !_connecting && !_sending && _selected is { IsArchived: false } && (!attached || needsTurn);
        AutomationProperties.SetName(SessionConnectButton, _text["ConnectSessionHint"]);
        AutomationProperties.SetHelpText(SessionConnectButton, _text["ConnectSessionExplanation"]);
        AutomationProperties.SetHelpText(ResumeButton, _text["RestoreSessionExplanation"]);
        ToolTipService.SetToolTip(SessionConnectButton, attached && !needsTurn ? _connectionEndpoint ?? _text["SessionConnected"] : _text["ConnectSessionHint"]);
        var active = _selected?.State is SessionState.Active or SessionState.Waiting;
        var canSteer = active && !string.IsNullOrEmpty(_selected?.ActiveTurnId);
        Composer.IsEnabled = attached && _selected?.IsLive == true && (!active || canSteer) && !_sending;
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
            if (_client is not null) { var old = _client; _client = null; _attached.Clear(); InvalidateTranscriptRead(); await old.DisposeAsync(); }
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
            if (wasConnected && _selected is { } localSession && File.Exists(localSession.FilePath)) await LoadTranscriptAsync(localSession, false);
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
                _attached.Clear(); InvalidateTranscriptRead(); UpdateChrome(); NoticeText("DisconnectedHint", InfoBarSeverity.Warning);
                if (_selected is { } session && File.Exists(session.FilePath)) await LoadTranscriptAsync(session, false);
            }
        });
    }
    private void InvalidateTranscriptRead()
    { ++_transcriptReadGeneration; _selectionCts?.Cancel(); _serverRead = null; if (_client is not { IsConnected: true }) _serverEvents.Clear(); }
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
        var joined = runtime with { FilePath = original.FilePath, Title = original.Title, ParentId = original.ParentId, Model = original.Model, FileVersion = original.FileVersion };
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
        var local = (await _store.ListAsync(ct)).FirstOrDefault(s => s.Id == original.Id) ?? original;
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
        if (ProtocolEventCatalog.Notifications.TryGetValue(message.Method, out var definition) && definition.Route == ProtocolEventRoute.Server)
        {
            if (TranscriptEvents.Parse(message.Method, p, DateTimeOffset.Now) is { } systemEvent) _serverEvents[systemEvent.Id] = systemEvent;
            return;
        }
        UpdateSessionActivity(message);
        var threadId = p.Str("threadId");
        if (_selected is null || !_attached.Contains(_selected.Id) || threadId != _selected.Id && !(threadId.Length == 0 && TranscriptEvents.IsGlobal(message.Method))) return;
        if (_serverRead is { } reading && ReferenceEquals(reading.Client, client) && (reading.SessionId == threadId || threadId.Length == 0))
        { reading.Messages.Add(message); return; }
        var previous = _snapshot?.Entries ?? Array.Empty<TranscriptEntry>();
        var entries = TranscriptUpdates.Apply(previous, message.Method, p, DateTimeOffset.Now);
        if (!ReferenceEquals(previous, entries)) { _snapshot = _snapshot is null ? new(_selected, entries) : _snapshot with { Session = _selected, Entries = entries }; RenderMessages(); }
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
        UpdateTitle(); UpdateChrome();
    }
    private void UpdateSessionActivity(RpcMessage message)
    {
        var p = message.Parameters; var id = p.Str("threadId");
        if (!_sessionRows.TryGetValue(id, out var row)) return;
        var session = row.Session;
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
        if (changed != session)
        {
            _sessions = _sessions.Select(s => s.Id == id ? changed : s).ToArray();
            row.Update(changed, _text, true, _selected?.Id == id);
            if (_selected?.Id == id) _selected = changed;
        }
        if (message.Method is "item/agentMessage/delta" or "item/reasoning/summaryTextDelta" or "item/reasoning/textDelta" or "item/plan/delta" or "item/started" or "item/completed" or
            "turn/plan/updated" or "turn/diff/updated" or "error" or "warning") row.MarkUpdated(_selected?.Id == id);
    }
    private async Task HandleRequestAsync(CodexClient client, RpcMessage message)
    {
        try
        {
            if (message.Method is "item/commandExecution/requestApproval" or "item/fileChange/requestApproval")
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
        var scroll = FindVisual<ScrollViewer>(MessageList);
        if (ReferenceEquals(scroll, _messageScroll)) return;
        if (_messageScroll is not null) _messageScroll.ViewChanged -= OnTranscriptViewChanged;
        _messageScroll = scroll;
        if (_messageScroll is not null) _messageScroll.ViewChanged += OnTranscriptViewChanged;
    }
    private void OnTranscriptViewChanged(object? sender, ScrollViewerViewChangedEventArgs args)
    {
        if (args.IsIntermediate || _messageScroll is null) return;
        _following = !_readingActivityHistory && _messageScroll.ScrollableHeight - _messageScroll.VerticalOffset < 24;
        SetLatestVisible(!_following);
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
    private void ScrollLatest()
    {
        if (_messages.Count == 0) return;
        MessageList.ScrollIntoView(_messages[^1], ScrollIntoViewAlignment.Leading);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        { if (_following && _messages.Count > 0 && MessageList.ContainerFromIndex(_messages.Count - 1) is DependencyObject container) TranscriptCard.ScrollGroupToEnd(container, _messages[^1]); });
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
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        await _dialogs.WaitAsync(_lifetime.Token);
        var returnFocus = FocusManager.GetFocusedElement(Root.XamlRoot) as Control;
        try
        {
            dialog.XamlRoot = Root.XamlRoot; dialog.RequestedTheme = Root.RequestedTheme;
            dialog.FlowDirection = Root.FlowDirection; dialog.Language = Root.Language;
            dialog.GotFocus += (_, _) => AccessibilitySupport.NormalizeIconsAfterLayout(dialog);
            return await dialog.ShowAsync();
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
            if (await picker.PickSaveFileAsync() is { } file) { await File.WriteAllTextAsync(file.Path, TranscriptExport.Markdown(snapshot, _text)); NoticeText("Exported", InfoBarSeverity.Success); }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { NoticeText("ReadFailed", InfoBarSeverity.Error); }
    }
    private async void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        if (_selected?.Cwd is { Length: > 0 } folder && Directory.Exists(folder))
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
        if (!Directory.Exists(path.Text)) { NoticeText("InvalidHome", InfoBarSeverity.Error); return; }
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
            try { _settings.Save(); NoticeText("Saved", InfoBarSeverity.Success); }
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
        var language = new ComboBox { Header = _text["Language"], HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetName(mode, _text["Connection"]); AutomationProperties.SetName(theme, _text["Theme"]); AutomationProperties.SetName(language, _text["Language"]);
        var languages = new[] { "" }.Concat(LocalizationService.AvailableLanguages).ToArray();
        foreach (var tag in languages) language.Items.Add(tag.Length == 0 ? _text["System"] : CultureInfo.GetCultureInfo(tag).NativeName);
        language.SelectedIndex = Math.Max(0, Array.IndexOf(languages, _settings.Language));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }; error.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        foreach (var item in new FrameworkElement[] { mode, discovery, new TextBlock { Text = _text["ConnectionHelp"], TextWrapping = TextWrapping.Wrap }, home, exe, theme, language, new TextBlock { Text = _text["PrivacyNote"], TextWrapping = TextWrapping.Wrap, FontSize = 12 }, error }) panel.Children.Add(item);
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
        _settings = _settings with { CodexHome = Path.GetFullPath(home.Home), Executable = exe.Executable, UseSharedServer = mode.SelectedIndex == 1, Endpoint = discovery.Endpoint, Theme = themes[theme.SelectedIndex], Language = languages[language.SelectedIndex] };
        try { _settings.Save(); }
        catch (IOException) { NoticeText("ReadFailed", InfoBarSeverity.Error); return; }
        _store = new(_settings.CodexHome); _text = new(_settings.Language);
        if (changedConnection) { _sessionAvailabilityCancellation?.Cancel(); _sessionServerCheckedAt.Clear(); _sessionConnectionFailures.Clear(); }
        Localize();
        if (changedConnection && _client is not null) { var old = _client; _client = null; _attached.Clear(); await old.DisposeAsync(); NoticeText("RestartConnection"); }
        else NoticeText("Saved", InfoBarSeverity.Success);
        await RefreshAsync(true); UpdateChrome(); await CheckCliWelcomeAsync();
    }
}
