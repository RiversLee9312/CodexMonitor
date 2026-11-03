using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexMonitor.Core;

public sealed class RpcException(int code, string message) : Exception(message) { public int Code { get; } = code; }
public sealed record RpcMessage(string Method, JsonElement Parameters, JsonElement? Id = null, long Sequence = 0);
public sealed record RpcResult(JsonElement Value, long Sequence);

public sealed class JsonRpcClient : IAsyncDisposable
{
    private readonly IRpcTransport _transport;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<RpcResult>> _pending = new();
    private readonly SemaphoreSlim _writeLock = new(1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _pump;
    private long _nextId;
    private long _receiveSequence;
    private volatile bool _closed;
    public bool IsConnected => !_closed;
    public event Action<RpcMessage>? Message;
    public event Action<Exception?>? Disconnected;
    public JsonRpcClient(IRpcTransport transport) { _transport = transport; _pump = PumpAsync(); }

    public async Task<JsonElement> RequestAsync(string method, JsonObject parameters, CancellationToken ct = default) =>
        (await RequestStampedAsync(method, parameters, ct).ConfigureAwait(false)).Value;
    public async Task<RpcResult> RequestStampedAsync(string method, JsonObject parameters, CancellationToken ct = default)
    {
        if (_closed) throw new IOException("Connection closed");
        var id = Interlocked.Increment(ref _nextId).ToString(System.Globalization.CultureInfo.InvariantCulture);
        var completion = new TaskCompletionSource<RpcResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var registration = timeout.Token.Register(() => completion.TrySetCanceled(timeout.Token));
        try
        {
            await WriteAsync(new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters }, timeout.Token).ConfigureAwait(false);
            if (_closed) completion.TrySetException(new IOException("Connection closed"));
            return await completion.Task.ConfigureAwait(false);
        }
        finally { _pending.TryRemove(id, out _); }
    }
    public Task NotifyAsync(string method, JsonObject parameters, CancellationToken ct = default) => WriteAsync(new JsonObject { ["method"] = method, ["params"] = parameters }, ct);
    public Task RespondAsync(JsonElement id, JsonObject result, CancellationToken ct = default) => WriteAsync(new JsonObject { ["id"] = JsonNode.Parse(id.GetRawText()), ["result"] = result }, ct);
    public Task RejectAsync(JsonElement id, string message, CancellationToken ct = default) => WriteAsync(new JsonObject { ["id"] = JsonNode.Parse(id.GetRawText()), ["error"] = new JsonObject { ["code"] = -32601, ["message"] = message } }, ct);
    private async Task WriteAsync(JsonObject value, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try { await _transport.SendAsync(value.ToJsonString(), ct).ConfigureAwait(false); }
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
                var sequence = Interlocked.Increment(ref _receiveSequence);
                var root = doc.RootElement;
                var id = root.Field("id");
                if (root.Str("method") is { Length: > 0 } method)
                {
                    Message?.Invoke(new(method, root.Field("params").Clone(), id.ValueKind != JsonValueKind.Undefined ? id.Clone() : null, sequence));
                }
                else if (_pending.TryRemove(id.TextValue(), out var pending))
                {
                    var error = root.Field("error");
                    if (error.ValueKind == JsonValueKind.Object)
                        pending.TrySetException(new RpcException(error.Field("code").TryGetInt32(out var code) ? code : -1, error.Str("message")));
                    else pending.TrySetResult(new(root.Field("result").Clone(), sequence));
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
