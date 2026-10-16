using AiSlop.LocalLlm;

namespace ReaWin.Services;

/// <summary>
/// Finds the AI files (<c>llama-server.exe</c> and <c>model\default.gguf</c>). A future installer puts them in <c>&lt;app&gt;\ai</c>;
/// until then <c>scripts\Install-Ai.ps1</c> puts them in the user's folder. The first complete location wins.
/// </summary>
public static class AiFiles
{
    /// <summary>Points the app at another folder (development and tests); it is used as given, complete or not.</summary>
    public const string OverrideVariable = "REAWIN_AI_DIR";

    /// <summary>Where <c>scripts\Install-Ai.ps1</c> installs: <c>%LocalAppData%\ReverseEngineerAI\ai</c>.</summary>
    public static string UserFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ReverseEngineerAI", AiInstallation.FolderName);

    public static AiInstallation Locate() =>
        Locate(AppContext.BaseDirectory, Environment.GetEnvironmentVariable(OverrideVariable), UserFolder);

    /// <summary>
    /// The override folder if one is given; otherwise the first of <c>&lt;app&gt;\ai</c> and the user folder that holds both
    /// files; if neither does, the user folder, so the "not installed" message points at where to install.
    /// </summary>
    public static AiInstallation Locate(string appFolder, string? overrideFolder, string userFolder)
    {
        if (!string.IsNullOrWhiteSpace(overrideFolder)) return new AiInstallation(overrideFolder);

        var beside = AiInstallation.Locate(appFolder);
        if (beside.IsComplete) return beside;

        var user = new AiInstallation(userFolder);
        return user;
    }
}
