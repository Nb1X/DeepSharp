using System.Text.Json;

namespace DeepSharp;

/// <summary>Persistent namespace-aware agent memory and transcript store (JSON-backed).</summary>
public sealed class MemoryStore : IDisposable
{
    private readonly object _gate = new(); private readonly string _path; private Data _data; public string Namespace { get; }
    public MemoryStore(string path, string @namespace = "default") { _path = Path.GetFullPath(path); Namespace = @namespace; Directory.CreateDirectory(Path.GetDirectoryName(_path)!); try { _data = JsonSerializer.Deserialize<Data>(File.ReadAllText(_path)) ?? new(); } catch { _data = new(); } }
    private void Save() => File.WriteAllText(_path, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
    public Dictionary<string, object?> Remember(string content, IEnumerable<string>? tags = null, double importance = .5)
    { content = content.Trim(); if (content.Length == 0) throw new ArgumentException("Memory content cannot be empty."); lock (_gate) { var m = new MemoryItem("mem_" + Guid.NewGuid().ToString("N"), Namespace, content, (tags ?? []).ToArray(), Math.Clamp(importance, 0, 1), DateTimeOffset.UtcNow.ToUnixTimeSeconds()); _data.Memories.Add(m); Save(); return ToDictionary(m); } }
    public Dictionary<string, object?> Get(string id) { lock (_gate) return ToDictionary(_data.Memories.FirstOrDefault(m => m.Id == id && m.Namespace == Namespace) ?? throw new ArgumentException("Unknown memory: " + id)); }
    public IReadOnlyList<Dictionary<string, object?>> Recall(string query, int limit = 8) { lock (_gate) return _data.Memories.Where(m => m.Namespace == Namespace && (m.Content.Contains(query, StringComparison.OrdinalIgnoreCase) || m.Tags.Any(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)))).OrderByDescending(m => m.Importance).ThenByDescending(m => m.Updated).Take(Math.Clamp(limit,1,50)).Select(ToDictionary).ToArray(); }
    public IReadOnlyList<Dictionary<string, object?>> List(int limit = 50) { lock (_gate) return _data.Memories.Where(m => m.Namespace == Namespace).OrderByDescending(m => m.Updated).Take(Math.Clamp(limit,1,500)).Select(ToDictionary).ToArray(); }
    public object Forget(string id) { lock (_gate) { var item = _data.Memories.FirstOrDefault(m => m.Id == id && m.Namespace == Namespace) ?? throw new ArgumentException("Unknown memory: " + id); _data.Memories.Remove(item); Save(); return new { forgotten = id, content = item.Content }; } }
    public string CreateSession(string model, string? title = null) { lock (_gate) { var id = "session_" + Guid.NewGuid().ToString("N"); _data.Sessions.Add(new SessionItem(id, Namespace, model, title, "", DateTimeOffset.UtcNow.ToUnixTimeSeconds(), [])); Save(); return id; } }
    public void AddTurn(string id, string role, string content) { lock (_gate) { var s = Session(id); s.Turns.Add(new(role, content)); Save(); } }
    public bool HasTurns(string id) { lock (_gate) return Session(id).Turns.Count > 0; }
    public string SessionContext(string id, int limit = 20) { lock (_gate) { var s = Session(id); var turns = s.Turns.TakeLast(Math.Clamp(limit,1,100)).Select(t => $"{t.Role}: {t.Content}"); return string.Join("\n", (s.Summary.Length > 0 ? new[]{"summary: " + s.Summary} : []).Concat(turns)); } }
    public object Checkpoint(string id, string summary) { lock (_gate) { var s = Session(id); s.Summary = summary; Save(); return new { session_id = id, summary }; } }
    public IReadOnlyList<object> ListSessions(int limit = 20) { lock (_gate) return _data.Sessions.Where(s => s.Namespace == Namespace).OrderByDescending(s => s.Updated).Take(Math.Clamp(limit,1,100)).Cast<object>().ToArray(); }
    private SessionItem Session(string id) => _data.Sessions.FirstOrDefault(s => s.Id == id && s.Namespace == Namespace) ?? throw new ArgumentException("Unknown session: " + id);
    private static Dictionary<string, object?> ToDictionary(MemoryItem m) => new() { ["id"] = m.Id, ["namespace"] = m.Namespace, ["content"] = m.Content, ["tags"] = m.Tags, ["importance"] = m.Importance, ["created_at"] = m.Updated, ["updated_at"] = m.Updated };
    public IReadOnlyList<Tool> Definitions => [new("remember","Store information in durable long-term memory.", new { type="object", properties=new { content=new { type="string" }, tags=new { type="array", items=new { type="string" } }, importance=new { type="number" } }, required=new[]{"content"} }), new("recall","Search durable memory.",new { type="object", properties=new { query=new { type="string" }, limit=new { type="integer" } }, required=new[]{"query"} }), new("list_memories","List stored memories.", new { type="object" }), new("forget","Remove memory by ID.",new { type="object", properties=new { memory_id=new { type="string" } }, required=new[]{"memory_id"} }), new("list_sessions","List resumable sessions.",new { type="object" })];
    public Dictionary<string, Func<JsonElement, object?>> Functions => new() { ["remember"] = a => Remember(a.String("content") ?? "", a.Prop("tags").ValueKind == JsonValueKind.Array ? a.GetProperty("tags").EnumerateArray().Select(t => t.GetString() ?? "") : null, a.Prop("importance").ValueKind == JsonValueKind.Number ? a.GetProperty("importance").GetDouble() : .5), ["recall"] = a => Recall(a.String("query") ?? "", a.Int("limit",8)), ["list_memories"] = a => List(a.Int("limit",50)), ["forget"] = a => Forget(a.String("memory_id") ?? ""), ["list_sessions"] = a => ListSessions(a.Int("limit",20)) };
    public void Dispose() { }
    private sealed class Data { public List<MemoryItem> Memories { get; set; } = []; public List<SessionItem> Sessions { get; set; } = []; }
    private sealed record MemoryItem(string Id, string Namespace, string Content, string[] Tags, double Importance, long Updated);
    private sealed class SessionItem(string id, string @namespace, string model, string? title, string summary, long updated, List<Turn> turns) { public string Id { get; } = id; public string Namespace { get; } = @namespace; public string Model { get; } = model; public string? Title { get; } = title; public string Summary { get; set; } = summary; public long Updated { get; set; } = updated; public List<Turn> Turns { get; } = turns; }
    private sealed record Turn(string Role, string Content);
}
