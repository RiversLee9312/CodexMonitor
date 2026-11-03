using CodexMonitor.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace CodexMonitor.App;

internal sealed partial class ServerDiscoveryPanel : StackPanel, IDisposable
{
    private readonly IServerDiscovery _discovery;
    private readonly Func<string> _home;
    private readonly Func<string> _executable;
    private readonly LocalizationService _text;
    private readonly CancellationTokenSource _lifetime;
    private CancellationTokenSource? _scanCancellation;
    private readonly ComboBox _choices;
    private readonly TextBox _address;
    private readonly Button _scan;
    private readonly ProgressRing _progress;
    private readonly TextBlock _status;
    private int _addressRevision;
    private bool _active;
    public bool IsScanning { get; private set; }
    public IReadOnlyList<DiscoveredServer> Results { get; private set; } = [];
    public string Endpoint { get => _address.Text.Trim(); set => _address.Text = value; }

    public ServerDiscoveryPanel(IServerDiscovery discovery, Func<string> home, Func<string> executable, string endpoint,
        LocalizationService text, CancellationToken lifetime)
    {
        _discovery = discovery; _home = home; _executable = executable; _text = text;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Spacing = 10;
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = text["AvailableServers"], TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
        _progress = new ProgressRing { Width = 18, Height = 18, IsActive = false, Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(_progress, text["ScanningServers"]);
        Grid.SetColumn(_progress, 1); header.Children.Add(_progress);
        _scan = new Button { Content = text["ScanServers"], MinHeight = 36 };
        AutomationProperties.SetName(_scan, text["ScanServers"]);
        _scan.Click += async (_, _) => await ScanAsync();
        Grid.SetColumn(_scan, 2); header.Children.Add(_scan);
        Children.Add(header);
        _choices = new ComboBox { PlaceholderText = text["SelectServer"], HorizontalAlignment = HorizontalAlignment.Stretch, Visibility = Visibility.Collapsed };
        AutomationProperties.SetName(_choices, text["AvailableServers"]);
        _choices.SelectionChanged += (_, _) =>
        {
            if (_choices.SelectedItem is ComboBoxItem { Tag: DiscoveredServer { CanConnect: true } server }) Endpoint = server.Endpoint;
        };
        Children.Add(_choices);
        _status = new TextBlock { Text = text["ServerDiscoveryHint"], TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        Children.Add(_status);
        _address = new TextBox { Header = text["ServerAddress"], Text = endpoint, PlaceholderText = text["EndpointHint"], FlowDirection = FlowDirection.LeftToRight };
        AutomationProperties.SetName(_address, text["ServerAddress"]);
        _address.TextChanged += (_, _) => _addressRevision++;
        Children.Add(_address);
    }
    public void Activate() { if (_active) return; _active = true; if (!IsScanning) _ = ScanAsync(); }
    public void Deactivate() { _active = false; _scanCancellation?.Cancel(); }

    public async Task ScanAsync()
    {
        if (_lifetime.IsCancellationRequested) return;
        _scanCancellation?.Cancel();
        var scan = _scanCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var revision = _addressRevision; var originalAddress = Endpoint; var home = _home();
        IsScanning = true; _scan.IsEnabled = false; _progress.IsActive = true; _progress.Visibility = Visibility.Visible;
        _status.Text = _text["ScanningServers"];
        try
        {
            var found = await _discovery.FindAsync(home, _executable(), scan.Token);
            if (scan.IsCancellationRequested || !ReferenceEquals(_scanCancellation, scan)) return;
            if (_home() != home) { _status.Text = _text["ServerScanFailed"]; return; }
            Results = found;
            _choices.Items.Clear();
            foreach (var server in found)
            {
                var label = (server.IsLocalDaemon ? _text["LocalDaemon"] + " · " : "") + server.Endpoint +
                    (server.RequiresAuthentication ? " · " + _text["AuthenticationRequired"] : "");
                var item = new ComboBoxItem { Content = new TextBlock { Text = label, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 420 }, Tag = server, IsEnabled = server.CanConnect };
                AutomationProperties.SetName(item, label);
                ToolTipService.SetToolTip(item, server.Candidate.SocketPath ?? label);
                _choices.Items.Add(item);
            }
            _choices.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            _status.Text = found.Count > 0 ? _text["ServerDiscoveryHint"] : _text["NoServersFound"] + "\n" + _text["NoServersHint"];
            // Preserve typing while a scan is in progress. A single result can replace the untouched initial example.
            if (revision == _addressRevision && Endpoint == originalAddress)
            {
                var selected = found.FirstOrDefault(s => s.CanConnect && s.Endpoint.Equals(originalAddress, StringComparison.OrdinalIgnoreCase));
                if (selected is null && (originalAddress.Length == 0 || originalAddress == "ws://127.0.0.1:4500") && found.Count(s => s.CanConnect) == 1)
                    selected = found.First(s => s.CanConnect);
                if (selected is not null) _choices.SelectedItem = _choices.Items.Cast<ComboBoxItem>().First(i => ReferenceEquals(i.Tag, selected));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { if (!scan.IsCancellationRequested && ReferenceEquals(_scanCancellation, scan)) _status.Text = _text["ServerScanFailed"]; }
        finally
        {
            if (ReferenceEquals(_scanCancellation, scan))
            {
                IsScanning = false; _scan.IsEnabled = true; _progress.IsActive = false; _progress.Visibility = Visibility.Collapsed;
                _scanCancellation = null;
            }
            scan.Dispose();
        }
    }
    public void Dispose() { _active = false; _lifetime.Cancel(); _scanCancellation?.Cancel(); _lifetime.Dispose(); }
}
