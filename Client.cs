using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Buffers.Binary;
using Wasmtime;

namespace DeepSharp;

public sealed class Client : IDisposable
{
    public const string Version = "0.3.1";
    public static string BaseUrl { get; set; } = Environment.GetEnvironmentVariable("DEEPSEEK_BASE_URL") ?? "https://chat.deepseek.com";
    public HttpClient Http { get; }
    public ChatsApi Chats { get; }
    public FilesApi Files { get; }
    public PowApi Pow { get; }
    public NativeTools NativeTools { get; }
    public TaskPlan Plan { get; } = new();
    public MemoryStore? Memory { get; }
    public ProjectIntelligence Project { get; }
    public bool AgentMode { get; }
    public int? MaxAgentRounds { get; }
    public Dictionary<string, (Tool Definition, Func<JsonElement, object?> Handler)> CustomTools { get; } = new();

    public Client(string? apiKey = null, bool allowBrowserAuth = false, int? authTimeout = null, bool agentMode = true, string? workingDirectory = null, double commandTimeout = 30, string? shell = null, int outputBudget = 8000, int? maxAgentRounds = null, bool memoryEnabled = true, string? memoryPath = null, string memoryNamespace = "default", HttpClient? httpClient = null)
    {
        AgentMode = agentMode; MaxAgentRounds = maxAgentRounds;
        var token = apiKey ?? Environment.GetEnvironmentVariable("DEEPWRAP_API_KEY") ?? Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY") ?? new ConfigStore().Load().Token;
        if (string.IsNullOrWhiteSpace(token) && allowBrowserAuth)
        {
            var captured = BrowserAuthentication.GetBearerTokenAsync(timeout: authTimeout is null ? null : TimeSpan.FromSeconds(authTimeout.Value)).GetAwaiter().GetResult();
            if (!string.IsNullOrWhiteSpace(captured)) token = captured;
        }
        if (string.IsNullOrWhiteSpace(token)) throw new InvalidOperationException("Missing API key. Pass apiKey, set DEEPWRAP_API_KEY, or enable browser authentication.");
        token = BearerToken.Normalize(token);
        Http = httpClient ?? new HttpClient();
        Http.Timeout = TimeSpan.FromSeconds(30);
        Http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:151.0) Gecko/20100101 Firefox/151.0");
        Http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");
        foreach (var (key, value) in new Dictionary<string,string> { ["x-client-platform"]="web", ["x-client-version"]="2.0.0", ["x-client-locale"]="en_US", ["x-client-timezone-offset"]="14400", ["x-app-version"]="2.0.0" }) Http.DefaultRequestHeaders.TryAddWithoutValidation(key, value);
        NativeTools = new(workingDirectory, commandTimeout, shell, outputBudget);
        var cfgDir = System.IO.Path.GetDirectoryName(new ConfigStore().Path)!;
        Memory = memoryEnabled ? new(memoryPath ?? System.IO.Path.Combine(cfgDir, "memory.json"), memoryNamespace) : null;
        var root = NativeTools.WorkingDirectory;
        var keyHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..16].ToLowerInvariant();
        Project = new(root, System.IO.Path.Combine(cfgDir, "projects", keyHash + ".json"));
        Chats = new(this); Files = new(this); Pow = new(this);
    }
    public IReadOnlyList<Tool> AgentTools => NativeTools.Definitions.Concat(Memory?.Definitions ?? []).Concat(Project.Definitions).Concat(Plan.Definitions).Concat(CustomTools.Values.Select(x => x.Definition)).ToArray();
    public static Client FromBrowserAuth(int? timeout = null) => new(allowBrowserAuth: true, authTimeout: timeout);
    public Dictionary<string, Func<JsonElement, object?>> AgentFunctions => NativeTools.Functions.Concat(Memory?.Functions ?? new()).Concat(Project.Functions).Concat(Plan.Functions).Concat(CustomTools.ToDictionary(x => x.Key, x => x.Value.Handler)).ToDictionary(x => x.Key, x => x.Value);
    public void RegisterTool(Tool tool, Func<JsonElement, object?> function, bool replace = false)
    {
        if (AgentFunctions.ContainsKey(tool.Name) && !CustomTools.ContainsKey(tool.Name)) throw new ArgumentException($"Cannot replace built-in tool: {tool.Name}");
        if (CustomTools.ContainsKey(tool.Name) && !replace) throw new ArgumentException($"Tool is already registered: {tool.Name}");
        CustomTools[tool.Name] = (tool, function);
    }
    public void Dispose() { Memory?.Dispose(); Http.Dispose(); }
}

public sealed class ChatsApi(Client client)
{
    public static readonly string[] SupportedModels = ["expert", "default", "vision"];
    public async Task<ChatSession> CreateSessionAsync(string model = "expert", bool godMode = false, CancellationToken cancellationToken = default)
    {
        if (!SupportedModels.Contains(model)) throw new ArgumentException($"Unsupported model: {model}");
        using var response = await client.Http.PostAsync(Client.BaseUrl.TrimEnd('/') + "/api/v0/chat_session/create", new StringContent("{}", Encoding.UTF8, "application/json"), cancellationToken);
        response.EnsureSuccessStatusCode(); using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var data = doc.RootElement.GetProperty("data").GetProperty("biz_data").GetProperty("chat_session");
        if (doc.RootElement.Int("code") != 0) throw new InvalidOperationException("create_session failed: " + doc.RootElement);
        return new(client, data.GetProperty("id").GetString()!, model, godMode);
    }
    public async Task<JsonElement?> FetchLastAssistantMessageIdAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var url = Client.BaseUrl.TrimEnd('/') + "/api/v0/chat/history_messages?chat_session_id=" + Uri.EscapeDataString(sessionId);
        using var response = await client.Http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var messages = doc.RootElement.Prop("data").Prop("biz_data").Prop("chat_messages");
        if (messages.ValueKind != JsonValueKind.Array) return null;
        JsonElement? lastAssistant = null;
        foreach (var message in messages.EnumerateArray())
        {
            var id = message.Prop("message_id");
            if (id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String)) continue;
            if (string.Equals(message.String("role"), "ASSISTANT", StringComparison.OrdinalIgnoreCase)) lastAssistant = id.Clone();
        }
        return lastAssistant;
    }
    public async Task<IReadOnlyList<ChatHistoryEntry>> FetchHistoryAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var url = Client.BaseUrl.TrimEnd('/') + "/api/v0/chat/history_messages?chat_session_id=" + Uri.EscapeDataString(sessionId);
        using var response = await client.Http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var messages = doc.RootElement.Prop("data").Prop("biz_data").Prop("chat_messages");
        if (messages.ValueKind != JsonValueKind.Array) return [];
        var result = new List<ChatHistoryEntry>();
        foreach (var message in messages.EnumerateArray())
        {
            var role = message.String("role")?.ToLowerInvariant();
            if (role is not ("user" or "assistant")) continue;
            var content = new StringBuilder();
            var fragments = message.Prop("fragments");
            if (fragments.ValueKind == JsonValueKind.Array)
            {
                foreach (var fragment in fragments.EnumerateArray())
                {
                    var type = fragment.String("type") ?? "";
                    var isToolFragment = type.Contains("TOOL", StringComparison.OrdinalIgnoreCase) ||
                        type.Contains("FUNCTION", StringComparison.OrdinalIgnoreCase) ||
                        type.Contains("CALL", StringComparison.OrdinalIgnoreCase);
                    var isVisibleFragment = role == "assistant"
                        ? type.Equals("RESPONSE", StringComparison.OrdinalIgnoreCase)
                        : !type.Equals("THINK", StringComparison.OrdinalIgnoreCase) && !isToolFragment;
                    if (!isVisibleFragment) continue;
                    var text = fragment.String("content");
                    if (!string.IsNullOrEmpty(text)) content.Append(text);
                }
            }
            if (content.Length == 0 && (fragments.ValueKind != JsonValueKind.Array || role == "user"))
                content.Append(message.String("content") ?? message.String("prompt") ?? "");

            var displayText = content.ToString();
            if (role == "user")
            {
                const string userMessageMarker = "\n\nUSER MESSAGE: ";
                var marker = displayText.LastIndexOf(userMessageMarker, StringComparison.Ordinal);
                if (marker >= 0) displayText = displayText[(marker + userMessageMarker.Length)..];
                if (displayText.StartsWith("Original user request:", StringComparison.OrdinalIgnoreCase) ||
                    displayText.StartsWith("[DEEPWRAP TOOLS]", StringComparison.OrdinalIgnoreCase)) continue;
            }
            else
            {
                displayText = ChatSession.StripToolMarkup(displayText, client.AgentTools).Trim();
            }

            if (displayText.Length > 0) result.Add(new(role, displayText));
        }
        return result;
    }
    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var url = Client.BaseUrl.TrimEnd('/') + "/api/v0/chat_session/delete";
        using var response = await client.Http.PostAsync(url, JsonContent.Create(new { chat_session_id = sessionId }), cancellationToken);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var code = doc.RootElement.Int("code", -1);
        if (code != 0) throw new InvalidOperationException("DeepSeek refused to delete the conversation: " + (doc.RootElement.String("msg") ?? doc.RootElement.String("message") ?? doc.RootElement.ToString()));
    }
    public ChatSession CreateSession(string model = "expert", bool godMode = false) => CreateSessionAsync(model, godMode).GetAwaiter().GetResult();
}

public sealed class PowApi(Client client)
{
    private static readonly HttpClient WasmHttp = new();
    private static readonly SemaphoreSlim WasmGate = new(1,1);
    private static byte[]? _wasmBytes;
    public sealed record PowChallenge(string Algorithm,string Challenge,string Salt,string Signature,int Difficulty,long ExpireAt,long ExpireAfter,string TargetPath);
    public static async Task<byte[]> LoadWasmAsync(CancellationToken cancellationToken = default)
    {
        if (_wasmBytes is not null) return _wasmBytes;
        await WasmGate.WaitAsync(cancellationToken);
        try
        {
            if (_wasmBytes is not null) return _wasmBytes;
            var local = Environment.GetEnvironmentVariable("DEEPWRAP_WASM_PATH");
            if (!string.IsNullOrWhiteSpace(local) && File.Exists(local)) return _wasmBytes = await File.ReadAllBytesAsync(local,cancellationToken);
            var cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"deepwrap","sha3_wasm_bg.wasm");
            if (File.Exists(cache)) return _wasmBytes = await File.ReadAllBytesAsync(cache,cancellationToken);
            var bytes = await WasmHttp.GetByteArrayAsync("https://fe-static.deepseek.com/chat/static/sha3_wasm_bg.7b9ca65ddd.wasm",cancellationToken);
            Directory.CreateDirectory(Path.GetDirectoryName(cache)!); await File.WriteAllBytesAsync(cache,bytes,cancellationToken); return _wasmBytes=bytes;
        }
        finally { WasmGate.Release(); }
    }
    public async Task<string> BuildHeaderAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        using var response = await client.Http.PostAsync(Client.BaseUrl.TrimEnd('/') + "/api/v0/chat/create_pow_challenge", JsonContent.Create(new { target_path = targetPath }), cancellationToken);
        response.EnsureSuccessStatusCode(); using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        var challenge = doc.RootElement.GetProperty("data").GetProperty("biz_data").GetProperty("challenge");
        var item = new PowChallenge(challenge.String("algorithm")!,challenge.String("challenge")!,challenge.String("salt")!,challenge.String("signature")!,challenge.Int("difficulty"),challenge.Prop("expire_at").GetInt64(),challenge.Int("expire_after"),challenge.String("target_path")!);
        var answer = await SolveAsync(item,cancellationToken);
        var payload = new { algorithm=item.Algorithm, challenge=item.Challenge, salt=item.Salt, answer, signature=item.Signature, target_path=targetPath };
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload,new JsonSerializerOptions{WriteIndented=false})));
    }
    public static async Task<long> SolveAsync(PowChallenge challenge,CancellationToken cancellationToken=default)
    {
        var bytes=await LoadWasmAsync(cancellationToken); using var engine=new Engine(); using var module=Module.FromBytes(engine,"deepseek-pow",bytes); using var store=new Store(engine); using var linker=new Linker(engine); var instance=linker.Instantiate(store,module);
        var memory=instance.GetMemory("memory") ?? throw new InvalidOperationException("PoW WASM has no exported memory.");
        var malloc=instance.GetFunction<int,int,int>("__wbindgen_export_0") ?? throw new InvalidOperationException("PoW WASM allocator export is missing.");
        var realloc=instance.GetFunction<int,int,int,int,int>("__wbindgen_export_1") ?? throw new InvalidOperationException("PoW WASM realloc export is missing.");
        var stack=instance.GetFunction<int,int>("__wbindgen_add_to_stack_pointer") ?? throw new InvalidOperationException("PoW WASM stack export is missing.");
        var solve=instance.GetFunction("wasm_solve") ?? throw new InvalidOperationException("PoW WASM solve export is missing.");
        var challengeBytes=Encoding.UTF8.GetBytes(challenge.Challenge); var prefixBytes=Encoding.UTF8.GetBytes($"{challenge.Salt}_{challenge.ExpireAt}_");
        var challengePtr=malloc(challengeBytes.Length,1); challengeBytes.AsSpan().CopyTo(memory.GetSpan(challengePtr,challengeBytes.Length));
        var prefixPtr=malloc(prefixBytes.Length,1); prefixBytes.AsSpan().CopyTo(memory.GetSpan(prefixPtr,prefixBytes.Length));
        var ret=stack(-16);
        try
        {
            solve.Invoke(new ValueBox[] { ret, challengePtr, challengeBytes.Length, prefixPtr, prefixBytes.Length, (double)challenge.Difficulty });
            var ok=memory.ReadInt32(ret); var raw=memory.GetSpan(ret+8,8); var answer=BinaryPrimitives.ReadInt64LittleEndian(raw);
            if(ok==0) throw new InvalidOperationException($"No PoW solution found (algorithm={challenge.Algorithm}, difficulty={challenge.Difficulty}).");
            return checked((long)BitConverter.Int64BitsToDouble(answer));
        }
        finally { stack(16); }
    }
}

public sealed class ChatSession(Client client, string sessionId, string modelType, bool godMode = false, JsonElement? parentMessageId = null)
{
    private JsonElement? _lastMessageId = parentMessageId;
    private bool _godModeTriggered;
    public string SessionId { get; } = sessionId;
    public string ModelType { get; } = modelType;
    public bool GodMode { get; } = godMode;
    public JsonElement? LastMessageId => _lastMessageId;

    public async IAsyncEnumerable<(string Kind, string Text)> RespondStructuredAsync(string prompt, bool thinking = true, bool search = true, IEnumerable<string>? fileIds = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (GodMode && !_godModeTriggered) { prompt = "[DEEPWRAP DIRECT MODE]\nAnswer like a senior engineer briefing a peer:\n1. No preamble, no restating the question, no filler.\n2. Lead with the answer, then only the reasoning that changes it.\n3. Be concrete: exact steps, names, numbers, commands, code.\n4. Say plainly what you do not know or could not verify.\n5. No moralizing and no unsolicited disclaimers; a real caveat gets one line.\n6. Use the user's language and stop when the answer is complete.\n\nUSER MESSAGE: " + prompt; _godModeTriggered = true; }
        var ids = (fileIds ?? []).ToArray();
        if (ids.Length > 0 && ModelType != "vision") throw new ArgumentException("File attachments are supported only by the vision model.");
        var body = new { chat_session_id = SessionId, parent_message_id = _lastMessageId, model_type = _lastMessageId is null ? ModelType : null, prompt, ref_file_ids = ids, thinking_enabled = thinking, search_enabled = ModelType == "default" && search, action = (object?)null, preempt = false };
        var url = Client.BaseUrl.TrimEnd('/') + "/api/v0/chat/completion";
        var pow = await client.Pow.BuildHeaderAsync("/api/v0/chat/completion", cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.TryAddWithoutValidation("x-ds-pow-response", pow); request.Headers.Accept.ParseAdd("text/event-stream");
        using var response = await client.Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken); response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken); using var reader = new StreamReader(stream);
        string? currentKind = null; string? line;
        while ((line = await reader.ReadLineAsync(cancellationToken)) is not null)
        {
            if (line.StartsWith("event: close")) yield break;
            if (!line.StartsWith("data:")) continue;
            var raw = line[5..].Trim(); if (raw.Length == 0) continue;
            JsonDocument doc; try { doc = JsonDocument.Parse(raw); } catch (JsonException) { continue; }
            using (doc)
            {
                var root = doc.RootElement; var v = root.Prop("v");
                var messageId = FindMessageId(root);
                if (messageId is not null) _lastMessageId = messageId;
                if (v.ValueKind == JsonValueKind.Object)
                {
                    var resp = v.Prop("response");
                    var fragments = resp.Prop("fragments");
                    if (fragments.ValueKind == JsonValueKind.Array) foreach (var fragment in fragments.EnumerateArray()) { var kind = fragment.String("type") switch { "THINK" => "think", "RESPONSE" => "response", _ => null }; var content = fragment.String("content") ?? ""; if (kind is not null) { currentKind = kind; if (content.Length > 0) yield return (kind, content); } }
                }
                else if (root.String("p") == "response/fragments/-1/content" && v.ValueKind == JsonValueKind.String && currentKind is not null) yield return (currentKind, v.GetString()!);
                else if (v.ValueKind == JsonValueKind.String && root.Prop("p").ValueKind == JsonValueKind.Undefined && currentKind is not null) yield return (currentKind, v.GetString()!);
                else if (root.String("p") == "response/fragments" && v.ValueKind == JsonValueKind.Array) foreach (var fragment in v.EnumerateArray()) { var kind = fragment.String("type") switch { "THINK" => "think", "RESPONSE" => "response", _ => null }; var content = fragment.String("content") ?? ""; if (kind is not null) { currentKind = kind; if (content.Length > 0) yield return (kind, content); } }
            }
        }
    }
    private static JsonElement? FindMessageId(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        var response = element.Prop("response");
        if (response.ValueKind == JsonValueKind.Object)
        {
            var responseId = response.Prop("message_id");
            if (responseId.ValueKind is JsonValueKind.Number or JsonValueKind.String) return responseId.Clone();
            var responseGenericId = response.Prop("id");
            if (responseGenericId.ValueKind is JsonValueKind.Number or JsonValueKind.String) return responseGenericId.Clone();
        }
        var directId = element.Prop("message_id");
        if (directId.ValueKind is JsonValueKind.Number or JsonValueKind.String) return directId.Clone();
        var path = element.String("p");
        if (path is not null && (path.EndsWith("message_id", StringComparison.OrdinalIgnoreCase) || path.EndsWith("/id", StringComparison.OrdinalIgnoreCase)))
        {
            var pathValue = element.Prop("v");
            if (pathValue.ValueKind is JsonValueKind.Number or JsonValueKind.String) return pathValue.Clone();
        }
        var value = element.Prop("v");
        if (value.ValueKind == JsonValueKind.Object)
        {
            var nested = FindMessageId(value);
            if (nested is not null) return nested;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var nested = FindMessageId(item);
                if (nested is not null) return nested;
            }
        }
        return null;
    }
    public async Task<string> RespondAsync(string prompt, bool thinking = true, bool search = true, CancellationToken cancellationToken = default, IEnumerable<string>? fileIds = null)
    {
        var sb = new StringBuilder(); await foreach (var (_, text) in RespondStructuredAsync(prompt, thinking, search, fileIds, cancellationToken)) sb.Append(text); return sb.ToString();
    }
    public async Task<ToolResponse> RespondWithToolsAsync(string prompt, IEnumerable<Tool> tools, bool thinking = true, CancellationToken cancellationToken = default, IEnumerable<string>? fileIds = null, Action<string>? onThinkingComplete = null, Action<string>? onResponseChunk = null)
    {
        var available = tools.ToArray();
        var protocol = "When a tool is needed, call it using its XML-style tool tag, with one child tag for each argument. " +
            "Return the tool call itself without code fences. For example, to write a file, return exactly this shape (replace the example values with the requested path and full content):\n" +
            "<write_file>\n<path>DEEPSEEK.md</path>\n<content># DEEPSEEK.md\\n\\nA short description.</content>\n</write_file>\n" +
            "Use the exact tool and argument names from the available tools below. After receiving a tool result, continue the user's request and give a concise confirmation; do not stop with an empty response. " +
            "Available tools:\n" + JsonSerializer.Serialize(available.Select(t => t.AsDictionary())) +
            "\nIf no tool is needed, answer normally. Do not put tool calls in code fences.";
        var response = new StringBuilder(); var thought = new StringBuilder();
        var thinkingReported = false;
        void ReportThinking()
        {
            if (thinkingReported) return;
            thinkingReported = true;
            onThinkingComplete?.Invoke(thought.ToString());
        }
        var markupGuard = onResponseChunk is null ? null : new ToolMarkupStreamGuard(onResponseChunk, available.Select(t=>t.Name));
        await foreach (var (kind, chunk) in RespondStructuredAsync(protocol + "\n\nUSER MESSAGE: " + prompt, thinking, search:false, fileIds, cancellationToken))
        {
            if (kind == "think") thought.Append(chunk);
            else
            {
                response.Append(chunk);
                ReportThinking();
                markupGuard?.Feed(chunk);
            }
        }
        ReportThinking();
        markupGuard?.Finish();
        var calls = ParseToolCalls(response.ToString(),available);
        var text = StripToolMarkup(response.ToString(),available).Trim();
        return new(text, calls, thought.ToString());
    }

    private static List<ToolCall> ParseToolCalls(string text,IReadOnlyList<Tool> available)
    {
        var calls = new List<ToolCall>();
        foreach (Match match in Regex.Matches(text, @"<deepwrap_tool_call>\s*(\{.*?\})\s*</deepwrap_tool_call>", RegexOptions.Singleline))
            TryAddJsonToolCall(match.Groups[1].Value, calls);

        const RegexOptions nativeOptions = RegexOptions.Singleline | RegexOptions.IgnoreCase;
        var invokePattern = @"<[^<>]{0,40}?invoke\s+name\s*=\s*['\x22]([\w.\-]+)['\x22][^<>]{0,60}?>(.*?)</[^<>]{0,40}?invoke\s*>";
        foreach (Match match in Regex.Matches(text, invokePattern, nativeOptions))
            TryAddNativeToolCall(match.Groups[1].Value, match.Groups[2].Value, calls);

        var hybridPattern = @"<deepwrap_tool_call>\s*(\{.*?\})\s*</[^<>]{0,40}?parameter\s*>";
        foreach (Match match in Regex.Matches(text, hybridPattern, nativeOptions))
            TryAddJsonToolCall(match.Groups[1].Value, calls);

        foreach(var tool in available)
        {
            var tag=Regex.Escape(tool.Name);
            var wrapperPattern=$@"<\s*{tag}\s*>(.*?)</\s*{tag}\s*>";
            foreach(Match call in Regex.Matches(text,wrapperPattern,nativeOptions))
                TryAddTaggedToolCall(tool,call.Groups[1].Value,calls);
        }
        return calls;
    }

    private static void TryAddTaggedToolCall(Tool tool,string body,List<ToolCall> calls)
    {
        var arguments=new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase);
        var properties=tool.Parameters.Prop("properties");
        if(properties.ValueKind==JsonValueKind.Object)
        {
            foreach(var property in properties.EnumerateObject())
            {
                var name=Regex.Escape(property.Name);
                var match=Regex.Match(body,$@"<\s*{name}(?:\s+[^>]*)?\s*>([\s\S]*?)</\s*{name}\s*>",RegexOptions.IgnoreCase);
                if(!match.Success)continue;
                var raw=System.Net.WebUtility.HtmlDecode(match.Groups[1].Value.Trim());
                try { using var doc=JsonDocument.Parse(raw); arguments[property.Name]=doc.RootElement.Clone(); }
                catch(JsonException) { arguments[property.Name]=JsonSerializer.SerializeToElement(raw); }
            }
        }
        calls.Add(new(tool.Name,arguments));
    }

    private static void TryAddJsonToolCall(string payload, List<ToolCall> calls)
    {
        try
        {
            using var doc = JsonDocument.Parse(payload); var root = doc.RootElement; var name = root.String("name");
            if (string.IsNullOrWhiteSpace(name)) return;
            var args = root.Prop("arguments");
            if(args.ValueKind==JsonValueKind.String)
            {
                try { using var nested=JsonDocument.Parse(args.GetString()!); args=nested.RootElement.Clone(); }
                catch(JsonException) { args=default; }
            }
            if(args.ValueKind==JsonValueKind.Object && args.Prop("arguments").ValueKind==JsonValueKind.Object) args=args.GetProperty("arguments");
            var values=args.ValueKind==JsonValueKind.Object
                ? args.EnumerateObject().ToDictionary(p=>p.Name.ToLowerInvariant(),p=>p.Value.Clone(),StringComparer.OrdinalIgnoreCase)
                : root.EnumerateObject().Where(p=>!p.Name.Equals("name",StringComparison.OrdinalIgnoreCase)&&!p.Name.Equals("arguments",StringComparison.OrdinalIgnoreCase)).ToDictionary(p=>p.Name.ToLowerInvariant(),p=>p.Value.Clone(),StringComparer.OrdinalIgnoreCase);
            calls.Add(new(name,values));
        }
        catch (JsonException) { }
    }

    private static void TryAddNativeToolCall(string name, string body, List<ToolCall> calls)
    {
        var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (Match item in Regex.Matches(body, @"<[^<>]{0,40}?parameter\s+name\s*=\s*['\x22]([\w.\-]+)['\x22][^<>]{0,80}?>(.*?)</[^<>]{0,40}?parameter\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var key = item.Groups[1].Value; var value = item.Groups[2].Value.Trim();
            if (key == "arguments")
            {
                try
                {
                    using var doc = JsonDocument.Parse(value); if (doc.RootElement.ValueKind != JsonValueKind.Object) return;
                    var payload=doc.RootElement; var nested=payload.Prop("arguments");
                    if(nested.ValueKind==JsonValueKind.Object) payload=nested;
                    foreach (var property in payload.EnumerateObject()) arguments[property.Name.ToLowerInvariant()] = property.Value.Clone();
                }
                catch (JsonException) { return; }
            }
            else
            {
                try { using var doc = JsonDocument.Parse(value); arguments[key.ToLowerInvariant()] = doc.RootElement.Clone(); }
                catch (JsonException) { arguments[key.ToLowerInvariant()] = JsonSerializer.SerializeToElement(value); }
            }
        }
        calls.Add(new(name, arguments));
    }

    internal static string StripToolMarkup(string text,IReadOnlyList<Tool> available)
    {
        text = Regex.Replace(text, @"<deepwrap_tool_call>.*?</deepwrap_tool_call>", "", RegexOptions.Singleline);
        text = Regex.Replace(text, @"<deepwrap_tool_call>.*?</[^<>]{0,40}?parameter\s*>\s*</[^<>]{0,40}?invoke\s*>\s*</[^<>]{0,40}?calls?\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^<>]{0,40}?invoke\s+name\s*=\s*['\x22][\w.\-]+['\x22][^<>]{0,60}?>(.*?)</[^<>]{0,40}?invoke\s*>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"</?[^<>]{0,40}?(?:tool[_\s]*calls?|DSML)[^<>]{0,30}?>", "", RegexOptions.IgnoreCase);
        foreach(var tool in available)
        {
            var tag=Regex.Escape(tool.Name);
            text=Regex.Replace(text,$@"<\s*{tag}\s*>[\s\S]*?</\s*{tag}\s*>","",RegexOptions.IgnoreCase);
        }
        return Regex.Replace(text, @"\n{3,}", "\n\n");
    }
    public async Task<AgentResponse> RespondAgentAsync(string prompt, bool thinking = true, bool search = true, int? maxRounds = null, Action<AgentEvent>? onEvent = null, CancellationToken cancellationToken = default, IEnumerable<string>? fileIds = null, Action<string>? onAnswerChunk = null)
    {
        var tools = client.AgentTools; var functions = client.AgentFunctions; var executions = new List<ToolExecution>(); var events = new List<AgentEvent>();
        void Emit(AgentEvent e) { events.Add(e); onEvent?.Invoke(e); }
        Emit(new("started", "Agent turn started.")); var rounds=0; var originalPrompt=prompt; var current=prompt; string answer="";
        while(maxRounds is null || rounds < maxRounds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var thinkingReported=false;
            void ReportThinking(string content)
            {
                if(thinkingReported)return;
                thinkingReported=true;
                Emit(new("thinking",content));
            }
            var parsed=await RespondWithToolsAsync(current,tools,thinking,cancellationToken,rounds==0?fileIds:null,ReportThinking,onAnswerChunk); answer=parsed.Content;
            ReportThinking(parsed.Thinking);
            if(parsed.ToolCalls.Count==0) break;
            rounds++;
            var toolResults=new StringBuilder();
            foreach(var call in parsed.ToolCalls)
            {
                if(!functions.TryGetValue(call.Name,out var handler)) { toolResults.AppendLine($"Tool '{call.Name}' is unavailable."); continue; }
                Emit(new("tool_started",$"Running {call.Name}.",call.Name,call.Arguments)); var watch=System.Diagnostics.Stopwatch.StartNew(); object? output;
                try { var args=JsonSerializer.SerializeToElement(call.Arguments); output=handler(args); }
                catch(Exception e) { output=new { error=e.Message }; }
                watch.Stop(); executions.Add(new(call.Name,call.Arguments,output)); Emit(new("tool_completed",$"Completed {call.Name}.",call.Name,call.Arguments,output,watch.Elapsed.TotalSeconds));
                toolResults.AppendLine($"Tool result for {call.Name}:"); toolResults.AppendLine(JsonSerializer.Serialize(output));
            }
            current=$"Original user request:\n{originalPrompt}\n\nResults from this round:\n{toolResults}\nContinue the original request. Use another tool if needed; otherwise give a concise final answer.";
            if (maxRounds is not null && rounds>=maxRounds) current += "\nTool round limit reached. Give the best final answer now and state any unfinished work.";
        }
        Emit(new("completed","Agent turn completed.")); return new(answer,executions,events);
    }
    public async IAsyncEnumerable<string> StreamAsync(string prompt, bool thinking = true, bool search = true, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default, IEnumerable<string>? fileIds = null)
    {
        string? kind = null;
        await foreach (var (next, text) in RespondStructuredAsync(prompt, thinking, search, fileIds, cancellationToken)) { if (kind != next) { if (kind == "think") yield return "</think>"; if (next == "think") yield return "<think>"; kind = next; } yield return text; }
        if (kind == "think") yield return "</think>";
    }

    private sealed class ToolMarkupStreamGuard
    {
        private const string Marker = "<deepwrap_tool_call>";
        private static readonly Regex NativeMarker = new(@"<[^<>]{0,40}?(?:DSML|invoke\s+name\s*=|parameter\s+name\s*=|tool[_\s]*calls?)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private readonly Action<string> _emit;
        private readonly Regex _taggedToolMarker;
        private string _pending = "";
        private bool _tripped;
        public ToolMarkupStreamGuard(Action<string> emit,IEnumerable<string> toolNames)
        {
            _emit=emit;
            var names=string.Join("|",toolNames.Select(Regex.Escape));
            _taggedToolMarker=new Regex(names.Length==0?"(?!)":$@"<\s*(?:{names})\s*>",RegexOptions.IgnoreCase|RegexOptions.Compiled);
        }
        public void Feed(string text)
        {
            if (_tripped) return;
            _pending += text;
            var markerAt = _pending.IndexOf(Marker, StringComparison.Ordinal);
            var native = NativeMarker.Match(_pending);
            var tagged=_taggedToolMarker.Match(_pending);
            if (markerAt >= 0 || native.Success || tagged.Success)
            {
                var start = new[]{markerAt,native.Success?native.Index:-1,tagged.Success?tagged.Index:-1}.Where(i=>i>=0).Min();
                if (start > 0) _emit(_pending[..start]);
                _pending = ""; _tripped = true; return;
            }
            var openTag = _pending.LastIndexOf('<');
            if (openTag >= 0 && _pending.IndexOf('>', openTag) < 0 && _pending.Length - openTag <= 100)
            {
                if (openTag > 0) _emit(_pending[..openTag]);
                _pending = _pending[openTag..]; return;
            }
            var safeLength = _pending.Length;
            if (safeLength > 0) { _emit(_pending[..safeLength]); _pending = _pending[safeLength..]; }
        }
        public void Finish()
        {
            if (_tripped) return;
            var rest = _pending; _pending = "";
            if (rest.Length > 0) _emit(rest);
        }
    }
}

public sealed class FilesApi(Client client)
{
    public async Task<UploadedFile> UploadAsync(string path, string model = "vision", CancellationToken cancellationToken = default)
    {
        var file = new FileInfo(path); if (!file.Exists) throw new FileNotFoundException("File does not exist", path);
        var target = "/api/v0/file/upload_file"; var pow = await client.Pow.BuildHeaderAsync(target, cancellationToken);
        using var form = new MultipartFormDataContent(); using var content = new StreamContent(file.OpenRead()); content.Headers.ContentType = new("application/octet-stream"); form.Add(content, "file", file.Name);
        using var request = new HttpRequestMessage(HttpMethod.Post, Client.BaseUrl.TrimEnd('/') + target) { Content = form };
        request.Headers.TryAddWithoutValidation("x-thinking-enabled", "true"); request.Headers.TryAddWithoutValidation("x-model-type", model); request.Headers.TryAddWithoutValidation("x-file-size", file.Length.ToString()); request.Headers.TryAddWithoutValidation("x-ds-pow-response", pow);
        using var response = await client.Http.SendAsync(request, cancellationToken); response.EnsureSuccessStatusCode(); using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)); return Read(doc.RootElement.GetProperty("data").GetProperty("biz_data"));
    }
    public async Task<UploadedFile> WaitUntilReadyAsync(string id, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var until = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(60));
        while (DateTime.UtcNow < until) { using var response = await client.Http.GetAsync(Client.BaseUrl.TrimEnd('/') + "/api/v0/file/fetch_files?file_ids=" + Uri.EscapeDataString(id), cancellationToken); response.EnsureSuccessStatusCode(); using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken)); var files = doc.RootElement.GetProperty("data").GetProperty("biz_data").GetProperty("files"); if (files.GetArrayLength() > 0) { var f = Read(files[0]); if (f.Status == "SUCCESS") return f; if (new[]{"FAILED","ERROR","REJECTED","CANCELLED","EXPIRED"}.Contains(f.Status.ToUpperInvariant())) throw new InvalidOperationException($"File processing failed for {id}: {f.Status}"); } await Task.Delay(500, cancellationToken); }
        throw new TimeoutException("Timed out waiting for file processing: " + id);
    }
    public async Task<UploadedFile> UploadAndWaitAsync(string path, string model = "vision", TimeSpan? timeout = null, CancellationToken cancellationToken = default) => await WaitUntilReadyAsync((await UploadAsync(path, model, cancellationToken)).Id, timeout, cancellationToken);
    private static UploadedFile Read(JsonElement x) => new(x.String("id") ?? "", x.String("file_name") ?? "", x.Int("file_size"), x.String("status") ?? "UNKNOWN", x.Bool("is_image"), x.String("model_kind") ?? "");
}
