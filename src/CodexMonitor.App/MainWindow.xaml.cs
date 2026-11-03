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
    private CodexClient? _client;
    private IReadOnlyList<SessionInfo> _sessions = [];
    private SessionInfo? _selected;
    private TranscriptSnapshot? _snapshot;
    private readonly ObservableCollection<EntryRow> _messages = [];
    private readonly ObservableCollection<SessionRow> _visibleSessionRows = [];
    private readonly Dictionary<string, SessionRow> _sessionRows = [];
    private readonly List<string> _sessionOrder = [];
    private readonly TransitionCollection _sessionTransitions = [new AddDeleteThemeTransition(), new RepositionThemeTransition()];
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
    private long _selectedLength = -1;

    public MainWindow() : this(false) { }
    internal MainWindow(bool reviewMode)
    {
        _reviewMode = reviewMode;
        _text = new(_settings.Language); _store = new(_settings.CodexHome);
        InitializeComponent();
        Root.ActualThemeChanged += (_, _) => UpdateCaptionTheme();
        Title = "Codex Monitor";
        ExtendsContentIntoTitleBar = true; SetTitleBar(TitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860));
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        MessageList.ItemsSource = _messages;
        SessionList.ItemsSource = _visibleSessionRows;
        SessionList.ItemContainerTransitions = _noSessionTransitions;
        _timer = DispatcherQueue.CreateTimer(); _timer.Interval = TimeSpan.FromSeconds(3);
        _timer.Tick += async (_, _) => await RefreshAsync();
        Closed += async (_, _) =>
        {
            _closing = true; _timer.Stop(); _lifetime.Cancel(); _selectionCts?.Cancel();
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
        if (_reviewMode) { _ready = true; return; }
        var scale = Root.XamlRoot.RasterizationScale;
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var width = Math.Min((int)(1180 * scale), area.Width - 48);
        var height = Math.Min((int)(800 * scale), area.Height - 48);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
        _ready = true; await RefreshAsync(true); _timer.Start();
    }
    private void Localize()
    {
        Root.FlowDirection = _text.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        Root.Language = _text.LanguageTag;
        Root.RequestedTheme = _settings.Theme switch { "Dark" => ElementTheme.Dark, "Light" => ElementTheme.Light, _ => ElementTheme.Default };
        Root.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        AppTitle.Text = _text["AppName"]; SessionsHeading.Text = _text["Sessions"];
        SessionSearch.PlaceholderText = _text["SearchSessions"];
        AutomationProperties.SetName(SessionSearch, _text["SearchSessions"]);
        var filter = Math.Max(0, SessionFilter.SelectedIndex);
        _syncing = true; SessionFilter.Items.Clear();
        foreach (var key in new[] { "AllSessions", "Active", "Archived" }) SessionFilter.Items.Add(_text[key]);
        SessionFilter.SelectedIndex = filter; _syncing = false;
        AutomationProperties.SetName(SessionFilter, _text["Sessions"]);
        Label(NewButton, "NewSession"); Label(RefreshButton, "Refresh"); Label(SettingsButton, "Settings"); Label(PaneButton, "ShowSessions");
        Label(FindButton, "SearchTranscript"); Label(MoreButton, "Details"); Label(CloseFindButton, "Close");
        DetailsMenu.Text = _text["Details"]; ExportMenu.Text = _text["Export"]; OpenFolderMenu.Text = _text["OpenFolder"];
        ToolsCheck.Content = _text["ShowTools"]; ToolsCheck.IsChecked = _settings.ShowTools;
        LatestButton.Content = _text["JumpToLatest"];
        Composer.PlaceholderText = _text["MessagePlaceholder"]; AutomationProperties.SetName(Composer, _text["MessagePlaceholder"]);
        TranscriptSearch.PlaceholderText = _text["SearchTranscript"]; AutomationProperties.SetName(TranscriptSearch, _text["SearchTranscript"]);
        NoSessionsTitle.Text = _text["NoSessions"]; NoSessionsHint.Text = _text["NoSessionsHint"];
        WelcomeTitle.Text = _text["SelectSession"]; WelcomeHint.Text = _text["SelectSessionHint"];
        NoMessagesText.Text = _text["NoMessages"]; ResumeButton.Content = _text["Resume"];
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
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
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
        ActivityNote.Visibility = e.NewSize.Width < 1040 ? Visibility.Collapsed : Visibility.Visible;
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
                    var merged = local.ToDictionary(x => x.Id);
                    foreach (var item in live)
                    {
                        if (merged.TryGetValue(item.Id, out var disk)) merged[item.Id] = item with
                        { FilePath = disk.FilePath, Title = string.IsNullOrEmpty(item.Title) ? disk.Title : item.Title, ParentId = item.ParentId ?? disk.ParentId, Model = disk.Model,
                            State = item.State == SessionState.Unknown ? disk.State : item.State, FileVersion = disk.FileVersion };
                        else merged[item.Id] = item;
                    }
                    _sessions = merged.Values.OrderByDescending(x => x.UpdatedAt).ToArray();
                }
                catch (Exception e) when (e is IOException or RpcException or OperationCanceledException)
                { _sessions = local; if (force) NoticeText("ReadFailed", InfoBarSeverity.Warning); }
            }
            else _sessions = local;
            ApplyFilter(force);
            if (_selected is { } selected)
            {
                var updated = _sessions.FirstOrDefault(x => x.Id == selected.Id);
                if (updated is not null)
                {
                    _selected = updated with { ActiveTurnId = updated.ActiveTurnId ?? (_attached.Contains(updated.Id) ? selected.ActiveTurnId : null) };
                    UpdateTitle(); UpdateChrome();
                    var length = File.Exists(updated.FilePath) ? new FileInfo(updated.FilePath).Length : -1;
                    if (!string.IsNullOrEmpty(updated.FilePath) && (length != _selectedLength || force)) await LoadTranscriptAsync(_selected, false);
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
        var animateStructure = _sessionBaselineLoaded && (removed.Length > 0 || added.Length > 0);
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
            SessionList.ItemContainerTransitions = animateStructure ? _sessionTransitions : _noSessionTransitions;
            var visibleIds = visible.Select(row => row.Session.Id).ToHashSet();
            for (int i = _visibleSessionRows.Count - 1; i >= 0; i--) if (!visibleIds.Contains(_visibleSessionRows[i].Session.Id)) _visibleSessionRows.RemoveAt(i);
            for (int i = 0; i < visible.Length; i++)
                if (i >= _visibleSessionRows.Count || !ReferenceEquals(_visibleSessionRows[i], visible[i])) _visibleSessionRows.Insert(i, visible[i]);
            if (_selected is not null) SessionList.SelectedItem = visible.FirstOrDefault(x => x.Session.Id == _selected.Id);
        }
        finally { _syncing = false; }
        NoSessionsPanel.Visibility = visible.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        SessionCount.Text = _text.Format("SessionCount", visible.Length);
    }
    private void OnSessionSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => ApplyFilter();
    private void OnFilterChanged(object sender, SelectionChangedEventArgs e) { if (!_syncing) ApplyFilter(); }
    private async void OnRefresh(object sender, RoutedEventArgs e) => await RefreshAsync(true);
    private async void OnSessionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || SessionList.SelectedItem is not SessionRow row) return;
        row.MarkRead();
        if (_selected is not null) _drafts[_selected.Id] = Composer.Text;
        _selected = row.Session; _snapshot = null; _selectedLength = -1; _following = true;
        Composer.Text = _drafts.GetValueOrDefault(_selected.Id, "");
        TranscriptSearch.Text = ""; Notice.IsOpen = false;
        WelcomePanel.Visibility = Visibility.Collapsed; Conversation.Visibility = Visibility.Visible;
        UpdateTitle(); UpdateChrome();
        if (Shell.DisplayMode == SplitViewDisplayMode.Overlay) Shell.IsPaneOpen = false;
        await LoadTranscriptAsync(_selected, true);
    }
    private async Task LoadTranscriptAsync(SessionInfo session, bool showLoading)
    {
        _selectionCts?.Cancel(); _selectionCts?.Dispose();
        var cts = _selectionCts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        if (showLoading) { _messages.Clear(); LoadingRing.IsActive = true; LoadingRing.Visibility = Visibility.Visible; }
        try
        {
            TranscriptSnapshot snapshot;
            if (_attached.Contains(session.Id) && _client is { IsConnected: true } attachedClient)
                snapshot = new(session, await attachedClient.ReadTranscriptAsync(session.Id, cts.Token));
            else if (File.Exists(session.FilePath))
            {
                var initialLength = new FileInfo(session.FilePath).Length;
                snapshot = await Task.Run(() => new TranscriptParser().ReadAsync(session, cts.Token), cts.Token);
                if (_selected?.Id != session.Id || cts.IsCancellationRequested) return;
                _selectedLength = initialLength;
                if (session.IsLive) snapshot = snapshot with { Session = session };
            }
            else if (_client is { IsConnected: true }) snapshot = new(session, await _client.ReadTranscriptAsync(session.Id, cts.Token));
            else throw new IOException("Rollout file unavailable");
            if (_selected?.Id != session.Id || cts.IsCancellationRequested) return;
            _snapshot = snapshot; _selected = snapshot.Session;
            RenderMessages(showLoading); UpdateTitle(); UpdateChrome();
            if (snapshot.InvalidRecords > 0) NoticeText("ReadWarnings", InfoBarSeverity.Warning, _text.Number(snapshot.InvalidRecords));
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or RpcException) { if (_selected?.Id == session.Id) NoticeText("ReadFailed", InfoBarSeverity.Error); }
        finally { if (_selectionCts == cts) { LoadingRing.IsActive = false; LoadingRing.Visibility = Visibility.Collapsed; } }
    }
    private void RenderMessages(bool reset = false)
    {
        if (_snapshot is null) { _messages.Clear(); return; }
        var query = TranscriptSearch.Text.Trim();
        var entries = _snapshot.Entries.Where(e => (_settings.ShowTools || e.Kind is EntryKind.User or EntryKind.Assistant)
            && (query.Length == 0 || e.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase))).ToArray();
        // Virtualized ListView keeps the entire transcript accessible without an artificial history cutoff.
        if (reset) _messages.Clear();
        var common = 0;
        while (common < _messages.Count && common < entries.Length && _messages[common].Entry == entries[common] && _messages[common].Text.LanguageTag == _text.LanguageTag) common++;
        while (_messages.Count > common) _messages.RemoveAt(_messages.Count - 1);
        for (int i = common; i < entries.Length; i++) _messages.Add(new EntryRow(entries[i], _text));
        NoMessagesText.Visibility = entries.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_following && query.Length == 0) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ScrollLatest);
    }
    private void UpdateTitle()
    {
        SessionTitle.Text = _selected is null ? _text["Transcript"] : new SessionRow(_selected, _text).Title;
        SessionSubtitle.Text = _selected is null ? "" : $"{_selected.FolderName} · {_text.State(_selected.State)}";
        FindButton.IsEnabled = MoreButton.IsEnabled = _selected is not null;
    }
    private void UpdateChrome()
    {
        var connected = _client is { IsConnected: true };
        foreach (var row in _sessionRows.Values) row.SetConnection(connected);
        ConnectButton.Content = _text[connected ? "Disconnect" : "Connect"]; ConnectButton.IsEnabled = !_connecting;
        ConnectionStatus.Text = _text[connected ? "Connected" : "LocalFiles"];
        ActivityNote.Text = connected ? "" : _text["ActivityNote"];
        NewButton.IsEnabled = connected && !_connecting;
        var attached = connected && _selected is not null && _attached.Contains(_selected.Id) && !_selected.IsArchived;
        var active = _selected?.State is SessionState.Active or SessionState.Waiting;
        var canSteer = active && !string.IsNullOrEmpty(_selected?.ActiveTurnId);
        Composer.IsEnabled = attached && (!active || canSteer) && !_sending;
        SendButton.IsEnabled = Composer.IsEnabled && !string.IsNullOrWhiteSpace(Composer.Text);
        SendButton.Content = _text[canSteer ? "Steer" : "Send"];
        StopButton.Visibility = attached && canSteer ? Visibility.Visible : Visibility.Collapsed;
        StopButton.IsEnabled = !_sending;
        ResumeButton.Visibility = connected && _selected is { IsArchived: false } && !attached ? Visibility.Visible : Visibility.Collapsed;
        ResumeButton.IsEnabled = !_sending;
        ComposerHint.Text = _text[_selected?.IsArchived == true ? "ArchivedReadOnly" : !connected ? "ReadOnlyHint" :
            !attached && _selected?.State == SessionState.RecordedActive ? "ExternalActiveHint" : !attached ? "ResumeHint" : canSteer ? "SteerHint" : "SendHint"];
    }
    private void OnComposerChanged(object sender, TextChangedEventArgs e) { if (_ready) UpdateChrome(); }
    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        if (_connecting) return;
        _connecting = true; UpdateChrome();
        try
        {
            var wasConnected = _client is { IsConnected: true };
            if (_client is not null) { var old = _client; _client = null; _attached.Clear(); await old.DisposeAsync(); }
            if (!wasConnected)
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(15));
                IRpcTransport transport = _settings.UseSharedServer ? await WebSocketTransport.ConnectAsync(_settings.Endpoint, timeout.Token) : StdioTransport.Start(_settings.Executable, _settings.CodexHome);
                var client = new CodexClient(new JsonRpcClient(transport));
                client.Rpc.Message += message => DispatcherQueue.TryEnqueue(() => HandleMessage(client, message));
                client.Rpc.Disconnected += _ => DispatcherQueue.TryEnqueue(() => { if (!_closing && ReferenceEquals(_client, client)) { _attached.Clear(); UpdateChrome(); NoticeText("DisconnectedHint", InfoBarSeverity.Warning); } });
                try { await client.InitializeAsync(timeout.Token); _client = client; }
                catch { await client.DisposeAsync(); throw; }
            }
            Notice.IsOpen = false; await RefreshAsync(true);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.Net.WebSockets.WebSocketException or RpcException or OperationCanceledException or System.ComponentModel.Win32Exception)
        { NoticeText("ConnectionFailed", InfoBarSeverity.Error); }
        finally { _connecting = false; UpdateChrome(); }
    }
    private async void OnResume(object sender, RoutedEventArgs e) => await ResumeAsync();
    private async Task ResumeAsync()
    {
        if (_client is not { IsConnected: true } client || _selected is not { IsArchived: false } session || _sending) return;
        _sending = true; UpdateChrome();
        try
        {
            var result = await client.ResumeAsync(session.Id, _lifetime.Token);
            if (result.State is SessionState.Active or SessionState.Waiting && string.IsNullOrEmpty(result.ActiveTurnId))
            {
                var turns = await client.Rpc.RequestAsync("thread/turns/list", new { threadId = session.Id, limit = 10, sortDirection = "desc", itemsView = "notLoaded" }, _lifetime.Token);
                var active = turns.Field("data").Items().FirstOrDefault(x => x.Str("status") == "inProgress");
                result = result with { ActiveTurnId = active.Str("id") is { Length: > 0 } id ? id : null };
            }
            _attached.Add(session.Id);
            if (_selected?.Id == session.Id)
            {
                _selected = result with { FilePath = session.FilePath, Title = session.Title, ParentId = session.ParentId, Model = session.Model };
                await LoadTranscriptAsync(_selected, false); Composer.Focus(FocusState.Programmatic);
            }
        }
        catch (Exception e) when (e is IOException or RpcException or OperationCanceledException) { NoticeText("ResumeFailed", InfoBarSeverity.Error); }
        finally { _sending = false; UpdateTitle(); UpdateChrome(); }
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
        if (!ReferenceEquals(client, _client) || _closing) return;
        if (message.Id is not null) { _ = HandleRequestAsync(client, message); return; }
        var p = message.Parameters;
        UpdateSessionActivity(message);
        if (p.Str("threadId") != _selected?.Id) return;
        switch (message.Method)
        {
            case "turn/started":
                _selected = _selected with { State = SessionState.Active, ActiveTurnId = p.Field("turn").Str("id") }; break;
            case "turn/completed":
                _selected = _selected with { ActiveTurnId = null, State = p.Field("turn").Str("status") switch { "failed" => SessionState.Error, "interrupted" => SessionState.Interrupted, _ => SessionState.Completed } };
                break;
            case "item/agentMessage/delta":
                var id = p.Str("itemId");
                if (_snapshot is null) _snapshot = new(_selected, []);
                var list = _snapshot.Entries.ToList(); var at = list.FindIndex(e => e.Id == id);
                if (at < 0) list.Add(new(id, EntryKind.Assistant, p.Str("delta"), DateTimeOffset.Now));
                else list[at] = list[at] with { Text = list[at].Text + p.Str("delta") };
                _snapshot = _snapshot with { Entries = list }; RenderMessages(); break;
            case "item/started":
            case "item/completed":
                var entry = CodexClient.ParseItem(p.Field("item"), DateTimeOffset.Now);
                if (_snapshot is null) _snapshot = new(_selected, []);
                var entries = _snapshot.Entries.ToList(); var index = entries.FindIndex(x => x.Id == entry.Id);
                if (index >= 0) entries[index] = entry with { Timestamp = entries[index].Timestamp }; else entries.Add(entry);
                _snapshot = _snapshot with { Entries = entries }; RenderMessages(); break;
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
        }
        if (message.Method is "item/agentMessage/delta" or "item/started" or "item/completed") row.MarkUpdated(_selected?.Id == id);
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
                await client.Rpc.RespondAsync(message.Id!.Value, new { decision = result == ContentDialogResult.Primary ? "accept" : "decline" }, _lifetime.Token);
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
    {
        _messageScroll = FindVisual<ScrollViewer>(MessageList);
        if (_messageScroll is not null) _messageScroll.ViewChanged += (_, args) =>
        {
            if (args.IsIntermediate) return;
            _following = _messageScroll.ScrollableHeight - _messageScroll.VerticalOffset < 80;
            LatestButton.Visibility = _following ? Visibility.Collapsed : Visibility.Visible;
        };
    }
    private static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) return match; if (FindVisual<T>(child) is { } nested) return nested; }
        return null;
    }
    private void ScrollLatest() { if (_messages.Count > 0) MessageList.ScrollIntoView(_messages[^1], ScrollIntoViewAlignment.Leading); }
    private void OnLatest(object sender, RoutedEventArgs e) { _following = true; ScrollLatest(); }
    private void OnToolsChanged(object sender, RoutedEventArgs e) { _settings = _settings with { ShowTools = ToolsCheck.IsChecked == true }; _settings.Save(); RenderMessages(true); }
    private void OnTranscriptSearchChanged(object sender, TextChangedEventArgs e) { if (_ready) RenderMessages(true); }
    private void ToggleFind(bool open) { FindPanel.Visibility = open && _selected is not null ? Visibility.Visible : Visibility.Collapsed; if (open) TranscriptSearch.Focus(FocusState.Keyboard); else TranscriptSearch.Text = ""; }
    private void OnFind(object sender, RoutedEventArgs e) => ToggleFind(true);
    private void OnCloseFind(object sender, RoutedEventArgs e) => ToggleFind(false);
    private void OnTogglePane(object sender, RoutedEventArgs e) => Shell.IsPaneOpen = !Shell.IsPaneOpen;
    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        await _dialogs.WaitAsync(_lifetime.Token);
        try
        {
            dialog.XamlRoot = Root.XamlRoot; dialog.RequestedTheme = Root.RequestedTheme;
            dialog.FlowDirection = Root.FlowDirection; dialog.Language = Root.Language;
            return await dialog.ShowAsync();
        }
        finally { _dialogs.Release(); }
    }
    private async void OnDetails(object sender, RoutedEventArgs e)
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
        if (_client is not { IsConnected: true } client) return;
        var path = new TextBox { Header = _text["Folder"], Text = _selected?.Cwd ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), FlowDirection = FlowDirection.LeftToRight };
        var browse = new Button { Content = _text["Browse"] }; browse.Click += async (_, _) => { if (await PickFolderAsync() is { } selected) path.Text = selected; };
        var panel = new StackPanel { Spacing = 12 }; panel.Children.Add(path); panel.Children.Add(browse);
        if (await ShowDialogAsync(new ContentDialog { Title = _text["NewSessionTitle"], Content = panel, PrimaryButtonText = _text["Start"], CloseButtonText = _text["Cancel"], DefaultButton = ContentDialogButton.Primary }) != ContentDialogResult.Primary) return;
        if (!Directory.Exists(path.Text)) { NoticeText("InvalidHome", InfoBarSeverity.Error); return; }
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
    private async Task SettingsAsync()
    {
        var panel = new StackPanel { Spacing = 20, MaxWidth = 520 };
        var home = new TextBox { Header = _text["CodexHome"], Text = _settings.CodexHome, FlowDirection = FlowDirection.LeftToRight };
        var browse = new Button { Content = _text["Browse"] }; browse.Click += async (_, _) => { if (await PickFolderAsync() is { } folder) home.Text = folder; };
        var exe = new TextBox { Header = _text["CodexExecutable"], Text = _settings.Executable, FlowDirection = FlowDirection.LeftToRight };
        var mode = new ComboBox { Header = _text["Connection"], HorizontalAlignment = HorizontalAlignment.Stretch };
        mode.Items.Add(_text["LocalServer"]); mode.Items.Add(_text["SharedServer"]); mode.SelectedIndex = _settings.UseSharedServer ? 1 : 0;
        var endpoint = new TextBox { Header = _text["Endpoint"], Text = _settings.Endpoint, PlaceholderText = _text["EndpointHint"], FlowDirection = FlowDirection.LeftToRight, Visibility = _settings.UseSharedServer ? Visibility.Visible : Visibility.Collapsed };
        mode.SelectionChanged += (_, _) => endpoint.Visibility = mode.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        var theme = new ComboBox { Header = _text["Theme"], HorizontalAlignment = HorizontalAlignment.Stretch };
        var themes = new[] { "System", "Light", "Dark" }; foreach (var key in themes) theme.Items.Add(_text[key]); theme.SelectedIndex = Math.Max(0, Array.IndexOf(themes, _settings.Theme));
        var language = new ComboBox { Header = _text["Language"], HorizontalAlignment = HorizontalAlignment.Stretch };
        var languages = new[] { "" }.Concat(LocalizationService.AvailableLanguages).ToArray();
        foreach (var tag in languages) language.Items.Add(tag.Length == 0 ? _text["System"] : CultureInfo.GetCultureInfo(tag).NativeName);
        language.SelectedIndex = Math.Max(0, Array.IndexOf(languages, _settings.Language));
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }; error.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        foreach (var item in new FrameworkElement[] { home, browse, exe, mode, endpoint, new TextBlock { Text = _text["ConnectionHelp"], TextWrapping = TextWrapping.Wrap }, theme, language, new TextBlock { Text = _text["PrivacyNote"], TextWrapping = TextWrapping.Wrap, FontSize = 12 }, error }) panel.Children.Add(item);
        var dialog = new ContentDialog { Title = _text["Settings"], Content = new ScrollViewer { Content = panel, MaxHeight = 530 }, PrimaryButtonText = _text["Save"], CloseButtonText = _text["Cancel"], DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += (_, args) =>
        {
            var errorKey = !Directory.Exists(home.Text) ? "InvalidHome" : mode.SelectedIndex == 1 && !WebSocketTransport.IsAllowedEndpoint(endpoint.Text) ? "InvalidEndpoint" : "";
            if (errorKey.Length > 0) { error.Text = _text[errorKey]; error.Visibility = Visibility.Visible; args.Cancel = true; }
        };
        if (await ShowDialogAsync(dialog) != ContentDialogResult.Primary) return;
        var changedConnection = home.Text != _settings.CodexHome || exe.Text != _settings.Executable || endpoint.Text != _settings.Endpoint || (mode.SelectedIndex == 1) != _settings.UseSharedServer;
        _settings = _settings with { CodexHome = Path.GetFullPath(home.Text), Executable = exe.Text, UseSharedServer = mode.SelectedIndex == 1, Endpoint = endpoint.Text, Theme = themes[theme.SelectedIndex], Language = languages[language.SelectedIndex] };
        try { _settings.Save(); }
        catch (IOException) { NoticeText("ReadFailed", InfoBarSeverity.Error); return; }
        _store = new(_settings.CodexHome); _text = new(_settings.Language);
        Localize();
        if (changedConnection && _client is not null) { var old = _client; _client = null; _attached.Clear(); await old.DisposeAsync(); NoticeText("RestartConnection"); }
        else NoticeText("Saved", InfoBarSeverity.Success);
        await RefreshAsync(true); UpdateChrome();
    }
}
