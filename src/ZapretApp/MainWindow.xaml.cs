using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Windows.Documents;
using System.Windows.Media;

namespace ZapretApp;

public partial class MainWindow : Window
{
    readonly Engine engine = new();
    readonly Settings settings;
    readonly Statistics statistics;
    bool loadingStatistics = true;
    readonly DispatcherTimer statisticsTimer = new() { Interval = TimeSpan.FromMinutes(10) };
    CancellationTokenSource? operation;
    Release? release;
    Release? appRelease;
    Task? updateCheck;
    bool appCheckRunning;
    string? notifiedAppVersion;
    readonly CancellationTokenSource lifetime = new();
    readonly DispatcherTimer appUpdateMonitor = new() { Interval = TimeSpan.FromHours(6) };
    readonly List<Result> results = [];
    readonly DispatcherTimer monitor = new() { Interval = TimeSpan.FromSeconds(3) };
    bool wasRunning;
    bool allowExit, trayHintShown;
    readonly bool preview;
    readonly bool quiet;
    readonly TrayIcon? tray;
    DateTimeOffset? testedAt;
    Row? cardProfile;
    internal string? PowerStrategy => cardProfile?.Name;
    bool loadingTheme = true;
    double expandedHeight = 760;
    double listWidth = 560;

    readonly Func<CancellationToken, Task<Release?>> latestAppRelease;
    public MainWindow(bool preview = false, bool startup = false, bool quiet = false, Func<CancellationToken, Task<Release?>>? latestAppRelease = null)
    {
        this.latestAppRelease = latestAppRelease ?? AppUpdater.Latest;
        InitializeComponent(); PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { HelpPopup.IsOpen = false; SelectedExpander.IsOpen = false; } }; this.preview = preview; this.quiet = quiet; settings = Store.Load();
        statistics = new(settings.ShareStatistics && settings.StatisticsConsentVersion >= 2, preview || quiet);
        StatisticsBox.IsChecked = settings.ShareStatistics && settings.StatisticsConsentVersion >= 2;
        ShowStatisticsIdentity();
        loadingStatistics = false;
        Appearance.Apply(this, settings.Theme); Select(ThemeBox, settings.Theme); loadingTheme = false;
        SourceInitialized += (_, _) => Appearance.Apply(this, settings.Theme);
        TrayBox.IsChecked = settings.CloseToTray;
        try { StartupBox.IsChecked = Startup.Enabled(); }
        catch (Exception ex) { StartupBox.IsChecked = settings.StartWithWindows; Footer.Text = "Не удалось прочитать автозапуск: " + ex.Message; }
        if (!preview) tray = new TrayIcon(() => Dispatcher.Invoke(ShowFromTray), () => Dispatcher.Invoke(ExitApp));
        Select(GameBox, settings.GameMode); Select(IpsetBox, settings.Ipset);
        AutoConnectBox.IsChecked = settings.AutoConnect; UpdatesBox.IsChecked = settings.CheckUpdates;
        var saved = Path.Combine(Store.Data, "last-results.json");
        if (File.Exists(saved))
        {
            try
            {
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(saved));
                var root = json.RootElement;
                if (root.TryGetProperty("Date", out var date)) testedAt = date.GetDateTimeOffset();
                if (root.GetProperty("Engine").GetString() == Updater.Version && root.GetProperty("GameMode").GetString() == settings.GameMode && root.GetProperty("Ipset").GetString() == settings.Ipset)
                    results.AddRange(System.Text.Json.JsonSerializer.Deserialize<List<Result>>(root.GetProperty("Results")) ?? []);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or KeyNotFoundException) { Store.Log("Не удалось прочитать прошлые результаты: " + ex.Message); }
        }
        ApplyStrategyView();
        ApplyStrategiesVisibility(false);
        AppVersionText.Text = AppUpdater.Version;
        EngineVersionText.Text = Updater.Version;
        Refresh();
        if (settings.SetupComplete) { StatusTitle.Text = "Готов к подключению"; StatusDetail.Text = "Сохранённый профиль готов. Повторный подбор не нужен."; }
        AdminPanel.Visibility = AdminButton.Visibility = Engine.Admin ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) =>
        {
            if (preview) return;
            if (!quiet && Statistics.Configured && (!settings.StatisticsConsentAsked || settings.ShareStatistics && settings.StatisticsConsentVersion < 2))
                ApplyStatisticsConsent(AskStatisticsConsent());
            ReadUpdateReceipt();
            if (startup) Hide();
            if (settings.SetupComplete && (settings.AutoConnect || startup) && settings.Strategy != null && Engine.Admin)
                await Work(async ct => { await engine.Start(settings.Strategy, settings, ct); Connected(); }, "strategy_launch");
            else if (settings.SetupComplete && !Engine.Admin) Footer.Text = "Для включения обхода нажмите «Права администратора».";
            await Task.WhenAll(CheckAppUpdate(lifetime.Token), settings.CheckUpdates ? CheckUpdate() : Task.CompletedTask);
        };
        appUpdateMonitor.Tick += async (_, _) => { if (operation == null) await CheckAppUpdate(lifetime.Token); };
        if (!preview) appUpdateMonitor.Start();
        statisticsTimer.Tick += async (_, _) => await statistics.Flush(lifetime.Token);
        if (!preview && !quiet) statisticsTimer.Start();
        monitor.Tick += (_, _) => { bool running = engine.Running; if (operation == null && wasRunning && !running) { statistics.UnexpectedExit(settings.Strategy); StatusTitle.Text = "Обход остановился"; StatusDetail.Text = "Движок завершился. Откройте журнал или повторите подбор."; ToggleButton.Content = "Включить"; Refresh(); } wasRunning = running; };
        if (!preview) monitor.Start();
        Closing += (_, e) => { if (!preview && !allowExit && settings.CloseToTray) { e.Cancel = true; HideToTray(); return; } if (operation != null) { operation.Cancel(); e.Cancel = true; Footer.Text = "Отменяем операцию и восстанавливаем состояние. После завершения можно закрыть окно."; } };
        Closed += (_, _) => { lifetime.Cancel(); statisticsTimer.Stop(); statistics.Dispose(); appUpdateMonitor.Stop(); monitor.Stop(); tray?.Dispose(); engine.Dispose(); };
    }
    void ReadUpdateReceipt()
    {
        if (quiet) return;
        var path = Path.Combine(Store.Data, "app-update-result.json");
        try
        {
            if (!File.Exists(path)) return;
            var receipt = System.Text.Json.JsonSerializer.Deserialize<AppUpdateReceipt>(File.ReadAllText(path));
            File.Delete(path);
            if (receipt != null && (receipt.Outcome == "failure" || receipt.Version == AppUpdater.Version))
                statistics.Update("app_install", receipt.Outcome, receipt.Version);
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }
    }
    void StatisticsChanged(object sender, RoutedEventArgs e)
    {
        if (loadingStatistics) return;
        var value = StatisticsBox.IsChecked == true;
        if (value && settings.StatisticsConsentVersion < 2 && !preview && !quiet) value = AskStatisticsConsent();
        ApplyStatisticsConsent(value);
    }
    bool AskStatisticsConsent() => MessageBox.Show(this,
        "Помочь улучшить Razret?\n\nОтправлять результаты проверок стратегий, технические коды ошибок и итоги обновлений. Случайный постоянный код свяжет отчёты этой установки, чтобы разработчик мог разобраться с вашей проблемой. Его можно сообщить разработчику.\n\nБез истории включений, точного времени действий, имени компьютера, журналов и содержимого трафика. Отправку можно выключить в любой момент; код и неотправленные отчёты удалятся.",
        "Статистика качества", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes;
    void ApplyStatisticsConsent(bool value)
    {
        settings.ShareStatistics = value; settings.StatisticsConsentAsked = true; settings.StatisticsConsentVersion = 2;
        Store.Save(settings); statistics.SetEnabled(value && !preview && !quiet);
        loadingStatistics = true; StatisticsBox.IsChecked = value; loadingStatistics = false;
        ShowStatisticsIdentity();
    }
    void ShowStatisticsIdentity()
    {
        StatisticsIdentityPanel.Visibility = statistics.SupportCode == null ? Visibility.Collapsed : Visibility.Visible;
        StatisticsCode.Text = statistics.SupportCode ?? "";
    }
    void CopyStatisticsCode(object sender, RoutedEventArgs e)
    {
        if (statistics.SupportCode is string code) { Clipboard.SetText(code); Footer.Text = "Код тестировщика скопирован."; }
    }
    static void Select(ComboBox box, string tag) => box.SelectedItem = box.Items.Cast<ComboBoxItem>().First(x => (string)x.Tag == tag);
    void ApplyStrategiesVisibility(bool resize)
    {
        var hidden = settings.HideStrategies;
        ResultsPanel.Visibility = ResultsGrid.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        StrategyActions.Visibility = !hidden && (ManualButton.Visibility == Visibility.Visible || BackToActiveButton.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;
        ViewModeButton.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
        StrategiesChevron.RenderTransform = new RotateTransform(hidden ? 180 : 0);
        StrategiesToggleButton.ToolTip = hidden ? "Показать стратегии" : "Свернуть стратегии";
        System.Windows.Automation.AutomationProperties.SetName(StrategiesToggleButton, (string)StrategiesToggleButton.ToolTip);
        ResizeForContent();
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(ResizeForContent));
    }
    void ToggleStrategiesVisibility(object sender, RoutedEventArgs e)
    {
        SelectedExpander.IsOpen = false;
        if (!settings.HideStrategies && settings.StrategyView != "tiles" && WindowState == WindowState.Normal)
        { expandedHeight = Math.Max(620, Height - NoticeHeight); listWidth = Width; }
        settings.HideStrategies = !settings.HideStrategies;
        ApplyStrategiesVisibility(true);
        if (!preview && !quiet) Store.Save(settings);
    }
    double NoticeHeight => (AppUpdateBanner.Visibility == Visibility.Visible ? 76 : 0) + (EngineUpdateBanner.Visibility == Visibility.Visible ? 76 : 0);
    void ResizeForContent()
    {
        if (settings == null || Pages == null) return;
        bool compact = settings.HideStrategies && Pages.SelectedIndex == 0;
        double available = SystemParameters.WorkArea.Height;
        Pages.MinHeight = compact ? 48 : Pages.SelectedIndex == 0 ? 160 : 240;
        bool grid = !compact && Pages.SelectedIndex == 0 && settings.StrategyView == "tiles";
        MinWidth = Math.Min(SystemParameters.WorkArea.Width, grid ? 720 : 520);
        MinHeight = Math.Min(available, compact ? 290 : grid ? 540 : 620);
        MaxHeight = compact ? available : double.PositiveInfinity;
        if (WindowState == WindowState.Normal)
        {
            SizeToContent = compact ? SizeToContent.Height : SizeToContent.Manual;
            if (grid)
            {
                var layout = StrategyLayout.Grid(SystemParameters.WorkArea.Width, available, Math.Max(ResultsGrid.Items.Count, Strategies.List(Store.Engine).Length),
                    NoticeHeight + (operation != null ? 64 : 0) + (AdminButton.Visibility == Visibility.Visible ? 46 : 0)
                    + (StrategyActions.Visibility == Visibility.Visible ? 44 : 0) + (string.IsNullOrEmpty(Footer.Text) ? 0 : 24));
                var panel = new FrameworkElementFactory(typeof(System.Windows.Controls.Primitives.UniformGrid));
                panel.SetValue(System.Windows.Controls.Primitives.UniformGrid.ColumnsProperty, layout.Columns);
                ResultsGrid.ItemsPanel = new ItemsPanelTemplate(panel);
                var tileStyle = new Style(typeof(ListBoxItem), (Style)Resources["StrategyTile"]);
                tileStyle.Setters.Add(new Setter(FrameworkElement.HeightProperty, layout.TileExtent - 6));
                ResultsGrid.ItemContainerStyle = tileStyle;
                ResultsGrid.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, layout.AllVisible ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto);
                Width = layout.Width; Height = layout.Height;
            }
            else
            {
                Width = Math.Min(SystemParameters.WorkArea.Width, listWidth);
                ResultsGrid.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Auto);
                if (!compact) Height = Math.Min(available, expandedHeight + NoticeHeight);
            }
        }
    }
    void ApplyStrategyView()
    {
        bool tiles = settings.StrategyView == "tiles";
        ResultsGrid.ItemsPanel = (ItemsPanelTemplate)Resources[tiles ? "StrategyTilePanel" : "StrategyListPanel"];
        ResultsGrid.ItemTemplate = (DataTemplate)Resources[tiles ? "StrategyTileTemplate" : "StrategyListTemplate"];
        ResultsGrid.ItemContainerStyle = (Style)Resources[tiles ? "StrategyTile" : "StrategyRow"];
        ViewModeIcon.Data = (Geometry)Resources[tiles ? "IconList" : "IconGrid"];
        ViewModeButton.ToolTip = tiles ? "Показать списком" : "Показать плашками";
        System.Windows.Automation.AutomationProperties.SetName(ViewModeButton, (string)ViewModeButton.ToolTip);
    }
    void ToggleStrategyView(object sender, RoutedEventArgs e)
    {
        SelectedExpander.IsOpen = false;
        if (settings.StrategyView != "tiles" && WindowState == WindowState.Normal)
        { listWidth = Width; expandedHeight = Math.Max(620, Height - NoticeHeight); }
        settings.StrategyView = settings.StrategyView == "tiles" ? "list" : "tiles";
        ApplyStrategyView();
        ResizeForContent();
        if (ResultsGrid.SelectedItem is Row row) Dispatcher.BeginInvoke(new Action(() => ResultsGrid.ScrollIntoView(row)));
        if (!preview && !quiet) Store.Save(settings);
    }
    void PageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != Pages || settings == null) return;
        HelpPopup.IsOpen = SelectedExpander.IsOpen = false;
        ShowUpdateNotices();
        UpdateCancelButton.Visibility = operation != null && Pages.SelectedIndex != 0 ? Visibility.Visible : Visibility.Collapsed;
        ResizeForContent();
    }
    void SettingsClick(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 1;
    void DiagnosticsClick(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 3;
    void Refresh()
    {
        VersionLabel.Text = "zapret " + Updater.Version;
        ModeLabel.Text = settings.SetupComplete ? "" : "ПЕРВЫЙ ЗАПУСК";
        var selected = (ResultsGrid.SelectedItem as Row)?.Name ?? engine.Active ?? settings.Strategy;
        var rows = Checks.Rank(results).Select(r => Row.From(r.Name, r, engine.Running ? engine.Active : null, settings.Strategy)).ToList();
        rows.AddRange(Strategies.List(Store.Engine).Where(n => results.All(r => r.Name != n)).Select(n => Row.From(n, null, engine.Running ? engine.Active : null, settings.Strategy)));
        rows = rows.OrderByDescending(r => r.IsActive).ToList();
        ResultsGrid.ItemsSource = rows;
        ResultsGrid.SelectedItem = rows.FirstOrDefault(r => r.Name == selected) ?? rows.FirstOrDefault();
        AutoButton.Content = "Подобрать";
        AutoButton.SetResourceReference(Control.BackgroundProperty, "Accent");
        AutoButton.SetResourceReference(Control.ForegroundProperty, "AccentText");
        UpdateActive();
        ResizeForContent();

    }
    public record Row(string Name, string DisplayName, string Marker, string Discord, string Youtube, string Ping, string Response, Result? Result)
    {
        public bool IsActive => Marker == "● активна";
        public string TestState
        {
            get
            {
                if (Result == null) return "unknown";
                var groups = Result.Probes.GroupBy(p => p.Service).ToList();
                int passed = groups.Count(g => g.All(p => p.Ok));
                if (groups.Count == 0 || passed * 2 < groups.Count) return "bad";
                var pings = Result.Probes.Where(p => p.Ping.HasValue).Select(p => p.Ping!.Value).ToArray();
                return Result.Complete && Result.Delay <= 1500 && (pings.Length == 0 || pings.Average() <= 150) ? "good" : "warning";
            }
        }
        public string TileServices => $"D {Discord}   Y {Youtube}";
        public string PassedChecks
        {
            get
            {
                var groups = Result?.Probes.GroupBy(p => p.Service).ToList();
                return groups?.Count > 0 ? $"{groups.Count(g => g.All(p => p.Ok))}/{groups.Count}" : "—";
            }
        }
        public string CompactResult => Result == null ? "Не проверена" : $"{PassedChecks} · {Response}";
        public string TileTip => $"{DisplayName}\nDiscord: {Discord} · YouTube: {Youtube}\nPing: {Ping} · Отклик: {Response}\n" + (TestState switch { "good" => "Все проверки пройдены; ping до 150 мс, отклик до 1500 мс", "warning" => "Часть проверок не прошла или задержка повышена", "bad" => "Большинство проверок не прошло", _ => "Ещё не проверялась" }) + (IsActive ? "\nВключена сейчас" : "") + "\nДвойной щелчок — подробности проверки";
        public static string ShortName(string name) => System.Text.RegularExpressions.Regex.Replace(name.Replace("general (", "").Replace(").bat", "").Replace("general.bat", "Основная"), @"ALT(\d+)", "ALT $1");
        public static Row From(string name, Result? result, string? active, string? saved)
        {
            var ping = result?.Probes.Where(p => p.Ping.HasValue).Select(p => p.Ping!.Value).ToArray() ?? [];
            string Group(string prefix, int count)
            {
                var probes = result?.Probes.Where(p => p.Service.StartsWith(prefix)).ToList() ?? [];
                return probes.Select(p => p.Service).Distinct().Count() < count ? "—" : probes.All(p => p.Ok) ? "✓" : "✕";
            }
            return new(name, ShortName(name), name == active ? "● активна" : name == saved ? "сохранена" : "", Group("Discord", 3), Group("YouTube", 2), ping.Length > 0 ? $"{ping.Average():0} мс" : "—", result?.Passed > 0 ? $"{result.Delay:0} мс" : "—", result);
        }
    }
    void StrategySelected(object sender, SelectionChangedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not Row row) return;
        var pinned = ResultsGrid.Items.Cast<Row>().FirstOrDefault(r => r.IsActive)
            ?? ResultsGrid.Items.Cast<Row>().FirstOrDefault(r => r.Name == settings.Strategy) ?? row;
        RenderStrategyCard(pinned);
        SelectedProfileText.Text = "Выбрана: " + row.DisplayName + (row.IsActive ? " · работает сейчас" : "");
        SelectedDetails.Text = row.Result == null ? "Проверка ещё не выполнялась." :
            $"Ping: {row.Ping} · Отклик: {row.Response}\n" + string.Join("\n", row.Result.Probes.GroupBy(p => p.Service).Select(g => $"{(g.All(p => p.Ok) ? "✓" : "✕")} {g.Key} · {g.Average(p => p.Milliseconds):0} мс" + (g.Any(p => !p.Ok) ? " · " + string.Join("; ", g.Where(p => !p.Ok).Select(p => p.Detail).Distinct()) : ""))) +
            "\nПроверка сайтов и соединения Discord. Голос и воспроизведение видео проверяются в самих приложениях.";
        BackToActiveButton.Visibility = !row.IsActive && ResultsGrid.Items.Cast<Row>().Any(r => r.IsActive) ? Visibility.Visible : Visibility.Collapsed;
        ManualButton.Visibility = row.IsActive || row.Name == settings.Strategy ? Visibility.Collapsed : Visibility.Visible;
        ManualButton.IsEnabled = operation == null;
        StrategyActions.Visibility = !settings.HideStrategies && (ManualButton.Visibility == Visibility.Visible || BackToActiveButton.Visibility == Visibility.Visible) ? Visibility.Visible : Visibility.Collapsed;

    }
    void RenderStrategyCard(Row row)
    {
        cardProfile = row;
        bool active = row.IsActive;
        bool firstRun = !settings.SetupComplete && !active && results.Count == 0;
        WelcomeText.Visibility = firstRun && operation == null ? Visibility.Visible : Visibility.Collapsed;
        WelcomeText.Text = "Подберём подходящую стратегию и сохраним её для следующего запуска.";
        ToggleButton.Visibility = firstRun ? Visibility.Collapsed : Visibility.Visible;
        HeroTitle.Text = operation != null ? "Выполняем…" : firstRun ? "Начнём с подключения" : active ? "Подключено" : "Готов к подключению";
        HeroTitle.SetResourceReference(TextBlock.ForegroundProperty, active ? "CardText" : "Text");
        ManualButton.SetResourceReference(Control.BackgroundProperty, firstRun ? "Button" : "Accent");
        ManualButton.SetResourceReference(Control.ForegroundProperty, firstRun ? "Text" : "AccentText");
        StrategyCard.SetResourceReference(Border.BackgroundProperty, active ? "CardActive" : "Card");
        StrategyCard.SetResourceReference(Border.BorderBrushProperty, active ? "CardActiveBorder" : "Border");
        StrategyCard.SetResourceReference(TextElement.ForegroundProperty, active ? "CardText" : "Text");
        CardCaption.Text = active ? "АКТИВНАЯ СТРАТЕГИЯ" : row.Name == settings.Strategy ? "СОХРАНЁННАЯ СТРАТЕГИЯ" : "ВЫБЕРИТЕ СТРАТЕГИЮ";
        CardCaption.SetResourceReference(TextBlock.ForegroundProperty, active ? "CardMuted" : "Muted");
        SelectionTitle.SetResourceReference(TextBlock.ForegroundProperty, active ? "CardText" : "Text");
        SelectionTitle.Text = row.DisplayName;
        ConnectionDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, active ? "Good" : "Unknown");
        ConnectionHalo.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
        ToggleButton.SetResourceReference(Control.BackgroundProperty, "Accent");
        ToggleButton.SetResourceReference(Control.ForegroundProperty, "AccentText");
        if (!settings.SetupComplete && !active && results.Count == 0)
        {
            CardCaption.Text = "ДОБРО ПОЖАЛОВАТЬ";
            SelectionTitle.Text = "Discord и YouTube";
        }
        SelectionStatus.Text = active ? "Работает сейчас" : "Не включена";
        SelectionStatus.SetResourceReference(TextBlock.ForegroundProperty, active ? "BadgeText" : "Muted");
        CardBadge.SetResourceReference(Border.BackgroundProperty, active ? "Badge" : "Button");
        CardSubtitle.Text = active ? "Обход включён" : "Готова к запуску";
        CardSubtitle.SetResourceReference(TextBlock.ForegroundProperty, active ? "CardMuted" : "Muted");
        MetricsGrid.Visibility = firstRun ? Visibility.Collapsed : Visibility.Visible;
        TestTimestamp.Visibility = Visibility.Collapsed;
        CardPing.Text = row.Ping; CardResponse.Text = row.Response;
        var groups = row.Result?.Probes.GroupBy(p => p.Service).ToList();
        CardPassed.Text = groups?.Count > 0 ? $"{groups.Count(g => g.All(p => p.Ok))} / {groups.Count}" : "—";
        TestTimestamp.Text = row.Result == null ? "Стратегия ещё не проверялась" : testedAt.HasValue ? $"Последний тест · {testedAt.Value.LocalDateTime:dd.MM, HH:mm}" : "Результаты последнего теста";
        foreach (var label in new[] { CardPingLabel, CardResponseLabel, CardPassedLabel, TestTimestamp }) label.SetResourceReference(TextBlock.ForegroundProperty, active ? "CardMuted" : "Muted");
        foreach (var metric in new[] { CardPing, CardResponse, CardPassed }) metric.SetResourceReference(TextBlock.ForegroundProperty, active ? "CardText" : "Text");
    }
    void ThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (loadingTheme || settings == null || ThemeBox.SelectedItem is not ComboBoxItem item) return;
        settings.Theme = (string)item.Tag; Appearance.Apply(this, settings.Theme);
        if (cardProfile != null) RenderStrategyCard(cardProfile);
        if (!preview && !quiet) Store.Save(settings);
    }
    void BackToActiveClick(object sender, RoutedEventArgs e)
    {
        var active = ResultsGrid.Items.Cast<Row>().FirstOrDefault(r => r.IsActive);
        if (active != null) { ResultsGrid.SelectedItem = active; ResultsGrid.ScrollIntoView(active); }
    }
    internal void PreviewActiveState(bool otherSelected = false)
    {
        if (!preview) throw new InvalidOperationException("Preview only");
        string activeName = settings.Strategy ?? Strategies.List(Store.Engine)[0];
        var rows = ResultsGrid.Items.Cast<Row>().Select(r => Row.From(r.Name, r.Result, activeName, settings.Strategy)).OrderByDescending(r => r.IsActive).ToList();
        ResultsGrid.ItemsSource = rows;
        ResultsGrid.SelectedItem = otherSelected ? rows.First(r => !r.IsActive) : rows.First(r => r.IsActive);
        StatusTitle.Text = "Соединение готово"; StatusDetail.Text = "Можно свернуть окно — обход останется в трее.";
        ModeLabel.Text = ""; ToggleButton.Content = "Отключить"; AdminPanel.Visibility = AdminButton.Visibility = Visibility.Collapsed;
        Footer.Text = "Предпросмотр интерфейса · движок не запущен";
    }
    internal void PreviewBusyState()
    {
        if (!preview) throw new InvalidOperationException("Preview only");
        SetBusy(true); StatusDetail.Text = "19 из 22 · SIMPLE FAKE ALT";
        Progress.Value = 86; Footer.Text = "";
    }
    void UpdateActive()
    {
        ModeLabel.Text = settings.SetupComplete ? "" : "ПЕРВЫЙ ЗАПУСК";
        tray?.Status(engine.Running);
    }
    void HomeClick(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 0;
    void DetailsClick(object sender, RoutedEventArgs e) => SelectedExpander.IsOpen = !SelectedExpander.IsOpen;
    void StrategyDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (operation == null && e.OriginalSource is DependencyObject source && ItemsControl.ContainerFromElement(ResultsGrid, source) is ListBoxItem)
            SelectedExpander.IsOpen = true;
    }
    void HelpClick(object sender, RoutedEventArgs e) { var button = (Button)sender; if (HelpPopup.IsOpen && HelpPopup.PlacementTarget == button) { HelpPopup.IsOpen = false; return; } HelpText.Text = (string)button.Tag; HelpPopup.PlacementTarget = button; HelpPopup.IsOpen = true; }
    void HideClick(object sender, RoutedEventArgs e) => HideToTray();
    void HideToTray() { Hide(); if (!trayHintShown) { if (!quiet) tray?.Hint(); trayHintShown = true; } }
    internal void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    void ExitClick(object sender, RoutedEventArgs e) => ExitApp();
    internal void ExitApp() { allowExit = true; if (operation != null) { operation.Cancel(); Footer.Text = "Останавливаем проверку перед выходом…"; } else Close(); }
    async Task Work(Func<CancellationToken, Task> action, string scope = "general", string? failureStrategy = null, bool recordFailure = true)
    {
        if (operation != null) return;
        StatusDetail.Text = scope switch { "app_install" => "Обновляем приложение…", "engine_install" => "Обновляем движок обхода…", "strategy_test" => "Готовим проверку стратегий…", "strategy_launch" => "Подключаемся…", _ => "Выполняем операцию…" };
        Progress.Value = 0;
        Progress.IsIndeterminate = scope != "strategy_test";
        operation = new(); SetBusy(true);
        try { await action(operation.Token); }
        catch (OperationCanceledException) { if (!engine.Running) StatusTitle.Text = "Проверка остановлена"; Footer.Text = "Операция отменена. Предыдущее состояние восстановлено, если оно было активно."; }
        catch (Exception ex) { if (recordFailure) statistics.Failure(ex, failureStrategy ?? settings.Strategy, scope); if (!engine.Running) StatusTitle.Text = "Нужно ваше внимание"; Footer.Text = ex.Message; Store.Log(ex.ToString()); MessageBox.Show(this, ex.Message, "Не удалось завершить", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { operation.Dispose(); operation = null; SetBusy(false); Progress.IsIndeterminate = false; Progress.Visibility = Visibility.Collapsed; if (allowExit) _ = Dispatcher.BeginInvoke(new Action(Close)); Refresh(); wasRunning = engine.Running; ToggleButton.Content = engine.Running ? "Отключить" : "Включить"; UpdateActive(); _ = statistics.Flush(lifetime.Token); }
    }
    void SetBusy(bool busy)
    {
        BusyPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateCancelButton.Visibility = busy && Pages.SelectedIndex != 0 ? Visibility.Visible : Visibility.Collapsed;
        if (busy && string.IsNullOrWhiteSpace(StatusDetail.Text)) StatusDetail.Text = "Выполняем операцию…";
        ResizeForContent();
        AppCheckButton.IsEnabled = !busy;
        AppInstallButton.IsEnabled = !busy && appRelease != null;
        foreach (var button in new[] { AutoButton, ToggleButton, ManualButton, SingleButton, SaveButton, CheckButton, DiagnoseButton, AdminButton }) button.IsEnabled = !busy;
        GameBox.IsEnabled = IpsetBox.IsEnabled = AutoConnectBox.IsEnabled = UpdatesBox.IsEnabled = StartupBox.IsEnabled = TrayBox.IsEnabled = !busy;
        AutoButton.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        StatusDetail.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; LaterButton.IsEnabled = !busy; Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; CancelButton.IsEnabled = busy; CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; UpdateButton.IsEnabled = !busy && release != null && release.Version != Updater.Version;
    }
    void Connected()
    {
        StatusTitle.Text = "Соединение готово"; StatusDetail.Text = settings.CloseToTray ? "Можно свернуть окно — обход останется в трее." : "Обход работает, пока приложение открыто.";
        ToggleButton.Content = "Отключить"; Footer.Text = ""; wasRunning = true; Refresh(); BackToActiveClick(this, new RoutedEventArgs());
    }
    async Task Diagnose(CancellationToken ct)
    {
        Strategies.Validate(Store.Engine);
        var conflicts = engine.Conflicts();
        using var bfe = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\BFE");
        using var proxy = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings");
        using var zapret = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\zapret");
        var info = new List<string> { $"Комплект {Updater.Version}: корректен, стратегий {Strategies.List(Store.Engine).Length}", $"Права администратора: {(Engine.Admin ? "есть" : "нужны для запуска")}", $"Чужие winws: {(conflicts.Length == 0 ? "не обнаружены" : string.Join(", ", conflicts))}", $"Существующая служба zapret: {(zapret == null ? "нет" : "есть — автоматически не изменяется")}", $"BFE зарегистрирована: {bfe != null}", $"Системный прокси включён: {Equals(proxy?.GetValue("ProxyEnable"), 1)}", "Проверки выполняются напрямую, без системного HTTP-прокси.", "Не изменяем TCP timestamps, hosts, DNS, службы и кэш Discord." };
        ct.ThrowIfCancellationRequested();
        using var query = Process.Start(new ProcessStartInfo("sc.exe", "query BFE") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true });
        if (query != null) { var output = await query.StandardOutput.ReadToEndAsync(ct); await query.WaitForExitAsync(ct); info.Add("BFE:\n" + output.Trim()); }
        DiagnosticsText.Text = string.Join("\n\n", info);
        Store.Log(DiagnosticsText.Text);
        if (conflicts.Length > 0) throw new InvalidOperationException("Другой zapret уже работает. Завершите его в исходной программе перед подбором.");
    }
    async void AutoClick(object sender, RoutedEventArgs e) => await Work(ct => Scan(Strategies.List(Store.Engine), true, ct), "strategy_test");
    async Task CheckAppUpdate(CancellationToken ct = default)
    {
        if (appCheckRunning || lifetime.IsCancellationRequested) return;
        appCheckRunning = true;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        AppCheckButton.IsEnabled = false;
        try
        {
            AppUpdateText.Text = "Проверяем обновления Razret…";
            appRelease = await latestAppRelease(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            AppUpdateText.Text = !AppUpdater.Configured ? "Razret " + AppUpdater.Version + ". Источник обновлений будет подключён после настройки GitHub."
                : appRelease == null ? "Razret " + AppUpdater.Version + " — новых выпусков нет."
                : "Доступен Razret " + appRelease.Version + ". Настройки и выбранная стратегия сохранятся. После установки приложение перезапустится.";
            AppReleaseNotes.Text = appRelease?.Notes ?? "";
            ShowUpdateNotices();
            if (appRelease != null && notifiedAppVersion != appRelease.Version)
            {
                notifiedAppVersion = appRelease.Version;
                if (!quiet) tray?.UpdateAvailable(appRelease.Version, () => Dispatcher.Invoke(() => { ShowFromTray(); Pages.SelectedIndex = 2; }));
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { AppUpdateText.Text = "Не удалось проверить обновления Razret. Можно повторить позже. " + ex.Message; }
        finally { appCheckRunning = false; AppCheckButton.IsEnabled = operation == null; AppInstallButton.IsEnabled = operation == null && appRelease != null; }
    }
    async void AppCheckClick(object sender, RoutedEventArgs e) => await Work(CheckAppUpdate);
    void OpenUpdateSection(FrameworkElement section)
    {
        Pages.SelectedIndex = 2;
        Dispatcher.BeginInvoke(new Action(() => section.BringIntoView()));
    }
    void AppNoticeClick(object sender, RoutedEventArgs e) => OpenUpdateSection(AppUpdateSection);
    void EngineNoticeClick(object sender, RoutedEventArgs e) => OpenUpdateSection(EngineUpdateSection);
    void ShowUpdateNotices()
    {
        if (settings == null || AppUpdateBanner == null) return;
        bool home = Pages.SelectedIndex == 0;
        bool appAvailable = appRelease != null;
        bool engineAvailable = release != null && release.Version != Updater.Version;
        AppBannerTitle.Text = appAvailable ? "Доступен Razret " + appRelease!.Version : "";
        EngineBannerTitle.Text = engineAvailable ? "Доступен zapret " + release!.Version : "";
        AppUpdateBanner.Visibility = home && appAvailable && settings.DismissedAppVersion != appRelease!.Version ? Visibility.Visible : Visibility.Collapsed;
        EngineUpdateBanner.Visibility = home && engineAvailable && settings.DismissedEngineVersion != release!.Version && settings.PostponedVersion != release.Version ? Visibility.Visible : Visibility.Collapsed;
        UpdateDot.Visibility = appAvailable || engineAvailable ? Visibility.Visible : Visibility.Collapsed;
        UpdateBadge.ToolTip = appAvailable || engineAvailable ? "Доступны обновления. Посмотреть изменения и установить." : "Обновления приложения и движка";
        ResizeForContent();
    }
    void DismissAppUpdate(object sender, RoutedEventArgs e)
    {
        settings.DismissedAppVersion = appRelease?.Version;
        if (!preview && !quiet) Store.Save(settings);
        ShowUpdateNotices();
    }
    void DismissEngineUpdate(object sender, RoutedEventArgs e)
    {
        settings.DismissedEngineVersion = release?.Version;
        if (!preview && !quiet) Store.Save(settings);
        ShowUpdateNotices();
    }
    internal void PreviewUpdatesState(string appVersion = "0.8.0", string engineVersion = "1.11.0")
    {
        if (!preview) throw new InvalidOperationException("Preview only");
        appRelease = new(appVersion, "https://example.invalid/app.zip", "", 1, "Новый интерфейс и улучшения стабильности.");
        release = new(engineVersion, "https://example.invalid/engine.zip", "", 1, "Обновлены стратегии обхода.");
        AppUpdateText.Text = "Доступен Razret " + appVersion + ". Настройки сохранятся. После установки приложение перезапустится.";
        AppReleaseNotes.Text = appRelease.Notes;
        AppInstallButton.IsEnabled = true;
        ShowRelease();
    }
    async void AppInstallClick(object sender, RoutedEventArgs e) => await Work(async ct =>
    {
        if (appRelease == null) return;
        AppUpdateText.Text = "Скачиваем и проверяем Razret…";
        Progress.IsIndeterminate = true;
        string area;
        try { area = await AppUpdater.Stage(appRelease, ct); statistics.Update("app_download", "success", appRelease.Version); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { statistics.Update("app_download", "failure", appRelease.Version, ex); throw; }
        ct.ThrowIfCancellationRequested();
        await statistics.Flush(ct);
        try { AppUpdater.Launch(area); }
        catch (Exception ex) { statistics.Update("app_install", "failure", appRelease.Version, ex); throw; }
        engine.Stop(); allowExit = true;
    }, "app_install", recordFailure: false);
    async void SingleClick(object sender, RoutedEventArgs e)
    {
        SelectedExpander.IsOpen = false;
        if (ResultsGrid.SelectedItem is Row row) await Work(ct => Scan([row.Name], false, ct), "strategy_test", row.Name);
        else Footer.Text = "Выберите стратегию в таблице.";
    }
    async Task Scan(string[] names, bool activate, CancellationToken ct)
    {
        await Diagnose(ct);
        if (!Engine.Admin) throw new InvalidOperationException("Сначала нажмите «Права администратора», затем запустите подбор.");
        var previous = engine.Active; bool accepted = false;
        try
        {
            engine.Stop(); if (activate) results.Clear(); else results.RemoveAll(r => names.Contains(r.Name)); Refresh();
            StatusTitle.Text = "Проверяем соединение"; StatusDetail.Text = "Исходная доступность без обхода…";
            var baseline = await Checks.Run(ct); Store.Write("baseline.json", baseline);
            for (int i = 0; i < names.Length; i++)
            {
                ct.ThrowIfCancellationRequested(); StatusDetail.Text = $"{i + 1} из {names.Length} · {names[i]}"; Progress.Value = 100.0 * i / names.Length;
                try { await engine.Start(names[i], settings, ct); results.Add(new(names[i], await Checks.Run(ct))); }
                catch (IOException ex) { statistics.Failure(ex, names[i], "strategy_launch"); Store.Log(ex.Message); results.Add(new(names[i], [new("Запуск", false, 0, ex.Message, Code: Statistics.TechnicalCause(ex))])); }
                finally { engine.Stop(); }
                Refresh(); StatusDetail.Text = $"Проверено {i + 1} из {names.Length}. Продолжаем…";
            }
            var ranked = Checks.Rank(results).ToList();
            // Recheck the three strongest complete candidates; instability demotes a candidate.
            if (activate)
            {
                foreach (var candidate in ranked.Where(r => r.Complete).Take(3))
                {
                    StatusDetail.Text = "Повторная проверка: " + candidate.Name;
                    await engine.Start(candidate.Name, settings, ct);
                    try { var repeat = await Checks.Run(ct); results.RemoveAll(r => r.Name == candidate.Name); results.Add(new(candidate.Name, candidate.Probes.Concat(repeat).ToList())); }
                    finally { engine.Stop(); }
                }
            }
            testedAt = DateTimeOffset.Now;
            foreach (var result in results.Where(r => names.Contains(r.Name))) statistics.Test(result);
            Store.Write("last-results.json", new { Date = testedAt.Value, Engine = Updater.Version, settings.GameMode, settings.Ipset, Results = Checks.Rank(results).ToList() });
            Refresh(); Progress.Value = 100;
            var best = Checks.Rank(results).FirstOrDefault(r => r.Complete && (!activate || r.Probes.Count >= 10));
            if (activate && best != null)
            {
                await engine.Start(best.Name, settings, ct); settings.Strategy = best.Name; settings.SetupComplete = true; Store.Save(settings); accepted = true; Refresh(); Connected();
            }
            else { StatusTitle.Text = activate ? "Нужна дополнительная настройка" : "Проверка завершена"; StatusDetail.Text = activate ? "Ни одна стратегия не прошла все проверки. Результаты сохранены; можно выбрать вариант вручную." : "Результат выбранной стратегии появился в таблице."; }
        }
        finally
        {
            if (!accepted) { engine.Stop(); if (previous != null) { await engine.Start(previous, settings, CancellationToken.None); Connected(); } }
        }
    }
    async void ToggleClick(object sender, RoutedEventArgs e) => await Work(async ct =>
    {
        if (engine.Running) { engine.Stop(); StatusTitle.Text = "Обход выключен"; Footer.Text = "Профиль сохранён."; }
        else if (PowerStrategy is string name) await ActivateStrategy(name, ct);
        else Footer.Text = "Выберите стратегию или нажмите «Подобрать автоматически».";
    }, "strategy_launch", PowerStrategy);
    async Task ActivateStrategy(string name, CancellationToken ct)
    {
        var previous = engine.Active;
        try
        {
            await Diagnose(ct); await engine.Start(name, settings, ct);
            settings.Strategy = name; settings.SetupComplete = true; Store.Save(settings); Connected();
        }
        catch { engine.Stop(); if (previous != null) await engine.Start(previous, settings, CancellationToken.None); throw; }
    }
    async void ManualClick(object sender, RoutedEventArgs e)
    {
        if (ResultsGrid.SelectedItem is not Row row) { Footer.Text = "Выберите стратегию в таблице."; return; }
        await Work(ct => ActivateStrategy(row.Name, ct), "strategy_launch", row.Name);
    }
    void CancelClick(object sender, RoutedEventArgs e) => operation?.Cancel();
    async void DiagnoseClick(object sender, RoutedEventArgs e) => await Work(Diagnose, "diagnostic");
    void SaveClick(object sender, RoutedEventArgs e)
    {
        try
        {
            bool startup = StartupBox.IsChecked == true;
            if (startup && (!settings.SetupComplete || settings.Strategy == null)) throw new InvalidOperationException("Сначала подберите и сохраните рабочую стратегию, затем включите автозапуск.");
            string game = (string)((ComboBoxItem)GameBox.SelectedItem).Tag, ipset = (string)((ComboBoxItem)IpsetBox.SelectedItem).Tag;
            if (startup && (game != settings.GameMode || ipset != settings.Ipset)) throw new InvalidOperationException("Сначала сохраните фильтры и повторите подбор; после этого включите автозапуск.");
            if (startup != Startup.Enabled()) Startup.Set(startup);
            bool filtersChanged = game != settings.GameMode || ipset != settings.Ipset;
            if (filtersChanged) { engine.Stop(); settings.SetupComplete = false; results.Clear(); StatusTitle.Text = "Параметры изменены"; }
            settings.GameMode = game; settings.Ipset = ipset; settings.AutoConnect = AutoConnectBox.IsChecked == true; settings.CheckUpdates = UpdatesBox.IsChecked == true;
            settings.StartWithWindows = startup; settings.CloseToTray = TrayBox.IsChecked == true;
            Store.Save(settings); Refresh(); ToggleButton.Content = engine.Running ? "Отключить" : "Включить";
            Footer.Text = filtersChanged ? "Настройки сохранены. Подберите стратегию заново." : "Настройки сохранены" + (startup ? " · обход включится при входе в Windows" : "");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Настройки Razret", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    void ShowRelease()
    {
        if (release == null) return;
        EngineVersionText.Text = Updater.Version;
        bool available = release.Version != Updater.Version;
        UpdateBadgeText.Text = available ? (settings.PostponedVersion == release.Version ? "Доступна " : "Новая ") + release.Version : "Движок " + release.Version;
        UpdateBadge.ToolTip = available ? "Доступно обновление движка обхода. Открыть изменения и установку." : "Движок обхода актуален. Открыть обновления.";
        UpdateBadge.SetResourceReference(Control.BackgroundProperty, available ? "Notice" : "Button");
        UpdateText.Text = available ? $"Доступна версия {release.Version}. Сохраним ваши списки и предыдущую версию." : $"Установлена актуальная версия {release.Version}.";
        ReleaseNotes.Text = ReleaseNotesRu.Summarize(release.Notes);
        LaterButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        LaterButton.Content = "Позже";
        UpdateButton.IsEnabled = operation == null && available;
        ShowUpdateNotices();
    }
    async Task CheckUpdate(CancellationToken ct = default)
    {
        if (updateCheck != null) { await updateCheck; return; }
        updateCheck = CheckUpdateCore(ct);
        try { await updateCheck; }
        finally { updateCheck = null; }
    }
    async Task CheckUpdateCore(CancellationToken ct)
    {
        UpdateBadgeText.Text = "Проверяем…";
        try { release = await Updater.Latest(ct); ShowRelease(); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { UpdateBadgeText.Text = "Повторить проверку"; UpdateText.Text = "Проверка недоступна. Установленный комплект сохранён. " + ex.Message; }
    }
    void UpdateBadgeClick(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 2;
    void LaterClick(object sender, RoutedEventArgs e)
    {
        if (release == null) return;
        settings.PostponedVersion = release.Version; Store.Save(settings); ShowRelease();
        UpdateText.Text = "Оставили версию " + Updater.Version + ". Обновление доступно здесь, когда решите установить.";
        Footer.Text = "Обновление отложено · текущий обход продолжает работать";
    }
    async void CheckClick(object sender, RoutedEventArgs e) => await Work(CheckUpdate);
    async void UpdateClick(object sender, RoutedEventArgs e) => await Work(async ct =>
    {
        if (release == null || release.Version == Updater.Version) return;
        var previous = engine.Active; string? backup = null;
        Progress.IsIndeterminate = true; UpdateText.Text = "Скачиваем и проверяем архив…";
        string candidate;
        try { candidate = await Updater.Stage(release, ct); statistics.Update("engine_download", "success", release.Version); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { statistics.Update("engine_download", "failure", release.Version, ex); throw; }
        ct.ThrowIfCancellationRequested();
        List<Probe>? before = previous != null ? await Checks.Run(ct) : null;
        engine.Stop();
        try
        {
            UpdateText.Text = "Применяем обновление…"; backup = Updater.Swap(candidate);
            if (settings.Strategy != null && !Strategies.List(Store.Engine).Contains(settings.Strategy)) throw new IOException("В новой версии нет сохранённой стратегии. Сохранена предыдущая версия.");
            if (previous != null)
            {
                await engine.Start(previous, settings, ct); var after = await Checks.Run(ct);
                if (before!.Any(p => p.Ok && !after.Any(a => a.Service == p.Service && a.Ok))) throw new IOException("После обновления ухудшилась доступность. Выполнен откат.");
            }
            Updater.Commit(); statistics.Update("engine_install", "success", release.Version); Refresh(); ShowRelease(); if (previous != null) Connected(); UpdateText.Text = "Установлена версия " + Updater.Version + ". Предыдущий комплект сохранён в backups.";
        }
        catch (Exception ex)
        {
            if (ex is not OperationCanceledException) statistics.Update("engine_install", "failure", release.Version, ex);
            engine.Stop(); if (backup != null) Updater.Rollback(backup);
            if (previous != null) await engine.Start(previous, settings, CancellationToken.None);
            throw;
        }
    }, "engine_install", recordFailure: false);
    void Elevate(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Store.Root }); allowExit = true; Close(); }
        catch (System.ComponentModel.Win32Exception) { Footer.Text = "Windows не предоставила права администратора. Можно продолжать просмотр."; }
    }
    void ListsClick(object sender, RoutedEventArgs e) { Process.Start(new ProcessStartInfo("explorer.exe", Path.Combine(Store.Engine, "lists")) { UseShellExecute = true }); }
    void LogClick(object sender, RoutedEventArgs e) { Store.Log("Открытие журнала"); Process.Start(new ProcessStartInfo("notepad.exe", Path.Combine(Store.Data, "app.log")) { UseShellExecute = true }); }
}
