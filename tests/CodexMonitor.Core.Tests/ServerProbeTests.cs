using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodexMonitor.Core;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CodexMonitor.Core.Tests;

[TestClass]
public sealed class ServerProbeTests
{
    [TestMethod]
    [DataRow("codex_cli_rs/test", 1)]
    [DataRow("unrelated-service/test", 0)]
    public async Task DiscoveryUsesCodexHandshakeAndNeverStartsOrResumesThreads(string agent, int expected)
    {
        await using var server = new ProbeServer(agent);
        var discovery = new LocalServerDiscovery((_, _) => Task.FromResult<IReadOnlyList<ServerCandidate>>([new(server.Endpoint)]));
        var results = await discovery.FindAsync(LocalSessionStore.DefaultHome, "codex");
        Assert.HasCount(expected, results);
        Assert.IsTrue(server.Methods.All(m => m is "initialize" or "initialized"));
        Assert.IsTrue(server.Methods.Contains("initialize"));
    }
    [TestMethod]
    public async Task AuthenticationRequiredServerIsListedButCannotBeSelectedForConnection()
    {
        await using var server = new ProbeServer("", requireAuthentication: true);
        var discovery = new LocalServerDiscovery((_, _) => Task.FromResult<IReadOnlyList<ServerCandidate>>([new(server.Endpoint)]));
        var results = await discovery.FindAsync(LocalSessionStore.DefaultHome, "codex");
        Assert.HasCount(1, results); Assert.IsTrue(results[0].RequiresAuthentication); Assert.IsFalse(results[0].CanConnect);
        Assert.HasCount(0, server.Methods);
    }

    private sealed class ProbeServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _lifetime = new(TimeSpan.FromSeconds(15));
        private readonly Task _worker;
        public string Endpoint { get; }
        public ConcurrentQueue<string> Methods { get; } = new();
        public ProbeServer(string agent, bool requireAuthentication = false)
        {
            _listener.Start(); Endpoint = $"ws://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _worker = ServeAsync(agent, requireAuthentication);
        }
        private async Task ServeAsync(string agent, bool requireAuthentication)
        {
            try
            {
                using var tcp = await _listener.AcceptTcpClientAsync(_lifetime.Token);
                await using var stream = tcp.GetStream();
                var header = new StringBuilder(); var one = new byte[1];
                while (header.Length < 16384 && await stream.ReadAsync(one, _lifetime.Token) > 0)
                { header.Append((char)one[0]); if (header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) break; }
                if (requireAuthentication)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 401 Unauthorized\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), _lifetime.Token);
                    return;
                }
                var key = header.ToString().Split("\r\n").First(l => l.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
                var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n"), _lifetime.Token);
                using var socket = WebSocket.CreateFromStream(stream, true, null, Timeout.InfiniteTimeSpan);
                while (!_lifetime.IsCancellationRequested)
                {
                    using var message = new MemoryStream(); var buffer = new byte[4096]; WebSocketReceiveResult read;
                    do
                    {
                        read = await socket.ReceiveAsync(buffer, _lifetime.Token);
                        if (read.MessageType == WebSocketMessageType.Close) return;
                        message.Write(buffer, 0, read.Count);
                    } while (!read.EndOfMessage);
                    using var json = JsonDocument.Parse(message.ToArray());
                    var request = json.RootElement; var method = request.Str("method"); Methods.Enqueue(method);
                    if (method == "initialize")
                        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { id = request.Field("id"), result = new { userAgent = agent } }), WebSocketMessageType.Text, true, _lifetime.Token);
                }
            }
            catch (Exception e) when (e is IOException or SocketException or WebSocketException or OperationCanceledException) { }
        }
        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync(); _listener.Stop(); await _worker; _lifetime.Dispose();
        }
    }
}
