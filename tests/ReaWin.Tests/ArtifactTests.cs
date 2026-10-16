using ReaWin.Models;
using ReaWin.Services;

namespace ReaWin.Tests;

/// <summary>The two extra artifacts of an explanation: a human-readable name and a brief for the graph, and how they reach the graph.</summary>
public class ArtifactTests
{
    private const string Good =
        "Name: Initialize global list\n" +
        "Brief: Creates an empty list at startup and registers its cleanup for program exit.\n" +
        "Purpose: Sets up an empty circular list at startup; probably part of initialization.\n" +
        "Role: Called from FUN_140001a00.\n" +
        "Details:\n- Allocates a block.\nEvidence: the self-referencing writes.\nUnclear: nothing.";

    // ---- the prompt asks for them --------------------------------------------------------------------

    [Fact]
    public void The_prompt_asks_for_a_name_and_a_brief_and_the_worked_example_shows_both()
    {
        var m = FunctionPrompts.Build(new FunctionToDescribe("FUN_1", "0x1", "void f(void){}"), 32768).Request.Messages;

        Assert.Contains("Name: a short human-readable name", m[0].Content);
        Assert.Contains("Brief: one sentence of at most 20 words", m[0].Content);
        Assert.Contains("shown on a diagram", m[0].Content);
        Assert.StartsWith("Name:", m[2].Content);                       // the example answer shows the form...
        Assert.Contains("\nBrief:", m[2].Content);
        Assert.DoesNotContain("snake_case", m[0].Content);              // ...and the old snake_case suggestion is gone
        var example = FunctionPrompts.ExtractArtifacts(m[2].Content);
        Assert.Equal("Initialize global list", example.Title);          // the example itself passes the rules it teaches
        Assert.NotNull(example.Brief);
        Assert.True(example.Brief!.Split(' ').Length <= 20);
    }

    // ---- reading them -------------------------------------------------------------------------------

    [Fact]
    public void A_good_reply_gives_its_name_and_brief_unchanged()
    {
        var a = FunctionPrompts.ExtractArtifacts(Good);

        Assert.Equal("Initialize global list", a.Title);
        Assert.Equal("Creates an empty list at startup and registers its cleanup for program exit.", a.Brief);
    }

    [Theory]
    [InlineData("Name: \"Initialize global list\"", "Initialize global list")]
    [InlineData("Name: `Initialize global list`.", "Initialize global list")]
    [InlineData("Name: **Initialize global list**", "Initialize global list")]
    [InlineData("name:   initialize global list  ", "Initialize global list")]          // case of the key and the first letter
    [InlineData("Name: init_global_list", "Init global list")]                           // snake_case becomes words
    [InlineData("Name: Runtime: free memory block", "Runtime: free memory block")]
    public void A_name_is_cleaned_into_plain_words(string line, string expected)
    {
        Assert.Equal(expected, FunctionPrompts.ExtractArtifacts(line + "\nPurpose: x.").Title);
    }

    [Theory]
    [InlineData("Name: FUN_1400027b0")]                                                  // a placeholder is not a name
    [InlineData("Name: thunk_FUN_1400027b0")]
    [InlineData("Name: keep")]
    [InlineData("Name: none")]
    [InlineData("Name: unknown")]
    [InlineData("Name: ")]
    [InlineData("Name: This function allocates a block, stores it in a global and registers a handler for exit")]   // a sentence
    [InlineData("Name: Initialize the global list of things that the program needs to clean up when it exits normally")]   // too long
    public void A_name_that_is_not_a_name_is_dropped_not_shown(string line)
    {
        Assert.Null(FunctionPrompts.ExtractArtifacts(line + "\nPurpose: x.").Title);
    }

    [Fact]
    public void A_reply_without_a_name_or_a_brief_gives_neither_and_does_not_fail()
    {
        var a = FunctionPrompts.ExtractArtifacts("Behavior:\n- walks the list");

        Assert.Null(a.Title);
        Assert.Null(a.Brief);
    }

    [Fact]
    public void A_missing_brief_falls_back_to_the_first_sentence_of_the_purpose()
    {
        var a = FunctionPrompts.ExtractArtifacts("Name: Walk list\nPurpose: Walks the list and frees each node. It is called at exit.\nRole: x");

        Assert.Equal("Walks the list and frees each node.", a.Brief);
    }

    [Fact]
    public void An_old_style_reply_with_a_summary_line_has_no_brief_and_no_title()
    {
        // descriptions written before these artifacts existed are not rewritten: they simply have none
        var a = FunctionPrompts.ExtractArtifacts("Summary: Frees the list.\nInputs: none.");

        Assert.Null(a.Title);
        Assert.Null(a.Brief);
    }

    [Fact]
    public void A_long_brief_is_cut_at_a_word_with_an_ellipsis_and_stays_one_line()
    {
        var long_ = string.Join(" ", Enumerable.Repeat("walks the list and frees every node it finds", 12));

        var a = FunctionPrompts.ExtractArtifacts($"Brief: {long_}\nPurpose: x.");

        Assert.True(a.Brief!.Length <= FunctionPrompts.MaxBriefChars);
        Assert.EndsWith("…", a.Brief);
        Assert.DoesNotContain("\n", a.Brief);
        Assert.False(a.Brief[..^1].EndsWith(" "));
    }

    [Fact]
    public void The_first_name_and_brief_win_when_the_model_repeats_the_keys()
    {
        var a = FunctionPrompts.ExtractArtifacts("Name: First one\nBrief: Does a.\nDetails:\n- Name: Second\nBrief: Does b.");

        Assert.Equal("First one", a.Title);
        Assert.Equal("Does a.", a.Brief);
    }

    // ---- they are kept with the description -----------------------------------------------------------

    [Fact]
    public void Title_and_brief_round_trip_with_the_description_and_an_old_one_has_none()
    {
        var d = new FunctionDescription("Name: x", 1, 1, "m", DateTime.UtcNow) { Title = "Walk list", Brief = "Walks the list." };
        var old = new FunctionDescription("Summary: x", 1, 1, "m", DateTime.UtcNow);

        var json = System.Text.Json.JsonSerializer.Serialize(new[] { d, old });
        var back = System.Text.Json.JsonSerializer.Deserialize<FunctionDescription[]>(json)!;

        Assert.Equal("Walk list", back[0].Title);
        Assert.Equal("Walks the list.", back[0].Brief);
        Assert.Null(back[1].Title);
        Assert.Null(back[1].Brief);
    }

    [Fact]
    public void A_brief_is_the_one_liner_given_to_other_functions_before_the_purpose()
    {
        var withBrief = new FunctionDescription("Purpose: A long purpose that goes on.", 1, 1, "m", DateTime.UtcNow) { Brief = "Frees the list." };
        var withoutBrief = new FunctionDescription("Purpose: A long purpose that goes on.", 1, 1, "m", DateTime.UtcNow);

        Assert.Equal("Frees the list.", NeighbourSummaries.OneLineFrom(withBrief));
        Assert.Equal("A long purpose that goes on.", NeighbourSummaries.OneLineFrom(withoutBrief));
    }

    // ---- on the graph --------------------------------------------------------------------------------

    [Fact]
    public void The_graph_node_carries_the_title_and_the_brief_and_keeps_the_name_REA_reported()
    {
        var graph = new CallGraphIndex(new CallGraph { Callees = { ["0x1000"] = ["0x2000"], ["0x2000"] = [] } });
        var procs = new Dictionary<string, ProcedureInfo>
        {
            ["0x1000"] = new("0x1000", "FUN_1000"), ["0x2000"] = new("0x2000", "FUN_2000"),
        };

        var hood = GraphModel.Build("0x1000", graph, procs, new GraphOptions(), _ => false,
            a => a == "0x2000" ? "Frees the list." : null, a => a == "0x2000" ? "Free list" : null);

        var node = hood.Nodes.Single(n => n.Address == "0x2000");
        Assert.Equal("Free list", node.Title);
        Assert.Equal("Frees the list.", node.Summary);
        Assert.Equal("FUN_2000", node.Name);                              // never replaced: REA's name stays exact
        Assert.Null(hood.Nodes.Single(n => n.IsFocus).Title);              // nothing explained for the focus
    }

    [Fact]
    public void A_graph_built_without_titles_still_works()
    {
        var graph = new CallGraphIndex(new CallGraph { Callees = { ["0x1000"] = [] } });
        var procs = new Dictionary<string, ProcedureInfo> { ["0x1000"] = new("0x1000", "FUN_1000") };

        var hood = GraphModel.Build("0x1000", graph, procs, new GraphOptions(), _ => false, _ => null);

        Assert.Null(hood.Nodes.Single().Title);
    }
}
