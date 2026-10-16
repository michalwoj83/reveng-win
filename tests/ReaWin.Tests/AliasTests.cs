using AiSlop.LocalLlm;
using ReaWin.Models;
using ReaWin.Services;

namespace ReaWin.Tests;

/// <summary>
/// The alias implementation: the same code with readable names and comments. The point of these tests is the guarantee: whatever the
/// model writes, an alias that changed anything but names and comments is rejected, and nothing is saved.
/// </summary>
public class AliasTests
{
    // the function of the worked example in the alias prompt (Ghidra style, with its blank lines)
    private const string Original =
        "void FUN_1400027b0(void)\n\n{\n" +
        "  DAT_1401ac170 = FUN_1400e7870(0x18);\n" +
        "  *(longlong *)DAT_1401ac170 = DAT_1401ac170;\n" +
        "  *(longlong *)(DAT_1401ac170 + 8) = DAT_1401ac170;\n" +
        "  atexit(FUN_14011f590);\n" +
        "  return;\n}";

    private const string GoodAlias =
        "// Sets up an empty circular list at startup.\n" +
        "void init_global_list(void)\n\n{\n" +
        "  // allocate the list head\n" +
        "  global_list_head = FUN_1400e7870(0x18);\n" +
        "  *(longlong *)global_list_head = global_list_head;\n" +
        "  *(longlong *)(global_list_head + 8) = global_list_head;\n" +
        "  atexit(FUN_14011f590);   // free the list at exit\n" +
        "  return;\n}";

    private static AliasCheck Check(string alias, Dictionary<string, string>? callees = null) =>
        AliasVerifier.Verify(Original, alias, callees);

    // ---- the tokenizer -------------------------------------------------------------------------

    [Fact]
    public void The_tokenizer_splits_code_and_drops_comments_but_counts_them()
    {
        var t = CodeTokenizer.Tokenize("int f(char *p) // note\n{ /* two\nlines */ return p->x <<= 0x1fULL; }");

        Assert.Null(t.Error);
        Assert.Equal(2, t.Comments);
        Assert.Equal(new[] { "int", "f", "(", "char", "*", "p", ")", "{", "return", "p", "->", "x", "<<=", "0x1fULL", ";", "}" }, t.Tokens.Select(x => x.Text).ToArray());
    }

    [Fact]
    public void Strings_wide_strings_and_characters_are_single_tokens_with_their_escapes()
    {
        var t = CodeTokenizer.Tokenize("f(L\"a \\\"b\\\" // not a comment\", 'x', \"u8\", u8\"y\");");

        Assert.Equal(0, t.Comments);
        Assert.Equal(new[] { "f", "(", "L\"a \\\"b\\\" // not a comment\"", ",", "'x'", ",", "\"u8\"", ",", "u8\"y\"", ")", ";" }, t.Tokens.Select(x => x.Text).ToArray());
        Assert.Equal(CodeTokenKind.String, t.Tokens[2].Kind);
    }

    [Fact]
    public void A_variable_named_like_a_string_prefix_is_still_a_name()
    {
        var t = CodeTokenizer.Tokenize("uVar1 = Lvalue + u;");

        Assert.Equal(new[] { "uVar1", "=", "Lvalue", "+", "u", ";" }, t.Tokens.Select(x => x.Text).ToArray());
    }

    [Theory]
    [InlineData("int f(void) { /* never closed")]
    [InlineData("int f(void) { puts(\"never closed); }")]
    public void Code_that_cannot_be_read_is_reported_not_guessed_at(string code)
    {
        Assert.NotNull(CodeTokenizer.Tokenize(code).Error);
    }

    // ---- the verifier: what is the same code ---------------------------------------------------------

    [Fact]
    public void An_alias_with_readable_names_and_comments_is_the_same_code()
    {
        var check = Check(GoodAlias);

        Assert.True(check.IsFaithful, check.Reason);
        Assert.Equal("init_global_list", check.FunctionName);
        Assert.Equal("init_global_list", check.Renames["FUN_1400027b0"]);
        Assert.Equal("global_list_head", check.Renames["DAT_1401ac170"]);
        Assert.Equal(2, check.Renames.Count);
        Assert.Equal(3, check.Comments);
    }

    [Fact]
    public void The_worked_example_in_the_prompt_passes_the_rules_it_teaches()
    {
        var code = AliasWriter.ExtractCode(AliasWriter.ExampleReply);

        var check = Check(code);

        Assert.True(check.IsFaithful, check.Reason);
        Assert.Equal("init_global_list", check.FunctionName);
    }

    [Fact]
    public void Layout_may_change_and_a_comment_may_sit_anywhere()
    {
        var alias = "/* header */ void init_global_list ( void ) { global_list_head = FUN_1400e7870 ( 0x18 ) ; " +
                    "*( longlong * ) global_list_head = global_list_head ; *( longlong * )( global_list_head + 8 ) = global_list_head ; " +
                    "atexit ( FUN_14011f590 ) ; return ; }";

        Assert.True(Check(alias).IsFaithful);
    }

    [Theory]
    [InlineData("0x18", "0x20", "0x18")]                      // a number
    [InlineData("atexit(FUN_14011f590);", "atexit(FUN_14011f590); atexit(FUN_14011f590);", "more code")]
    [InlineData("(longlong *)DAT_1401ac170 = DAT_1401ac170;", "(longlong *)DAT_1401ac170 == DAT_1401ac170;", "an operator")]
    [InlineData("(longlong *)(DAT_1401ac170 + 8)", "(int *)(DAT_1401ac170 + 8)", "a type")]
    [InlineData("return;", "", "a dropped statement")]
    public void Any_change_to_the_code_itself_is_rejected(string from, string to, string what)
    {
        var changed = GoodAlias.Replace(from.Replace("DAT_1401ac170", "global_list_head"), to.Replace("DAT_1401ac170", "global_list_head"));
        if (changed == GoodAlias) changed = GoodAlias.Replace(from, to);
        Assert.NotEqual(GoodAlias, changed);

        var check = Check(changed);

        Assert.False(check.IsFaithful, what + " must not pass");
        Assert.NotEmpty(check.Reason);
    }

    [Fact]
    public void A_real_name_must_not_be_renamed()
    {
        var check = Check(GoodAlias.Replace("atexit(", "register_exit_handler("));

        Assert.False(check.IsFaithful);
        Assert.Contains("`atexit`", check.Reason);
        Assert.Contains("real name", check.Reason);
    }

    [Fact]
    public void Another_function_with_a_placeholder_name_keeps_it_unless_a_name_was_given()
    {
        var renamed = GoodAlias.Replace("FUN_1400e7870(", "allocate_node(");

        var without = Check(renamed);
        var wrongName = Check(renamed, new Dictionary<string, string> { ["FUN_1400e7870"] = "allocate_list_head" });
        var given = Check(GoodAlias.Replace("FUN_1400e7870(", "allocate_list_head("), new Dictionary<string, string> { ["FUN_1400e7870"] = "allocate_list_head" });

        Assert.False(without.IsFaithful);
        Assert.Contains("another function", without.Reason);
        Assert.False(wrongName.IsFaithful);
        Assert.Contains("allocate_list_head", wrongName.Reason);                   // says which name is allowed
        Assert.True(given.IsFaithful, given.Reason);
    }

    [Fact]
    public void A_name_must_be_changed_the_same_way_everywhere()
    {
        var check = Check(GoodAlias.Replace("(global_list_head + 8)", "(list_head + 8)"));

        Assert.False(check.IsFaithful);
        Assert.Contains("the same way everywhere", check.Reason);
    }

    [Fact]
    public void Two_different_things_cannot_get_the_same_name()
    {
        var check = Check(GoodAlias.Replace("FUN_1400e7870(", "global_list_head("));    // would make the callee and the global one name

        Assert.False(check.IsFaithful);
    }

    [Fact]
    public void A_new_name_cannot_be_a_name_the_code_already_uses_for_something_else()
    {
        // two different variables both renamed to the same word
        var original = "void FUN_1000(int param_1)\n{\n  int iVar1;\n  iVar1 = param_1 + 1;\n  puts(\"x\");\n}";
        var alias = "// c\nvoid add_one(int count)\n{\n  int count;\n  count = count + 1;\n  puts(\"x\");\n}";

        var check = AliasVerifier.Verify(original, alias);

        Assert.False(check.IsFaithful);
        Assert.Contains("share a name", check.Reason);
    }

    [Theory]
    [InlineData("tree node", "snake_case")]
    [InlineData("TreeNode", "snake_case")]
    [InlineData("tree__node", "snake_case")]
    [InlineData("_tree_node", "snake_case")]
    [InlineData("int", "keyword")]
    [InlineData("undefined8", "keyword")]
    [InlineData("local_28", "placeholder")]
    [InlineData("param_2", "placeholder")]
    [InlineData("x", null)]
    [InlineData("tree_node", null)]
    [InlineData("node2", null)]
    public void New_names_must_be_readable_snake_case_identifiers(string name, string? reasonContains)
    {
        var bad = AliasVerifier.BadName(name);

        if (reasonContains is null) Assert.Null(bad);
        else Assert.Contains(reasonContains, bad ?? "");
    }

    [Fact]
    public void A_name_with_a_space_in_the_alias_is_rejected_because_it_is_not_even_one_identifier()
    {
        // "tree node" is two tokens, so the token count differs from the original: the rewrite is not the same code
        var check = Check(GoodAlias.Replace("global_list_head", "list head"));

        Assert.False(check.IsFaithful);
    }

    [Fact]
    public void The_function_must_get_a_readable_name_and_the_alias_must_have_comments()
    {
        var stillPlaceholder = Check(GoodAlias.Replace("init_global_list", "FUN_1400027b0"));
        var noComments = Check("void init_global_list(void)\n\n{\n  global_list_head = FUN_1400e7870(0x18);\n" +
                               "  *(longlong *)global_list_head = global_list_head;\n  *(longlong *)(global_list_head + 8) = global_list_head;\n" +
                               "  atexit(FUN_14011f590);\n  return;\n}");

        Assert.False(stillPlaceholder.IsFaithful);
        Assert.Contains("readable name", stillPlaceholder.Reason);
        Assert.False(noComments.IsFaithful);
        Assert.Contains("no comments", noComments.Reason);
    }

    [Fact]
    public void A_function_that_already_has_a_real_name_may_keep_it_and_unrenamed_variables_are_counted()
    {
        var original = "int parse_header(char *param_1)\n{\n  int iVar1;\n  iVar1 = strlen(param_1);\n  return iVar1;\n}";
        var alias = "// parses\nint parse_header(char *text)\n{\n  int iVar1;   // length\n  iVar1 = strlen(text);\n  return iVar1;\n}";

        var check = AliasVerifier.Verify(original, alias);

        Assert.True(check.IsFaithful, check.Reason);
        Assert.Equal("parse_header", check.FunctionName);
        Assert.Equal(1, check.Unrenamed);                  // iVar1 was left as it was: best effort for variables, never for the code
        Assert.Equal("text", check.Renames["param_1"]);
    }

    [Fact]
    public void A_reply_that_ends_early_or_runs_on_says_where()
    {
        var early = Check(GoodAlias.Replace("  return;\n}", "}"));
        var longer = Check(GoodAlias + "\nvoid extra(void) { }");

        Assert.False(early.IsFaithful);
        Assert.False(longer.IsFaithful);
        Assert.Contains("more code", longer.Reason);
    }

    [Fact]
    public void Code_that_is_not_a_function_cannot_be_given_an_alias()
    {
        var check = AliasVerifier.Verify("x = 1;", "// c\ny = 1;");

        Assert.False(check.IsFaithful);
        Assert.Contains("function signature", check.Reason);
    }

    [Fact]
    public void Placeholder_names_are_recognized_and_real_ones_are_not()
    {
        foreach (var name in new[] { "param_1", "local_28", "local_res8", "iVar1", "pcVar12", "uVar2", "ppuVar3", "DAT_1401ac170", "_DAT_14017d3a8", "FUN_140001000",
                     "thunk_FUN_1400ede10", "LAB_1400123", "PTR_DAT_140147470", "unaff_RBX", "in_RAX", "extraout_RAX", "uStack_28", "s_Hello_140001000" })
            Assert.True(AliasVerifier.IsPlaceholder(name), name);
        foreach (var name in new[] { "atexit", "CreateFileW", "__security_cookie", "entry", "malloc", "param", "int", "init_global_list" })
            Assert.False(AliasVerifier.IsPlaceholder(name), name);
    }

    // ---- reading the reply ---------------------------------------------------------------------------

    [Theory]
    [InlineData("```c\nint f(void){}\n```", "int f(void){}")]
    [InlineData("Here you go:\n```\nint f(void){}\n```\nDone.", "int f(void){}")]
    [InlineData("<think>hmm</think>\n```c\nint f(void){}\n```", "int f(void){}")]
    [InlineData("int f(void){}", "int f(void){}")]
    [InlineData("```c\nint f(void){}", "int f(void){}")]            // a reply that was cut before the closing fence
    public void The_code_is_taken_out_of_the_reply(string reply, string expected)
    {
        Assert.Equal(expected, AliasWriter.ExtractCode(reply));
    }

    // ---- the request ---------------------------------------------------------------------------------

    private static FunctionDescription Description() => new("Name: Initialize global list\nBrief: Creates a list.\nPurpose: Sets up a list.", 100, 100, "m", DateTime.UtcNow);

    [Fact]
    public void The_request_carries_the_code_the_description_and_the_names_chosen_for_callees()
    {
        var built = AliasWriter.Build(new FunctionToDescribe("FUN_1400027b0", "0x1400027b0", Original), Description(),
            new Dictionary<string, string> { ["FUN_1400e7870"] = "allocate_list_head" }, 32768);

        var m = built.Request.Messages;
        Assert.Equal(new[] { "system", "user", "assistant", "user" }, m.Select(x => x.Role).ToArray());
        Assert.Contains("exactly the same code", m[0].Content);
        Assert.Contains("snake_case", m[0].Content);
        Assert.Contains("tree_node", m[0].Content);                                   // the identifier rule, with its example
        Assert.Contains("Purpose: Sets up a list.", m[3].Content);                    // the description is in the context
        Assert.Contains("FUN_1400e7870 -> allocate_list_head", m[3].Content);
        Assert.Contains("DAT_1401ac170 = FUN_1400e7870(0x18);", m[3].Content);        // the original code
        Assert.Equal("none", built.Request.Extra!["reasoning_effort"]);
        Assert.True(built.Request.MaxTokens >= 256);
    }

    [Fact]
    public void A_retry_carries_the_rejected_reply_and_the_reason()
    {
        var built = AliasWriter.Build(new FunctionToDescribe("FUN_1", "0x1", Original), Description(), new Dictionary<string, string>(), 32768,
            rejection: "the original has `0x18` here and the alias has `0x20`", previousReply: "bad code");

        var m = built.Request.Messages;
        Assert.Equal(6, m.Count);
        Assert.Equal("bad code", m[4].Content);
        Assert.Contains("was rejected because the original has `0x18`", m[5].Content);
    }

    [Fact]
    public void A_function_too_long_to_rewrite_in_the_window_is_refused_with_a_reason()
    {
        var huge = "void FUN_1(void)\n{\n" + string.Join("\n", Enumerable.Range(0, 4000).Select(i => $"  local_{i:x} = FUN_14000{i:x5}(param_1, {i});")) + "\n}";

        var ex = Assert.Throws<AliasException>(() => AliasWriter.Build(new FunctionToDescribe("FUN_1", "0x1", huge), Description(), new Dictionary<string, string>(), 32768));

        Assert.Contains("too long", ex.Message);
    }

    // ---- the whole round with a scripted model ---------------------------------------------------------

    private sealed class ScriptedLlm(params string[] replies) : ILlmProvider
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
        private static async IAsyncEnumerable<string> One(string text) { await Task.Yield(); yield return text; }
        public void Dispose() { }
    }

    [Fact]
    public async Task A_faithful_reply_becomes_the_alias_on_the_first_try()
    {
        var llm = new ScriptedLlm("```c\n" + GoodAlias + "\n```");

        var alias = await AliasWriter.WriteAsync(llm, new FunctionToDescribe("FUN_1400027b0", "0x1400027b0", Original), Description(),
            new Dictionary<string, string>(), 32768);

        Assert.Single(llm.Requests);
        Assert.Equal("init_global_list", alias.Name);
        Assert.Equal(GoodAlias, alias.Code);                                           // the model's text, comments and all
        Assert.Equal("global_list_head", alias.Renames["DAT_1401ac170"]);
        Assert.Equal(3, alias.Comments);
        Assert.Equal(AiProfile.ModelLabel, alias.Model);
    }

    [Fact]
    public async Task An_unfaithful_reply_is_retried_once_with_the_reason_and_a_good_second_reply_is_kept()
    {
        var llm = new ScriptedLlm("```c\n" + GoodAlias.Replace("0x18", "0x20") + "\n```", "```c\n" + GoodAlias + "\n```");

        var alias = await AliasWriter.WriteAsync(llm, new FunctionToDescribe("FUN_1400027b0", "0x1400027b0", Original), Description(),
            new Dictionary<string, string>(), 32768);

        Assert.Equal(2, llm.Requests.Count);
        Assert.Contains("was rejected because", llm.Requests[1].Messages[^1].Content);
        Assert.Contains("0x18", llm.Requests[1].Messages[^1].Content);                 // the model is told exactly what it changed
        Assert.Equal("init_global_list", alias.Name);
    }

    [Fact]
    public async Task Two_unfaithful_replies_save_nothing_and_say_why()
    {
        var llm = new ScriptedLlm("```c\n" + GoodAlias.Replace("0x18", "0x20") + "\n```");

        var ex = await Assert.ThrowsAsync<AliasException>(() => AliasWriter.WriteAsync(llm,
            new FunctionToDescribe("FUN_1400027b0", "0x1400027b0", Original), Description(), new Dictionary<string, string>(), 32768));

        Assert.Equal(AliasWriter.MaxAttempts, llm.Requests.Count);
        Assert.Contains("did not produce the same code", ex.Message);
        Assert.Contains("Nothing was saved", ex.Message);
    }

    [Fact]
    public async Task An_empty_reply_counts_as_a_failed_try()
    {
        var llm = new ScriptedLlm("", "```c\n" + GoodAlias + "\n```");

        var alias = await AliasWriter.WriteAsync(llm, new FunctionToDescribe("FUN_1400027b0", "0x1400027b0", Original), Description(),
            new Dictionary<string, string>(), 32768);

        Assert.Equal(2, llm.Requests.Count);
        Assert.NotNull(alias);
    }

    // ---- stored with the project ---------------------------------------------------------------------

    [Fact]
    public void An_alias_round_trips_in_a_project_and_an_older_project_has_none()
    {
        var alias = new FunctionAlias(GoodAlias, "init_global_list", new Dictionary<string, string> { ["FUN_1400027b0"] = "init_global_list" }, 3, "m", DateTime.UtcNow);
        var project = new ProjectFile { Overview = new OverviewResult("e", "s", "pe", "x", "0x1", 1, 0, [], []), Aliases = { ["0x1400027b0"] = alias } };

        var json = System.Text.Json.JsonSerializer.Serialize(project);
        var back = System.Text.Json.JsonSerializer.Deserialize<ProjectFile>(json)!;
        var old = System.Text.Json.JsonSerializer.Deserialize<ProjectFile>(
            "{\"Version\":1,\"Overview\":{\"EvidenceId\":\"e\",\"Sha256\":\"s\",\"Format\":\"pe\",\"Architecture\":\"x\",\"ImageBase\":\"0x1\",\"ProcedureCount\":1,\"StringCount\":0,\"Segments\":[],\"Limitations\":[]}}")!;

        Assert.Equal("init_global_list", back.Aliases["0x1400027b0"].Name);
        Assert.Equal(GoodAlias, back.Aliases["0x1400027b0"].Code);
        Assert.Empty(old.Aliases);
    }
}
