using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ZapretApp;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.FirstOrDefault() == "--apply-app-update") return AppUpdater.Apply(args);
            if (args.Contains("--self-test")) { SelfTest.Run(); return 0; }
            if (args.Contains("--statistics-test")) { StatisticsTests.Run().GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--probe")) { Store.Write("baseline-manual.json", Checks.Run(CancellationToken.None).GetAwaiter().GetResult()); return 0; }
            if (args.Contains("--update-test")) { SelfTest.Update().GetAwaiter().GetResult(); return 0; }
            if (args.Contains("--ui-test")) { SelfTest.Desktop(); return 0; }
            bool inspection = args.Any(a => a.StartsWith("--snapshot") || a == "--measure" || a == "--ui-test");
            if (!inspection && !args.Contains("--no-elevate") && !Engine.Admin)
            {
                var settings = Store.Load();
                if (settings.SetupComplete && settings.Strategy != null && (settings.AutoConnect || args.Contains("--startup")))
                {
                    try
                    {
                        var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Store.Root };
                        foreach (var arg in args) info.ArgumentList.Add(arg);
                        System.Diagnostics.Process.Start(info); return 0;
                    }
                    catch (System.ComponentModel.Win32Exception) { /* User can still open settings after declining UAC. */ }
                }
            }
            using var mutex = new Mutex(false, inspection ? "Local\\RazretInspection" : "Local\\TihoZapretPrototype");
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
            {
                try { using var existing = EventWaitHandle.OpenExisting("Local\\RazretActivate"); existing.Set(); }
                catch (Exception) { MessageBox.Show("Razret уже открыт. Найдите значок R рядом с часами и дважды нажмите на него."); }
                return 0;
            }
            try
            {
                Directory.CreateDirectory(Store.Data); Updater.Recover(); Strategies.Validate(Store.Engine);
                var app = new Application(); var window = new MainWindow(inspection, args.Contains("--startup"));
                using var activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, inspection ? "Local\\RazretInspectActivate" : "Local\\RazretActivate");
                var registration = ThreadPool.RegisterWaitForSingleObject(activateEvent, (_, _) => window.Dispatcher.BeginInvoke(new Action(window.ShowFromTray)), null, Timeout.Infinite, false);
                window.Closed += (_, _) => registration.Unregister(null);
                if (args.Contains("--measure")) window.Loaded += (_, _) =>
                {
                    var process = System.Diagnostics.Process.GetCurrentProcess(); var start = process.TotalProcessorTime;
                    var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
                    timer.Tick += (_, _) => { timer.Stop(); process.Refresh(); Store.Write("idle-metrics.json", new { WorkingSetBytes = process.WorkingSet64, PrivateBytes = process.PrivateMemorySize64, CpuSecondsOver10Seconds = (process.TotalProcessorTime - start).TotalSeconds, Note = "Open UI, no bypass; startup is included in CPU interval" }); window.Close(); }; timer.Start();
                };
                if (args.Any(a => a.StartsWith("--snapshot"))) window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
                {
                    if (args.Contains("--snapshot-settings")) window.Pages.SelectedIndex = 1;
                    if (args.Contains("--snapshot-updates")) { window.PreviewUpdatesState(); window.Pages.SelectedIndex = 2; }
                    if (args.Contains("--snapshot-updates-current")) { window.PreviewCurrentUpdates(); window.Pages.SelectedIndex = 2; }
                    if (args.Contains("--snapshot-dark")) Appearance.Apply(window, "dark");
                    if (args.Contains("--snapshot-advanced")) { window.Pages.SelectedIndex = 1; window.AdvancedExpander.IsExpanded = true; }
                    if (args.Contains("--snapshot-active")) window.PreviewActiveState();
                    if (args.Contains("--snapshot-warning")) { window.PreviewActiveState(); window.PreviewMetricState("warning"); }
                    if (args.Contains("--snapshot-bad")) { window.PreviewActiveState(); window.PreviewMetricState("bad"); }
                    if (args.Contains("--snapshot-light")) { Appearance.Apply(window, "light"); window.PreviewActiveState(); }
                    if (args.Contains("--snapshot-narrow")) { window.Width = 500; window.Height = 620; window.PreviewActiveState(true); }
                    if (args.Contains("--snapshot-other")) window.PreviewActiveState(true);
                    if (args.Contains("--snapshot-hidden")) window.StrategiesToggleButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    if (args.Contains("--snapshot-tiles")) window.ViewModeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    if (args.Contains("--snapshot-grid-short")) { window.Width = 700; window.Height = 460; }
                    if (args.Contains("--snapshot-notices")) window.PreviewUpdatesState();
                    if (args.Contains("--snapshot-busy")) window.PreviewBusyState();
                    if (args.Contains("--snapshot-expanded")) window.SelectedExpander.IsOpen = true;
                    if (args.Contains("--snapshot-theme")) { window.Pages.SelectedIndex = 1; window.ThemeBox.IsDropDownOpen = true; }
                    var snapshotDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                    snapshotDelay.Tick += (_, _) =>
                    {
                    snapshotDelay.Stop();
                    window.UpdateLayout();
                    window.StrategyCard.InvalidateMeasure(); window.UpdateLayout();
                    if (args.Contains("--snapshot-advanced")) { window.AdvancedExpander.BringIntoView(); window.UpdateLayout(); }
                    Store.Write("preview-layout.json", new { window.Width, window.Height, WindowContentHeight = ((FrameworkElement)window.Content).ActualHeight, Elements = new FrameworkElement[] { window.StrategyCard, window.MetricsGrid, window.Pages, window.StrategiesHeader, window.ResultsPanel, window.Footer, window.UpdateBadge, window.FooterSettingsButton }.Select(e => new { e.Name, Width = e.ActualWidth, Height = e.ActualHeight, Y = e.TranslatePoint(new Point(), window).Y, e.IsVisible }) });
                    var content = (FrameworkElement)window.Content;
                    if (args.Contains("--snapshot-theme")) { content = (FrameworkElement)((System.Windows.Controls.Primitives.Popup)window.ThemeBox.Template.FindName("PART_Popup", window.ThemeBox)).Child; content.UpdateLayout(); }
                    var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    var filename = args.Contains("--snapshot-light") ? "preview-light.png" : args.Contains("--snapshot-settings") ? "preview-settings.png" : args.Contains("--snapshot-updates") ? "preview-updates.png" : args.Contains("--snapshot-active") ? "preview-active.png" : args.Contains("--snapshot-other") ? "preview-other.png" : "preview.png";
                    if (args.Contains("--snapshot-hidden")) filename = "preview-hidden.png";
                    if (args.Contains("--snapshot-busy")) filename = "preview-busy.png";
                    if (args.Contains("--snapshot-tiles")) filename = "preview-tiles.png";
                    if (args.Contains("--snapshot-grid-short")) filename = "preview-grid-short.png";
                    if (args.Contains("--snapshot-notices")) filename = "preview-notices.png";
                    if (args.Contains("--snapshot-narrow")) filename = "preview-narrow.png";
                    if (args.Contains("--snapshot-advanced")) filename = "preview-advanced.png";
                    if (args.Contains("--snapshot-theme")) filename = "preview-theme.png";
                    if (args.Contains("--snapshot-expanded")) filename = "preview-details.png";
                    if (args.Contains("--snapshot-warning")) filename = "preview-warning.png";
                    if (args.Contains("--snapshot-bad")) filename = "preview-bad.png";
                    using (var output = File.Create(Path.Combine(Store.Data, filename))) png.Save(output);
                    Store.Write("preview-metrics.json", new { WorkingSetBytes = System.Diagnostics.Process.GetCurrentProcess().WorkingSet64, PrivateBytes = System.Diagnostics.Process.GetCurrentProcess().PrivateMemorySize64, Note = "UI snapshot, no running bypass, one measurement" });
                    window.Close();
                    };
                    snapshotDelay.Start();
                }));
                return app.Run(window);
            }
            finally { mutex.ReleaseMutex(); }
        }
        catch (Exception ex)
        {
            Store.Log(ex.ToString());
            if (args.Length == 0) MessageBox.Show(ex.Message, "Ошибка запуска");
            else File.WriteAllText(Path.Combine(Store.Data, "command-error.txt"), ex.ToString());
            return 1;
        }
    }
}

public static class SelfTest
{
    public static void Desktop()
    {
        var actualRoot = Store.Root;
        var temp = Path.Combine(Path.GetTempPath(), "razret-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(temp, "engine"));
        foreach (var file in Directory.GetFiles(Store.Engine, "general*.bat")) File.Copy(file, Path.Combine(temp, "engine", Path.GetFileName(file)));
        File.Copy(Path.Combine(Store.Engine, "version.txt"), Path.Combine(temp, "engine", "version.txt"));
        Exception? failure = null; bool closed = false;
        try
        {
            Store.Root = temp + Path.DirectorySeparatorChar;
            Store.Save(new Settings { AutoConnect = false, CheckUpdates = false, CloseToTray = true, Theme = "dark" });
            Task<Release?> appResponse = Task.FromResult<Release?>(new Release("99.0.0", "https://example.invalid/update.zip", "", 1, "Проверка уведомления"));
            Task<Release> engineResponse = Task.FromResult(new Release(Updater.Version, "https://example.invalid/engine.zip", "", 1, "Обновлен IPSet"));
            var app = new Application(); var window = new MainWindow(quiet: true,
                latestAppRelease: ct => appResponse.WaitAsync(ct), latestEngineRelease: ct => engineResponse.WaitAsync(ct));
            window.Closed += (_, _) => closed = true;
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(async () =>
            {
                try
                {
                    Assert(window.AppUpdateBanner.IsVisible, "app notice appears with automatic engine checks disabled");
                    Assert(window.AppInstallButton.IsEnabled && window.AppReleaseNotes.Text.Contains("Проверка уведомления"), "app update waits for explicit installation");
                    Assert(window.AppUpdateBanner.TranslatePoint(new Point(), window).Y < window.StrategyCard.TranslatePoint(new Point(), window).Y, "notice above current strategy");
                    window.AppNotice.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert(window.Pages.SelectedIndex == 2, "notice opens app updates");
                    window.FooterSettingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert(window.Pages.SelectedIndex == 1, "bottom navigation opens settings from updates");
                    window.Pages.SelectedIndex = 0;
                    window.DismissAppButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert(!window.AppUpdateBanner.IsVisible && window.UpdateDot.Visibility == Visibility.Visible && window.AppInstallButton.IsEnabled, "dismissal hides banner but preserves available update");
                    Assert(window.AppUpdateStatus.Phase == UpdatePhase.Available && window.EngineUpdateStatus.Phase == UpdatePhase.Unknown, "app update status independent of unchecked engine");
                    var pendingApp = new TaskCompletionSource<Release?>(); appResponse = pendingApp.Task;
                    window.AppCheckButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert(window.AppUpdateStatus.Phase == UpdatePhase.Checking && !window.AppInstallButton.IsEnabled, "pending check has visible checking status and prevents concurrent app installation");
                    pendingApp.SetResult(null); await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert(window.AppUpdateStatus.Phase == UpdatePhase.Current && window.AppUpdateStatus.Title.Contains("последняя") && !window.AppInstallButton.IsEnabled, "successful check confirms latest installed app");
                    appResponse = Task.FromException<Release?>(new IOException("offline fixture"));
                    window.AppCheckButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert(window.AppUpdateStatus.Phase == UpdatePhase.CheckFailed && !window.AppUpdateStatus.Title.Contains("последняя"), "offline app check cannot claim latest version");
                    appResponse = Task.FromResult<Release?>(null);
                    window.AppCheckButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    var cancelledApp = new TaskCompletionSource<Release?>(); appResponse = cancelledApp.Task;
                    window.AppCheckButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    window.CancelButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert(window.AppUpdateStatus.Phase == UpdatePhase.Current && window.AppCheckButton.IsEnabled, "cancelled app check restores last confirmed status");
                    window.CheckButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert(window.EngineUpdateStatus.Phase == UpdatePhase.Current && !window.UpdateButton.IsEnabled, "engine check confirms installed latest version");
                    engineResponse = Task.FromException<Release>(new IOException("offline fixture"));
                    window.CheckButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert(window.EngineUpdateStatus.Phase == UpdatePhase.CheckFailed && window.AppUpdateStatus.Phase == UpdatePhase.Current, "failed engine check changes only engine status");
                    engineResponse = Task.FromResult(new Release("99.0.0", "https://example.invalid/engine.zip", "", 1, "Обновлен IPSet"));
                    window.CheckButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                    Assert(window.EngineUpdateStatus.Phase == UpdatePhase.Available && window.UpdateButton.IsEnabled, "new engine release has an available status and install action");
                    window.Close(); Assert(!closed && !window.IsVisible, "close hides to tray");
                    window.ShowFromTray(); Assert(window.IsVisible && !closed, "restore from tray");
                    window.Pages.SelectedIndex = 1; window.UpdateLayout(); Assert(window.StartupBox.IsVisible, "settings reachable");
                    var dark = ((SolidColorBrush)window.Resources["Text"]).Color;
                    window.ThemeBox.SelectedIndex = 1; Assert(((SolidColorBrush)window.Resources["Text"]).Color != dark, "theme switches without restart");
                    window.ThemeBox.SelectedIndex = 0; Assert(((SolidColorBrush)window.Resources["Text"]).Color == dark, "dark theme restored");
                    var visual = new MainWindow(preview: true);
                    try
                    {
                        visual.Show(); visual.ResultsGrid.SelectedIndex = 2;
                        var embeddedFace = new Typeface(visual.FontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                        Assert(embeddedFace.TryGetGlyphTypeface(out var glyphFace) && glyphFace.FamilyNames.Values.Any(n => n.StartsWith("Inter")), "embedded Inter font resolves without system installation");
                        Assert(glyphFace.FontUri.ToString().Contains("Assets/Fonts", StringComparison.OrdinalIgnoreCase), "UI uses packaged font instead of a machine-specific fallback");
                        Assert(visual.PowerStrategy == ((MainWindow.Row)visual.ResultsGrid.SelectedItem).Name, "power uses selected strategy before saving");
                        visual.PreviewActiveState(); visual.UpdateLayout();
                        var active = (MainWindow.Row)visual.ResultsGrid.Items[0];
                        Assert(active.IsActive, "active strategy pinned first");
                        visual.PreviewActiveState(true); visual.UpdateLayout();
                        var selectedName = ((MainWindow.Row)visual.ResultsGrid.SelectedItem).Name;
                        Assert(visual.PowerStrategy == active.Name && selectedName != active.Name, "browsing preserves active strategy");
                        visual.ViewModeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); visual.UpdateLayout();
                        Assert(((MainWindow.Row)visual.ResultsGrid.SelectedItem).Name == selectedName && visual.PowerStrategy == active.Name && visual.Width > 560, "grid expands window while preserving selected and active strategies");
                        var finalTile = visual.ResultsGrid.ItemContainerGenerator.ContainerFromIndex(visual.ResultsGrid.Items.Count - 1) as FrameworkElement;
                        Assert(finalTile != null && finalTile.TranslatePoint(new Point(0, finalTile.ActualHeight), visual.ResultsGrid).Y <= visual.ResultsGrid.ActualHeight + 1, "all tiles visible at once in expanded grid");
                        var overviewSize = new Size(visual.Width, visual.Height);
                        visual.Width = 700; visual.Height = 460; visual.UpdateLayout(); visual.UpdateLayout();
                        Assert(visual.Width == 700 && visual.Height == 460, "manual resize does not force overview dimensions");
                        var gridScroller = FindVisual<System.Windows.Controls.ScrollViewer>(visual.ResultsGrid)!;
                        gridScroller.ScrollToTop(); visual.UpdateLayout();
                        gridScroller.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent }); visual.UpdateLayout();
                        Assert(gridScroller.VerticalOffset > 0, "mouse wheel scrolls shortened grid");
                        gridScroller.ScrollToTop(); visual.UpdateLayout();
                        Assert(gridScroller.ScrollableHeight > 0 && gridScroller.ComputedVerticalScrollBarVisibility == Visibility.Visible, "short grid gets vertical scrolling");
                        var firstTile = (System.Windows.Controls.ListBoxItem)visual.ResultsGrid.ItemContainerGenerator.ContainerFromIndex(0);
                        Assert(firstTile.ActualHeight == 54, "tiles retain fixed readable height after resize");
                        var lamp = FindNamedVisual(firstTile, "TileLamp")!;
                        var name = FindNamedVisual(firstTile, "TileName")!;
                        double lampCenter = lamp.TranslatePoint(new Point(0, lamp.ActualHeight / 2), firstTile).Y;
                        double textCenter = name.TranslatePoint(new Point(0, name.ActualHeight / 2), firstTile).Y;
                        Assert(Math.Abs(lampCenter - textCenter) < 1, "lamp and tile name share a vertical center");
                        visual.ResultsGrid.ScrollIntoView(visual.ResultsGrid.Items[visual.ResultsGrid.Items.Count - 1]); visual.UpdateLayout();
                        var bottomTile = (FrameworkElement)visual.ResultsGrid.ItemContainerGenerator.ContainerFromIndex(visual.ResultsGrid.Items.Count - 1);
                        Assert(bottomTile.TranslatePoint(new Point(0, bottomTile.ActualHeight), visual.ResultsGrid).Y <= visual.ResultsGrid.ActualHeight + 1, "last tile reachable in shortened window");
                        visual.ResultsGrid.ScrollIntoView(visual.ResultsGrid.Items[0]);
                        visual.Width = overviewSize.Width; visual.Height = overviewSize.Height; visual.UpdateLayout(); visual.UpdateLayout();
                        Assert(gridScroller.ScrollableHeight == 0, $"scrollbar disappears when all tiles fit again: window {visual.Width}x{visual.Height}, viewport {gridScroller.ViewportHeight}, extent {gridScroller.ExtentHeight}, panel {visual.ResultsGrid.ActualHeight}");
                        foreach (var key in new[] { "IconHelp", "IconSettings", "IconGrid", "IconWifi", "IconClock", "IconChecks", "IconTick" })
                        {
                            var bounds = ((Geometry)visual.Resources[key]).Bounds;
                            Assert(bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= 24 && bounds.Bottom <= 24, "icons fit their common 24-unit canvas");
                        }
                        foreach (var group in new[] { visual.MetricPingGroup, visual.MetricResponseGroup, visual.MetricPassedGroup })
                        {
                            var segment = (FrameworkElement)VisualTreeHelper.GetParent(group);
                            double center = group.TranslatePoint(new Point(group.ActualWidth / 2, 0), segment).X;
                            Assert(Math.Abs(center - segment.ActualWidth / 2) < 1.1, "metric icon and text group centered in segment");
                        }
                        Assert(visual.ViewModeButton.ToolTip.ToString() == "Показать списком", "tiles offer list alternative");
                        visual.ViewModeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); visual.UpdateLayout();
                        Assert(visual.Width == 460, "return to list restores narrow window");
                        foreach (var state in new[] { "good", "warning", "bad" })
                        {
                            visual.PreviewMetricState(state);
                            var expected = (SolidColorBrush)visual.Resources[Quality.Brush(state)];
                            Assert(((SolidColorBrush)visual.CardPing.Foreground).Color == expected.Color && ((SolidColorBrush)visual.CardPingIcon.Stroke).Color == expected.Color, "ping icon and value share quality color");
                            Assert(((SolidColorBrush)visual.CardResponse.Foreground).Color == expected.Color && ((SolidColorBrush)visual.CardPassedIcon.Stroke).Color == expected.Color, "response and availability quality appear in dashboard");
                        }
                        visual.PreviewActiveState(true); visual.UpdateLayout();
                        var last = visual.ResultsGrid.Items[visual.ResultsGrid.Items.Count - 1];
                        visual.ResultsGrid.ScrollIntoView(last); visual.UpdateLayout();
                        var lastRow = (System.Windows.Controls.ListBoxItem)visual.ResultsGrid.ItemContainerGenerator.ContainerFromItem(last);
                        var end = lastRow.TranslatePoint(new Point(0, lastRow.ActualHeight), visual.ResultsGrid);
                        Assert(end.Y <= visual.ResultsGrid.ActualHeight + 1 && end.Y > 0, "last strategy reachable by scrolling");
                        Assert(!visual.CardCaption.IsVisible && !visual.SelectionStatus.IsVisible && !visual.CardSubtitle.IsVisible, "redundant captions hidden");
                        visual.BackToActiveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        Assert(((MainWindow.Row)visual.ResultsGrid.SelectedItem).IsActive, "return to active action");
                        // Details belong to the app and can be closed without clicking the desktop.
                        visual.DetailsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); visual.UpdateLayout();
                        Assert(visual.SelectedExpander.IsOpen && visual.CloseDetailsButton.IsVisible, "details have an explicit close control");
                        var detailsEnd = visual.DetailsCard.TranslatePoint(new Point(visual.DetailsCard.ActualWidth, visual.DetailsCard.ActualHeight), (UIElement)visual.Content);
                        Assert(detailsEnd.X <= ((FrameworkElement)visual.Content).ActualWidth && detailsEnd.Y <= ((FrameworkElement)visual.Content).ActualHeight, "details remain inside window bounds");
                        visual.CloseDetailsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        Assert(!visual.SelectedExpander.IsOpen && !visual.SelectedExpander.IsVisible, "details close button hides the overlay");
                        Assert(!visual.SelectedExpander.HasAnimatedProperties && visual.SelectedExpander.Opacity == 1, "closing details cancels fade and resets opacity");
                        visual.DetailsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        visual.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(visual), Environment.TickCount, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                        Assert(!visual.SelectedExpander.IsOpen, "Escape dismisses details");
                        visual.HelpPopup.PlacementTarget = visual.StrategiesToggleButton;
                        visual.HelpText.Text = "Проверка подсказки"; visual.HelpPopup.IsOpen = true;
                        Assert(visual.HelpPopup.PopupAnimation == (SystemParameters.ClientAreaAnimation ? System.Windows.Controls.Primitives.PopupAnimation.Fade : System.Windows.Controls.Primitives.PopupAnimation.None), "help animation follows Windows motion setting");
                        visual.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, PresentationSource.FromVisual(visual), Environment.TickCount, System.Windows.Input.Key.Escape) { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent });
                        Assert(!visual.HelpPopup.IsOpen, "Escape closes help popup during opening animation");
                        var expanded = visual.Height;
                        visual.StrategiesToggleButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); visual.UpdateLayout();
                        Assert(visual.Height < expanded && !visual.ResultsPanel.IsVisible, "collapse reduces height");
                        var collapsedFooter = visual.UpdateBadge.TranslatePoint(new Point(0, visual.UpdateBadge.ActualHeight), (UIElement)visual.Content);
                        Assert(collapsedFooter.Y <= ((FrameworkElement)visual.Content).ActualHeight - 8 && visual.StrategiesHeader.ActualHeight >= 30, "compact controls fit without clipping");
                        visual.Pages.SelectedIndex = 1; visual.UpdateLayout();
                        Assert(visual.Height >= 600 && visual.StartupBox.IsVisible, "settings expand collapsed window");
                        Assert(visual.SaveButton.IsVisible && visual.SaveButton.TranslatePoint(new Point(0, visual.SaveButton.ActualHeight), visual).Y < visual.ActualHeight - 45, "settings save stays reachable without scrolling");
                        visual.Pages.SelectedIndex = 0; visual.UpdateLayout();
                        Assert(visual.Height < expanded && !visual.ResultsPanel.IsVisible, "home restores compact state");
                        visual.PreviewUpdatesState(); visual.UpdateLayout();
                        Assert(visual.AppUpdateBanner.IsVisible && visual.EngineUpdateBanner.IsVisible, "independent app and engine banners");
                        Assert(visual.StrategiesHeader.TranslatePoint(new Point(0, visual.StrategiesHeader.ActualHeight), visual).Y < visual.ActualHeight - 50, "banners fit in compact mode");
                        visual.DismissAppButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        Assert(!visual.AppUpdateBanner.IsVisible && visual.EngineUpdateBanner.IsVisible, "dismissal independent by update type");
                        visual.PreviewUpdatesState(); Assert(!visual.AppUpdateBanner.IsVisible, "same dismissed release stays hidden");
                        visual.PreviewUpdatesState("0.9.0"); Assert(visual.AppUpdateBanner.IsVisible, "new release reappears");
                        visual.EngineNotice.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        Assert(visual.Pages.SelectedIndex == 2 && !visual.AppUpdateBanner.IsVisible, "engine banner opens updates without duplicated banner");
                        visual.DismissEngineButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        visual.Pages.SelectedIndex = 0; visual.DismissAppButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        visual.StrategiesToggleButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        visual.Width = 440; visual.Height = 620; visual.UpdateLayout();
                        Assert(visual.ResultsGrid.ActualHeight >= 120, "minimum window retains useful list viewport");
                        var action = visual.AutoButton.TranslatePoint(new Point(visual.AutoButton.ActualWidth, visual.AutoButton.ActualHeight), visual);
                        Assert(action.X < visual.ActualWidth - 12 && action.Y < visual.ActualHeight - 50, "actions fit minimum window");
                        visual.Pages.SelectedIndex = 2; visual.UpdateLayout();
                        visual.PreviewCurrentUpdates(); visual.UpdateLayout();
                        Assert(visual.AppUpdateStatus.Phase == UpdatePhase.Current && visual.EngineUpdateStatus.Phase == UpdatePhase.Current && !visual.AppInstallButton.IsEnabled && !visual.UpdateButton.IsEnabled, "current releases have explicit status and no install action");
                        Assert(!visual.AppUpdateBanner.IsVisible && !visual.EngineUpdateBanner.IsVisible && visual.UpdateDot.Visibility == Visibility.Collapsed, "current releases clear update notices");
                        visual.AppUpdateStatus.BringIntoView(); visual.UpdateLayout();
                        Assert(visual.AppUpdateStatus.ActualHeight >= 40 && visual.AppUpdateStatus.ActualWidth <= visual.AppUpdateSection.ActualWidth - 24, "prominent update status fits narrow window");
                        Assert(visual.AppInstallButton.Visibility == Visibility.Collapsed && visual.AppChangesHeading.Visibility == Visibility.Collapsed && visual.UpdateButton.Visibility == Visibility.Collapsed, "current versions hide unavailable installation and empty notes");
                        visual.PreviewUpdatesState(); visual.UpdateLayout();
                        Assert(visual.UpdatesHeader.ActualHeight >= 34 && visual.UpdatesHeader.TranslatePoint(new Point(), visual).Y >= 0, "update heading remains inside window after status and notice changes");
                        Assert(visual.AppInstallButton.Visibility == Visibility.Visible && visual.AppChangesHeading.Visibility == Visibility.Visible && visual.UpdateButton.Visibility == Visibility.Visible, "available releases restore installation and release details");
                        visual.AppInstallButton.BringIntoView(); visual.UpdateLayout();
                        Assert(visual.AppInstallButton.ActualWidth <= visual.AppUpdateSection.ActualWidth - 30, "app installation button fits narrow update panel");
                        visual.PreviewBusyState(); visual.UpdateLayout();
                        Assert(visual.BusyPanel.IsVisible && visual.UpdateCancelButton.IsVisible, "update progress and cancel reachable outside home");
                    }
                    finally { visual.Close(); }
                    var options = new Settings { StrategyView = "tiles", DismissedAppVersion = "0.9.0", DismissedEngineVersion = "1.11.0" };
                    Store.Save(options); var restored = Store.Load();
                    Assert(restored.StrategyView == "tiles" && restored.DismissedAppVersion == "0.9.0" && restored.DismissedEngineVersion == "1.11.0", "view and dismissed release preferences persist");
                }
                catch (Exception ex) { failure = ex; }
                finally { window.ExitApp(); }
            }));
            app.Run(window); Assert(closed, "explicit exit closes application");
            if (failure != null) throw failure;
        }
        finally
        {
            Store.Root = actualRoot;
            if (Path.GetDirectoryName(temp) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(temp).StartsWith("razret-ui-")) Directory.Delete(temp, true);
        }
        Store.Write("ui-test.json", new { Passed = true, Checks = new[] { "Tray, themes and navigation", "Narrow scrollable list and expanded grid overview", "Manual grid resize, fixed tile height, wheel scrolling and last tile reachability", "Overview restored without scrolling when tiles fit", "Aligned tile indicators and centered metric groups", "Vector icons stay within their shared canvas", "Selection and active strategy preserved across view changes", "Collapse and settings navigation", "App notification independent of engine checks", "Dismissals, new releases and update actions", "Update progress and cancel", "Persistent view and dismissal preferences" }, NetworkChanged = false });
    }
    public static async Task Update()
    {
        string actualRoot = Store.Root, source = Store.Engine;
        string temp = Path.Combine(Path.GetTempPath(), "tiho-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        var messages = new List<string>();
        try
        {
            Store.Root = temp + Path.DirectorySeparatorChar;
            foreach (var path in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(Store.Engine, Path.GetRelativePath(source, path)); Directory.CreateDirectory(Path.GetDirectoryName(dest)!); File.Copy(path, dest);
            }
            File.WriteAllText(Path.Combine(Store.Engine, "lists", "list-general-user.txt"), "example.com\n");
            var latest = await Updater.Latest(CancellationToken.None);
            var candidate = await Updater.Stage(latest, CancellationToken.None);
            Assert(File.ReadAllText(Path.Combine(candidate, "lists", "list-general-user.txt")) == "example.com\n", "user lists migrate");
            messages.Add("PASS: official GitHub API, archive download, digest, structure, strategy validation, user list migration");
            var backup = Updater.Swap(candidate);
            Assert(Updater.Version == latest.Version, "version after swap"); Updater.Rollback(backup);
            Assert(File.ReadAllText(Path.Combine(Store.Engine, "lists", "list-general-user.txt")) == "example.com\n", "rollback preserves user list");
            messages.Add("PASS: installation and rollback in isolated temporary folder");
            bool rejected = false;
            try { await Updater.Stage(latest with { Digest = new string('0', 64) }, CancellationToken.None); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "checksum mismatch rejected"); messages.Add("PASS: invalid SHA256 rejected before replacing engine");
        }
        finally
        {
            Store.Root = actualRoot;
            if (Path.GetDirectoryName(temp) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(temp).StartsWith("tiho-tests-")) Directory.Delete(temp, true);
        }
        Store.Write("update-test.json", new { Passed = true, Tests = messages });
    }
    static void Assert(bool value, string message) { if (!value) throw new Exception("TEST FAILED: " + message); }
    static T? FindVisual<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) return found;
            if (FindVisual<T>(child) is T nested) return nested;
        }
        return null;
    }
    static FrameworkElement? FindNamedVisual(DependencyObject root, string name)
    {
        if (root is FrameworkElement element && element.Name == name) return element;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindNamedVisual(VisualTreeHelper.GetChild(root, i), name) is { } match) return match;
        return null;
    }
    public static void Run()
    {
        var messages = new List<string>();
        var desktopGrid = StrategyLayout.Grid(1920, 1040, 22);
        Assert(desktopGrid.AllVisible && desktopGrid.Columns == 4, "desktop grid displays every strategy in four columns");
        var laptopGrid = StrategyLayout.Grid(1366, 720, 22);
        Assert(laptopGrid.AllVisible && laptopGrid.Width <= 1366 && laptopGrid.Height <= 720 && laptopGrid.TileExtent >= 60, "grid adapts to laptop work area without tiny tiles");
        var limitedGrid = StrategyLayout.Grid(800, 540, 22);
        Assert(!limitedGrid.AllVisible && limitedGrid.Width <= 800 && limitedGrid.Height <= 540, "small work area gets a reachable fallback rather than clipped strategies");
        foreach (var scale in new[] { 1.0, 1.25, 1.5 })
        {
            var scaledGrid = StrategyLayout.Grid(1920 / scale, 1040 / scale, 22);
            Assert(scaledGrid.AllVisible && scaledGrid.Width <= 1920 / scale && scaledGrid.Height <= 1040 / scale, "grid fits logical work area at common display scales");
        }
        messages.Add("PASS: grid overview fits desktop and laptop, constrained screens remain reachable");
        Strategies.Validate(Store.Engine);
        int count = 0;
        foreach (var name in Strategies.List(Store.Engine))
        foreach (var mode in new[] { "off", "tcp", "udp", "all" })
        {
            var args = Strategies.Parse(File.ReadAllText(Path.Combine(Store.Engine, name)), Store.Engine, new() { GameMode = mode });
            Assert(args.Any(a => a.StartsWith("--wf-tcp=")) && args.Any(a => a.StartsWith("--wf-udp=")), name);
            Assert(args.All(a => !a.Contains('%')), "unresolved variable"); count++;
        }
        messages.Add($"PASS: {count} combinations of release strategies and Game Filter");
        var text = File.ReadAllText(Path.Combine(Store.Engine, Strategies.List(Store.Engine)[0]));
        foreach (var bad in new[] { text + "\r\ntaskkill /im winws.exe", text.Replace("%BIN%", "%UNKNOWN%"), text + " & echo unsafe" })
        {
            bool rejected = false; try { Strategies.Parse(bad, Store.Engine, new()); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "unsafe strategy rejected");
        }
        messages.Add("PASS: command injection / unknown variable rejected");
        var complete = new Result("complete", [new("A", true, 100), new("B", true, 100)]);
        var fast = new Result("partial-fast", [new("A", true, 1), new("B", false, 1)]);
        Assert(Checks.Rank([fast, complete]).First() == complete, "availability before delay");
        Assert(Checks.Rank([complete, new("faster-complete", [new("A", true, 20), new("B", true, 20)])]).First().Name == "faster-complete", "latency tie breaker");
        messages.Add("PASS: ranking prioritizes complete coverage, then response delay");
        var unavailableRow = MainWindow.Row.From("general.bat", null, null, "general.bat");
        Assert(unavailableRow.Discord == "—" && unavailableRow.Ping == "—" && unavailableRow.Marker == "сохранена", "unknown status is not a pass");
        var rowResult = new Result("general.bat", [new("Discord API", true, 50, "", 5), new("Discord CDN", true, 80, "", 7), new("Discord Gateway", true, 100), new("YouTube", true, 120), new("YouTube изображения", false, 150)]);
        var testedRow = MainWindow.Row.From(rowResult.Name, rowResult, rowResult.Name, rowResult.Name);
        Assert(testedRow.Discord == "✓" && testedRow.Youtube == "✕" && testedRow.Marker == "● активна" && testedRow.Ping == "6 мс", "per-service display and active state");
        messages.Add("PASS: active strategy, per-service marks and separate ping display");
        Assert(Quality.Ping(rowResult) == "good" && Quality.Response(rowResult) == "good" && Quality.Checks(rowResult) == "warning", "metrics assess latency separately from availability");
        Assert(Quality.Ping(rowResult with { Probes = rowResult.Probes.Select(p => p with { Ping = 100 }).ToList() }) == "warning", "intermediate ping is amber");
        Assert(Quality.Ping(rowResult with { Probes = rowResult.Probes.Select(p => p with { Ping = 200 }).ToList() }) == "bad", "slow ping is red");
        Assert(Quality.Response(rowResult with { Probes = rowResult.Probes.Select(p => p with { Milliseconds = 900 }).ToList() }) == "warning", "intermediate response is amber");
        Assert(Quality.Response(rowResult with { Probes = rowResult.Probes.Select(p => p with { Milliseconds = 2000 }).ToList() }) == "bad", "slow response is red");
        Assert(Quality.Checks(rowResult with { Probes = rowResult.Probes.Select(p => p with { Ok = p.Service != "Discord Gateway" }).ToList() }) == "bad", "unavailable Discord is red even when other probes pass");
        Assert(Quality.Checks(null) == "unknown" && Quality.Ping(null) == "unknown", "untested metrics are neutral");
        Assert(MainWindow.Row.From("general.bat", rowResult, null, null).TestState == "warning", "partial coverage is orange");
        var allPassed = rowResult with { Probes = rowResult.Probes.Select(p => p with { Ok = true }).ToList() };
        Assert(MainWindow.Row.From("general.bat", allPassed, null, null).TestState == "good", "complete low-latency coverage is green");
        Assert(MainWindow.Row.From("general.bat", allPassed with { Probes = allPassed.Probes.Select(p => p with { Ping = 250 }).ToList() }, null, null).TestState == "warning", "high ping is orange even with complete coverage");
        Assert(MainWindow.Row.From("general.bat", allPassed with { Probes = allPassed.Probes.Select(p => p with { Ok = false }).ToList() }, null, null).TestState == "bad", "failed coverage is red");
        Assert(MainWindow.Row.From("general.bat", null, null, null).TestState == "unknown", "untested strategy is neutral");
        using (var wire = new MemoryStream(new byte[] { 0x01, 6 }.Concat(Encoding.UTF8.GetBytes("{\"op\":"))
            .Concat(new byte[] { 0x80, 3 }).Concat(Encoding.UTF8.GetBytes("10}")).ToArray()))
        using (var socket = System.Net.WebSockets.WebSocket.CreateFromStream(wire, false, null, Timeout.InfiniteTimeSpan))
            Assert(Checks.ReadHello(socket, CancellationToken.None).GetAwaiter().GetResult(), "fragmented Discord Hello accepted");
        int attempts = 0;
        var retry = Checks.Retry(() => Task.FromResult(new Probe("Discord API", ++attempts == 2, 1, "fixture")), CancellationToken.None).GetAwaiter().GetResult();
        Assert(retry.Ok && attempts == 2 && retry.Detail.Contains("второй"), "transient failure retried and disclosed");
        attempts = 0;
        _ = Checks.Retry(() => { attempts++; return Task.FromResult(new Probe("Discord API", true, 1)); }, CancellationToken.None).GetAwaiter().GetResult();
        Assert(attempts == 1, "successful probe is not repeated");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); bool stopped = false;
            try { Checks.Retry(() => Task.FromResult(new Probe("Discord API", false, 1)), cancelled.Token).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { stopped = true; }
            Assert(stopped, "retry respects cancellation");
        }
        Assert(AppUpdater.IsNewer("v0.8.0") && !AppUpdater.IsNewer(AppUpdater.Version) && !AppUpdater.IsNewer("0.6.0") && !AppUpdater.IsNewer("garbage"), "app update versions");
        messages.Add("PASS: fragmented Discord Hello, transient retry, cancellation and app version comparison");
        var notes = ReleaseNotesRu.Summarize("## What's Changed\n* Новая стратегия **ALT13**\n* Улучшение диагностики by @someone in https://github.com/example\n* Added experimental recovery mechanism\n## New Contributors\n* @contributor made their first contribution");
        Assert(notes.Contains("Новая стратегия ALT13") && notes.Contains("Улучшение диагностики") && !notes.Contains("experimental") && !notes.Contains("contribution"), "Russian-only summary");
        var latestNotes = ReleaseNotesRu.Summarize("* Изменен стандартный TLS фейк в **ALT11**, **ALT12**, **EXP**, **SIMPLE FAKE ALT2**\n* Он использовался в HTTP/GameFilter-TCP\n* Обновлен IPSet до последней версии by @V3nilla in https://github.com/example\n* Добавлен детект Happ в диагностике");
        Assert(latestNotes.Contains("ALT11") && latestNotes.Contains("GameFilter-TCP") && latestNotes.Contains("Happ") && !latestNotes.Contains("@V3nilla"), "Russian upstream technical details are retained");
        Assert(ReleaseNotesRu.Summarize("Updated strategies\nImproved diagnostics").Contains("Обновлены стратегии"), "known English changes have a Russian summary");
        messages.Add("PASS: Russian release summary preserves technical changes and excludes contributor noise");
        var engineNotes = "* Изменен TLS фейк в ALT12\n* Новые фейки TLS+QUIC\n* Обновлен IPSet\n* Добавлен детект Happ в диагностике\n* Улучшена диагностика DNS\n* Добавлен новый неизвестный механизм";
        var explained = ReleaseNotesRu.ForEngine(engineNotes, "none");
        Assert(explained.Contains("ALT12") && explained.Contains("не означает, что все они включены") && explained.Contains("список IP-адресов не используется"), "engine changes explain strategy and IPSet applicability");
        Assert(explained.Contains("Happ: проверка службы уже встроена") && explained.Contains("не добавляются автоматически") && explained.Contains("применимость в Razret не подтверждена"), "engine changes separate supported diagnostics, external tools and unknown features");
        Assert(ReleaseNotesRu.ForEngine(engineNotes, "loaded").Contains("будет использоваться") && ReleaseNotesRu.ForEngine(engineNotes, "any").Contains("список не ограничивает"), "engine notes follow current IPSet mode");
        Assert(!ReleaseNotesRu.ForEngine("* Улучшена диагностика Happ", "none").Contains("уже встроена"), "unknown future Happ diagnostics are not claimed as implemented");
        Assert(!ReleaseNotesRu.ForEngine("* Обновлена служба winws", "none").Contains("Изменения стратегий и компонентов по описанию"), "service changes do not become automatic GUI features");
        Assert(HappDiagnostics.Describe(new(null, 1060)).Contains("не установлена") && HappDiagnostics.Describe(new(1)).Contains("остановлена"), "Happ absent and stopped states are distinct");
        Assert(HappDiagnostics.Describe(new(4)).Contains("не доказывает конфликт") && HappDiagnostics.Describe(new(2)).Contains("переходном"), "running Happ is advisory, pending state is not stopped");
        Assert(HappDiagnostics.Describe(new(null, 5)).Contains("не удалось") && HappDiagnostics.Describe(new(null)).Contains("неизвестно"), "Happ query errors never report a clean system");
        Assert(!string.IsNullOrWhiteSpace(HappDiagnostics.Describe(HappDiagnostics.Check())), "real Windows service query completes without changing services");
        messages.Add("PASS: contextual engine notes and read-only Happ diagnostics, including absent, stopped, running and error states");
        var taskXml = System.Xml.Linq.XDocument.Parse(Startup.DefinitionXml());
        var ns = taskXml.Root!.Name.Namespace;
        Assert(taskXml.Descendants(ns + "RunLevel").Single().Value == "HighestAvailable", "elevated autostart");
        Assert(taskXml.Descendants(ns + "Arguments").Single().Value == "--startup", "startup mode");
        Assert(taskXml.Descendants(ns + "LogonTrigger").Any() && taskXml.Descendants(ns + "Delay").Single().Value == "PT20S", "logon trigger");
        Assert(taskXml.Descendants(ns + "LogonType").Single().Value == "InteractiveToken", "passwordless interactive logon");
        messages.Add("PASS: Windows scheduler validates startup definition (no task registered)");
        _ = Startup.Enabled();
        messages.Add("PASS: missing Windows startup task is handled without an error");
        var actualRoot = Store.Root;
        string temp = Path.Combine(Path.GetTempPath(), "tiho-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(temp);
        try
        {
            var zip = Path.Combine(temp, "bad.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create)) { using var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open()); writer.Write("bad"); }
            bool rejected = false; try { Updater.Extract(zip, Path.Combine(temp, "extract")); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected && !File.Exists(Path.Combine(temp, "escape.txt")), "zip slip"); messages.Add("PASS: archive traversal rejected");
            Store.Root = temp + Path.DirectorySeparatorChar;
            var targetExe = Path.Combine(temp, "Razret.exe"); var candidateExe = Path.Combine(temp, "candidate.exe"); var backupExe = Path.Combine(temp, "previous.exe");
            File.WriteAllText(targetExe, "old"); File.WriteAllText(candidateExe, "new");
            AppUpdater.Replace(candidateExe, targetExe, backupExe);
            Assert(File.ReadAllText(targetExe) == "new" && File.ReadAllText(backupExe) == "old", "atomic app replacement and backup");
            var appZip = Path.Combine(temp, "app.zip");
            using (var archive = ZipFile.Open(appZip, ZipArchiveMode.Create)) { using var writer = new StreamWriter(archive.CreateEntry("data/settings.json").Open()); writer.Write("bad"); }
            rejected = false; try { AppUpdater.Extract(appZip, temp); } catch (InvalidDataException) { rejected = true; }
            Assert(rejected, "app archive cannot overwrite user data");
            messages.Add("PASS: atomic app replacement, backup and rejection of unexpected app archive files");
            Directory.CreateDirectory(Store.Engine); File.WriteAllText(Path.Combine(Store.Engine, "marker"), "old");
            var candidate = Path.Combine(temp, "candidate"); Directory.CreateDirectory(candidate); File.WriteAllText(Path.Combine(candidate, "marker"), "new");
            var backup = Updater.Swap(candidate); Assert(File.ReadAllText(Path.Combine(Store.Engine, "marker")) == "new", "swap");
            Updater.Recover(); Assert(File.ReadAllText(Path.Combine(Store.Engine, "marker")) == "old", "crash recovery");
            Assert(!File.Exists(Path.Combine(Store.Data, "update-journal.json")), "journal cleanup"); messages.Add("PASS: interrupted update restores previous directory");
            var settings = new Settings { Strategy = "general.bat", GameMode = "udp", Ipset = "loaded", SetupComplete = true }; Store.Save(settings);
            Assert(Store.Load().GameMode == "udp" && Store.Load().Strategy == settings.Strategy, "persistence"); messages.Add("PASS: settings persistence");
            using (var engine = new Engine()) Assert(!engine.Running, "job object initializes without running engine");
            messages.Add("PASS: Windows process job initializes");
        }
        finally
        {
            Store.Root = actualRoot;
            // Only remove the newly created, uniquely named test directory after verifying its parent.
            if (Path.GetDirectoryName(temp) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) && Path.GetFileName(temp).StartsWith("tiho-tests-")) Directory.Delete(temp, true);
        }
        Store.Write("self-test.json", new { Passed = true, Tests = messages });
    }
}
