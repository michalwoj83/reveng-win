namespace ReaWin.Services;

/// <summary>
/// Token estimates for fitting a prompt into the context window without a tokenizer. Measured with the model's own vocabulary:
/// decompiled C takes about 2.4 characters per token (hex addresses and placeholder names split into many pieces), so code is
/// counted at 2.3 to err on the safe side; prose is counted at the 3.0 the shared package assumes.
/// </summary>
public static class ContextBudget
{
    public const double CodeCharsPerToken = 2.3;
    public const double TextCharsPerToken = 3.0;

    public static int CodeTokens(string code) => code.Length == 0 ? 0 : (int)Math.Ceiling(code.Length / CodeCharsPerToken);

    public static int TextTokens(string text) => text.Length == 0 ? 0 : (int)Math.Ceiling(text.Length / TextCharsPerToken);
}
