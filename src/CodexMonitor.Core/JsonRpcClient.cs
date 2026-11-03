using System.Collections.Concurrent;
using System.Text.Json;

namespace CodexMonitor.Core;

public sealed class RpcException(int code, string message) : Exception(message) { public int Code { get; } = code; }
public sealed record RpcMessage(string Method, JsonElement Parameters, JsonElement? Id = null);

public sealed class JsonRpcClient : IAsyncDisposable
{
    private readonly IRpcTransport _transport;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _pump;
    private long _nextId;
    private volatile bool _closed;
    public bool IsConnected => !_closed;
    public event Action<RpcMessage>? Message;
    public event Action<Exception?>? Disconnected;
    public JsonRpcClient(IRpcTransport transport) { _transport = transport; _pump = PumpAsync(); }

    public async Task<JsonElement> RequestAsync(string method, object parameters, CancellationToken ct = default)
    {
        if (_closed) throw new IOException("Connection closed");
        var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
        try
        {
            await WriteAsync(new { id, method, @params = parameters }, timeout.Token).ConfigureAwait(false);
            if (_closed) completion.TrySetException(new IOException("Connection closed"));
            return await completion.Task.ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }
    public Task NotifyAsync(string method, object parameters, CancellationToken ct = default) => WriteAsync(new { method, @params = parameters }, ct);
    public Task RespondAsync(JsonElement id, object result, CancellationToken ct = default) => WriteAsync(new { id, result }, ct);
    public Task RejectAsync(JsonElement id, string message, CancellationToken ct = default) => WriteAsync(new { id, error = new { code = -32601, message } }, ct);
    private async Task WriteAsync(object value, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _transport.SendAsync(JsonSerializer.Serialize(value), ct).ConfigureAwait(false); }
        finally { _writeLock.Release(); }
    }
    private async Task PumpAsync()
    {
        Exception? failure = null;
        try
        {
            while (await _transport.ReceiveAsync(_lifetime.Token).ConfigureAwait(false) is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var id = root.Field("id");
                if (root.Str("method") is { Length: > 0 } method)
                {
                    Message?.Invoke(new(method, root.Field("params").Clone(), id.ValueKind != JsonValueKind.Undefined ? id.Clone() : null));
                }
                else if (_pending.TryRemove(id.TextValue(), out var pending))
                {
                    var error = root.Field("error");
                    if (error.ValueKind == JsonValueKind.Object)
                        pending.TrySetException(new RpcException(error.Field("code").TryGetInt32(out var code) ? code : -1, error.Str("message")));
                    else pending.TrySetResult(root.Field("result").Clone());
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) when (e is IOException or JsonException or System.Net.WebSockets.WebSocketException or ObjectDisposedException) { failure = e; }
        finally
        {
            _closed = true;
            foreach (var pending in _pending.Values) pending.TrySetException(failure ?? new IOException("Connection closed"));
            _pending.Clear();
            Disconnected?.Invoke(failure);
        }
    }
    public async ValueTask DisposeAsync()
    {
        _closed = true;
        await _lifetime.CancelAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        await _pump.ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
