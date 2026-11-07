using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeepSharp;

public sealed class NativeTools
{
    private readonly double _timeout;
    private readonly int _budget;
    public string WorkingDirectory { get; }
    public string Shell { get; }
    public NativeTools(string? workingDirectory = null, double timeout = 30, string? shell = null, int outputBudget = 8000)
    {
        WorkingDirectory = Path.GetFullPath(workingDirectory ?? Environment.CurrentDirectory);
        _timeout = timeout; _budget = Math.Max(1000, outputBudget);
        Shell = shell ?? Environment.GetEnvironmentVariable("DEEPWRAP_SHELL") ?? (OperatingSystem.IsWindows() ? "powershell.exe" : Environment.GetEnvironmentVariable("SHELL") ?? "/bin/sh");
    }
    private string GetArg(JsonElement args, string key, string fallback = "") => args.String(key) ?? fallback;
    private object Run(JsonElement a)
    {
        var command = GetArg(a, "command"); var shellName = Path.GetFileNameWithoutExtension(Shell).ToLowerInvariant();
        var psi = new ProcessStartInfo(Shell) { WorkingDirectory = WorkingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (shellName is "powershell" or "pwsh") { psi.ArgumentList.Add("-NoProfile"); psi.ArgumentList.Add("-Command"); psi.ArgumentList.Add(command); }
        else if (shellName == "cmd") { psi.ArgumentList.Add("/d"); psi.ArgumentList.Add("/s"); psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(command); }
        else { psi.ArgumentList.Add("-c"); psi.ArgumentList.Add(command); }
        using var process = new Process { StartInfo = psi }; process.Start(); var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
        var completed = process.WaitForExit((int)(Math.Max(0.1, a.Int("timeout", (int)_timeout)) * 1000));
        if (!completed) { try { process.Kill(true); } catch { } return new { exit_code = (int?)null, stdout = "", stderr = "", timed_out = true }; }
        var output = stdout.GetAwaiter().GetResult(); var error = stderr.GetAwaiter().GetResult();
        var truncated = output.Length > _budget || error.Length > _budget;
        return new { exit_code = process.ExitCode, stdout = Clip(output), stderr = Clip(error), shell = Path.GetFileName(Shell).ToLowerInvariant(), truncated };
    }
    private string Clip(string value) => value.Length <= _budget ? value : value[..(int)(_budget * .6)] + $"\n[... {value.Length - _budget} of {value.Length} characters omitted.]\n" + value[^(int)(_budget * .4)..];
    private string Resolve(string path) => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(WorkingDirectory, path));
    private object Read(JsonElement a)
    {
        var path = Resolve(GetArg(a, "path")); var offset = Math.Max(0, a.Int("offset")); var limit = Math.Clamp(a.Int("limit", _budget), 1, 1_000_000);
        var lines = File.ReadLines(path).Skip(offset).Take(limit + 1).ToArray(); return new { path, offset, content = string.Join("\n", lines.Take(limit)), has_more = lines.Length > limit, next_offset = offset + Math.Min(limit, lines.Length) };
    }
    private object Write(JsonElement a) { var path = Resolve(GetArg(a, "path")); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, GetArg(a, "content")); return new { path, written = true, chars = GetArg(a, "content").Length }; }
    private object Edit(JsonElement a)
    {
        var path = Resolve(GetArg(a, "path")); var source = File.ReadAllText(path); var old = GetArg(a, "old_string"); var replacement = GetArg(a, "new_string"); var count = source.Split(old).Length - 1;
        if (old.Length == 0 || count != 1) throw new InvalidOperationException($"Expected exactly one occurrence of old_string, found {count}.");
        File.WriteAllText(path, source.Replace(old, replacement)); return new { path, edited = true };
    }
    private object Grep(JsonElement a)
    {
        var pattern = new Regex(GetArg(a, "pattern"), RegexOptions.IgnoreCase | RegexOptions.Multiline); var glob = GetArg(a, "glob", "*"); var offset = a.Int("offset"); var limit = Math.Clamp(a.Int("limit", 100), 1, 10000); var rows = new List<object>();
        foreach (var file in Directory.EnumerateFiles(WorkingDirectory, "*", SearchOption.AllDirectories).Where(f => !f.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar) && Glob(Path.GetFileName(f), glob)))
        { try { var i = 0; foreach (var line in File.ReadLines(file)) { i++; if (pattern.IsMatch(line)) rows.Add(new { path = Path.GetRelativePath(WorkingDirectory, file).Replace('\\','/'), line = i, text = line }); } } catch { } }
        return new { matches = rows.Skip(offset).Take(limit), has_more = rows.Count > offset + limit, next_offset = Math.Min(rows.Count, offset + limit), total = rows.Count };
    }
    private static bool Glob(string name, string glob) { var regex = "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$"; return Regex.IsMatch(name, regex, RegexOptions.IgnoreCase); }
    private object Code(JsonElement a) { var code = GetArg(a, "code"); var temp = Path.Combine(Path.GetTempPath(), "deepwrap-" + Guid.NewGuid().ToString("N") + ".py"); File.WriteAllText(temp, code); try { using var p = Process.Start(new ProcessStartInfo("python", $"\"{temp}\"") { WorkingDirectory = WorkingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!; var output = p.StandardOutput.ReadToEnd(); var error = p.StandardError.ReadToEnd(); p.WaitForExit((int)(_timeout * 1000)); return new { exit_code = p.ExitCode, stdout = Clip(output), stderr = Clip(error) }; } finally { File.Delete(temp); } }
    public object Execute(string name, JsonElement args) => name switch { "exec" => Run(args), "read_file" => Read(args), "write_file" => Write(args), "edit_file" => Edit(args), "grep" => Grep(args), "exec_code" => Code(args), _ => throw new ArgumentException("Unknown native tool: " + name) };
    private static object Parameters(object fields, string[]? required = null) => new { type = "object", properties = fields, required = required ?? [] };
    public IReadOnlyList<Tool> Definitions => [
        new("exec", "Run a host operating-system shell command.", Parameters(new { command=new { type="string" }, timeout=new { type="number" } }, ["command"])),
        new("grep", "Regex search file contents with an optional filename glob.", Parameters(new { pattern=new { type="string" }, glob=new { type="string" }, offset=new { type="integer" }, limit=new { type="integer" } }, ["pattern"])),
        new("read_file", "Read a text file with bounded pagination.", Parameters(new { path=new { type="string" }, offset=new { type="integer" }, limit=new { type="integer" } }, ["path"])),
        new("write_file", "Write complete text content to a file.", Parameters(new { path=new { type="string" }, content=new { type="string" } }, ["path","content"])),
        new("edit_file", "Perform a guarded exact-string replacement.", Parameters(new { path=new { type="string" }, old_string=new { type="string" }, new_string=new { type="string" } }, ["path","old_string","new_string"])),
        new("exec_code", "Run Python code using the current working directory.", Parameters(new { code=new { type="string" }, timeout=new { type="number" } }, ["code"]))
    ];
    public Dictionary<string, Func<JsonElement, object?>> Functions => Definitions.ToDictionary(t => t.Name, t => (Func<JsonElement, object?>)(a => Execute(t.Name, a)));
}
