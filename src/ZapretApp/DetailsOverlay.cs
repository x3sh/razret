using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace ZapretApp;

public sealed class DetailsOverlay : Border
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(DetailsOverlay),
        new FrameworkPropertyMetadata(false, (sender, e) => ((DetailsOverlay)sender).SetOpen((bool)e.NewValue)));
    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    void SetOpen(bool open)
    {
        BeginAnimation(OpacityProperty, null); Opacity = 1;
        Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open && SystemParameters.ClientAreaAnimation)
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(130)) { FillBehavior = FillBehavior.Stop });
    }
}
