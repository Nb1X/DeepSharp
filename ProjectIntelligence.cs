using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeepSharp;

/// <summary>Persistent lightweight index of source files, symbols, imports and call sites.</summary>
public sealed class ProjectIntelligence
{
    private readonly object _gate = new(); private readonly Dictionary<string, FileIndex> _files = new(StringComparer.OrdinalIgnoreCase); public string Root { get; } public string IndexPath { get; }
    public ProjectIntelligence(string root, string indexPath) { Root = Path.GetFullPath(root); IndexPath = Path.GetFullPath(indexPath); try { var data = JsonSerializer.Deserialize<Dictionary<string, FileIndex>>(File.ReadAllText(IndexPath)); if (data is not null) foreach (var x in data) _files[x.Key] = x.Value; } catch { } }
    public object IndexProject(string glob = "**/*")
    {
        lock (_gate)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var changed = 0; var errors = new List<object>();
            if (Directory.Exists(Root)) foreach (var path in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Where(p => !p.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar) && Matches(Path.GetRelativePath(Root,p).Replace('\\','/'), glob)))
            { var relative = Path.GetRelativePath(Root,path).Replace('\\','/'); seen.Add(relative); try { var bytes = File.ReadAllBytes(path); var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(); if (_files.TryGetValue(relative,out var previous) && previous.Hash == hash) continue; var source = Encoding.UTF8.GetString(bytes); _files[relative] = Analyze(source, hash); changed++; } catch(Exception e) { errors.Add(new { path=relative, error=e.Message }); } }
            var removed = _files.Keys.Where(k => !seen.Contains(k)).ToArray(); foreach(var key in removed) _files.Remove(key);
            Directory.CreateDirectory(Path.GetDirectoryName(IndexPath)!); File.WriteAllText(IndexPath, JsonSerializer.Serialize(_files,new JsonSerializerOptions{WriteIndented=true}));
            return new { root=Root, files=_files.Count, changed, removed, errors };
        }
    }
    private static bool Matches(string path, string glob) { var pattern = "^"+Regex.Escape(glob).Replace("\\*\\*",".*").Replace("\\*","[^/]*").Replace("\\?",".")+"$"; return Regex.IsMatch(path, pattern, RegexOptions.IgnoreCase); }
    private static FileIndex Analyze(string source,string hash)
    {
        var symbols = new List<Symbol>(); var imports = new HashSet<string>(); var calls = new List<Call>(); var lines=source.Split('\n');
        for (var i=0;i<lines.Length;i++)
        {
            var line=lines[i]; var py = Regex.Match(line,@"^\s*(class|async\s+def|def)\s+([A-Za-z_]\w*)"); var cs = Regex.Match(line,@"\b(class|interface|struct|enum|record)\s+([A-Za-z_]\w*)|\b(?:public|private|protected|internal)?\s*(?:static\s+|async\s+|virtual\s+|override\s+|sealed\s+|partial\s+)*(?:[\w<>?\[\],]+)\s+([A-Za-z_]\w*)\s*\([^;]*\)\s*(?:\{|=>)");
            if(py.Success) symbols.Add(new(py.Groups[2].Value,py.Groups[1].Value.Contains("class")?"class":"function",i+1,i+1));
            else if(cs.Success) { var name=cs.Groups[2].Success?cs.Groups[2].Value:cs.Groups[3].Value; symbols.Add(new(name,cs.Groups[1].Success?"class":"function",i+1,i+1)); }
            var imp=Regex.Match(line,@"^\s*(?:using|import)\s+([\w.]+)"); if(imp.Success) imports.Add(imp.Groups[1].Value);
            foreach(Match call in Regex.Matches(line,@"\b([A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*\(")) if(!new[]{"if","for","while","switch","catch","function","def","class"}.Contains(call.Groups[1].Value)) calls.Add(new(call.Groups[1].Value,i+1));
        }
        return new(hash,lines.Length,symbols,imports.Order().ToList(),calls);
    }
    public object Overview() { lock(_gate) return new { root=Root, files=_files.Count, lines=_files.Values.Sum(x=>x.Lines), symbols=_files.Values.Sum(x=>x.Symbols.Count), imports=_files.Values.SelectMany(x=>x.Imports).Distinct().Order().ToArray() }; }
    public object FindSymbol(string query) { lock(_gate) return _files.SelectMany(f=>f.Value.Symbols.Where(s=>s.Name.Contains(query,StringComparison.OrdinalIgnoreCase)).Select(s=>new { path=f.Key,s.Name,s.Kind,s.Line,s.EndLine })).ToArray(); }
    public object References(string name) { lock(_gate) return _files.SelectMany(f=>f.Value.Calls.Where(c=>c.Name==name||c.Name.EndsWith("."+name,StringComparison.Ordinal)).Select(c=>new { path=f.Key,c.Name,c.Line })).ToArray(); }
    public IReadOnlyList<Tool> Definitions => [new("index_project","Incrementally index source files.",new{type="object",properties=new{glob=new{type="string"}}}),new("project_overview","Return indexed project size, symbols and dependencies.",new{type="object"}),new("find_symbol","Find classes or functions by source location.",new{type="object",properties=new{query=new{type="string"}},required=new[]{"query"}}),new("find_references","Find indexed call sites.",new{type="object",properties=new{name=new{type="string"}},required=new[]{"name"}})];
    public Dictionary<string,Func<JsonElement,object?>> Functions=>new(){["index_project"]=a=>IndexProject(a.String("glob")??"**/*"),["project_overview"]=a=>Overview(),["find_symbol"]=a=>FindSymbol(a.String("query")??""),["find_references"]=a=>References(a.String("name")??"")};
    private sealed record FileIndex(string Hash,int Lines,List<Symbol> Symbols,List<string> Imports,List<Call> Calls);
    private sealed record Symbol(string Name,string Kind,int Line,int EndLine);
    private sealed record Call(string Name,int Line);
}
