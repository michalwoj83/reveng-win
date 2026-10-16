using AiSlop.LocalLlm;
using ReaWin.Models;

namespace ReaWin.Services;

/// <summary>The model gave nothing usable (empty, or stuck repeating itself). The message says what to do.</summary>
public sealed class DescriptionException(string message) : Exception(message);

/// <summary>The request was not about the code, so the model declined it. Nothing was changed. The message is the model's reason.</summary>
public sealed class RequestRefusedException(string message) : Exception(message);

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
        FunctionContext? context = null, string? request = null, FunctionDescription? previous = null)
    {
        var built = FunctionPrompts.Build(function, contextSize, context, request, previous);
        var raw = await LlmCompletion.CollectAsync(llm.StreamCompletionAsync(built.Request, ct), ct);
        var text = FunctionPrompts.FormatReply(raw);

        if (text.Length == 0)
            throw new DescriptionException("The model did not return a description. Try again.");
        if (!string.IsNullOrWhiteSpace(request) && FunctionPrompts.TryReadRefusal(text, out var reason))
            throw new RequestRefusedException(reason);
        if (FunctionPrompts.LooksRunaway(text))
            throw new DescriptionException("The model got stuck repeating itself, so its reply was discarded. Try again.");

        var artifacts = FunctionPrompts.ExtractArtifacts(text);
        return new FunctionDescription(text, built.CodeCharsSent, built.CodeCharsTotal, AiProfile.ModelLabel, DateTime.UtcNow)
        {
            Title = artifacts.Title,
            Brief = artifacts.Brief,
            Request = string.IsNullOrWhiteSpace(request) ? null : request.Trim(),
            Context = context?.ForStorage ?? "",
            NeighbourAddresses = context?.NeighbourAddresses.ToArray() ?? [],
        };
    }
}
