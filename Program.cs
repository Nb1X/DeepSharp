using System.Text;
using System.Text.Json;

namespace DeepSharp;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        if (args.Length == 0) { await Interactive(); return; }
        if(args[0]=="--enable-experimental-features")
        {
            var store=new ConfigStore(); store.Save(store.Load() with { ExperimentalFeatures=true });
            Console.WriteLine("Experimental features enabled.");
            return;
        }
        switch (args[0].ToLowerInvariant())
        {
            case "--version": case "-v": Console.WriteLine($"deepsharp {Client.Version}"); break;
            case "--help": case "-h": Help(); break;
            case "auth": await Auth(args[1..]); break;
            case "config": Config(); break;
            case "logout": Logout(); break;
            case "chat": await ChatCommand(args[1..]); break;
            case "ui": DesktopUi.Run(args[1..]); break;
            default: Console.Error.WriteLine($"Unknown command '{args[0]}'. Use --help."); Environment.ExitCode = 2; break;
        }
    }
    private static void Help() => Console.WriteLine("DeepSharp .NET 9\nUsage: deepsharp [auth|config|logout|chat|ui]\n  auth [--token TOKEN | --manual] [--timeout SECONDS] [--no-save]\n  chat MESSAGE [--model expert|default|vision] [--no-thinking] [--no-search] [--stream]\n  ui                    Open the experimental desktop interface\n  --enable-experimental-features  Enable experimental features");
    private static string? Option(string[] args,string name) { var i=Array.IndexOf(args,name); return i>=0&&i+1<args.Length?args[i+1]:null; }
    private static async Task Auth(string[] args)
    {
        var token=Option(args,"--token");
        if(token is null && args.Contains("--manual")) { Console.Write("Bearer token: "); token=ReadHidden(); }
        else if(token is null)
        {
            Console.WriteLine("Starting browser authentication...");
            var seconds=int.TryParse(Option(args,"--timeout"),out var parsed)?parsed:(int?)null;
            try { token=await BrowserAuthentication.GetBearerTokenAsync(timeout:seconds is null?null:TimeSpan.FromSeconds(seconds.Value),progress:Console.WriteLine); }
            catch(Exception e) { Console.Error.WriteLine("Browser authentication failed: "+e.Message); Environment.ExitCode=1; return; }
        }
        if(string.IsNullOrWhiteSpace(token)) { Console.Error.WriteLine("Authentication failed: no bearer token was captured."); Environment.ExitCode=1; return; }
        token=BearerToken.Normalize(token); var store=new ConfigStore(); if(!args.Contains("--no-save")) { store.UpdateToken(token); Console.WriteLine("Authentication successful. Token saved to: "+store.Path); } else Console.WriteLine("Authentication successful. Token was not saved.");
        if(args.Contains("--show-token"))Console.WriteLine(token);
    }
    private static string ReadHidden() { var b=new StringBuilder(); ConsoleKeyInfo k; do { k=Console.ReadKey(true); if(k.Key==ConsoleKey.Backspace&&b.Length>0)b.Length--; else if(!char.IsControl(k.KeyChar))b.Append(k.KeyChar); }while(k.Key!=ConsoleKey.Enter); Console.WriteLine(); return b.ToString(); }
    private static void Config() { var s=new ConfigStore(); var c=s.Load(); Console.WriteLine($"Config path: {s.Path}\nToken configured: {(string.IsNullOrEmpty(c.Token)?"no":"yes")}\nDefault model: {c.Model}\nShow thinking: {(c.ShowThinking?"yes":"no")}\nSearch enabled: {(c.SearchEnabled?"yes":"no")}\nDirect mode: {(c.GodMode?"yes":"no")}"); }
    private static void Logout() { var s=new ConfigStore(); s.Save(s.Load() with { Token=null }); Console.WriteLine("Saved token removed."); }
    private static async Task ChatCommand(string[] args)
    {
        var message=string.Join(' ',args.TakeWhile(a=>!a.StartsWith("--"))).Trim(); if(message.Length==0) { Console.Error.WriteLine("Message cannot be empty."); Environment.ExitCode=2; return; }
        try { using var client=new Client(Option(args,"--token")); var chat=await client.Chats.CreateSessionAsync(Option(args,"--model")??"expert",args.Contains("--god")); if(args.Contains("--stream")) await foreach(var chunk in chat.StreamAsync(message,!args.Contains("--no-thinking"),!args.Contains("--no-search"))) Console.Write(chunk); else Console.WriteLine(await chat.RespondAgentAsync(message,!args.Contains("--no-thinking"),!args.Contains("--no-search"),client.MaxAgentRounds)); }
        catch(Exception e) { Console.Error.WriteLine("Chat failed: "+e.Message); Environment.ExitCode=1; }
    }
    private static async Task Interactive()
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        try { Console.Clear(); } catch (IOException) { }
        catch (InvalidOperationException) { }
        PrintBanner();
        var store=new ConfigStore(); var token=Environment.GetEnvironmentVariable("DEEPWRAP_API_KEY")??Environment.GetEnvironmentVariable("DEEPSEEK_API_KEY")??store.Load().Token;
        if(string.IsNullOrWhiteSpace(token))
        {
            Console.WriteLine("No token configured. Choose authentication: [B]rowser, [M]anual token, [S]kip (B): "); var method=(Console.ReadLine()??"").Trim().ToLowerInvariant();
            if(method is "s" or "skip") { Console.WriteLine("Authentication skipped. Run 'dotnet run -- auth' when ready."); return; }
            if(method is "m" or "manual") { Console.Write("Bearer token: "); token=ReadHidden(); }
            else { Console.WriteLine("Starting browser authentication..."); try { token=await BrowserAuthentication.GetBearerTokenAsync(progress:Console.WriteLine); } catch(Exception e) { Console.Error.WriteLine("Browser authentication failed: "+e.Message); return; } }
            if(string.IsNullOrWhiteSpace(token)) { Console.WriteLine("Authentication did not return a token."); return; }
            token=BearerToken.Normalize(token); store.UpdateToken(token); Console.WriteLine("Token saved to "+store.Path);
        }
        var conversationStore=new ConversationStore(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(store.Path)!,"sessions.json"));
        using var client=new Client(token); ChatSession? chat=null; SavedConversation? currentConversation=null; var currentModel="expert"; var showThinking=store.Load().ShowThinking;
        while(true) { Console.Write("you> "); var line=ReadInteractiveLine(); if(line is null||line=="/exit") break;
            if(line=="/help") { Console.WriteLine("Available commands:\n  /help                 Show this list\n  /new                  Start a new conversation\n  /list                 List saved conversations\n  /resume <id>          Resume by 6-character or full DeepSharp ID\n  /rm <id>              Delete by 6-character ID (DeepSeek and local list)\n  /model <type>         Change model (expert, default, vision)\n  /thinking on|off      Toggle thinking display\n  /exit                 Quit DeepSharp"); continue; }
            if(line=="/new") { if(chat is not null) currentModel=chat.ModelType; chat=null; currentConversation=null; Console.WriteLine("New conversation ready; it will be created when you send a message."); continue; }
            if(line=="/list")
            {
                var saved=conversationStore.List();
                if(saved.Count==0) Console.WriteLine("No saved conversations.");
                foreach(var item in saved)
                {
                    Console.WriteLine($"{(item.Id==currentConversation?.Id?"*":" ")} {item.ShortId}  {item.Name}");
                    Console.WriteLine($"  DeepSharp ID: {item.Id}");
                    Console.WriteLine($"  {item.Url}");
                }
                continue;
            }
            if(line.Equals("/rm",StringComparison.OrdinalIgnoreCase) || line.StartsWith("/rm ",StringComparison.OrdinalIgnoreCase))
            {
                var parts=line.Split(' ',StringSplitOptions.RemoveEmptyEntries);
                if(parts.Length!=2 || parts[1].Length!=6 || !parts[1].All(char.IsAsciiLetterOrDigit)) { Console.WriteLine("Usage: /rm <conv-id-6characters>"); continue; }
                var target=conversationStore.Find(parts[1]);
                if(target is null || !target.ShortId.Equals(parts[1],StringComparison.OrdinalIgnoreCase)) { Console.WriteLine($"Conversation not found: {parts[1]}. Use /list to see saved conversations."); continue; }
                try { await client.Chats.DeleteSessionAsync(target.DeepSeekSessionId); }
                catch(Exception e) { Console.WriteLine($"Could not delete the conversation from DeepSeek: {e.Message}"); continue; }
                if(target.Id==currentConversation?.Id) { if(chat is not null) currentModel=chat.ModelType; chat=null; currentConversation=null; }
                try
                {
                    if(conversationStore.Remove(target.Id)) Console.WriteLine($"Deleted conversation {target.ShortId} from DeepSeek and DeepSharp.");
                    else Console.WriteLine($"Deleted conversation {target.ShortId} from DeepSeek; it was already absent from the local list.");
                }
                catch(Exception e) { Console.WriteLine($"Deleted from DeepSeek, but could not update sessions.json: {e.Message}"); }
                continue;
            }
            if(line.StartsWith("/resume ",StringComparison.OrdinalIgnoreCase))
            {
                var requestedId=line[8..].Trim(); var saved=conversationStore.Find(requestedId);
                if(saved is null) Console.WriteLine($"Conversation not found: {requestedId}. Use /list to see saved conversations.");
                else
                {
                    var parentMessageId=saved.LastMessageId;
                    if(parentMessageId is null)
                    {
                        try { parentMessageId=await client.Chats.FetchLastAssistantMessageIdAsync(saved.DeepSeekSessionId); }
                        catch(Exception e) { Console.WriteLine($"Could not load conversation history: {e.Message}"); continue; }
                        if(parentMessageId is not null) saved=conversationStore.UpdateLastMessageId(saved.Id,parentMessageId.Value);
                        else { Console.WriteLine("This conversation has no saved assistant reply to continue from. Use /new to start a separate conversation."); continue; }
                    }
                    chat=new ChatSession(client,saved.DeepSeekSessionId,saved.Model,saved.GodMode,parentMessageId); currentConversation=saved; currentModel=saved.Model; Console.WriteLine($"Resumed {saved.ShortId}: {saved.Name}");
                }
                continue;
            }
            if(line.StartsWith("/model ")) { var model=line[7..].Trim().ToLowerInvariant(); if(!ChatsApi.SupportedModels.Contains(model,StringComparer.Ordinal)) { Console.WriteLine("Usage: /model expert|default|vision"); continue; } currentModel=model; chat=null; currentConversation=null; Console.WriteLine($"Model set to {model}; a conversation will be created when you send a message."); continue; }
            if(line=="/thinking"||line.StartsWith("/thinking "))
            {
                var option=line.Length>9?line[9..].Trim().ToLowerInvariant():"";
                if(option is "on" or "off") { showThinking=option=="on"; store.Save(store.Load() with { ShowThinking=showThinking }); Console.WriteLine($"Thinking {(showThinking?"enabled":"disabled")}."); }
                else Console.WriteLine($"Usage: /thinking on|off (currently {(showThinking?"on":"off")})");
                continue;
            }
            if(string.IsNullOrWhiteSpace(line)) continue;
            using var working = new WorkingIndicator();
            var answerLineStarted=false;
            try
            {
                if(chat is null)
                {
                    var newChat=await client.Chats.CreateSessionAsync(currentModel);
                    var newRecord=conversationStore.Create(newChat.SessionId,newChat.ModelType,newChat.GodMode);
                    chat=newChat; currentConversation=newRecord;
                }
                var activeChat=chat;
                var activeConversation=currentConversation!;
                var response=await activeChat.RespondAgentAsync(line,thinking:showThinking,onEvent:e=>
                {
                    if(e.Type=="thinking")
                    {
                        if(!showThinking)return;
                        working.Pause();
                        try
                        {
                            Console.ForegroundColor=ConsoleColor.DarkCyan; Console.WriteLine("Thinking..."); Console.ResetColor();
                            if(!string.IsNullOrWhiteSpace(e.Message)) Console.WriteLine(e.Message.TrimEnd());
                            Console.WriteLine();
                        }
                        finally { working.Resume(); }
                    }
                    else if(e.Type=="tool_started")
                    {
                        working.Pause();
                        try
                        {
                            if(answerLineStarted) { Console.WriteLine(); answerLineStarted=false; }
                            if(e.ToolName=="write_file" && e.Arguments is Dictionary<string,JsonElement> arguments)
                            {
                                if(!PrintWriteProgress(arguments,client.NativeTools.WorkingDirectory))
                                { Console.ForegroundColor=ConsoleColor.Yellow; Console.WriteLine("write_file called without a valid path and content."); Console.ResetColor(); }
                            }
                            else Console.WriteLine($"  → {e.Message}");
                        }
                        finally { working.Resume(); }
                    }
                },onAnswerChunk:chunk=>
                {
                    working.Pause();
                    if(!answerLineStarted)
                    {
                        Console.ForegroundColor=ConsoleColor.Cyan; Console.Write("assistant> "); Console.ResetColor(); answerLineStarted=true;
                    }
                    Console.Write(chunk); Console.Out.Flush();
                });
                currentConversation=conversationStore.UpdateAfterTurn(activeConversation.Id,activeChat.LastMessageId,line);
                working.Pause();
                if(answerLineStarted) Console.WriteLine();
                else
                {
                    Console.ForegroundColor=ConsoleColor.Cyan; Console.WriteLine("assistant>"); Console.ResetColor();
                    if(response.Content.Length>0) Console.WriteLine(response.Content); else Console.WriteLine("(empty response)");
                }
            }
            catch(Exception e) { working.Pause(); Console.WriteLine("\n[DeepSharp] "+e.Message); }
        }
    }
    private static string? ReadInteractiveLine()
    {
        if(Console.IsInputRedirected || Console.IsOutputRedirected) return Console.ReadLine();
        var input=new StringBuilder();
        var inputRow=Console.CursorTop;
        var inputColumn=Console.CursorLeft;
        var menuStartRow=-1;
        var menuLines=new[]
        {
            "Commands:",
            "  /help                 Show available commands",
            "  /new                  Start a new conversation",
            "  /list                 List saved conversations",
            "  /resume <id>          Resume a saved conversation",
            "  /rm <id>              Delete a conversation",
            "  /model <type>         Change model: expert, default, vision",
            "  /thinking on|off      Toggle thinking display",
            "  /exit                 Quit DeepSharp"
        };
        void ClearMenu()
        {
            if(menuStartRow<0)return;
            var row=Console.CursorTop; var column=Console.CursorLeft;
            for(var i=0;i<menuLines.Length;i++)
            {
                var y=menuStartRow+i;
                if(y>=Console.BufferHeight)break;
                Console.SetCursorPosition(0,y); Console.Write(new string(' ',Console.BufferWidth));
            }
            Console.SetCursorPosition(Math.Min(inputColumn+input.Length,Console.BufferWidth-1),inputRow);
            menuStartRow=-1;
        }
        while(true)
        {
            var key=Console.ReadKey(true);
            if(key.Key==ConsoleKey.Enter)
            {
                ClearMenu(); Console.WriteLine(); return input.ToString();
            }
            if(key.Key==ConsoleKey.Backspace)
            {
                if(input.Length==0)continue;
                input.Length--;
                if(menuStartRow>=0 && input.Length==0) ClearMenu();
                Console.SetCursorPosition(Math.Min(inputColumn+input.Length,Console.BufferWidth-1),inputRow);
                Console.Write(' '); Console.SetCursorPosition(Math.Min(inputColumn+input.Length,Console.BufferWidth-1),inputRow);
                continue;
            }
            if(char.IsControl(key.KeyChar))continue;
            input.Append(key.KeyChar); Console.Write(key.KeyChar);
            if(input.Length==1 && key.KeyChar=='/')
            {
                menuStartRow=inputRow+1;
                var savedRow=Console.CursorTop; var savedColumn=Console.CursorLeft;
                Console.SetCursorPosition(0,menuStartRow);
                foreach(var item in menuLines)
                {
                    Console.ForegroundColor=item=="Commands:"?ConsoleColor.Magenta:ConsoleColor.DarkGray;
                    Console.WriteLine(item); Console.ResetColor();
                }
                Console.SetCursorPosition(Math.Min(savedColumn,Console.BufferWidth-1),savedRow);
            }
        }
    }
    private static bool PrintWriteProgress(Dictionary<string,JsonElement> arguments,string workingDirectory)
    {
        if(!arguments.TryGetValue("path",out var pathValue)||pathValue.ValueKind!=JsonValueKind.String||string.IsNullOrWhiteSpace(pathValue.GetString())||!arguments.TryGetValue("content",out var contentValue)||contentValue.ValueKind!=JsonValueKind.String) return false;
        var requestedPath=pathValue.GetString()!; var content=contentValue.GetString()??"";
        var fullPath=Path.GetFullPath(requestedPath,workingDirectory);
        var name=Path.GetFileName(fullPath); var directory=Path.GetDirectoryName(fullPath)??workingDirectory;
        Console.ForegroundColor=ConsoleColor.Green; Console.WriteLine($"Writing file {name} in '{directory}'..."); Console.ResetColor();
        foreach(var line in content.Replace("\r\n","\n",StringComparison.Ordinal).Split('\n').Take(5))
        {
            if(!Console.IsOutputRedirected) Console.ForegroundColor=ConsoleColor.DarkGray;
            Console.Write("  │ ");
            Console.ResetColor();
            Console.WriteLine(line);
        }
        Console.WriteLine();
        return true;
    }
    private static void PrintBanner()
    {
        var logo = """
        ██████╗ ███████╗███████╗██████╗ ███████╗██╗  ██╗ █████╗ ██████╗ ██████╗
        ██╔══██╗██╔════╝██╔════╝██╔══██╗██╔════╝██║  ██║██╔══██╗██╔══██╗██╔══██╗
        ██║  ██║█████╗  █████╗  ██████╔╝███████╗███████║███████║██████╔╝██████╔╝
        ██║  ██║██╔══╝  ██╔══╝  ██╔═══╝ ╚════██║██╔══██║██╔══██║██╔══██╗██╔═══╝
        ██████╔╝███████╗███████╗██║     ███████║██║  ██║██║  ██║██║  ██║██║
        ╚═════╝ ╚══════╝╚══════╝╚═╝     ╚══════╝╚═╝  ╚═╝╚═╝  ╚═╝╚═╝  ╚═╝╚═╝
        """;
        if (!Console.IsOutputRedirected) Console.ForegroundColor=ConsoleColor.Magenta;
        Console.WriteLine(logo);
        Console.ResetColor();
        Console.WriteLine("A CLI interface for DeepSeek, written in C#");
        Console.WriteLine("----------------------");
        Console.WriteLine("Type /help for commands, /exit to quit.\n");
    }
    private sealed class WorkingIndicator : IDisposable
    {
        private readonly object _gate = new();
        private readonly CancellationTokenSource _stop = new();
        private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
        private readonly bool _enabled = !Console.IsOutputRedirected;
        private readonly Task _animation;
        private bool _paused;
        private int _phase;
        private int _lastWidth;

        public WorkingIndicator() => _animation = _enabled ? AnimateAsync(_stop.Token) : Task.CompletedTask;

        public void Pause()
        {
            if (!_enabled) return;
            lock (_gate) { _paused = true; ClearLine(); }
        }

        public void Resume()
        {
            if (!_enabled) return;
            lock (_gate) _paused = false;
        }

        private async Task AnimateAsync(CancellationToken cancellationToken)
        {
            try
            {
                while (true)
                {
                    await Task.Delay(120, cancellationToken);
                    lock (_gate)
                    {
                        if (_paused) continue;
                        var seconds = (long)_clock.Elapsed.TotalSeconds;
                        var elapsed = seconds >= 3600 ? $"{seconds / 3600}h{seconds % 3600 / 60:D2}m" : $"{seconds / 60}m{seconds % 60:D2}";
                        var dots = string.Concat(Enumerable.Range(0, 5).Select(i => i == _phase ? '●' : '·'));
                        var line = $"   {dots}  Working... {elapsed}";
                        Console.Write("\r" + line.PadRight(_lastWidth));
                        _lastWidth = line.Length;
                        _phase = (_phase + 1) % 5;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        }

        private void ClearLine()
        {
            if (_lastWidth == 0) return;
            Console.Write("\r" + new string(' ', _lastWidth) + "\r");
            _lastWidth = 0;
        }

        public void Dispose()
        {
            Pause();
            _stop.Cancel();
            try { _animation.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
            _clock.Stop(); _stop.Dispose();
        }
    }
}
