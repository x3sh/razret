using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace ZapretApp;

public static class Appearance
{
    public static void Apply(Window window, string mode)
    {
        bool dark = mode == "dark";
        if (mode == "system")
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = Equals(key?.GetValue("AppsUseLightTheme"), 0);
        }
        var values = new Dictionary<string, (string Dark, string Light)>
        {
            ["Canvas"] = ("#1C2229", "#F2F4F8"), ["Surface"] = ("#252D36", "#FFFFFF"),
            ["MetricSurface"] = ("#20262F", "#F5F7FA"),
            ["Text"] = ("#F1F5FC", "#202B3D"), ["Muted"] = ("#A6B5C9", "#617087"),
            ["Border"] = ("#37414E", "#DFE5EE"), ["Button"] = ("#303A47", "#E7ECF4"),
            ["Accent"] = ("#147BF1", "#256ED0"), ["AccentText"] = ("#FFFFFF", "#FFFFFF"),
            ["RowHover"] = ("#2C3745", "#F0F5FD"), ["RowSelected"] = ("#263F60", "#E5EFFF"), ["SelectionBorder"] = ("#447BB9", "#4A87D1"),
            ["RowActive"] = ("#223A34", "#EAF7EF"), ["RowActiveText"] = ("#96E5B5", "#237046"),
            ["FailedSurface"] = ("#33282C", "#FCF0F0"), ["FailedBorder"] = ("#67434A", "#DDB6BC"),
            ["WarningSurface"] = ("#3B3023", "#FFF6E7"), ["WarningBorder"] = ("#87612F", "#DCBF8E"),
            ["Card"] = ("#202A36", "#FFFFFF"), ["CardActive"] = ("#202A36", "#FFFFFF"),
            ["CardActiveBorder"] = ("#3A495C", "#CBD7E8"), ["CardText"] = ("#F1F5FC", "#202B3D"),
            ["CardMuted"] = ("#A6B5C9", "#617087"), ["Badge"] = ("#2C4F3D", "#D7EFDF"),
            ["BadgeText"] = ("#B7F1CC", "#267345"), ["Notice"] = ("#233750", "#E7F0FF"),
            ["Good"] = ("#59D38E", "#209657"), ["Warning"] = ("#E7B958", "#B77716"),
            ["Bad"] = ("#E17480", "#D64550"), ["Unknown"] = ("#8899AB", "#8794A6"),
            ["MetricGood"] = ("#80C9A8", "#217351"), ["MetricWarning"] = ("#D9B577", "#8C611D"), ["MetricBad"] = ("#DC929B", "#AD3C4D")
        };
        foreach (var pair in values)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(dark ? pair.Value.Dark : pair.Value.Light)!;
            brush.Freeze(); window.Resources[pair.Key] = brush;
        }
        var card = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(dark ? "#29313C" : "#FFFFFF"),
            (Color)ColorConverter.ConvertFromString(dark ? "#252D36" : "#F7FAFE"), new Point(0, 0), new Point(1, 1));
        card.Freeze(); window.Resources["CardActive"] = card;
        var selection = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(dark ? "#243C59" : "#E3EEFD"), (Color)ColorConverter.ConvertFromString(dark ? "#29394D" : "#F0F5FD"), new Point(0, 0), new Point(1, 0));
        selection.Freeze(); window.Resources["RowSelected"] = selection;
        var primary = new LinearGradientBrush((Color)ColorConverter.ConvertFromString(dark ? "#168CFF" : "#3488E8"),
            (Color)ColorConverter.ConvertFromString(dark ? "#0868DF" : "#1D65C7"), new Point(0, 0), new Point(0, 1));
        primary.Freeze(); window.Resources["Accent"] = primary;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != nint.Zero) { int value = dark ? 1 : 0; DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)); }
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint handle, int attribute, ref int value, int size);
}
