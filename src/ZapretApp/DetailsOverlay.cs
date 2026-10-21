using System.Windows;
using System.Windows.Controls;

namespace ZapretApp;

public sealed class DetailsOverlay : Border
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(DetailsOverlay),
        new FrameworkPropertyMetadata(false, (sender, e) => ((DetailsOverlay)sender).Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed));
    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
}
