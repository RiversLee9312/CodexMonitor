using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;

namespace CodexMonitor.Core;

public interface IRpcTransport : IAsyncDisposable
{
    Task SendAsync(string message, CancellationToken cancellationToken);
    Task<string?> ReceiveAsync(CancellationToken cancellationToken);
}

public sealed class StdioTransport : IRpcTransport
{
    private readonly Process _process;
    private readonly Task _stderrDrain;
    private StdioTransport(Process process)
    {
        _process = process;
        // Drain without retaining stderr: it can contain user paths and credentials from provider errors.
        _stderrDrain = DrainAsync(process.StandardError);
    }
    private static async Task DrainAsync(StreamReader reader)
    {
        try { while (await reader.ReadLineAsync().ConfigureAwait(false) is not null) { } }
        catch (IOException) { }
    }

    public static StdioTransport Start(string executable, string codexHome)
        => StartProcess(executable, codexHome, ["app-server", "--listen", "stdio://"]);

    private static StdioTransport StartProcess(string executable, string codexHome, IEnumerable<string> arguments)
    {
        var resolved = ResolveExecutable(executable);
        Directory.CreateDirectory(codexHome);
        var start = new ProcessStartInfo(resolved) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["CODEX_HOME"] = Path.GetFullPath(codexHome);
        return new(Process.Start(start) ?? throw new IOException("Unable to start Codex"));
    }

    public static string ResolveExecutable(string executable)
        => new CodexExecutableDiscovery().Resolve(executable);

    public async Task SendAsync(string message, CancellationToken ct)
    {
        await _process.StandardInput.WriteLineAsync(message.AsMemory(), ct).ConfigureAwait(false);
        await _process.StandardInput.FlushAsync(ct).ConfigureAwait(false);
    }
    public async Task<string?> ReceiveAsync(CancellationToken ct) => await _process.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false);
    public async ValueTask DisposeAsync()
    {
        _process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await _process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
        await _stderrDrain.ConfigureAwait(false);
        _process.Dispose();
    }
}

public sealed class ServerAuthenticationRequiredException() : IOException("The app server requires authentication");

public sealed class WebSocketTransport : IRpcTransport
{
    private readonly ClientWebSocket _socket = new();
    private HttpMessageInvoker? _http;
    public static bool IsAllowedEndpoint(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme is "ws" or "wss" && uri.IsLoopback && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public static async Task<WebSocketTransport> ConnectAsync(string endpoint, CancellationToken ct = default)
    {
        if (!IsAllowedEndpoint(endpoint)) throw new ArgumentException("Only loopback WebSocket endpoints are supported", nameof(endpoint));
        var t = new WebSocketTransport();
        t._socket.Options.Proxy = null;
        t._socket.Options.CollectHttpResponseDetails = true;
        try { await t._socket.ConnectAsync(new Uri(endpoint), ct).ConfigureAwait(false); return t; }
        catch (WebSocketException) when (t._socket.HttpStatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        { t._socket.Dispose(); throw new ServerAuthenticationRequiredException(); }
        catch { t._socket.Dispose(); throw; }
    }
    public static async Task<WebSocketTransport> ConnectLocalSocketAsync(string path, CancellationToken ct = default)
    {
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("A local absolute socket path is required", nameof(path));
        var transport = new WebSocketTransport();
        transport._http = new HttpMessageInvoker(new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false,
            ConnectCallback = async (_, token) =>
            {
                var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
                try
                {
                    await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(path), token).ConfigureAwait(false);
                    return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
                }
                catch { socket.Dispose(); throw; }
            }
        });
        transport._socket.Options.CollectHttpResponseDetails = true;
        try
        {
            await transport._socket.ConnectAsync(new Uri("ws://localhost/"), transport._http, ct).ConfigureAwait(false);
            return transport;
        }
        catch { await transport.DisposeAsync().ConfigureAwait(false); throw; }
    }
    public async Task SendAsync(string message, CancellationToken ct) =>
        await _socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[32768];
        using var stream = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) throw new IOException("Expected a text WebSocket frame");
            stream.Write(buffer, 0, result.Count);
            if (stream.Length > 64 * 1024 * 1024) throw new IOException("Server message exceeds 64 MiB");
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(stream.GetBuffer(), 0, (int)stream.Length);
    }
    public ValueTask DisposeAsync() { _socket.Abort(); _socket.Dispose(); _http?.Dispose(); return ValueTask.CompletedTask; }
}
