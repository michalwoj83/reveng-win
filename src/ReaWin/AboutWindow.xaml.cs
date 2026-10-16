using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ReaWin;

/// <summary>The About window: product, version, the Ai.Slop.AI manifest, how the app works, and credits for what it builds on.</summary>
public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        MaxHeight = SystemParameters.WorkArea.Height * 0.95;
        ThemeManager.Attach(this);
        VersionText.Text = $"Version {ShortVersion()}";

        // White logo for the dark theme, dark logo for the light theme. A modal window: the theme cannot change while it is open.
        var file = ThemeManager.IsDark() ? "AiSlopAi-logo-dark.png" : "AiSlopAi-logo-light.png";
        LogoImage.Source = new BitmapImage(new Uri($"pack://application:,,,/Assets/{file}", UriKind.Absolute));
    }

    /// <summary>"0.1.0" from the assembly, without the "+commit" suffix the SDK appends.</summary>
    internal static string ShortVersion()
    {
        var info = typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = info?.Split('+')[0];
        return string.IsNullOrWhiteSpace(version) ? "0.0.0" : version;
    }
}
