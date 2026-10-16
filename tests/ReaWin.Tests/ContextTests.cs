using AiSlop.LocalLlm;
using ReaWin.Models;
using ReaWin.Services;

namespace ReaWin.Tests;

/// <summary>The call graph, the program card, the neighbour summaries and the way they are fitted into the prompt.</summary>
public class ContextTests
{
    private static CallGraph Graph() => new()
    {
        Callees =
        {
            ["0x1000"] = ["0x2000", "0x3000", "EXTERNAL:0x5a", "0x2000"],   // a repeated callee
            ["0x2000"] = ["EXTERNAL:0x5a"],
            ["0x3000"] = [],
        },
    };

    // ---- the call graph ---------------------------------------------------------------------------

    [Fact]
    public void Callers_are_derived_from_the_stored_callees_without_repeats()
    {
        var index = new CallGraphIndex(Graph());

        Assert.Equal(new[] { "0x2000", "0x3000", "EXTERNAL:0x5a" }, index.CalleesOf("0x1000"));
        Assert.Equal(new[] { "0x1000" }, index.CallersOf("0x2000"));
        Assert.Equal(new[] { "0x1000", "0x2000" }, index.CallersOf("EXTERNAL:0x5a"));   // an import's callers
        Assert.Empty(index.CallersOf("0x1000"));
    }

    [Fact]
    public void A_function_without_callees_is_known_but_a_function_never_queried_is_not()
    {
        var index = new CallGraphIndex(Graph());

        Assert.True(index.Knows("0x3000"));
        Assert.Empty(index.CalleesOf("0x3000"));    // "calls nothing"
        Assert.False(index.Knows("0x9999"));        // "unknown": not the same thing
        Assert.Empty(index.CalleesOf("0x9999"));
    }

    // ---- the program card ---------------------------------------------------------------------------

    private static OverviewResult Overview() => new("ev", "sha", "pe", "x86_64", "0x140000000", 5, 4, [], []);

    private static readonly ProcedureInfo[] Procs =
    [
        new("0x140001000", "entry"),
        new("EXTERNAL:0x1", "KERNEL32.DLL::CreateFileW"),
        new("EXTERNAL:0x2", "KERNEL32.DLL::ReadFile"),
        new("EXTERNAL:0x3", "ADVAPI32.DLL::RegOpenKeyExW"),
        new("EXTERNAL:0x4", "WS2_32.DLL::connect"),
    ];

    private static readonly StringInfo[] Strings =
    [
        new("0x140002000", "Software\\Sysinternals\\Process Explorer"),
        new("0x140002100", "ab"),                                         // too short
        new("0x140002200", "Cannot open the file %s"),
        new("0x140002300", new string('x', 500)),                         // an embedded license or blob
    ];

    [Fact]
    public void The_card_names_the_file_the_imports_and_what_they_suggest_without_claiming_use()
    {
        var card = ProgramCard.Build(Overview(), @"C:\Tools\procexp64.exe", Procs, Strings, null);

        Assert.Contains("procexp64.exe (pe, x86_64, image base 0x140000000)", card);
        Assert.Contains("KERNEL32.DLL (2)", card);
        Assert.Contains("file system (CreateFileW, ReadFile)", card);
        Assert.Contains("registry (RegOpenKeyExW)", card);
        Assert.Contains("network (connect)", card);
        Assert.Contains("a hint from API names, not proof of use", card);
    }

    [Fact]
    public void The_card_lists_distinctive_strings_and_skips_blobs_and_scraps()
    {
        var card = ProgramCard.Build(Overview(), @"C:\a.exe", Procs, Strings, null);

        Assert.Contains("\"Software\\Sysinternals\\Process Explorer\"", card);
        Assert.Contains("\"Cannot open the file %s\"", card);
        Assert.DoesNotContain("xxxxxxxxxx", card);
        Assert.DoesNotContain("\"ab\"", card);
    }

    [Fact]
    public void The_card_is_the_same_every_time_and_the_compact_one_leaves_out_the_strings()
    {
        var a = ProgramCard.Build(Overview(), @"C:\a.exe", Procs, Strings, null);
        var b = ProgramCard.Build(Overview(), @"C:\a.exe", Procs, Strings, null);
        var compact = ProgramCard.Build(Overview(), @"C:\a.exe", Procs, Strings, null, compact: true);

        Assert.Equal(a, b);     // a stable prefix is what lets a server reuse its work
        Assert.DoesNotContain("Some strings", compact);
        Assert.True(compact.Length < a.Length);
    }

    [Fact]
    public void The_card_adds_the_subsystem_and_version_resource_when_the_header_was_read()
    {
        var pe = new PeFileInfo(@"C:\a.exe", "a.exe", 1, false, false, "Amd64", "WindowsGui", 0, [], [],
            "Process Explorer", "Sysinternals Process Explorer", "Sysinternals - www.sysinternals.com", "procexp.exe", null);

        var card = ProgramCard.Build(Overview(), @"C:\a.exe", Procs, Strings, pe);

        Assert.Contains("Windows GUI program, Amd64", card);
        Assert.Contains("product \"Process Explorer\"", card);
        Assert.Contains("company \"Sysinternals - www.sysinternals.com\"", card);
    }

    // ---- facts and call sites -----------------------------------------------------------------------

    [Fact]
    public void Library_code_is_recognized_by_ghidras_own_comment_at_the_top()
    {
        Assert.True(FunctionContextBuilder.IsLibraryCode("\n/* Library Function - Single Match\n    __acrt_x\n */\nvoid f(void){}"));
        Assert.False(FunctionContextBuilder.IsLibraryCode("void f(void){}" + new string((char)32, 700) + "Library Function"));   // only the top of the code counts
        Assert.False(FunctionContextBuilder.IsLibraryCode(null));
    }

    [Fact]
    public void The_call_site_excerpt_shows_the_lines_around_each_call_and_merges_neighbours()
    {
        var code = string.Join("\n", Enumerable.Range(1, 30).Select(i => i is 10 or 12 ? $"  FUN_1400027b0({i});" : $"  other({i});"));

        var excerpt = FunctionContextBuilder.CallSiteExcerpt(code, "FUN_1400027b0");

        Assert.Contains("FUN_1400027b0(10);", excerpt);
        Assert.Contains("FUN_1400027b0(12);", excerpt);
        Assert.Contains("other(7);", excerpt);                          // three lines before the first call
        Assert.DoesNotContain("other(1);", excerpt);
        Assert.Single(excerpt.Split('\n'), l => l.Contains("other(11);"));   // the two windows overlap and are merged, not repeated
    }

    [Fact]
    public void A_name_that_is_only_part_of_another_name_is_not_a_call_site()
    {
        Assert.Equal("", FunctionContextBuilder.CallSiteExcerpt("  FUN_1400027b0x(1);\n  my_FUN_1400027b0(2);", "FUN_1400027b0"));
        Assert.Equal("", FunctionContextBuilder.CallSiteExcerpt("  (*pcVar1)();", "FUN_1400027b0"));   // an indirect call
    }

    [Fact]
    public void The_facts_say_what_is_known_and_what_is_not()
    {
        var target = new ProcedureInfo("0x1000", "FUN_1000");
        var caller = new FunctionContextBuilder.Neighbour("0x500", "FUN_500", false);
        var callee = new FunctionContextBuilder.Neighbour("0x2000", "__acrt_x", true);

        var facts = FunctionContextBuilder.Facts(target, false, true, [caller], [callee], ["KERNEL32.DLL::CloseHandle"]);
        Assert.Contains("Function: FUN_1000 (0x1000)", facts);
        Assert.Contains("Called by 1: FUN_500 (0x500)", facts);
        Assert.Contains("__acrt_x (0x2000) [library]", facts);
        Assert.Contains("KERNEL32.DLL::CloseHandle", facts);

        var none = FunctionContextBuilder.Facts(target, false, true, [], [], []);
        Assert.Contains("no direct callers", none);     // reached through a pointer, or unused: said, not guessed
        Assert.Contains("Calls: nothing.", none);

        var unknown = FunctionContextBuilder.Facts(target, false, false, [], [], []);
        Assert.Contains("Call information: not available.", unknown);
        Assert.DoesNotContain("no direct callers", unknown);
    }

    [Fact]
    public void Long_lists_are_cut_and_say_how_many_were_left_out()
    {
        var target = new ProcedureInfo("0x1000", "FUN_1000");
        var many = Enumerable.Range(0, 30).Select(i => new FunctionContextBuilder.Neighbour($"0x{i:x}", $"F{i}", false)).ToList();

        var facts = FunctionContextBuilder.Facts(target, false, true, many, [], []);

        Assert.Contains("Called by 30:", facts);
        Assert.Contains("and 18 more", facts);
    }

    // ---- neighbour summaries ------------------------------------------------------------------------

    [Fact]
    public void A_reply_is_read_line_by_line_and_only_for_the_functions_that_were_asked_about()
    {
        var reply = "0x1000 | Allocates a block; probably an allocator.\n0x2000: **Frees** it.\n0x9999 | A function nobody asked about.\nchatter";

        var parsed = NeighbourSummaries.ParseReply(reply, ["0x1000", "0x2000", "0x3000"]);

        Assert.Equal("Allocates a block; probably an allocator.", parsed["0x1000"]);
        Assert.Equal("Frees it.", parsed["0x2000"]);
        Assert.False(parsed.ContainsKey("0x9999"));
        Assert.False(parsed.ContainsKey("0x3000"));     // missing: tried again on a later request, never invented
    }

    [Fact]
    public void Addresses_are_matched_without_regard_to_case_but_stored_as_asked()
    {
        var parsed = NeighbourSummaries.ParseReply("0X140001A00 | Does x.", ["0x140001A00"]);

        Assert.Equal("Does x.", parsed["0x140001A00"]);
    }

    [Fact]
    public void Candidates_are_split_into_requests_that_fit_and_every_one_is_in_exactly_one()
    {
        var code = string.Join("\n", Enumerable.Range(0, 80).Select(i => $"  local_{i:x} = FUN_14000{i:x5}(param_1, {i});"));
        var candidates = Enumerable.Range(0, 8).Select(i => new NeighbourCandidate($"0x{i + 1:x4}", $"FUN_{i}", code)).ToList();

        var batches = NeighbourSummaries.BuildRequests(candidates, 8192);

        Assert.True(batches.Count > 1);
        Assert.Equal(candidates.Select(c => c.Address), batches.SelectMany(b => b.Items).Select(c => c.Address));
        foreach (var (request, items) in batches)
        {
            Assert.True(LlmContext.Fits(request, 8192), "a batch must fit the window");
            Assert.Equal("none", request.Extra!["reasoning_effort"]);
            Assert.True(request.MaxTokens >= 60 * items.Count);
            Assert.All(items, i => Assert.Contains($"=== {i.Address} {i.Name} ===", request.Messages[^1].Content));
        }
    }

    [Fact]
    public void A_small_batch_is_one_request_and_a_long_neighbour_is_cut_at_a_line_break()
    {
        var code = string.Join("\n", Enumerable.Range(0, 400).Select(i => $"  local_{i:x} = FUN_14000{i:x5}(param_1, {i});"));

        var batches = NeighbourSummaries.BuildRequests([new NeighbourCandidate("0x1000", "FUN_1000", code)], 32768);

        var sent = batches.Single().Request.Messages[^1].Content;
        Assert.Contains("/* ... cut ... */", sent);
        Assert.True(sent.Length < NeighbourSummaries.MaxCodeChars + 400);
    }

    [Fact]
    public void A_full_description_is_reused_as_a_one_line_note()
    {
        var d = new FunctionDescription("Purpose: Frees the list.\nRole: x", 1, 1, "m", DateTime.UtcNow);
        var old = new FunctionDescription("Summary: Frees it.\nInputs: none.", 1, 1, "m", DateTime.UtcNow);
        var none = new FunctionDescription("Behavior:\n- x", 1, 1, "m", DateTime.UtcNow);

        Assert.Equal("Frees the list.", NeighbourSummaries.OneLineFrom(d));
        Assert.Equal("Frees it.", NeighbourSummaries.OneLineFrom(old));     // descriptions written before this format
        Assert.Null(NeighbourSummaries.OneLineFrom(none));
    }

    // ---- fitting everything into the window ---------------------------------------------------------

    private static FunctionContext Context(int noteLines = 40) => new(
        ProgramCard: "File: a.exe (pe, x86_64)\n" + string.Join("\n", Enumerable.Range(0, 30).Select(i => $"- \"a string number {i} in the file\"")),
        ProgramCardCompact: "File: a.exe (pe, x86_64)",
        Facts: "Function: FUN_1000 (0x1000)\nCalled by 1: FUN_500 (0x500)",
        CallSites: "-- in FUN_500 (0x500):\n  FUN_1000(1);",
        Notes: string.Join("\n", Enumerable.Range(0, noteLines).Select(i => $"FUN_{i} (0x{i:x}): does something number {i}, probably.")),
        NeighbourAddresses: ["0x1"]);

    private static string UserMessage(BuiltPrompt built) => built.Request.Messages[^1].Content;

    [Fact]
    public void With_room_everything_is_sent_in_the_order_stable_parts_first_and_labeled_by_trust()
    {
        var built = FunctionPrompts.Build(new FunctionToDescribe("FUN_1000", "0x1000", "void f(void){}"), 32768, Context(3));
        var text = UserMessage(built);

        var order = new[] { "Program:", "Facts from REA and Ghidra (observed, not AI):", "How callers use this function", "Notes on neighbouring functions (written by an AI", "Name: FUN_1000" }
            .Select(s => text.IndexOf(s, StringComparison.Ordinal)).ToArray();
        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
        Assert.Contains("a string number 29", text);        // the full card
        Assert.False(built.Truncated);
    }

    [Fact]
    public void A_small_window_drops_the_card_before_anything_that_is_about_this_function_and_keeps_the_code()
    {
        var code = string.Join("\n", Enumerable.Range(0, 60).Select(i => $"  local_{i:x} = FUN_14000{i:x5}(param_1, {i});"));
        var built = FunctionPrompts.Build(new FunctionToDescribe("FUN_1000", "0x1000", code), 4096, Context(60));
        var text = UserMessage(built);

        Assert.Contains("Function: FUN_1000", text);                 // the facts stay
        Assert.DoesNotContain("a string number 29", text);          // the full card does not fit
        Assert.True(LlmContext.Fits(built.Request, 4096));
        Assert.True(built.CodeCharsSent >= 400, "the function's own code is never crowded out");
    }

    [Theory]
    [InlineData(4096)]
    [InlineData(8192)]
    [InlineData(16384)]
    [InlineData(32768)]
    public void The_estimated_prompt_plus_the_reply_always_fits_the_window(int window)
    {
        var code = string.Join("\n", Enumerable.Range(0, 3000).Select(i => $"  local_{i:x} = FUN_14000{i:x5}(param_1, {i});"));

        var built = FunctionPrompts.Build(new FunctionToDescribe("FUN_1000", "0x1000", code), window, Context(200));

        Assert.True(LlmContext.Fits(built.Request, window), $"does not fit {window}");
        Assert.True(built.Truncated);                                // the function itself was cut, and the UI says so
    }

    [Fact]
    public void A_large_window_holds_a_very_large_function_whole()
    {
        // the largest function seen in a real program: 22,011 characters
        var code = string.Join("\n", Enumerable.Range(0, 500).Select(i => $"  uVar{i} = FUN_14000{i:x4}({i});")).PadRight(22_011, ' ');

        var built = FunctionPrompts.Build(new FunctionToDescribe("FUN_1000", "0x1000", code), 32768, Context());

        Assert.False(built.Truncated);
    }

    [Fact]
    public void Without_context_the_prompt_is_the_instructions_and_the_function()
    {
        var text = UserMessage(FunctionPrompts.Build(new FunctionToDescribe("FUN_1000", "0x1000", "void f(void){}"), 8192));

        Assert.DoesNotContain("Program:", text);
        Assert.DoesNotContain("Facts from REA", text);
        Assert.Contains("Name: FUN_1000", text);
    }

    [Fact]
    public void What_is_stored_with_a_description_leaves_out_the_card_and_labels_each_part()
    {
        var stored = Context(2).ForStorage;

        Assert.DoesNotContain("a string number", stored);
        Assert.Contains("Facts (observed):", stored);
        Assert.Contains("Call sites (code as decompiled):", stored);
        Assert.Contains("AI-written, unverified", stored);
    }
}
