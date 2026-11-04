using System.Text.Json;

namespace CodexMonitor.Core;

/// <summary>Observed configuration is a live hint, never a substitute for historical per-turn evidence.</summary>
public sealed class ModelTimeline
{
    private readonly Dictionary<string, string> _current = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Thread, string Turn), string> _turns = [];
    private readonly Dictionary<string, string> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Thread, string Item), string> _items = [];
    public string? ForItem(string thread, string item) => _items.GetValueOrDefault((thread, item));
    public void Remember(string thread, string item, string? model) { if (model is { Length: > 0 }) _items.TryAdd((thread, item), model); }
    public string? Current(string thread) => _current.GetValueOrDefault(thread);
    public void Configure(string thread, string model) { if (model.Length > 0) _current[thread] = model; }
    public string? ForTurn(string thread, string? turn) => turn is null ? Current(thread) : _turns.GetValueOrDefault((thread, turn));
    public void Start(string thread, string turn, string? explicitModel = null)
    { if (turn.Length > 0) { _active[thread] = turn; if ((explicitModel ?? Current(thread)) is { Length: > 0 } model) _turns.TryAdd((thread, turn), model); } }
    public void Observe(string method, JsonElement p)
    {
        var thread = p.Str("threadId");
        if (method == "thread/settings/updated") Configure(thread, p.Field("threadSettings").Str("model"));
        if (method == "turn/started") Start(thread, p.Field("turn").Str("id"), p.Field("turn").Str("model") is { Length: > 0 } model ? model : null);
        if (method == "model/rerouted" && p.Str("toModel") is { Length: > 0 } target)
        { var turn = p.Str("turnId"); if (turn.Length > 0) _turns[(thread, turn)] = target; }
        if (method == "response.created" && p.Field("response").Str("model") is { Length: > 0 } actual)
        { var turn = p.Str("turnId", _active.GetValueOrDefault(thread, "")); if (turn.Length > 0) _turns[(thread, turn)] = actual; }
    }
}
