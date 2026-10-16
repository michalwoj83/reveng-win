using AiSlop.LocalLlm;
using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>What a request produced: the refreshed explanation, and the alias when it could be written and checked.</summary>
/// <param name="Description">The new explanation, with its name and brief. It always comes first: it is kept even when the alias fails.</param>
/// <param name="Alias">The refreshed alias implementation, or null when it could not be written.</param>
/// <param name="AliasProblem">Why there is no new alias (the model changed the code, the function is too long, the engine stopped), or null.</param>
public sealed record AskResult(FunctionDescription Description, FunctionAlias? Alias, string? AliasProblem);

/// <summary>
/// One request from the AI workspace. It is not a conversation: nothing is remembered between requests except what is stored for the function
/// (its explanation and alias), which the next request gets as context. A request is answered in the same two rounds as an explanation, with the
/// request given to both, so that everything the model generated for the function is refreshed to match it, and generated when it did not exist:
/// <list type="number">
/// <item>the explanation (with its name and brief, the request worked into it as extra context, never mentioned), written from the code, the usual context, the previous explanation and the request;</item>
/// <item>the alias implementation, from the code, the new explanation, the current alias and the request, checked against the original as always.</item>
/// </list>
/// A request that is not about the code is refused by the model in round one and nothing changes.
/// </summary>
public static class AskRunner
{
    /// <summary>Longest request accepted; a request is a sentence or two, not a document.</summary>
    public const int MaxRequestChars = 2000;

    /// <summary>The request tidied for the prompt: line breaks as <c>\n</c>, trimmed, no empty lines at the ends. Null when there is nothing in it.</summary>
    public static string? Clean(string? request)
    {
        if (string.IsNullOrWhiteSpace(request)) return null;
        var text = request.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return text.Length <= MaxRequestChars ? text : text[..MaxRequestChars];
    }

    /// <summary>
    /// Runs the two rounds for one request.
    /// </summary>
    /// <param name="context">The context an explanation normally gets (program card, facts, call sites, notes on neighbours).</param>
    /// <param name="previousDescription">The explanation the function already has, if any: context for the request.</param>
    /// <param name="previousAlias">The alias the function already has, if any: the starting point of the new one.</param>
    /// <param name="progress">Plain-language steps for the UI.</param>
    /// <exception cref="ArgumentException">The request is empty.</exception>
    /// <exception cref="RequestRefusedException">The request was not about the code; nothing was produced.</exception>
    /// <exception cref="DescriptionException">The model gave no usable explanation.</exception>
    /// <exception cref="LlmUnavailableException">The engine failed before round one finished.</exception>
    public static async Task<AskResult> RunAsync(
        ILlmProvider llm, FunctionToDescribe function, FunctionContext? context, string request,
        FunctionDescription? previousDescription, FunctionAlias? previousAlias,
        IReadOnlyDictionary<string, string> calleeAliases, int contextSize, IProgress<string>? progress, CancellationToken ct)
    {
        var ask = Clean(request) ?? throw new ArgumentException("The request is empty.", nameof(request));

        progress?.Report("Reading the code with your request…");
        var description = await FunctionDescriber.DescribeAsync(llm, function, contextSize, ct, context, ask, previousDescription);

        if (FunctionContextBuilder.IsLibraryCode(function.Code))
            return new AskResult(description, null, "This is library or runtime code, which keeps its names, so no alias was written.");
        if (description.Truncated)
            return new AskResult(description, null, "The function was cut to fit the context window, so an alias of all of it cannot be written. Raise the context size in Settings.");

        progress?.Report("Writing the alias code for your request…");
        try
        {
            var alias = await AliasWriter.WriteAsync(llm, function, description, calleeAliases, contextSize, ct, ask, previousAlias?.Code);
            return new AskResult(description, alias, null);
        }
        catch (AliasException ex) { return new AskResult(description, null, ex.Message); }
        catch (LlmContextExceededException ex) { return new AskResult(description, null, ex.Message); }
        catch (LlmUnavailableException ex) { return new AskResult(description, null, ex.Message); }
    }
}
