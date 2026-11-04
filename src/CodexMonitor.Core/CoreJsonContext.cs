using System.Text.Json.Serialization;

namespace CodexMonitor.Core;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(LocalSessionCache))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string))]
internal partial class CoreJsonContext : JsonSerializerContext;
