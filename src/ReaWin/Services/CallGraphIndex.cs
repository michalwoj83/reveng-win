using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>
/// Answers "who does this function call" and "who calls it" from the stored <see cref="CallGraph"/>. Only callees are stored:
/// REA's <c>procedure_callers</c> is exactly the inverse of <c>procedure_callees</c> (checked on a 3,039-function program), so
/// the callers are derived here once instead of being saved twice.
/// </summary>
public sealed class CallGraphIndex
{
    private static readonly string[] None = [];
    private readonly Dictionary<string, string[]> _callees;
    private readonly Dictionary<string, List<string>> _callers = new(StringComparer.Ordinal);

    public CallGraphIndex(CallGraph graph)
    {
        _callees = graph.Callees;
        // Addresses of functions in the order they were queried, so the callers of a function come out in a stable order.
        foreach (var (caller, targets) in _callees)
            foreach (var target in targets.Distinct(StringComparer.Ordinal))
            {
                if (!_callers.TryGetValue(target, out var list)) _callers[target] = list = new List<string>();
                list.Add(caller);
            }
    }

    /// <summary>How many functions the graph has callee lists for.</summary>
    public int Count => _callees.Count;

    /// <summary>True when <paramref name="address"/> was queried, so an empty answer means "calls nothing" and not "unknown".</summary>
    public bool Knows(string address) => _callees.ContainsKey(address);

    /// <summary>Direct callees in the order REA listed them, without repeats. Imports are included (<c>EXTERNAL:0x5a</c>).</summary>
    public IReadOnlyList<string> CalleesOf(string address) =>
        _callees.TryGetValue(address, out var list) ? list.Distinct(StringComparer.Ordinal).ToArray() : None;

    /// <summary>Direct callers, without repeats.</summary>
    public IReadOnlyList<string> CallersOf(string address) =>
        _callers.TryGetValue(address, out var list) ? list : None;
}
