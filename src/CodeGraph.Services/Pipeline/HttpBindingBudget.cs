using CodeGraph.Services.Configuration;
using System.Collections;
using System.Text.Json;

namespace CodeGraph.Services.Pipeline;

/// <summary>One cancellable deterministic allowance shared by every HTTP planning phase.
/// Exhaustion is a refusal, never a successful empty match.</summary>
internal sealed class HttpBindingBudget
{
    private readonly long _maximum;
    private readonly CancellationToken _ct;
    private readonly Dictionary<(string? Service, string? Dto, string? Target, HashSet<string> Names), string?> _hints = [];
    public long WorkUsed { get; private set; }
    public int MaximumDepth { get; }
    public HttpBindingBudget(IndexingOptions? options = null, CancellationToken ct = default)
    {
        options ??= new();
        _maximum = Math.Max(0, options.MaxHttpBindingWork);
        MaximumDepth = Math.Clamp(options.MaxHttpBindingDepth, 1, 512);
        _ct = ct;
    }
    public void Charge(long amount = 1)
    {
        _ct.ThrowIfCancellationRequested();
        if (amount < 0 || amount > _maximum - WorkUsed)
            throw new InvalidOperationException("HTTP binding work budget exhausted; prior graph and bindings must be retained.");
        WorkUsed += amount;
    }
    public void Text(params string?[] values)
    {
        Charge();
        foreach (var value in values) if (value is not null) Charge((long)value.Length * 8 + 1);
    }
    public void Properties<T>(IEnumerable<KeyValuePair<string, T>> properties)
    {
        foreach (var pair in properties) { Text(pair.Key); Value(pair.Value,0); }
    }
    private void Value(object? value, int depth)
    {
        Depth(depth);
        switch (value)
        {
            case null: return;
            case string text: Text(text); return;
            case JsonElement json:
                if (json.ValueKind == JsonValueKind.Object) { foreach (var property in json.EnumerateObject()) { Text(property.Name); Value(property.Value,depth+1); } }
                else if (json.ValueKind == JsonValueKind.Array) { foreach (var item in json.EnumerateArray()) Value(item,depth+1); }
                else Text(json.ToString());
                return;
            case IDictionary dictionary:
                foreach (DictionaryEntry pair in dictionary) { Text(pair.Key?.ToString()); Value(pair.Value,depth+1); }
                return;
            case IEnumerable sequence:
                foreach (var item in sequence) Value(item,depth+1);
                return;
            default: Text(value.ToString()); return;
        }
    }
    public string? Hint(string? service, string? dto, string? target, HashSet<string> names, Func<string?> resolve)
    {
        Text(service,dto,target);
        var key=(service,dto,target,names);
        if (_hints.TryGetValue(key,out var value)) return value;
        return _hints[key]=resolve();
    }
    public void Depth(int depth)
    {
        Charge();
        if (depth > MaximumDepth)
            throw new InvalidOperationException("HTTP binding path depth budget exhausted; prior graph and bindings must be retained.");
    }
}
