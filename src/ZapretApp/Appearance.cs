using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

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
            ["Canvas"] = ("#0E191C", "#EEF3F1"), ["Surface"] = ("#19282C", "#FFFFFF"),
            ["MetricSurface"] = ("#13FFFFFF", "#DCF0E6"),
            ["Text"] = ("#EDF7F4", "#183831"), ["Muted"] = ("#9FB9B4", "#58776D"),
            ["Border"] = ("#2A4142", "#DCE7E1"), ["Button"] = ("#293E43", "#E3EDE7"),
            ["Accent"] = ("#2BA987", "#187F63"), ["AccentText"] = ("#FFFFFF", "#FFFFFF"),
            ["RowHover"] = ("#23383D", "#F5F7FB"), ["RowSelected"] = ("#304750", "#E4EEE9"),
            ["RowActive"] = ("#203C33", "#EAF7EF"), ["RowActiveText"] = ("#96E5B5", "#237046"),
            ["Card"] = ("#19282C", "#FFFFFF"), ["CardActive"] = ("#163F34", "#DFF2E7"),
            ["CardActiveBorder"] = ("#36765E", "#B7DCC7"), ["CardText"] = ("#F0FBF4", "#1A4730"),
            ["CardMuted"] = ("#ADC2B5", "#567261"), ["Badge"] = ("#2C4F3D", "#D7EFDF"),
            ["BadgeText"] = ("#B7F1CC", "#267345"), ["Notice"] = ("#293E5F", "#DDEBFF")
        };
        foreach (var pair in values)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(dark ? pair.Value.Dark : pair.Value.Light)!;
            brush.Freeze(); window.Resources[pair.Key] = brush;
        }
        var card = new LinearGradientBrush(
            (Color)ColorConverter.ConvertFromString(dark ? "#205A46" : "#E5F7EC"),
            (Color)ColorConverter.ConvertFromString(dark ? "#14362F" : "#CDEADC"), new Point(0, 0), new Point(1, 1));
        card.Freeze(); window.Resources["CardActive"] = card;
    }
}
