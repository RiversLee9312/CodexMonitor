using System.Text.Json;
using System.Collections.Concurrent;

namespace CodexMonitor.Core;

/// <summary>Observed configuration is a live hint, never a substitute for historical per-turn evidence.</summary>
public sealed class ModelTimeline
{
    private readonly ConcurrentDictionary<string, string> _current = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Thread, string Turn), string> _turns = [];
    private readonly ConcurrentDictionary<string, string> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string Thread, string Item), string> _items = [];
    private readonly ConcurrentDictionary<(string Thread, string Turn), TurnInfo> _details = [];
    private readonly ConcurrentDictionary<string, TurnInfo> _configuration = new(StringComparer.Ordinal);
    public TurnInfo? Details(string thread, string turn) => _details.GetValueOrDefault((thread, turn));
    public IReadOnlyDictionary<string, TurnInfo> Details(string thread) => _details.Where(p => p.Key.Thread == thread).ToDictionary(p => p.Key.Turn, p => p.Value);
    public void Record(string thread, string turn, Func<TurnInfo, TurnInfo> change)
    { if (thread.Length > 0 && turn.Length > 0) _details.AddOrUpdate((thread, turn), _ => change(new(turn)), (_, old) => change(old)); }
    public string? ForItem(string thread, string item) => _items.GetValueOrDefault((thread, item));
    public void Remember(string thread, string item, string? model) { if (model is { Length: > 0 }) _items.TryAdd((thread, item), model); }
    public string? Current(string thread) => _current.GetValueOrDefault(thread);
    public void Configure(string thread, string model) { if (model.Length > 0) { _current[thread] = model; _configuration.AddOrUpdate(thread, new TurnInfo("") { Model = model, ModelSource = "ModelConfigured" }, (_, old) => old with { Model = model }); } }
    public string? ForTurn(string thread, string? turn) => turn is null ? Current(thread) : _turns.GetValueOrDefault((thread, turn));
    public void Start(string thread, string turn, string? explicitModel = null)
    {
        if (turn.Length == 0) return;
        _active[thread] = turn;
        if ((explicitModel ?? Current(thread)) is { Length: > 0 } model) _turns.TryAdd((thread, turn), model);
        Record(thread, turn, old => old.Model is not null ? old : old with { Model = _turns.GetValueOrDefault((thread, turn)), ModelSource = explicitModel is null ? "ModelConfigured" : "ModelRecorded",
            Provider = old.Provider ?? _configuration.GetValueOrDefault(thread)?.Provider, Effort = old.Effort ?? _configuration.GetValueOrDefault(thread)?.Effort, ServiceTier = old.ServiceTier ?? _configuration.GetValueOrDefault(thread)?.ServiceTier });
    }
    public void Observe(string method, JsonElement p)
    {
        var thread = p.Str("threadId");
        if (method == "thread/settings/updated")
        {
            var settings = p.Field("threadSettings"); Configure(thread, settings.Str("model"));
            _configuration.AddOrUpdate(thread, _ => Settings(new(""), settings), (_, old) => Settings(old, settings));
        }
        if (method == "codex/event/session_configured") Configure(thread, p.Str("model"));
        if (method == "turn/started") Start(thread, p.Field("turn").Str("id"), p.Field("turn").Str("model") is { Length: > 0 } model ? model : null);
        if (method is "turn/started" or "turn/completed") Record(thread, p.Field("turn").Str("id"), old => TurnInfo.ReadLifecycle(old, p.Field("turn")));
        if (method == "turn/completed" && _active.GetValueOrDefault(thread) == p.Field("turn").Str("id")) _active.TryRemove(thread, out _);
        if (method == "thread/tokenUsage/updated")
        {
            var usage = p.Field("tokenUsage");
            Record(thread, p.Str("turnId"), old => old with { LastRequest = TokenBreakdown.Parse(usage.Field("last")), ThreadTotal = TokenBreakdown.Parse(usage.Field("total")),
                ContextWindow = usage.Field("modelContextWindow").TryNumber(out var window) ? window : old.ContextWindow });
        }
        if (method == "model/rerouted" && p.Str("toModel") is { Length: > 0 } target)
        { var turn = p.Str("turnId"); if (turn.Length > 0) { _turns[(thread, turn)] = target; Record(thread, turn, old => old with { Model = target, ModelSource = "ModelRerouted" }); } }
        if (method == "response.created" && p.Field("response").Str("model") is { Length: > 0 } actual)
        { var turn = p.Str("turnId", _active.GetValueOrDefault(thread, "")); if (turn.Length > 0) { _turns[(thread, turn)] = actual; Record(thread, turn, old => old with { Model = actual, ModelSource = "ModelReported" }); } }
    }
    private static TurnInfo Settings(TurnInfo old, JsonElement settings) => old with { Provider = settings.Str("modelProvider", old.Provider ?? ""), Effort = settings.Str("effort", old.Effort ?? ""), ServiceTier = settings.Str("serviceTier", old.ServiceTier ?? "") };
}
