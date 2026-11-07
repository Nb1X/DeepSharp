using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace DeepSharp;

/// <summary>Finds Chromium browsers and captures a Bearer token through Chrome DevTools Protocol.</summary>
public static class BrowserAuthentication
{
    private static readonly HttpClient DevToolsHttp = new() { Timeout = TimeSpan.FromSeconds(2) };
    private static readonly string[] BrowserNames = ["chrome", "chrome.exe", "msedge", "msedge.exe", "brave", "brave.exe", "brave-browser", "chromium", "chromium-browser", "opera", "opera.exe", "vivaldi", "vivaldi.exe"];

    public static string? FindBrowser()
    {
        var found = new List<string>();
        foreach (var name in BrowserNames)
        {
            var path = FindOnPath(name); if (path is not null) found.Add(path);
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (var root in new[] { Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", Environment.GetEnvironmentVariable("PROGRAMFILES") ?? "", Environment.GetEnvironmentVariable("PROGRAMFILES(X86)") ?? "" })
                foreach (var relative in new[] { @"Google\Chrome\Application\chrome.exe", @"Microsoft\Edge\Application\msedge.exe", @"BraveSoftware\Brave-Browser\Application\brave.exe", @"Programs\Opera\opera.exe", @"Vivaldi\Application\vivaldi.exe" })
                    if (!string.IsNullOrEmpty(root)) found.Add(Path.Combine(root, relative));
        }
        else if (OperatingSystem.IsMacOS()) found.AddRange(["/Applications/Google Chrome.app/Contents/MacOS/Google Chrome", "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge", "/Applications/Brave Browser.app/Contents/MacOS/Brave Browser", "/Applications/Chromium.app/Contents/MacOS/Chromium", "/Applications/Opera.app/Contents/MacOS/Opera", "/Applications/Vivaldi.app/Contents/MacOS/Vivaldi"]);
        else found.AddRange(["/usr/bin/google-chrome", "/usr/bin/google-chrome-stable", "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/microsoft-edge", "/usr/bin/brave-browser", "/usr/bin/opera", "/usr/bin/vivaldi", "/snap/bin/chromium", "/snap/bin/brave"]);
        return found.FirstOrDefault(File.Exists);
    }

    private static string? FindOnPath(string name)
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim('"'), name);
            if (File.Exists(candidate)) return candidate;
            if (OperatingSystem.IsWindows() && !Path.HasExtension(candidate)) foreach (var ext in (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';')) if (File.Exists(candidate + ext)) return candidate + ext;
        }
        return null;
    }

    public static async Task<string?> GetBearerTokenAsync(string? loginUrl = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default, Action<string>? progress = null)
    {
        var browserPath = FindBrowser() ?? throw new InvalidOperationException("No Chromium-based browser was found. Install Chrome, Edge, Brave, Chromium, Opera, or Vivaldi.");
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var profile = Path.Combine(Path.GetTempPath(), "deepwrap-auth-profile-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(profile);
        using var browser = new Process { StartInfo = new ProcessStartInfo(browserPath) { UseShellExecute = false, CreateNoWindow = false, WindowStyle = ProcessWindowStyle.Normal } };
        browser.StartInfo.ArgumentList.Add($"--remote-debugging-port={port}");
        browser.StartInfo.ArgumentList.Add("--remote-debugging-address=127.0.0.1");
        browser.StartInfo.ArgumentList.Add("--remote-allow-origins=*");
        browser.StartInfo.ArgumentList.Add($"--user-data-dir={profile}");
        browser.StartInfo.ArgumentList.Add("--no-first-run"); browser.StartInfo.ArgumentList.Add("--no-default-browser-check"); browser.StartInfo.ArgumentList.Add("--disable-popup-blocking");
        browser.StartInfo.ArgumentList.Add(loginUrl ?? Client.BaseUrl.TrimEnd('/') + "/sign_in");
        ClientWebSocket? socket = null;
        try
        {
            if (!browser.Start()) throw new InvalidOperationException($"Could not start browser: {browserPath}");
            progress?.Invoke($"Opening browser: {browserPath}");
            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); waitCts.CancelAfter(TimeSpan.FromSeconds(60));
            try { await WaitUntilReadyAsync(port, browser, waitCts.Token); }
            catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
            {
                var state = browser.HasExited ? $"Browser exited with code {browser.ExitCode}." : $"Browser process {browser.Id} is still running.";
                throw new TimeoutException($"{state} DevTools did not start on 127.0.0.1:{port} within 60 seconds. Browser: {browserPath}", e);
            }
            var wsUrl = await GetPageWebSocketUrlAsync(port, waitCts.Token);
            socket = new ClientWebSocket(); await socket.ConnectAsync(new Uri(wsUrl), waitCts.Token);
            await SendCommandAsync(socket, 1, "Network.enable", cancellationToken); await SendCommandAsync(socket, 2, "Page.enable", cancellationToken);
            progress?.Invoke("Browser ready. Sign in to DeepSeek; DeepSharp will capture the authorization token.");
            using var authCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (timeout is not null) authCts.CancelAfter(timeout.Value);
            var buffer = new byte[16 * 1024];
            while (true)
            {
                authCts.Token.ThrowIfCancellationRequested();
                using var message = new MemoryStream(); WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), authCts.Token);
                    if (result.MessageType == WebSocketMessageType.Close) return null;
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);
                using var doc = JsonDocument.Parse(message.ToArray()); var token = ExtractBearer(doc.RootElement); if (token is not null) return token;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeout is not null) { return null; }
        finally
        {
            if (socket is not null)
            {
                try { if (socket.State == WebSocketState.Open) { using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await SendCommandAsync(socket, 3, "Browser.close", closeCts.Token); } } catch { }
                socket.Dispose();
            }
            try { if (!browser.HasExited) { if (!browser.CloseMainWindow()) browser.Kill(true); if (!browser.WaitForExit(2000) && !browser.HasExited) browser.Kill(true); } } catch { }
            try { Directory.Delete(profile, recursive: true); } catch { }
        }
    }

    private static async Task WaitUntilReadyAsync(int port, Process browser, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (browser.HasExited) throw new InvalidOperationException($"Browser exited during startup with code {browser.ExitCode}.");
            try { using var response = await DevToolsHttp.GetAsync($"http://127.0.0.1:{port}/json/version", cancellationToken); response.EnsureSuccessStatusCode(); return; }
            catch (HttpRequestException) { await Task.Delay(200, cancellationToken); }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { await Task.Delay(200, cancellationToken); }
        }
    }
    private static async Task<string> GetPageWebSocketUrlAsync(int port, CancellationToken cancellationToken)
    {
        using var response = await DevToolsHttp.GetAsync($"http://127.0.0.1:{port}/json", cancellationToken); response.EnsureSuccessStatusCode(); using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        foreach (var target in doc.RootElement.EnumerateArray()) if (target.String("type") == "page" && target.String("webSocketDebuggerUrl") is { } url) return url;
        throw new InvalidOperationException("Could not find a browser DevTools page target.");
    }
    private static async Task SendCommandAsync(ClientWebSocket socket, int id, string method, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { id, method })); await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
    }
    private static string? ExtractBearer(JsonElement message)
    {
        var method = message.String("method"); var parameters = message.Prop("params"); JsonElement headers;
        if (method == "Network.requestWillBeSent") headers = parameters.Prop("request").Prop("headers");
        else if (method == "Network.requestWillBeSentExtraInfo") headers = parameters.Prop("headers");
        else return null;
        if (headers.ValueKind != JsonValueKind.Object) return null;
        foreach (var property in headers.EnumerateObject())
            if (property.Name.Equals("authorization", StringComparison.OrdinalIgnoreCase))
            {
                var value = property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : property.Value.ToString();
                if (value?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true) return BearerToken.Normalize(value);
            }
        return null;
    }
}
