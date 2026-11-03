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
    {
        var resolved = ResolveExecutable(executable);
        var start = new ProcessStartInfo(resolved) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) };
        start.ArgumentList.Add("app-server");
        start.ArgumentList.Add("--listen"); start.ArgumentList.Add("stdio://");
        start.Environment["CODEX_HOME"] = Path.GetFullPath(codexHome);
        return new(Process.Start(start) ?? throw new IOException("Unable to start Codex"));
    }

    public static string ResolveExecutable(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) throw new ArgumentException("Executable is required");
        // npm's codex.cmd requires a shell. Resolve its native package instead of interpolating shell commands.
        var candidates = Path.IsPathFullyQualified(executable) ? new[] { executable } :
            (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).SelectMany(dir =>
                new[] { Path.Combine(dir, executable + ".exe"), Path.Combine(dir, executable + ".cmd"), Path.Combine(dir, executable) }).ToArray();
        foreach (var candidate in candidates)
        {
            if (!File.Exists(candidate)) continue;
            if (candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return candidate;
            if (candidate.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || candidate.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
            {
                var package = Path.Combine(Path.GetDirectoryName(candidate)!, "node_modules", "@openai");
                if (Directory.Exists(package))
                {
                    var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
                    { System.Runtime.InteropServices.Architecture.Arm64 => "aarch64", System.Runtime.InteropServices.Architecture.X64 => "x86_64", _ => "i686" };
                    var native = Directory.EnumerateFiles(package, "codex.exe", SearchOption.AllDirectories)
                        .FirstOrDefault(p => p.Contains(architecture, StringComparison.OrdinalIgnoreCase));
                    if (native is not null) return native;
                }
            }
        }
        throw new FileNotFoundException("Choose the native codex.exe executable in Settings", executable);
    }

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

public sealed class WebSocketTransport : IRpcTransport
{
    private readonly ClientWebSocket _socket = new();
    public static bool IsAllowedEndpoint(string address) => Uri.TryCreate(address, UriKind.Absolute, out var uri) &&
        uri.Scheme is "ws" or "wss" && uri.IsLoopback && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    public static async Task<WebSocketTransport> ConnectAsync(string endpoint, CancellationToken ct = default)
    {
        if (!IsAllowedEndpoint(endpoint)) throw new ArgumentException("Only loopback WebSocket endpoints are supported", nameof(endpoint));
        var t = new WebSocketTransport();
        try { await t._socket.ConnectAsync(new Uri(endpoint), ct).ConfigureAwait(false); return t; }
        catch { t._socket.Dispose(); throw; }
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
    public ValueTask DisposeAsync() { _socket.Abort(); _socket.Dispose(); return ValueTask.CompletedTask; }
}
