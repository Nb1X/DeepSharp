using System.Security.Cryptography;
using System.Text.Json;

namespace DeepSharp;

public sealed record SavedConversation(
    string Id,
    string ShortId,
    string Name,
    string DeepSeekSessionId,
    string Url,
    string Model,
    bool GodMode,
    JsonElement? LastMessageId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed class ConversationStore
{
    private const string Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly object _gate = new();
    private List<SavedConversation> _conversations;
    public string Path { get; }

    public ConversationStore(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        try { _conversations = File.Exists(Path) ? JsonSerializer.Deserialize<List<SavedConversation>>(File.ReadAllText(Path), JsonOptions) ?? [] : []; }
        catch { _conversations = []; }
    }

    public SavedConversation Create(string deepSeekSessionId, string model, bool godMode = false)
    {
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            string shortId;
            do { shortId = RandomId(6); } while (_conversations.Any(c => c.ShortId.Equals(shortId, StringComparison.OrdinalIgnoreCase)));
            var conversation = new SavedConversation(
                RandomId(64), shortId, "New conversation", deepSeekSessionId,
                $"https://chat.deepseek.com/a/chat/s/{Uri.EscapeDataString(deepSeekSessionId)}",
                model, godMode, null, now, now);
            _conversations.Add(conversation);
            Save();
            return conversation;
        }
    }

    public IReadOnlyList<SavedConversation> List()
    {
        lock (_gate) return _conversations.OrderByDescending(c => c.UpdatedAt).ToArray();
    }

    public SavedConversation? Find(string id)
    {
        lock (_gate) return _conversations.FirstOrDefault(c =>
            c.ShortId.Equals(id, StringComparison.OrdinalIgnoreCase) ||
            c.Id.Equals(id, StringComparison.OrdinalIgnoreCase) ||
            c.DeepSeekSessionId.Equals(id, StringComparison.OrdinalIgnoreCase));
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var index = _conversations.FindIndex(c => c.Id.Equals(id, StringComparison.Ordinal));
            if (index < 0) return false;
            var removed = _conversations[index];
            _conversations.RemoveAt(index);
            try { Save(); }
            catch { _conversations.Insert(index, removed); throw; }
            return true;
        }
    }

    public SavedConversation UpdateAfterTurn(string id, JsonElement? lastMessageId, string firstPrompt)
    {
        lock (_gate)
        {
            var index = _conversations.FindIndex(c => c.Id.Equals(id, StringComparison.Ordinal));
            if (index < 0) throw new InvalidOperationException("Conversation is missing from sessions.json: " + id);
            var old = _conversations[index];
            var name = old.Name is "New conversation" or "Nouvelle conversation" ? MakeName(firstPrompt) : old.Name;
            var updated = old with { Name = name, LastMessageId = lastMessageId ?? old.LastMessageId, UpdatedAt = DateTimeOffset.UtcNow };
            _conversations[index] = updated;
            Save();
            return updated;
        }
    }

    public SavedConversation UpdateLastMessageId(string id, JsonElement lastMessageId)
    {
        lock (_gate)
        {
            var index = _conversations.FindIndex(c => c.Id.Equals(id, StringComparison.Ordinal));
            if (index < 0) throw new InvalidOperationException("Conversation is missing from sessions.json: " + id);
            var updated = _conversations[index] with { LastMessageId = lastMessageId.Clone(), UpdatedAt = DateTimeOffset.UtcNow };
            _conversations[index] = updated;
            Save();
            return updated;
        }
    }

    private void Save()
    {
        var temporaryPath = Path + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(_conversations, JsonOptions));
        File.Move(temporaryPath, Path, true);
    }

    private static string MakeName(string prompt)
    {
        var name = string.Join(' ', prompt.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return name.Length <= 64 ? name : name[..61] + "...";
    }

    private static string RandomId(int length)
    {
        return string.Create(length, 0, static (span, _) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        });
    }
}
