using AiSlop.LocalLlm;

namespace ReaWin.Tests;

/// <summary>A model that answers from a script: the n-th request gets the n-th reply (the last one repeats). It records what it was asked.</summary>
internal sealed class ScriptedLlm(params string[] replies) : ILlmProvider
{
    private int _next;

    public List<LlmRequest> Requests { get; } = new();
    public string Name => "scripted";
    public string? Fault => null;
    public LlmState State => LlmState.Ready;
    public event Action? StateChanged { add { } remove { } }

    public Task<LlmAvailability> CheckAsync(CancellationToken cancellationToken = default) => Task.FromResult(new LlmAvailability(true, null));

    public Task WarmUpAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public IAsyncEnumerable<string> StreamCompletionAsync(LlmRequest request, CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        return One(replies[Math.Min(_next++, replies.Length - 1)]);
    }

    private static async IAsyncEnumerable<string> One(string text)
    {
        await Task.Yield();
        yield return text;
    }

    public void Dispose() { }
}
