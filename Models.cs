using System.Text.Json;

namespace DeepSharp;

public sealed record Tool(string Name, string Description, JsonElement Parameters)
{
    public Tool(string name, string description, object parameters) : this(name, description, JsonSerializer.SerializeToElement(parameters)) { }
    public object AsDictionary() => new { name = Name, description = Description, parameters = Parameters };
}

public sealed record ToolCall(string Name, Dictionary<string, JsonElement> Arguments);
public sealed record ToolExecution(string Name, Dictionary<string, JsonElement> Arguments, object? Output);
public sealed record ToolResponse(string Content, IReadOnlyList<ToolCall> ToolCalls, string Thinking = "");
public sealed record AgentEvent(string Type, string Message, string? ToolName = null, object? Arguments = null, object? Output = null, double? Duration = null);
public sealed record UploadedFile(string Id, string Name, long Size, string Status, bool IsImage, string ModelKind);
public sealed record ChatHistoryEntry(string Role, string Content);

public sealed class AgentResponse(string content, IReadOnlyList<ToolExecution>? toolsUsed = null, IReadOnlyList<AgentEvent>? events = null)
{
    public string Content { get; } = content;
    public IReadOnlyList<ToolExecution> ToolsUsed { get; } = toolsUsed ?? [];
    public IReadOnlyList<AgentEvent> Events { get; } = events ?? [];
    public override string ToString() => Content;
    public static implicit operator string(AgentResponse response) => response.Content;
}

public sealed class AgentStream : IEnumerable<string>
{
    private readonly Func<IEnumerable<string>> _stream;
    internal AgentStream(Func<IEnumerable<string>> stream) => _stream = stream;
    public IReadOnlyList<ToolExecution> ToolsUsed { get; internal set; } = [];
    public IReadOnlyList<AgentEvent> Events { get; internal set; } = [];
    public IEnumerator<string> GetEnumerator() => _stream().GetEnumerator();
    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}

public sealed record AppConfig
{
    public string? Token { get; init; }
    public string Model { get; init; } = "expert";
    public bool ShowThinking { get; init; }
    public bool SearchEnabled { get; init; } = true;
    public bool GodMode { get; init; }
    public bool AgentMode { get; init; } = true;
    public bool ExperimentalFeatures { get; init; }
}

internal static class JsonExt
{
    public static string? String(this JsonElement element, string name) => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
    public static JsonElement Prop(this JsonElement element, string name) => element.TryGetProperty(name, out var p) ? p : default;
    public static int Int(this JsonElement element, string name, int fallback = 0) => element.TryGetProperty(name, out var p) && p.TryGetInt32(out var n) ? n : fallback;
    public static bool Bool(this JsonElement element, string name, bool fallback = false) => element.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetBoolean() : fallback;
}
