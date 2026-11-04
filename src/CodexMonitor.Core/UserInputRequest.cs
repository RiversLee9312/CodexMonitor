using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexMonitor.Core;

public sealed record UserInputOption(string Label, string Description);
public sealed record UserInputQuestion(string Id, string Header, string Question, bool AllowOther, bool Secret, IReadOnlyList<UserInputOption> Options);
public sealed record UserInputRequest(string ThreadId, string TurnId, string ItemId, IReadOnlyList<UserInputQuestion> Questions)
{
    public static UserInputRequest Parse(JsonElement p)
    {
        var questions = p.Field("questions").Items().Select(q => new UserInputQuestion(q.Str("id"), q.Str("header"), q.Str("question"),
            q.Field("isOther").ValueKind == JsonValueKind.True, q.Field("isSecret").ValueKind == JsonValueKind.True,
            q.Field("options").Items().Select(o => new UserInputOption(o.Str("label"), o.Str("description"))).ToArray())).ToArray();
        if (questions.Length == 0 || questions.Any(q => q.Id.Length == 0) || questions.Select(q => q.Id).Distinct().Count() != questions.Length)
            throw new InvalidDataException("Invalid user input questions");
        return new(p.Str("threadId"), p.Str("turnId"), p.Str("itemId"), questions);
    }
    public JsonObject Respond(IReadOnlyDictionary<string, string> values)
    {
        var answers = new JsonObject();
        foreach (var q in Questions)
        {
            if (!values.TryGetValue(q.Id, out var value) || string.IsNullOrWhiteSpace(value)) throw new InvalidDataException("An answer is required");
            if (q.Options.Count > 0 && !q.AllowOther && !q.Options.Any(o => o.Label == value)) throw new InvalidDataException("Invalid answer option");
            answers[q.Id] = new JsonObject { ["answers"] = new JsonArray((JsonNode)JsonValue.Create(value)!) };
        }
        return new() { ["answers"] = answers };
    }
    public static JsonObject Cancelled() => new() { ["answers"] = new JsonObject() };
}
