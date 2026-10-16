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
