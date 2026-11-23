using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace RimManager;

public partial class App : Application
{
    private static bool darkTheme;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint window, int attribute, in int value, int size);

    internal static void ApplyCaptionTheme(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) || SystemParameters.HighContrast) return;
        const int UseImmersiveDarkMode = 20;
        var enabled = darkTheme ? 1 : 0;
        _ = DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, UseImmersiveDarkMode, in enabled, sizeof(int));
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        if (SystemParameters.HighContrast)
        {
            foreach (var key in new[] { "WindowSurface", "PanelSurface", "ControlSurface", "HoverSurface", "SelectedSurface" })
                Resources[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "Text", "SecondaryText", "Outline", "ControlOutline", "Accent", "WarningText", "ErrorText" })
                Resources[key] = SystemColors.WindowTextBrush;
            foreach (var key in new[] { "PrimarySurface", "PrimaryHover" })
                Resources[key] = SystemColors.HighlightBrush;
            Resources["OnPrimary"] = SystemColors.HighlightTextBrush;
        }
        else if (Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                     "AppsUseLightTheme", 1) is 0)
        {
            darkTheme = true;
            var colors = new Dictionary<string, string>
            {
                ["WindowSurface"] = "#202629", ["PanelSurface"] = "#2B3033", ["ControlSurface"] = "#33393C",
                ["Outline"] = "#484F52", ["ControlOutline"] = "#657074", ["Text"] = "#F0F4F4",
                ["SecondaryText"] = "#B7C3C7", ["SelectedSurface"] = "#26433C", ["HoverSurface"] = "#36413E",
                ["Accent"] = "#8BDAC5", ["PrimarySurface"] = "#92E1CC", ["PrimaryHover"] = "#B3EBDC",
                ["OnPrimary"] = "#12382E", ["WarningText"] = "#FFC37F", ["ErrorText"] = "#FF9D9D"
            };
            foreach (var (key, color) in colors)
            {
                var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
                brush.Freeze();
                Resources[key] = brush;
            }
        }
        base.OnStartup(e);
    }
}
