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
            ["Canvas"] = ("#171E27", "#F2F4F8"), ["Surface"] = ("#202A36", "#FFFFFF"),
            ["MetricSurface"] = ("#1B2430", "#F5F7FA"),
            ["Text"] = ("#F1F5FC", "#202B3D"), ["Muted"] = ("#A6B5C9", "#617087"),
            ["Border"] = ("#344151", "#DFE5EE"), ["Button"] = ("#2C394A", "#E7ECF4"),
            ["Accent"] = ("#3984E8", "#256ED0"), ["AccentText"] = ("#FFFFFF", "#FFFFFF"),
            ["RowHover"] = ("#29384A", "#F0F5FD"), ["RowSelected"] = ("#263F60", "#E5EFFF"),
            ["RowActive"] = ("#223A34", "#EAF7EF"), ["RowActiveText"] = ("#96E5B5", "#237046"),
            ["FailedSurface"] = ("#33282C", "#FCF0F0"), ["FailedBorder"] = ("#67434A", "#DDB6BC"),
            ["WarningSurface"] = ("#3B3023", "#FFF6E7"), ["WarningBorder"] = ("#87612F", "#DCBF8E"),
            ["Card"] = ("#202A36", "#FFFFFF"), ["CardActive"] = ("#202A36", "#FFFFFF"),
            ["CardActiveBorder"] = ("#3A495C", "#CBD7E8"), ["CardText"] = ("#F1F5FC", "#202B3D"),
            ["CardMuted"] = ("#A6B5C9", "#617087"), ["Badge"] = ("#2C4F3D", "#D7EFDF"),
            ["BadgeText"] = ("#B7F1CC", "#267345"), ["Notice"] = ("#233750", "#E7F0FF"),
            ["Good"] = ("#61D997", "#209657"), ["Warning"] = ("#F0B955", "#B77716"),
            ["Bad"] = ("#EF777C", "#D64550"), ["Unknown"] = ("#8090A4", "#8794A6")
        };
        foreach (var pair in values)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(dark ? pair.Value.Dark : pair.Value.Light)!;
            brush.Freeze(); window.Resources[pair.Key] = brush;
        }
        var card = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(dark ? "#283544" : "#FFFFFF"),
            (Color)ColorConverter.ConvertFromString(dark ? "#202A36" : "#F7FAFE"), new Point(0, 0), new Point(1, 1));
        card.Freeze(); window.Resources["CardActive"] = card;
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != nint.Zero) { int value = dark ? 1 : 0; DwmSetWindowAttribute(handle, 20, ref value, sizeof(int)); }
    }
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(nint handle, int attribute, ref int value, int size);
}
