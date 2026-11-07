using System.Text.Json;

namespace DeepSharp;

public sealed class TaskPlan
{
    private readonly object _gate = new();
    private List<PlanStep> _steps = [];
    public object SetPlan(IEnumerable<string> titles)
    {
        var list = titles.ToList();
        if (list.Count is < 1 or > 12) throw new ArgumentException("A plan needs between 1 and 12 steps.");
        lock (_gate) { _steps = list.Select((title, i) => new PlanStep(i + 1, title.Trim()[..Math.Min(120, title.Trim().Length)], i == 0 ? "in_progress" : "pending", null)).ToList(); return GetPlan(); }
    }
    public object UpdatePlan(int step, string status, string? note = null)
    {
        if (!new[] { "pending", "in_progress", "done", "blocked" }.Contains(status)) throw new ArgumentException("Invalid status.");
        lock (_gate)
        {
            var i = _steps.FindIndex(s => s.Index == step); if (i < 0) throw new ArgumentException($"Step {step} does not exist.");
            _steps[i] = _steps[i] with { Status = status, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(120, note.Trim().Length)] };
            if (status == "done") { var next = _steps.FindIndex(s => s.Index > step && s.Status == "pending"); if (next >= 0) _steps[next] = _steps[next] with { Status = "in_progress" }; }
            return GetPlan();
        }
    }
    public object GetPlan() { lock (_gate) return new { steps = _steps.ToArray(), total = _steps.Count, done = _steps.Count(s => s.Status == "done"), current = _steps.FirstOrDefault(s => s.Status == "in_progress")?.Title, complete = _steps.Count > 0 && _steps.All(s => s.Status == "done") }; }
    public string Summary() { lock (_gate) return string.Join("\n", _steps.Select(s => $"[{(s.Status == "done" ? "x" : s.Status == "in_progress" ? ">" : s.Status == "blocked" ? "!" : " ")}] {s.Index}. {s.Title}{(s.Note is null ? "" : $" ({s.Note})")}")); }
    public void Reset() { lock (_gate) _steps = []; }
    public IReadOnlyList<Tool> Definitions => [new("set_plan", "Write down an ordered plan before multi-step work.", new { type="object", properties=new { steps=new { type="array", items=new { type="string" }, maxItems=12 } }, required=new[]{"steps"} }), new("update_plan", "Update a plan step status.", new { type="object", properties=new { step=new { type="integer" }, status=new { type="string", @enum=new[]{"pending","in_progress","done","blocked"} }, note=new { type="string" } }, required=new[]{"step","status"} }), new("get_plan", "Read current plan and progress.", new { type="object" })];
    public Dictionary<string, Func<JsonElement, object?>> Functions => new() { ["set_plan"] = a => SetPlan(a.GetProperty("steps").EnumerateArray().Select(x => x.GetString() ?? "")), ["update_plan"] = a => UpdatePlan(a.GetProperty("step").GetInt32(), a.GetProperty("status").GetString()!, a.String("note")), ["get_plan"] = _ => GetPlan() };
    public sealed record PlanStep(int Index, string Title, string Status, string? Note);
}
