using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace ReaWin;

/// <summary>
/// Switches the app between the light and dark color tokens. "system" follows the Windows app-mode setting and
/// reacts when the user changes it while the app is open.
/// </summary>
public static class ThemeManager
{
    public const string FollowWindows = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    private const int DwmUseImmersiveDarkMode = 20;
    private static string _setting = FollowWindows;
    private static bool _hooked;

    /// <summary>Applies "system", "light" or "dark"; anything else is treated as "system".</summary>
    public static void Apply(string? setting)
    {
        _setting = setting is Light or Dark ? setting : FollowWindows;
        if (!_hooked)
        {
            _hooked = true;
            SystemEvents.UserPreferenceChanged += (_, _) =>
            {
                if (_setting == FollowWindows) Application.Current?.Dispatcher.Invoke(Refresh);
            };
        }
        Refresh();
    }

    /// <summary>Makes the title bar of <paramref name="window"/> follow the current theme once its handle exists.</summary>
    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) => SetTitleBar(window, IsDark());
    }

    private static void Refresh()
    {
        var dark = IsDark();
        var source = new Uri(dark ? "Themes/Tokens.Dark.xaml" : "Themes/Tokens.Light.xaml", UriKind.Relative);
        // Tokens are the first merged dictionary; replacing it restyles every DynamicResource at once.
        Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary { Source = source };
        foreach (Window w in Application.Current.Windows) SetTitleBar(w, dark);
    }

    /// <summary>True when the dark tokens are in use (for assets that come in light and dark versions).</summary>
    public static bool IsDark() => _setting switch
    {
        Dark => true,
        Light => false,
        _ => !AppsUseLightTheme(),
    };

    private static bool AppsUseLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    private static void SetTitleBar(Window window, bool dark)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var value = dark ? 1 : 0;
        // Fails harmlessly on Windows versions without dark title bars.
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref value, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
