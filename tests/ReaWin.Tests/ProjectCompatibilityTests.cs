using System.IO.Compression;
using System.Text;
using ReaWin.Models;
using ReaWin.Services;

namespace ReaWin.Tests;

/// <summary>Projects store the model's descriptions apart from REA's output, and older projects still open.</summary>
public class ProjectCompatibilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reawin-tests-" + Guid.NewGuid().ToString("N"));

    public ProjectCompatibilityTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static OverviewResult Overview() => new("ev", "22bb3c30cc79", "pe", "x86_64", "0x140000000", 3, 1,
        new[] { new SegmentInfo(".text", "0x1", "0x2", 1) }, new[] { "limit" });

    [Fact]
    public async Task The_call_graph_summaries_and_the_context_of_a_description_round_trip()
    {
        var when = new DateTime(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);
        var project = new ProjectFile
        {
            TargetPath = @"C:\x\a.exe",
            Overview = Overview(),
            CallGraph = new CallGraph { Callees = { ["0x1000"] = ["0x2000", "EXTERNAL:0x5a"], ["0x2000"] = [] } },
            Summaries = { ["0x2000"] = new NeighbourSummary("Frees the list.", AiProfile.ModelLabel, when) },
            Descriptions =
            {
                ["0x1000"] = new FunctionDescription("Purpose: x", 10, 10, AiProfile.ModelLabel, when)
                    { Context = "Facts (observed):\nFunction: f", NeighbourAddresses = ["0x2000"] },
            },
        };
        var path = Path.Combine(_dir, "g.reaproj");

        await ProjectStore.SaveAsync(path, project);
        var loaded = await ProjectStore.LoadAsync(path);

        Assert.Equal(new[] { "0x2000", "EXTERNAL:0x5a" }, loaded.CallGraph!.Callees["0x1000"]);   // addresses exactly as REA gave them
        Assert.Empty(loaded.CallGraph.Callees["0x2000"]);                                          // "calls nothing" survives
        Assert.Equal("Frees the list.", loaded.Summaries["0x2000"].Text);
        Assert.Equal(when, loaded.Summaries["0x2000"].GeneratedUtc);
        Assert.Equal("Facts (observed):\nFunction: f", loaded.Descriptions["0x1000"].Context);
        Assert.Equal(new[] { "0x2000" }, loaded.Descriptions["0x1000"].NeighbourAddresses);
    }

    [Fact]
    public async Task A_project_saved_before_call_graphs_opens_and_has_none_so_the_app_recomputes_it()
    {
        // what an older build wrote: no CallGraph, no Summaries, and a description without context
        var json = """
            {"Version":1,"SavedUtc":"2026-10-16T18:49:13Z","TargetPath":"C:\\x\\a.exe","Root":"C:\\x",
             "Overview":{"EvidenceId":"ev","Sha256":"s","Format":"pe","Architecture":"x86_64","ImageBase":"0x1","ProcedureCount":1,"StringCount":0,"Segments":[],"Limitations":[]},
             "Procedures":[{"Address":"0x1000","Name":"FUN_1000"}],"Strings":[],"Decompiled":{"0x1000":"void f(void){}"},"DecompileErrors":{},
             "Descriptions":{"0x1000":{"Text":"Summary: x","CodeCharsSent":10,"CodeCharsTotal":10,"Model":"m","GeneratedUtc":"2026-10-16T18:49:13Z"}}}
            """;
        var path = Path.Combine(_dir, "old.reaproj");
        await using (var file = File.Create(path))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            await gzip.WriteAsync(Encoding.UTF8.GetBytes(json));

        var loaded = await ProjectStore.LoadAsync(path);

        Assert.Null(loaded.CallGraph);
        Assert.Empty(loaded.Summaries);
        Assert.Equal("", loaded.Descriptions["0x1000"].Context);
        Assert.Empty(loaded.Descriptions["0x1000"].NeighbourAddresses);
    }

    [Fact]
    public async Task Descriptions_round_trip_with_their_model_and_truncation()
    {
        var when = new DateTime(2026, 10, 16, 20, 0, 0, DateTimeKind.Utc);
        var project = new ProjectFile
        {
            TargetPath = @"C:\x\a.exe",
            Overview = Overview(),
            Procedures = { new ProcedureInfo("0x140001000", "FUN_140001000") },
            Decompiled = { ["0x140001000"] = "void f(void){}" },
            Descriptions =
            {
                ["0x140001000"] = new FunctionDescription("Summary: x", 120, 4000, AiProfile.ModelLabel, when),
            },
        };
        var path = Path.Combine(_dir, "p.reaproj");

        await ProjectStore.SaveAsync(path, project);
        var loaded = await ProjectStore.LoadAsync(path);

        var d = loaded.Descriptions["0x140001000"];
        Assert.Equal("Summary: x", d.Text);
        Assert.Equal(AiProfile.ModelLabel, d.Model);
        Assert.True(d.Truncated);
        Assert.Equal(when, d.GeneratedUtc);
        Assert.Equal("void f(void){}", loaded.Decompiled["0x140001000"]);   // REA's output sits apart and unchanged
    }

    [Fact]
    public async Task A_project_saved_before_descriptions_existed_opens_with_none()
    {
        // what version 1 wrote: no "Descriptions" property at all
        var json = """
            {"Version":1,"SavedUtc":"2026-10-15T20:00:00Z","TargetPath":"C:\\x\\a.exe","Root":"",
             "Overview":{"EvidenceId":"ev","Sha256":"22bb","Format":"pe","Architecture":"x86_64","ImageBase":"0x1","ProcedureCount":1,"StringCount":0,"Segments":[],"Limitations":[]},
             "Procedures":[{"Address":"0x140001000","Name":"FUN_140001000"}],"Strings":[],
             "Decompiled":{"0x140001000":"void f(void){}"},"DecompileErrors":{}}
            """;
        var path = Path.Combine(_dir, "old.reaproj");
        await using (var file = File.Create(path))
        await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
            await gzip.WriteAsync(Encoding.UTF8.GetBytes(json));

        var loaded = await ProjectStore.LoadAsync(path);

        Assert.Empty(loaded.Descriptions);
        Assert.Single(loaded.Procedures);
        Assert.Equal("void f(void){}", loaded.Decompiled["0x140001000"]);
    }
}
