using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeepSharp;

internal static class DesktopUi
{
    public static void Run(string[] args)
    {
        var config = new ConfigStore().Load();
        if (!config.ExperimentalFeatures)
        {
            if (args.Contains("--shortcut", StringComparer.OrdinalIgnoreCase))
            {
                AppBuilder.Configure<ExperimentalFeaturesDisabledApplication>()
                    .UsePlatformDetect()
                    .StartWithClassicDesktopLifetime([]);
            }
            else
            {
                Console.WriteLine("The UI is experimental. Enable experimental features with the following command: deepsharp --enable-experimental-features");
            }
            return;
        }

        var appArgs = args.Where(argument => !argument.Equals("--shortcut", StringComparison.OrdinalIgnoreCase)).ToArray();
        AppBuilder.Configure<DeepSharpApplication>()
            .UsePlatformDetect()
            .StartWithClassicDesktopLifetime(appArgs);
    }
}

internal sealed class ExperimentalFeaturesDisabledApplication : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new ExperimentalFeaturesDisabledWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class ExperimentalFeaturesDisabledWindow : Window
{
    public ExperimentalFeaturesDisabledWindow()
    {
        Title = "DeepSharp";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://DeepSharp/appicon.ico")));
        Width = 560;
        Height = 280;
        MinWidth = 500;
        MinHeight = 250;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Brush.Parse("#111318");

        var message = "Experimental features are disabled, DeepSharp can't run on UI mode. Please enable experimental features with the following command: deepsharp --enable-experimental-features";
        var layout = new StackPanel { Spacing = 18, Margin = new Thickness(26) };
        layout.Children.Add(new TextBlock { Text = "Experimental features are disabled", FontSize = 19, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#c4a1ff") });
        layout.Children.Add(new TextBlock { Text = message, FontSize = 14, Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, LineHeight = 22 });
        var button = new Button
        {
            Content = "OK",
            Width = 92,
            Height = 40,
            CornerRadius = new CornerRadius(10),
            Background = Brush.Parse("#7951b5"),
            Foreground = Brushes.White,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        button.Click += (_, _) => Close();
        layout.Children.Add(button);
        Content = layout;
    }
}

internal sealed class DeepSharpApplication : Application
{
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.MainWindow = new ChatWindow();
        base.OnFrameworkInitializationCompleted();
    }
}

internal sealed class ChatWindow : Window
{
    private static readonly IBrush AppBackground = Brush.Parse("#111318");
    private static readonly IBrush PanelBackground = Brush.Parse("#191b22");
    private static readonly IBrush CardBackground = Brush.Parse("#1d2029");
    private static readonly IBrush Accent = Brush.Parse("#9870d5");
    private static readonly IBrush Muted = Brush.Parse("#9a9daa");

    private readonly Client _client = new();
    private readonly ConversationStore _sessions;
    private readonly AppConfig _config;
    private readonly StackPanel _conversationList = new() { Spacing = 5 };
    private readonly StackPanel _messages = new() { Spacing = 14 };
    private readonly ScrollViewer _messageScroll;
    private readonly Grid _root = new();
    private readonly ColumnDefinition _sidebarColumn = new() { Width = new GridLength(250) };
    private readonly ColumnDefinition _mainColumn = new() { Width = new GridLength(1, GridUnitType.Star) };
    private readonly TranslateTransform _sidebarTransform = new();
    private readonly Button _collapseSidebarButton = new() { Content = "☰", Width = 38, Height = 38, CornerRadius = new CornerRadius(10), Background = Brush.Parse("#282431"), Foreground = Brushes.White, FontSize = 18 };
    private readonly Button _expandSidebarButton = new() { Content = "☰", Width = 38, Height = 38, CornerRadius = new CornerRadius(10), Background = Brush.Parse("#282431"), Foreground = Brushes.White, FontSize = 18, IsVisible = false };
    private Control _sidebar = null!;
    private readonly TextBlock _title = new() { Text = "New conversation", FontSize = 19, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Foreground = Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBox _input = new()
    {
        Watermark = "Message DeepSeek...",
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 48,
        MaxHeight = 150,
        VerticalContentAlignment = VerticalAlignment.Center
    };
    private readonly Button _send = new() { Content = "➤  Send", MinWidth = 106, Height = 48, CornerRadius = new CornerRadius(12), HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(12), Background = Accent, Foreground = Brushes.White, FontWeight = FontWeight.SemiBold };
    private SavedConversation? _activeConversation;
    private ChatSession? _activeChat;
    private bool _busy;
    private bool _sidebarCollapsed;
    private bool _sidebarAnimating;

    public ChatWindow()
    {
        Title = "DeepSharp";
        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://DeepSharp/appicon.ico")));
        CanResize = true;
        SizeToContent = SizeToContent.Manual;
        Width = 960;
        Height = 640;
        MinWidth = 640;
        MinHeight = 420;
        Background = AppBackground;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var configStore = new ConfigStore();
        _config = configStore.Load();
        _sessions = new ConversationStore(Path.Combine(Path.GetDirectoryName(configStore.Path)!, "sessions.json"));
        _messageScroll = new ScrollViewer { Content = _messages, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        _input.Watermark = "Message DeepSeek...";

        _root.ColumnDefinitions.Add(_sidebarColumn);
        _root.ColumnDefinitions.Add(_mainColumn);
        _sidebar = BuildSidebar();
        _sidebar.RenderTransform = _sidebarTransform;
        _root.Children.Add(_sidebar);
        var main = BuildConversationPane();
        Grid.SetColumn(main, 1);
        _root.Children.Add(main);
        Content = _root;

        _send.Click += async (_, _) => await SendAsync();
        _input.KeyDown += async (_, e) =>
        {
            if (e.Key == Avalonia.Input.Key.Enter && !e.KeyModifiers.HasFlag(Avalonia.Input.KeyModifiers.Shift))
            {
                e.Handled = true;
                await SendAsync();
            }
        };
        Closed += (_, _) => _client.Dispose();
        RefreshConversationList();
        AddWelcome();
    }

    private Control BuildSidebar()
    {
        var sidebar = new DockPanel { Background = PanelBackground, LastChildFill = true };
        var top = new StackPanel { Spacing = 14, Margin = new Thickness(18, 20, 18, 16) };
        var brandRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        brandRow.Children.Add(new TextBlock { Text = "✦  DeepSharp", FontSize = 22, FontWeight = FontWeight.Bold, Foreground = Brush.Parse("#c4a1ff"), VerticalAlignment = VerticalAlignment.Center });
        _collapseSidebarButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        _collapseSidebarButton.VerticalContentAlignment = VerticalAlignment.Center;
        _collapseSidebarButton.Click += async (_, _) => await ToggleSidebarAsync();
        Grid.SetColumn(_collapseSidebarButton, 1);
        brandRow.Children.Add(_collapseSidebarButton);
        top.Children.Add(brandRow);
        var newButton = new Button
        {
            Content = "＋  New chat",
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Background = Brush.Parse("#282431"),
            Foreground = Brushes.White,
            Padding = new Thickness(13, 11),
            CornerRadius = new CornerRadius(12)
        };
        newButton.Click += (_, _) => StartNewConversation();
        top.Children.Add(newButton);
        DockPanel.SetDock(top, Dock.Top);
        sidebar.Children.Add(top);
        sidebar.Children.Add(new ScrollViewer { Content = _conversationList, Margin = new Thickness(11, 0, 11, 14), VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto });
        return sidebar;
    }

    private async Task ToggleSidebarAsync()
    {
        if (_sidebarAnimating) return;
        _sidebarAnimating = true;
        _collapseSidebarButton.IsEnabled = false;
        _expandSidebarButton.IsEnabled = false;

        var collapsing = !_sidebarCollapsed;
        const double expandedWidth = 250;
        const int frames = 18;
        for (var frame = 1; frame <= frames; frame++)
        {
            var progress = frame / (double)frames;
            var eased = progress * progress * (3 - 2 * progress);
            var width = collapsing ? expandedWidth * (1 - eased) : expandedWidth * eased;
            _sidebarColumn.Width = new GridLength(width);
            _sidebarTransform.X = collapsing ? -expandedWidth * eased : -expandedWidth * (1 - eased);
            _sidebar.Opacity = collapsing ? 1 - eased : eased;
            await Task.Delay(15);
        }

        _sidebarCollapsed = collapsing;
        _sidebarColumn.Width = new GridLength(_sidebarCollapsed ? 0 : expandedWidth);
        _sidebarTransform.X = _sidebarCollapsed ? -expandedWidth : 0;
        _sidebar.Opacity = _sidebarCollapsed ? 0 : 1;
        _collapseSidebarButton.IsVisible = !_sidebarCollapsed;
        _expandSidebarButton.IsVisible = _sidebarCollapsed;
        _collapseSidebarButton.IsEnabled = true;
        _expandSidebarButton.IsEnabled = true;
        _sidebarAnimating = false;
    }

    private Control BuildConversationPane()
    {
        var pane = new Grid { RowDefinitions = new RowDefinitions("64,*,Auto,Auto") };
        var headerContent = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 12 };
        _expandSidebarButton.HorizontalContentAlignment = HorizontalAlignment.Center;
        _expandSidebarButton.VerticalContentAlignment = VerticalAlignment.Center;
        _expandSidebarButton.Click += async (_, _) => await ToggleSidebarAsync();
        headerContent.Children.Add(_expandSidebarButton);
        Grid.SetColumn(_title, 1);
        headerContent.Children.Add(_title);
        var header = new Border { BorderBrush = Brush.Parse("#292c35"), BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(18, 0, 24, 0), Child = headerContent };
        pane.Children.Add(header);

        _messageScroll.Margin = new Thickness(24, 20, 24, 12);
        Grid.SetRow(_messageScroll, 1);
        pane.Children.Add(_messageScroll);

        var statusBorder = new Border { Padding = new Thickness(26, 2, 26, 8), Child = _status };
        Grid.SetRow(statusBorder, 2);
        pane.Children.Add(statusBorder);

        var composer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10, Margin = new Thickness(22, 0, 22, 20) };
        _input.Padding = new Thickness(14, 12);
        _input.Background = Brushes.Transparent;
        _input.Foreground = Brushes.White;
        _input.BorderBrush = Brushes.Transparent;
        var inputFrame = new Border
        {
            Background = CardBackground,
            BorderBrush = Brush.Parse("#373a46"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = _input
        };
        composer.Children.Add(inputFrame);
        Grid.SetColumn(_send, 1);
        composer.Children.Add(_send);
        Grid.SetRow(composer, 3);
        pane.Children.Add(composer);
        return pane;
    }

    private void RefreshConversationList()
    {
        _conversationList.Children.Clear();
        foreach (var conversation in _sessions.List())
        {
            var button = new Button
            {
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Background = conversation.Id == _activeConversation?.Id ? Brush.Parse("#30283d") : Brushes.Transparent,
                Foreground = Brushes.White,
                Padding = new Thickness(12, 10),
                CornerRadius = new CornerRadius(10),
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 9,
                    Children =
                    {
                        new TextBlock { Text = "▤", Foreground = Muted, VerticalAlignment = VerticalAlignment.Center },
                        new TextBlock { Text = conversation.Name == "Nouvelle conversation" ? "New conversation" : conversation.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 175, VerticalAlignment = VerticalAlignment.Center }
                    }
                }
            };
            button.Click += async (_, _) => await OpenConversationAsync(conversation);
            _conversationList.Children.Add(button);
        }
    }

    private async Task OpenConversationAsync(SavedConversation conversation)
    {
        if (_busy) return;
        _activeConversation = conversation;
        _activeChat = null;
        _title.Text = conversation.Name == "Nouvelle conversation" ? "New conversation" : conversation.Name;
        _status.Text = "Loading conversation...";
        RefreshConversationList();
        _messages.Children.Clear();
        try
        {
            var history = await _client.Chats.FetchHistoryAsync(conversation.DeepSeekSessionId);
            foreach (var message in history) AddMessage(message.Role, message.Content);
            _status.Text = "";
            ScrollToBottom();
        }
        catch (Exception e)
        {
            _status.Text = "Couldn't load this conversation: " + e.Message;
        }
    }

    private void StartNewConversation()
    {
        if (_busy) return;
        _activeConversation = null;
        _activeChat = null;
        _title.Text = "New conversation";
        _status.Text = "Your conversation will be created when you send your first message.";
        _messages.Children.Clear();
        AddWelcome();
        RefreshConversationList();
        _input.Focus();
    }

    private async Task SendAsync()
    {
        var prompt = _input.Text?.Trim() ?? "";
        if (prompt.Length == 0 || _busy) return;
        _input.Text = "";
        _busy = true;
        _send.IsEnabled = false;
        _input.IsEnabled = false;
        _status.Text = "DeepSeek is thinking...";
        AddMessage("user", prompt);
        ScrollToBottom();

        try
        {
            if (_activeChat is null)
            {
                _activeChat = _activeConversation is null
                    ? await _client.Chats.CreateSessionAsync(_config.Model, _config.GodMode)
                    : await ResumeChatAsync(_activeConversation);
                if (_activeConversation is null)
                    _activeConversation = _sessions.Create(_activeChat.SessionId, _activeChat.ModelType, _activeChat.GodMode);
            }

            var response = await _activeChat.RespondAgentAsync(prompt, thinking: _config.ShowThinking, search: _config.SearchEnabled, maxRounds: _client.MaxAgentRounds);
            _activeConversation = _sessions.UpdateAfterTurn(_activeConversation!.Id, _activeChat.LastMessageId, prompt);
            _title.Text = _activeConversation.Name;
            AddToolActivity(response.ToolsUsed);
            var visibleAnswer = ChatSession.StripToolMarkup(response.Content, _client.AgentTools).Trim();
            if (visibleAnswer.Length > 0) AddMessage("assistant", visibleAnswer);
            _status.Text = "";
            RefreshConversationList();
        }
        catch (Exception e)
        {
            AddMessage("assistant", "Something went wrong: " + e.Message);
            _status.Text = "Request failed";
            RefreshConversationList();
        }
        finally
        {
            _busy = false;
            _send.IsEnabled = true;
            _input.IsEnabled = true;
            _input.Focus();
            ScrollToBottom();
        }
    }

    private async Task<ChatSession> ResumeChatAsync(SavedConversation conversation)
    {
        var parentMessageId = conversation.LastMessageId;
        if (parentMessageId is null)
        {
            parentMessageId = await _client.Chats.FetchLastAssistantMessageIdAsync(conversation.DeepSeekSessionId);
            if (parentMessageId is null) throw new InvalidOperationException("This conversation does not have a DeepSeek reply yet.");
            _activeConversation = _sessions.UpdateLastMessageId(conversation.Id, parentMessageId.Value);
        }
        return new ChatSession(_client, conversation.DeepSeekSessionId, conversation.Model, conversation.GodMode, parentMessageId);
    }

    private void AddWelcome()
    {
        string[] prompts =
        {
            "What's new today?",
            "What are we building today?",
            "Drop your idea!",
            "Good evening, what's the plan?"
        };
        var welcome = new StackPanel
        {
            Spacing = 11,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "✦", Foreground = Accent, FontSize = 30, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = prompts[Random.Shared.Next(prompts.Length)], Foreground = Brushes.White, FontSize = 23, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
                new TextBlock { Text = "Start a conversation with DeepSeek.", Foreground = Muted, FontSize = 14, HorizontalAlignment = HorizontalAlignment.Center }
            }
        };
        _messages.Children.Add(new Border { MinHeight = 360, HorizontalAlignment = HorizontalAlignment.Stretch, Child = welcome });
    }

    private void AddToolActivity(IReadOnlyList<ToolExecution> tools)
    {
        if (tools.Count == 0) return;
        var activity = new StackPanel { Spacing = 5 };
        foreach (var tool in tools)
        {
            var summary = DescribeToolExecution(tool);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = "✓", Foreground = Accent, FontWeight = FontWeight.Bold, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = summary, Foreground = Muted, FontSize = 12, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center });
            activity.Children.Add(row);
        }
        _messages.Children.Add(new Border
        {
            Background = Brush.Parse("#17191f"),
            BorderBrush = Brush.Parse("#292c35"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 9),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MaxWidth = 900,
            Child = activity
        });
    }

    private static string DescribeToolExecution(ToolExecution execution)
    {
        var path = ToolArgument(execution.Arguments, "path");
        var command = ToolArgument(execution.Arguments, "command");
        var file = string.IsNullOrWhiteSpace(path) ? "a file" : path.Replace('\\', '/').Split('/').Last();
        return execution.Name switch
        {
            "exec" => "Ran a command: " + (string.IsNullOrWhiteSpace(command) ? "command" : command),
            "exec_code" => "Ran Python code",
            "read_file" => "Read the file: " + file,
            "write_file" => "Wrote a file: " + file,
            "edit_file" => "Edited the file: " + file,
            "grep" => "Searched files for: " + (ToolArgument(execution.Arguments, "pattern") is { Length: > 0 } pattern ? pattern : "a pattern"),
            "remember" => "Saved information to memory",
            "recall" => "Searched saved memories",
            "forget" => "Removed a saved memory",
            "index_project" => "Indexed project files",
            "project_overview" => "Read the project overview",
            "find_symbol" => "Searched for symbol: " + (ToolArgument(execution.Arguments, "query") ?? ""),
            "find_references" => "Searched references for: " + (ToolArgument(execution.Arguments, "name") ?? ""),
            "set_plan" => "Created a task plan",
            "update_plan" => "Updated the task plan",
            "get_plan" => "Read the task plan",
            _ => "Used tool: " + execution.Name
        };
    }

    private static string? ToolArgument(IReadOnlyDictionary<string, JsonElement> arguments, string name)
    {
        foreach (var pair in arguments)
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return pair.Value.ValueKind == JsonValueKind.String ? pair.Value.GetString() : pair.Value.ToString();
        return null;
    }

    private void AddMessage(string role, string content)
    {
        var isUser = role.Equals("user", StringComparison.OrdinalIgnoreCase);
        var card = new Border
        {
            Background = isUser ? Brush.Parse("#29253a") : CardBackground,
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(17, 14),
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Stretch,
            MaxWidth = 900
        };
        var body = new StackPanel { Spacing = 7 };
        body.Children.Add(new TextBlock { Text = isUser ? "YOU" : "DEEPSEEK", Foreground = Accent, FontSize = 11, FontWeight = FontWeight.Bold });
        if (isUser)
            body.Children.Add(MarkdownRenderer.RenderPlain(content));
        else
            body.Children.Add(MarkdownRenderer.Render(content));
        card.Child = body;
        _messages.Children.Add(card);
    }

    private void ScrollToBottom()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _messageScroll.Offset = new Vector(_messageScroll.Offset.X, _messageScroll.Extent.Height));
    }
}

internal static class MarkdownRenderer
{
    private static readonly IBrush TextColor = Brushes.White;
    private static readonly IBrush MutedColor = Brush.Parse("#a7a9b5");
    private static readonly IBrush AccentColor = Brush.Parse("#c4a1ff");
    private static readonly IBrush CodeBackground = Brush.Parse("#12141a");
    private static readonly IBrush BorderColor = Brush.Parse("#373a46");
    private static readonly FontFamily SystemEmojiFont = FindSystemEmojiFont();

    public static Control RenderPlain(string text)
    {
        var block = new TextBlock { Foreground = TextColor, TextWrapping = TextWrapping.Wrap, LineHeight = 23 };
        if (block.Inlines is { } inlines) AppendStyledRuns(inlines, text);
        else block.Text = text;
        return block;
    }

    public static Control Render(string markdown)
    {
        var panel = new StackPanel { Spacing = 10 };
        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var index = 0;
        while (index < lines.Length)
        {
            if (string.IsNullOrWhiteSpace(lines[index])) { index++; continue; }

            var trimmed = lines[index].TrimStart();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                var language = trimmed[3..].Trim();
                var code = new List<string>();
                index++;
                while (index < lines.Length && !lines[index].TrimStart().StartsWith("```", StringComparison.Ordinal)) code.Add(lines[index++]);
                if (index < lines.Length) index++;
                panel.Children.Add(CreateCodeBlock(language, string.Join('\n', code)));
                continue;
            }

            if (index + 1 < lines.Length && IsTableRow(lines[index]) && IsTableSeparator(lines[index + 1]))
            {
                var rows = new List<string[]> { SplitTableRow(lines[index]) };
                index += 2;
                while (index < lines.Length && IsTableRow(lines[index]) && !string.IsNullOrWhiteSpace(lines[index]))
                    rows.Add(SplitTableRow(lines[index++]));
                panel.Children.Add(CreateTable(rows));
                continue;
            }

            var heading = Regex.Match(trimmed, "^(#{1,6})\\s+(.+)$");
            if (heading.Success)
            {
                var size = heading.Groups[1].Length switch { 1 => 25d, 2 => 21d, 3 => 18d, _ => 16d };
                panel.Children.Add(CreateInlineText(heading.Groups[2].Value, size, FontWeight.Bold));
                index++;
                continue;
            }

            if (Regex.IsMatch(trimmed, "^(---+|\\*\\*\\*+|___+)$"))
            {
                panel.Children.Add(new Border { Height = 1, Background = BorderColor, Margin = new Thickness(0, 5) });
                index++;
                continue;
            }

            if (trimmed.StartsWith(">", StringComparison.Ordinal))
            {
                var quote = new List<string>();
                while (index < lines.Length && lines[index].TrimStart().StartsWith(">", StringComparison.Ordinal))
                    quote.Add(lines[index++].TrimStart()[1..].TrimStart());
                var quoteText = CreateInlineText(string.Join(' ', quote), 14, FontWeight.Normal);
                quoteText.Foreground = MutedColor;
                panel.Children.Add(new Border { BorderBrush = AccentColor, BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 2), Child = quoteText });
                continue;
            }

            var listItem = Regex.Match(trimmed, "^(?:[-+*]|\\d+[.)])\\s+(.+)$");
            if (listItem.Success)
            {
                var bullet = Regex.IsMatch(trimmed, "^\\d+[.)]") ? Regex.Match(trimmed, "^\\d+[.)]").Value : "•";
                var listRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
                listRow.Children.Add(new TextBlock { Text = bullet, Foreground = AccentColor, MinWidth = 20, FontWeight = FontWeight.SemiBold });
                listRow.Children.Add(CreateInlineText(listItem.Groups[1].Value, 14, FontWeight.Normal));
                panel.Children.Add(listRow);
                index++;
                continue;
            }

            var paragraph = new List<string> { lines[index++].Trim() };
            while (index < lines.Length && !string.IsNullOrWhiteSpace(lines[index]) && !StartsBlock(lines[index], lines, index))
                paragraph.Add(lines[index++].Trim());
            panel.Children.Add(CreateInlineText(string.Join(' ', paragraph), 14, FontWeight.Normal));
        }
        return panel;
    }

    private static bool StartsBlock(string line, string[] lines, int index)
    {
        var value = line.TrimStart();
        if (value.StartsWith("```", StringComparison.Ordinal) || value.StartsWith(">", StringComparison.Ordinal) ||
            Regex.IsMatch(value, "^#{1,6}\\s+") || Regex.IsMatch(value, "^(?:[-+*]|\\d+[.)])\\s+") ||
            Regex.IsMatch(value, "^(---+|\\*\\*\\*+|___+)$")) return true;
        return index + 1 < lines.Length && IsTableRow(line) && IsTableSeparator(lines[index + 1]);
    }

    private static TextBlock CreateInlineText(string text, double size, FontWeight weight)
    {
        var block = new TextBlock { FontSize = size, FontWeight = weight, Foreground = TextColor, TextWrapping = TextWrapping.Wrap, LineHeight = size + 8 };
        var inlines = block.Inlines;
        if (inlines is null) { block.Text = text; return block; }
        var pattern = new Regex("(\\*\\*.+?\\*\\*|__.+?__|~~.+?~~|`[^`]+`|\\*[^*]+\\*|_[^_]+_)");
        var offset = 0;
        foreach (Match match in pattern.Matches(text))
        {
            if (match.Index > offset) AppendStyledRuns(inlines, text[offset..match.Index]);
            var token = match.Value;
            if (token.StartsWith("**", StringComparison.Ordinal) || token.StartsWith("__", StringComparison.Ordinal))
                AppendStyledRuns(inlines, token[2..^2], run => run.FontWeight = FontWeight.Bold);
            else if (token.StartsWith('`'))
                AppendStyledRuns(inlines, token[1..^1], run => { run.FontFamily = new FontFamily("Consolas"); run.Foreground = AccentColor; });
            else if (token.StartsWith("~~", StringComparison.Ordinal))
                AppendStyledRuns(inlines, token[2..^2], run => { run.Foreground = MutedColor; run.TextDecorations = TextDecorations.Strikethrough; });
            else
                AppendStyledRuns(inlines, token[1..^1], run => run.FontStyle = FontStyle.Italic);
            offset = match.Index + match.Length;
        }
        if (offset < text.Length) AppendStyledRuns(inlines, text[offset..]);
        return block;
    }

    private static void AppendStyledRuns(InlineCollection inlines, string text, Action<Run>? style = null)
    {
        var elements = StringInfo.GetTextElementEnumerator(text);
        var chunk = new StringBuilder();
        bool? chunkIsEmoji = null;
        void Flush()
        {
            if (chunk.Length == 0 || chunkIsEmoji is null) return;
            var run = new Run(chunk.ToString());
            style?.Invoke(run);
            if (chunkIsEmoji.Value) run.FontFamily = SystemEmojiFont;
            inlines.Add(run);
            chunk.Clear();
        }

        while (elements.MoveNext())
        {
            var element = (string)elements.Current!;
            var isEmoji = ContainsEmoji(element);
            if (chunkIsEmoji is not null && chunkIsEmoji.Value != isEmoji) Flush();
            chunkIsEmoji = isEmoji;
            chunk.Append(element);
        }
        Flush();
    }

    private static bool ContainsEmoji(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            var value = rune.Value;
            if (value is >= 0x1F000 and <= 0x1FAFF or >= 0x2600 and <= 0x27BF or >= 0x2300 and <= 0x23FF or >= 0x2B00 and <= 0x2BFF or 0xFE0F or 0x20E3)
                return true;
        }
        return false;
    }

    private static FontFamily FindSystemEmojiFont()
    {
        if (OperatingSystem.IsWindows()) return new FontFamily("Segoe UI Emoji");
        if (OperatingSystem.IsMacOS()) return new FontFamily("Apple Color Emoji");
        // Let Avalonia use the Linux system font matcher, which selects the installed emoji font.
        return FontFamily.Default;
    }

    private static Control CreateCodeBlock(string language, string code)
    {
        var content = new StackPanel { Spacing = 7 };
        if (!string.IsNullOrWhiteSpace(language))
            content.Children.Add(new TextBlock { Text = language, FontSize = 11, Foreground = AccentColor, FontWeight = FontWeight.SemiBold });
        var codeText = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 13, Foreground = TextColor, TextWrapping = TextWrapping.NoWrap };
        if (codeText.Inlines is { } codeRuns)
            AppendStyledRuns(codeRuns, code, run => { run.FontFamily = new FontFamily("Consolas"); run.Foreground = TextColor; });
        else codeText.Text = code;
        content.Children.Add(new ScrollViewer { Content = codeText, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled });
        return new Border { Background = CodeBackground, BorderBrush = BorderColor, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(13), Child = content };
    }

    private static bool IsTableRow(string line) => line.Contains('|', StringComparison.Ordinal);

    private static bool IsTableSeparator(string line)
    {
        if (!IsTableRow(line)) return false;
        var cells = SplitTableRow(line);
        return cells.Length > 0 && cells.All(cell => Regex.IsMatch(cell.Trim(), "^:?-{3,}:?$"));
    }

    private static string[] SplitTableRow(string line)
    {
        var value = line.Trim().Trim('|').Replace("\\|", "\u0001", StringComparison.Ordinal);
        return value.Split('|').Select(cell => cell.Replace('\u0001', '|').Trim()).ToArray();
    }

    private static Control CreateTable(IReadOnlyList<string[]> rows)
    {
        var columns = rows.Max(row => row.Length);
        var grid = new Grid();
        for (var column = 0; column < columns; column++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        for (var row = 0; row < rows.Count; row++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        for (var row = 0; row < rows.Count; row++)
        for (var column = 0; column < columns; column++)
        {
            var value = column < rows[row].Length ? rows[row][column] : "";
            var cellText = CreateInlineText(value, 13, row == 0 ? FontWeight.SemiBold : FontWeight.Normal);
            var cell = new Border
            {
                Background = row == 0 ? Brush.Parse("#282431") : Brushes.Transparent,
                BorderBrush = BorderColor,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Padding = new Thickness(10, 8),
                Child = cellText
            };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            grid.Children.Add(cell);
        }
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }
}
