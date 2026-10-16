using System.Net.Http;
using AiSlop.LocalLlm;
using ReaWin.Services;

namespace ReaWin.Tests;

/// <summary>
/// Runs only when <c>REAWIN_LLAMA_SERVER</c> (the PrismML llama-server.exe) and <c>REAWIN_LLAMA_MODEL</c> (the Bonsai .gguf) are set;
/// otherwise it is reported as skipped. It starts the real server and the 27B model, so it needs the files from
/// <c>scripts\Install-Ai.ps1</c> and takes minutes on a CPU. <c>REAWIN_LLAMA_CPU=1</c> forces the CPU.
/// </summary>
public sealed class RealModelFactAttribute : FactAttribute
{
    public RealModelFactAttribute()
    {
        if (RealModel.Server is null || RealModel.Model is null)
            Skip = "Set REAWIN_LLAMA_SERVER and REAWIN_LLAMA_MODEL to run against the real model.";
    }
}

internal static class RealModel
{
    public static string? Server => Existing(Environment.GetEnvironmentVariable("REAWIN_LLAMA_SERVER"));
    public static string? Model => Existing(Environment.GetEnvironmentVariable("REAWIN_LLAMA_MODEL"));
    private static string? Existing(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : null;
}

public class RealModelTests
{
    // a real function from procexp64.exe, as REA returned it
    private const string Code = """

        void FUN_140002b40(undefined8 param_1)

        {
          if (DAT_1401ac1a0 != 0) {
            FUN_1400e7f20(param_1);
          }
          DAT_1401ac1a0 = 1;
          atexit(FUN_14011f590);
          return;
        }

        """;

    [RealModelFact]
    public async Task The_real_model_describes_a_function_in_the_requested_form_and_the_server_stops_after()
    {
        const int port = 18381;
        var ai = AiProfile.DefaultSettings with
        {
            Enabled = true,
            Port = port,
            Processor = Environment.GetEnvironmentVariable("REAWIN_LLAMA_CPU") is null ? AiProcessor.Gpu : AiProcessor.Cpu,
        };
        // the settings the app would use, but pointing at the files given by the environment
        var settings = AiProfile.ForEngine(ai, new AiInstallation(Path.GetTempPath())) with
        {
            ServerPath = RealModel.Server!,
            ModelPath = RealModel.Model!,
        };
        var log = Path.Combine(Path.GetTempPath(), "reawin-llama-" + Guid.NewGuid().ToString("N") + ".log");

        string text;
        using (var llm = LlmProviders.Create(settings, log))
        {
            Assert.IsType<LlamaServerProvider>(llm);
            var description = await FunctionDescriber.DescribeAsync(
                llm, new FunctionToDescribe("FUN_140002b40", "0x140002b40", Code), settings.ContextSize);
            Console.WriteLine(description.Text);
            text = description.Text;
        }
        Assert.StartsWith("Summary:", text);                     // the requested form
        Assert.Contains("atexit", text);                          // a fact from the code survives
        Assert.Contains("FUN_14011f590", text);                   // names are kept exactly
        Assert.DoesNotContain("<think>", text);                   // no reasoning trace leaked into the panel

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        await Assert.ThrowsAnyAsync<Exception>(() => http.GetAsync($"http://127.0.0.1:{port}/health"));   // the server stopped with the provider
    }

}
