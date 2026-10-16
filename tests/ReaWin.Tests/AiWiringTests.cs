using System.Runtime.CompilerServices;
using System.Text.Json;
using AiSlop.LocalLlm;
using ReaWin.Models;
using ReaWin.Services;

namespace ReaWin.Tests;

/// <summary>The model profile, where the AI files are looked for, and the describer against a fake provider.</summary>
public class AiWiringTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "reawin-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string MakeInstall(string name)
    {
        var folder = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.Combine(folder, "model"));
        File.WriteAllText(Path.Combine(folder, "llama-server.exe"), "x");
        File.WriteAllText(Path.Combine(folder, "model", "default.gguf"), "x");
        return folder;
    }

    // ---- profile --------------------------------------------------------------------------------

    [Fact]
    public void A_first_run_has_AI_off_and_the_context_size_this_model_is_used_with()
    {
        var ai = AiProfile.Resolve(new ReaSettings("", "", "", "node"));

        Assert.False(ai.Enabled);                       // opt-in
        Assert.Equal(AiProfile.DefaultContextSize, ai.ContextSize);
        Assert.Equal(AiProcessor.Cpu, ai.Processor);
    }

    [Fact]
    public void The_engine_runs_with_the_installed_paths_and_this_models_arguments()
    {
        var files = new AiInstallation(MakeInstall("ai"));

        var settings = AiProfile.ForEngine(AiProfile.DefaultSettings with { Enabled = true, Processor = AiProcessor.Gpu }, files);
        var args = LlamaServerArguments.Build(settings, "key");

        Assert.Equal(files.ModelPath, args[args.ToList().IndexOf("--model") + 1]);
        Assert.Equal(new[] { "-fa", "on" }, args.Skip(args.Count - 2).ToArray());    // flash attention, last
        Assert.Equal("8192", args[args.ToList().IndexOf("--ctx-size") + 1]);
        Assert.Empty(settings.Validate());
    }

    [Fact]
    public void The_packages_install_messages_are_replaced_by_ones_that_name_the_script()
    {
        Assert.Contains("Install-Ai.ps1", AiProfile.Translate("The AI engine is not installed. Reinstall the app to add it."));
        Assert.Contains("Install-Ai.ps1", AiProfile.Translate("The AI model is not installed. Run the app's setup again to download it."));
        Assert.Equal("Something else.", AiProfile.Translate("Something else."));
    }

    [Fact]
    public void Settings_keep_the_AI_choices_and_never_the_installed_paths()
    {
        var saved = new ReaSettings("g", "j", "r", "node", "dark", AiProfile.DefaultSettings with
        {
            Enabled = true, Processor = AiProcessor.Gpu, Startup = AiStartup.WithApp, ServerPath = @"C:\x\llama-server.exe",
        });

        var json = JsonSerializer.Serialize(saved);
        var loaded = JsonSerializer.Deserialize<ReaSettings>(json)!;

        Assert.DoesNotContain("llama-server", json);
        Assert.DoesNotContain("-fa", json);
        Assert.True(loaded.Ai!.Enabled);
        Assert.Equal(AiProcessor.Gpu, loaded.Ai.Processor);
        Assert.Equal(AiStartup.WithApp, loaded.Ai.Startup);
        Assert.Equal("", loaded.Ai.ServerPath);
    }

    [Fact]
    public void A_settings_file_from_before_the_AI_existed_still_loads()
    {
        var old = """{"GhidraInstallDir":"g","JavaHome":"j","ReaScript":"r","NodeExe":"node","Theme":"dark"}""";

        var loaded = JsonSerializer.Deserialize<ReaSettings>(old)!;

        Assert.Null(loaded.Ai);
        Assert.False(AiProfile.Resolve(loaded).Enabled);
    }

    // ---- where the files are --------------------------------------------------------------------

    [Fact]
    public void The_override_folder_is_used_as_given_even_when_it_is_empty()
    {
        var found = AiFiles.Locate(@"C:\app", @"D:\dev\ai", @"C:\user\ai");

        Assert.Equal(@"D:\dev\ai", found.Folder);
    }

    [Fact]
    public void A_complete_ai_folder_beside_the_app_beats_the_user_folder()
    {
        var app = Path.Combine(_dir, "app");
        Directory.CreateDirectory(Path.Combine(app, "ai", "model"));
        File.WriteAllText(Path.Combine(app, "ai", "llama-server.exe"), "x");
        File.WriteAllText(Path.Combine(app, "ai", "model", "default.gguf"), "x");

        var found = AiFiles.Locate(app, null, Path.Combine(_dir, "user"));

        Assert.Equal(Path.Combine(app, "ai"), found.Folder);
        Assert.True(found.IsComplete);
    }

    [Fact]
    public void Otherwise_the_user_folder_is_used_so_the_message_points_at_where_to_install()
    {
        var found = AiFiles.Locate(Path.Combine(_dir, "empty-app"), null, Path.Combine(_dir, "user"));

        Assert.Equal(Path.Combine(_dir, "user"), found.Folder);
        Assert.False(found.IsComplete);
    }

    // ---- describing, against a fake provider ----------------------------------------------------

    private sealed class FakeLlm(string reply) : ILlmProvider
    {
        public LlmRequest? Seen;
        public string Name => "fake";
        public string? Fault => null;
        public LlmState State => LlmState.Ready;
        public event Action? StateChanged { add { } remove { } }
        public Task<LlmAvailability> CheckAsync(CancellationToken ct = default) => Task.FromResult(new LlmAvailability(true));
        public Task WarmUpAsync(CancellationToken ct = default) => Task.CompletedTask;
        public async IAsyncEnumerable<string> StreamCompletionAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Seen = request;
            foreach (var piece in reply.Split(' ')) { ct.ThrowIfCancellationRequested(); yield return piece + " "; await Task.Yield(); }
        }
        public void Dispose() { }
    }

    private static readonly FunctionToDescribe Fn = new("FUN_140001000", "0x140001000", "void FUN_140001000(void)\n{\n  FUN_1400e7870(0x40);\n  return;\n}");

    [Fact]
    public async Task A_good_reply_becomes_a_description_labeled_with_the_model_and_how_much_code_it_saw()
    {
        var llm = new FakeLlm("**Summary:** Calls FUN_1400e7870 with 0x40.\nInputs: none.");

        var d = await FunctionDescriber.DescribeAsync(llm, Fn, 8192);

        Assert.Equal("Summary: Calls FUN_1400e7870 with 0x40.\nInputs: none.", d.Text);
        Assert.Equal(AiProfile.ModelLabel, d.Model);
        Assert.False(d.Truncated);
        Assert.Equal(Fn.Code.Length, d.CodeCharsTotal);
        Assert.Equal("none", llm.Seen!.Extra!["reasoning_effort"]);
    }

    [Fact]
    public async Task An_empty_reply_is_an_error_with_advice_not_an_empty_description()
    {
        var ex = await Assert.ThrowsAsync<DescriptionException>(() => FunctionDescriber.DescribeAsync(new FakeLlm("<think>hmm"), Fn, 8192));

        Assert.Contains("Try again", ex.Message);
    }

    [Fact]
    public async Task A_looping_reply_is_discarded()
    {
        var loop = string.Join("\n", Enumerable.Repeat("- Calls FUN_1400e7870 again and again", 5));

        await Assert.ThrowsAsync<DescriptionException>(() => FunctionDescriber.DescribeAsync(new FakeLlm(loop), Fn, 8192));
    }

    [Fact]
    public async Task Cancelling_stops_the_description()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FunctionDescriber.DescribeAsync(new FakeLlm("Summary: x"), Fn, 8192, cts.Token));
    }
}
