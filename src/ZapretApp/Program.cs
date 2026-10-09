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
                    if (args.Contains("--snapshot-dark")) Appearance.Apply(window, "dark");
                    if (args.Contains("--snapshot-advanced")) { window.Pages.SelectedIndex = 1; window.AdvancedExpander.IsExpanded = true; }
                    if (args.Contains("--snapshot-active")) window.PreviewActiveState();
                    if (args.Contains("--snapshot-light")) { Appearance.Apply(window, "light"); window.PreviewActiveState(); }
                    if (args.Contains("--snapshot-narrow")) { window.Width = 500; window.Height = 620; window.PreviewActiveState(true); }
                    if (args.Contains("--snapshot-other")) window.PreviewActiveState(true);
                    if (args.Contains("--snapshot-hidden")) window.StrategiesToggleButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    if (args.Contains("--snapshot-tiles")) window.ViewModeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    if (args.Contains("--snapshot-notices")) window.PreviewUpdatesState();
                    if (args.Contains("--snapshot-busy")) window.PreviewBusyState();
                    if (args.Contains("--snapshot-expanded")) window.SelectedExpander.IsOpen = true;
                    var snapshotDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                    snapshotDelay.Tick += (_, _) =>
                    {
                    snapshotDelay.Stop();
                    window.UpdateLayout();
                    window.StrategyCard.InvalidateMeasure(); window.UpdateLayout();
                    Store.Write("preview-layout.json", new { window.Width, window.Height, WindowContentHeight = ((FrameworkElement)window.Content).ActualHeight, Elements = new FrameworkElement[] { window.StrategyCard, window.MetricsGrid, window.Pages, window.StrategiesHeader, window.ResultsPanel, window.Footer, window.UpdateBadge, window.FooterSettingsButton }.Select(e => new { e.Name, Width = e.ActualWidth, Height = e.ActualHeight, Y = e.TranslatePoint(new Point(), window).Y, e.IsVisible }) });
                    var content = (FrameworkElement)window.Content;
                    if (args.Contains("--snapshot-expanded")) { window.SelectedExpander.Child.UpdateLayout(); content = (FrameworkElement)window.SelectedExpander.Child; }
                    var bitmap = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                    bitmap.Render(content); var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
                    var filename = args.Contains("--snapshot-light") ? "preview-light.png" : args.Contains("--snapshot-settings") ? "preview-settings.png" : args.Contains("--snapshot-updates") ? "preview-updates.png" : args.Contains("--snapshot-active") ? "preview-active.png" : args.Contains("--snapshot-other") ? "preview-other.png" : "preview.png";
                    if (args.Contains("--snapshot-hidden")) filename = "preview-hidden.png";
                    if (args.Contains("--snapshot-busy")) filename = "preview-busy.png";
                    if (args.Contains("--snapshot-tiles")) filename = "preview-tiles.png";
                    if (args.Contains("--snapshot-notices")) filename = "preview-notices.png";
                    if (args.Contains("--snapshot-narrow")) filename = "preview-narrow.png";
                    if (args.Contains("--snapshot-advanced")) filename = "preview-advanced.png";
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
            var app = new Application(); var window = new MainWindow(quiet: true,
                latestAppRelease: _ => Task.FromResult<Release?>(new Release("99.0.0", "https://example.invalid/update.zip", "", 1, "Проверка уведомления")));
            window.Closed += (_, _) => closed = true;
            window.Loaded += (_, _) => window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                try
                {
                    Assert(window.AppUpdateBanner.IsVisible, "app notice appears with automatic engine checks disabled");
                    Assert(window.AppInstallButton.IsEnabled && window.AppReleaseNotes.Text == "Проверка уведомления", "app update waits for explicit installation");
                    Assert(window.AppUpdateBanner.TranslatePoint(new Point(), window).Y < window.StrategyCard.TranslatePoint(new Point(), window).Y, "notice above current strategy");
                    window.AppNotice.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert(window.Pages.SelectedIndex == 2, "notice opens app updates");
                    window.FooterSettingsButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert(window.Pages.SelectedIndex == 1, "bottom navigation opens settings from updates");
                    window.Pages.SelectedIndex = 0;
                    window.DismissAppButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                    Assert(!window.AppUpdateBanner.IsVisible && window.UpdateDot.Visibility == Visibility.Visible && window.AppInstallButton.IsEnabled, "dismissal hides banner but preserves available update");
                    window.Close(); Assert(!closed && !window.IsVisible, "close hides to tray");
                    window.ShowFromTray(); Assert(window.IsVisible && !closed, "restore from tray");
                    window.Pages.SelectedIndex = 1; window.UpdateLayout(); Assert(window.StartupBox.IsVisible, "settings reachable");
                    var dark = ((SolidColorBrush)window.Resources["Canvas"]).Color;
                    window.ThemeBox.SelectedIndex = 1; Assert(((SolidColorBrush)window.Resources["Canvas"]).Color != dark, "theme switches without restart");
                    window.ThemeBox.SelectedIndex = 0; Assert(((SolidColorBrush)window.Resources["Canvas"]).Color == dark, "dark theme restored");
                    var visual = new MainWindow(preview: true);
                    try
                    {
                        visual.Show(); visual.ResultsGrid.SelectedIndex = 2;
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
                        Assert(visual.ViewModeButton.ToolTip.ToString() == "Показать списком", "tiles offer list alternative");
                        visual.ViewModeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); visual.UpdateLayout();
                        Assert(visual.Width == 560, "return to list restores narrow window");
                        var last = visual.ResultsGrid.Items[visual.ResultsGrid.Items.Count - 1];
                        visual.ResultsGrid.ScrollIntoView(last); visual.UpdateLayout();
                        var lastRow = (System.Windows.Controls.ListBoxItem)visual.ResultsGrid.ItemContainerGenerator.ContainerFromItem(last);
                        var end = lastRow.TranslatePoint(new Point(0, lastRow.ActualHeight), visual.ResultsGrid);
                        Assert(end.Y <= visual.ResultsGrid.ActualHeight + 1 && end.Y > 0, "last strategy reachable by scrolling");
                        Assert(!visual.CardCaption.IsVisible && !visual.SelectionStatus.IsVisible && !visual.CardSubtitle.IsVisible, "redundant captions hidden");
                        visual.BackToActiveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
                        Assert(((MainWindow.Row)visual.ResultsGrid.SelectedItem).IsActive, "return to active action");
                        var expanded = visual.Height;
                        visual.StrategiesToggleButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Button.ClickEvent)); visual.UpdateLayout();
                        Assert(visual.Height < expanded && !visual.ResultsPanel.IsVisible, "collapse reduces height");
                        var collapsedFooter = visual.UpdateBadge.TranslatePoint(new Point(0, visual.UpdateBadge.ActualHeight), (UIElement)visual.Content);
                        Assert(collapsedFooter.Y <= ((FrameworkElement)visual.Content).ActualHeight - 8 && visual.StrategiesHeader.ActualHeight >= 30, "compact controls fit without clipping");
                        visual.Pages.SelectedIndex = 1; visual.UpdateLayout();
                        Assert(visual.Height >= 620 && visual.StartupBox.IsVisible, "settings expand collapsed window");
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
                        visual.Width = 500; visual.Height = 620; visual.UpdateLayout();
                        Assert(visual.ResultsGrid.ActualHeight >= 120, "minimum window retains useful list viewport");
                        var action = visual.AutoButton.TranslatePoint(new Point(visual.AutoButton.ActualWidth, visual.AutoButton.ActualHeight), visual);
                        Assert(action.X < visual.ActualWidth - 12 && action.Y < visual.ActualHeight - 50, "actions fit minimum window");
                        visual.Pages.SelectedIndex = 2; visual.UpdateLayout();
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
        Store.Write("ui-test.json", new { Passed = true, Checks = new[] { "Tray, themes and navigation", "Narrow scrollable list and expanded grid overview", "Selection and active strategy preserved across view changes", "Collapse and settings navigation", "App notification independent of engine checks", "Dismissals, new releases and update actions", "Update progress and cancel", "Persistent view and dismissal preferences" }, NetworkChanged = false });
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
    public static void Run()
    {
        var messages = new List<string>();
        var desktopGrid = StrategyLayout.Grid(1920, 1040, 22);
        Assert(desktopGrid.AllVisible && desktopGrid.Columns == 4, "desktop grid displays every strategy in four columns");
        var laptopGrid = StrategyLayout.Grid(1366, 720, 22);
        Assert(laptopGrid.AllVisible && laptopGrid.Width <= 1366 && laptopGrid.Height <= 720 && laptopGrid.TileExtent >= 64, "grid adapts to laptop work area without tiny tiles");
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
        messages.Add("PASS: Russian release summary excludes untranslated prose and contributor noise");
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
