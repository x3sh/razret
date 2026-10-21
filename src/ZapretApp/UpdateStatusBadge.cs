using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace ZapretApp;

public enum UpdatePhase { Unknown, Unconfigured, Checking, Current, Available, Downloading, Installing, CheckFailed, InstallFailed }

public sealed class UpdateStatusBadge : Border
{
    readonly AppIcon icon = new() { Width = 21, Height = 21, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock title = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    public UpdatePhase Phase { get; private set; }
    public string Title => title.Text;

    public UpdateStatusBadge()
    {
        CornerRadius = new CornerRadius(9); Padding = new Thickness(11, 10, 11, 10);
        BorderThickness = new Thickness(1); Margin = new Thickness(0, 12, 0, 0);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(31) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(icon); Grid.SetColumn(title, 1); grid.Children.Add(title); Child = grid;
        SetResourceReference(BackgroundProperty, "MetricSurface");
        title.SetResourceReference(TextBlock.ForegroundProperty, "Text");
        Set(UpdatePhase.Unknown);
    }

    public void Set(UpdatePhase phase)
    {
        Phase = phase;
        var (text, tone, glyph) = phase switch {
            UpdatePhase.Unconfigured => ("Источник не подключён", "Unknown", "IconHelp"),
            UpdatePhase.Checking => ("Проверяем обновления…", "Muted", "IconUpdate"),
            UpdatePhase.Current => ("Установлена последняя версия", "Good", "IconTick"),
            UpdatePhase.Available => ("Доступно обновление", "SelectionBorder", "IconUpdate"),
            UpdatePhase.Downloading => ("Скачиваем и проверяем…", "SelectionBorder", "IconUpdate"),
            UpdatePhase.Installing => ("Устанавливаем обновление…", "SelectionBorder", "IconUpdate"),
            UpdatePhase.CheckFailed => ("Не удалось проверить обновления", "Warning", "IconHelp"),
            UpdatePhase.InstallFailed => ("Обновление не установлено", "Warning", "IconHelp"),
            _ => ("Обновления ещё не проверены", "Unknown", "IconHelp")
        };
        title.Text = text;
        SetResourceReference(BorderBrushProperty, tone);
        icon.SetResourceReference(AppIcon.StrokeProperty, tone);
        icon.SetResourceReference(AppIcon.DataProperty, glyph);
        AutomationProperties.SetName(this, text);
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
    }
}
