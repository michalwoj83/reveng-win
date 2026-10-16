using AiSlop.LocalLlm;
using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>The model gave nothing usable (empty, or stuck repeating itself). The message says what to do.</summary>
public sealed class DescriptionException(string message) : Exception(message);

/// <summary>Asks the local model to describe one decompiled function.</summary>
public static class FunctionDescriber
{
    /// <summary>
    /// Sends the function to the model and returns its description. The description is an <b>inference</b>: it is stored
    /// and shown apart from REA's observations and labeled with the model and whether the code was cut. <paramref name="context"/>
    /// is what the program and the function's neighbours add to the code; it is kept with the description.
    /// </summary>
    /// <exception cref="LlmUnavailableException">The AI engine is not available or answered with an error.</exception>
    /// <exception cref="LlmContextExceededException">The context window is too small for even the instructions.</exception>
    /// <exception cref="DescriptionException">The reply was empty or repetitive.</exception>
    public static async Task<FunctionDescription> DescribeAsync(
        ILlmProvider llm, FunctionToDescribe function, int contextSize, CancellationToken ct = default,
        FunctionContext? context = null)
    {
        var built = FunctionPrompts.Build(function, contextSize, context);
        var raw = await LlmCompletion.CollectAsync(llm.StreamCompletionAsync(built.Request, ct), ct);
        var text = FunctionPrompts.FormatReply(raw);

        if (text.Length == 0)
            throw new DescriptionException("The model did not return a description. Try again.");
        if (FunctionPrompts.LooksRunaway(text))
            throw new DescriptionException("The model got stuck repeating itself, so its reply was discarded. Try again.");

        return new FunctionDescription(text, built.CodeCharsSent, built.CodeCharsTotal, AiProfile.ModelLabel, DateTime.UtcNow)
        {
            Context = context?.ForStorage ?? "",
            NeighbourAddresses = context?.NeighbourAddresses.ToArray() ?? [],
        };
    }
}
