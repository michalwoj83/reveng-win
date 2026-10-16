using AiSlop.LocalLlm;
using ReaWin.Services;

namespace ReaWin.Tests;

public class FunctionPromptsTests
{
    private const string Small =
        "\r\nvoid FUN_140001000(void)\r\n\r\n{\r\n  FUN_1400e7870(0x40);\r\n  return;\r\n}\r\n\r\n";

    private static FunctionToDescribe Fn(string code) => new("FUN_140001000", "0x140001000", code);

    private static string LongCode(int lines) =>
        string.Join("\n", Enumerable.Range(0, lines).Select(i => $"  local_{i:x} = FUN_14000{i:x5}(param_1, {i});"));

    // ---- the request ----------------------------------------------------------------------------

    [Fact]
    public void The_request_is_system_then_a_worked_example_then_the_real_function()
    {
        var built = FunctionPrompts.Build(Fn(Small), 8192);
        var m = built.Request.Messages;

        Assert.Equal(new[] { "system", "user", "assistant", "user" }, m.Select(x => x.Role).ToArray());
        Assert.Contains("Ghidra", m[0].Content);
        Assert.Contains("FUN_1400027b0", m[1].Content);     // the example input
        Assert.StartsWith("Name:", m[2].Content);            // the example answer shows the form
        Assert.Contains("Name: FUN_140001000", m[3].Content);
        Assert.Contains("Address: 0x140001000", m[3].Content);
        Assert.Contains("FUN_1400e7870(0x40);", m[3].Content);
    }

    [Fact]
    public void The_instructions_keep_what_the_code_does_apart_from_guesses_and_forbid_inventing()
    {
        var system = FunctionPrompts.Build(Fn(Small), 8192).Request.Messages[0].Content;

        Assert.Contains("placeholders", system);                 // FUN_/DAT_/param_ names are not the author's
        Assert.Contains("Never invent", system);
        Assert.Contains("probably", system);                      // inference is worded as inference
        Assert.Contains("Unclear", system);                       // what cannot be known is said
        Assert.Contains("Plain text only", system);
    }

    [Fact]
    public void The_model_is_told_not_to_think_and_gets_the_model_cards_non_thinking_sampling()
    {
        var request = FunctionPrompts.Build(Fn(Small), 8192).Request;

        Assert.Equal("none", request.Extra!["reasoning_effort"]);
        Assert.Equal(0.8, request.Extra["top_p"]);
        Assert.Equal(20, request.Extra["top_k"]);
        Assert.False(request.Extra.ContainsKey("presence_penalty"));   // describing code repeats identifiers on purpose
        Assert.Equal(AiProfile.ReplyTokens, request.MaxTokens);
        Assert.Equal(AiProfile.Temperature, request.Temperature);
    }

    [Fact]
    public void Code_is_sent_with_plain_line_breaks_and_without_the_blank_lines_REA_puts_around_it()
    {
        var user = FunctionPrompts.Build(Fn(Small), 8192).Request.Messages[3].Content;

        Assert.DoesNotContain("\r", user);
        var code = user[(user.IndexOf("Code:\n", StringComparison.Ordinal) + "Code:\n".Length)..];
        Assert.StartsWith("void FUN_140001000", code);
        Assert.EndsWith("}", code);
    }

    // ---- long functions -------------------------------------------------------------------------

    [Fact]
    public void A_short_function_is_sent_whole_and_the_request_fits_the_window()
    {
        var built = FunctionPrompts.Build(Fn(Small), 8192);

        Assert.False(built.Truncated);
        Assert.Equal(built.CodeCharsTotal, built.CodeCharsSent);
        Assert.True(LlmContext.Fits(built.Request, 8192));
    }

    [Fact]
    public void A_function_too_long_for_the_window_is_cut_at_a_line_break_and_says_so()
    {
        var code = LongCode(2000);                      // far more than 8192 tokens of code
        var built = FunctionPrompts.Build(Fn(code), 8192);

        Assert.True(built.Truncated);
        Assert.True(built.CodeCharsSent < built.CodeCharsTotal);
        Assert.True(LlmContext.Fits(built.Request, 8192), "a truncated request must fit, or it would fail at the server");

        var user = built.Request.Messages[3].Content;
        Assert.Contains("did not fit the context window", user);              // the model is told the end is missing
        var sentCode = user[(user.IndexOf("Code:\n", StringComparison.Ordinal) + "Code:\n".Length)..];
        var beforeMarker = sentCode[..sentCode.IndexOf("\n/* ...", StringComparison.Ordinal)];
        Assert.StartsWith(beforeMarker, code);                                 // a prefix of the original, never altered
        Assert.EndsWith(";", beforeMarker);                                    // cut after a whole statement, not in the middle of one
        Assert.Equal(beforeMarker.Length, built.CodeCharsSent);
    }

    [Fact]
    public void A_bigger_window_lets_more_of_the_function_through()
    {
        var code = LongCode(2000);

        var small = FunctionPrompts.Build(Fn(code), 8192);
        var big = FunctionPrompts.Build(Fn(code), 32768);

        Assert.True(big.CodeCharsSent > small.CodeCharsSent * 2);
        Assert.True(LlmContext.Fits(big.Request, 32768));
    }

    [Fact]
    public void A_window_too_small_for_the_instructions_is_refused_with_a_message_about_the_setting()
    {
        var ex = Assert.Throws<LlmContextExceededException>(() => FunctionPrompts.Build(Fn(Small), 1024));

        Assert.Contains("context size", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1024, ex.ContextSize);
    }

    // ---- the reply ------------------------------------------------------------------------------

    [Fact]
    public void A_reasoning_trace_is_removed_whole_even_when_it_was_never_closed()
    {
        Assert.Equal("Summary: x", FunctionPrompts.FormatReply("<think>let me look\nat this</think>\nSummary: x"));
        Assert.Equal("", FunctionPrompts.FormatReply("<think>the reply was cut while the model was still thinking"));
    }

    [Fact]
    public void Markdown_the_panel_cannot_show_is_removed_but_the_words_are_kept()
    {
        var text = FunctionPrompts.FormatReply("## Summary\n**Summary:** Frees the list.\n\n\n\nBehavior:\n- walks it");

        Assert.Equal("Summary\nSummary: Frees the list.\n\nBehavior:\n- walks it", text);
    }

    [Fact]
    public void A_reply_wrapped_in_a_code_fence_is_unwrapped_and_line_breaks_are_normalized()
    {
        Assert.Equal("Summary: a\nInputs: none.", FunctionPrompts.FormatReply("```text\r\nSummary: a\r\nInputs: none.\r\n```"));
    }

    [Theory]
    [InlineData("Summary: Calls FUN_1400e7870 and writes DAT_1401ac170.")]
    [InlineData("Calls: __security_check_cookie (checks the stack cookie) and __scrt_common_main_seh.")]
    [InlineData("Behavior:\n- Casts param_1 to (longlong **) and then to (char **) before reading it.")]
    [InlineData("- Stores *(int *)(param_1 + 8) in DAT_1401ac170 and **ppvVar2 in local_28.")]
    public void Identifiers_and_C_pointer_types_are_never_mistaken_for_markdown(string reply)
    {
        Assert.Equal(reply, FunctionPrompts.FormatReply(reply));
    }

    [Fact]
    public void Real_bold_markers_are_still_removed_next_to_pointer_types()
    {
        var text = FunctionPrompts.FormatReply("**Behavior:** casts to (char **) first.");

        Assert.Equal("Behavior: casts to (char **) first.", text);
    }

    [Fact]
    public void A_looping_reply_is_recognised_and_a_normal_one_is_not()
    {
        var loop = string.Join("\n", Enumerable.Repeat("- Calls FUN_1400e7870 again", 4));
        Assert.True(FunctionPrompts.LooksRunaway(loop));

        var normal = "Summary: x\nBehavior:\n- Calls A\n- Calls B\n- Calls A\nCalls: A, B";
        Assert.False(FunctionPrompts.LooksRunaway(normal));
    }
}
