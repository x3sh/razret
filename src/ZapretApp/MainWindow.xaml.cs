using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Windows.Documents;

namespace ZapretApp;

public partial class MainWindow : Window
{
    readonly Engine engine = new();
    readonly Settings settings;
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
    readonly Func<CancellationToken, Task<Release?>> latestAppRelease;
    public MainWindow(bool preview = false, bool startup = false, bool quiet = false, Func<CancellationToken, Task<Release?>>? latestAppRelease = null)
    {
        this.latestAppRelease = latestAppRelease ?? AppUpdater.Latest;
        InitializeComponent(); PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) { HelpPopup.IsOpen = false; SelectedExpander.IsOpen = false; } }; this.preview = preview; this.quiet = quiet; settings = Store.Load();
        Appearance.Apply(this, settings.Theme); Select(ThemeBox, settings.Theme); loadingTheme = false;
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
        ResultsGrid.SizeChanged += (_, _) =>
        {
            double metricsWidth = ResultsGrid.Columns.Skip(1).Sum(column => column.Width.Value);
            ResultsGrid.Columns[0].Width = Math.Max(150, ResultsGrid.ActualWidth - metricsWidth - 24);
        };
        Refresh();
        if (settings.SetupComplete) { StatusTitle.Text = "Готов к подключению"; StatusDetail.Text = "Сохранённый профиль готов. Повторный подбор не нужен."; }
        AdminButton.Visibility = Engine.Admin ? Visibility.Collapsed : Visibility.Visible;
        Loaded += async (_, _) =>
        {
            if (preview) return;
            if (startup) Hide();
            if (settings.SetupComplete && (settings.AutoConnect || startup) && settings.Strategy != null && Engine.Admin)
                await Work(async ct => { await engine.Start(settings.Strategy, settings, ct); Connected(); });
            else if (settings.SetupComplete && !Engine.Admin) Footer.Text = "Для включения обхода нажмите «Права администратора».";
            await Task.WhenAll(CheckAppUpdate(lifetime.Token), settings.CheckUpdates ? CheckUpdate() : Task.CompletedTask);
        };
        appUpdateMonitor.Tick += async (_, _) => { if (operation == null) await CheckAppUpdate(lifetime.Token); };
        if (!preview) appUpdateMonitor.Start();
        monitor.Tick += (_, _) => { bool running = engine.Running; if (operation == null && wasRunning && !running) { StatusTitle.Text = "Обход остановился"; StatusDetail.Text = "Движок завершился. Откройте журнал или повторите подбор."; ToggleButton.Content = "Включить"; Refresh(); } wasRunning = running; };
        if (!preview) monitor.Start();
        Closing += (_, e) => { if (!preview && !allowExit && settings.CloseToTray) { e.Cancel = true; HideToTray(); return; } if (operation != null) { operation.Cancel(); e.Cancel = true; Footer.Text = "Отменяем операцию и восстанавливаем состояние. После завершения можно закрыть окно."; } };
        Closed += (_, _) => { lifetime.Cancel(); appUpdateMonitor.Stop(); monitor.Stop(); tray?.Dispose(); engine.Dispose(); };
    }
    static void Select(ComboBox box, string tag) => box.SelectedItem = box.Items.Cast<ComboBoxItem>().First(x => (string)x.Tag == tag);
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
        AutoButton.Content = "Подобрать автоматически";
        AutoButton.SetResourceReference(Control.BackgroundProperty, settings.SetupComplete ? "Button" : "Accent");
        AutoButton.SetResourceReference(Control.ForegroundProperty, settings.SetupComplete ? "Text" : "AccentText");
        UpdateActive();
    }
    public record Row(string Name, string DisplayName, string Marker, string Discord, string Youtube, string Ping, string Response, Result? Result)
    {
        public bool IsActive => Marker == "● активна";
        public static string ShortName(string name) => name.Replace("general (", "").Replace(").bat", "").Replace("general.bat", "Основная");
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
        TestTimestamp.Visibility = firstRun ? Visibility.Collapsed : Visibility.Visible;
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
        ModeLabel.Text = ""; ToggleButton.Content = "Выключить"; AdminButton.Visibility = Visibility.Collapsed;
        Footer.Text = "Предпросмотр интерфейса · движок не запущен";
    }
    void UpdateActive()
    {
        ModeLabel.Text = settings.SetupComplete ? "" : "ПЕРВЫЙ ЗАПУСК";
        tray?.Status(engine.Running);
    }
    void MenuClick(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        foreach (var item in button.ContextMenu.Items.OfType<MenuItem>())
            item.IsChecked = item.Tag is string page && int.TryParse(page, out var index) && index == Pages.SelectedIndex;
        button.ContextMenu.PlacementTarget = button; button.ContextMenu.IsOpen = true;
    }
    void Navigate(object sender, RoutedEventArgs e) => Pages.SelectedIndex = int.Parse((string)((MenuItem)sender).Tag);
    void HomeClick(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 0;
    void DetailsClick(object sender, RoutedEventArgs e) => SelectedExpander.IsOpen = !SelectedExpander.IsOpen;
    void HelpClick(object sender, RoutedEventArgs e) { var button = (Button)sender; if (HelpPopup.IsOpen && HelpPopup.PlacementTarget == button) { HelpPopup.IsOpen = false; return; } HelpText.Text = (string)button.Tag; HelpPopup.PlacementTarget = button; HelpPopup.IsOpen = true; }
    void HideClick(object sender, RoutedEventArgs e) => HideToTray();
    void HideToTray() { Hide(); if (!trayHintShown) { if (!quiet) tray?.Hint(); trayHintShown = true; } }
    internal void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    void ExitClick(object sender, RoutedEventArgs e) => ExitApp();
    internal void ExitApp() { allowExit = true; if (operation != null) { operation.Cancel(); Footer.Text = "Останавливаем проверку перед выходом…"; } else Close(); }
    async Task Work(Func<CancellationToken, Task> action)
    {
        if (operation != null) return;
        operation = new(); SetBusy(true);
        try { await action(operation.Token); }
        catch (OperationCanceledException) { if (!engine.Running) StatusTitle.Text = "Проверка остановлена"; Footer.Text = "Операция отменена. Предыдущее состояние восстановлено, если оно было активно."; }
        catch (Exception ex) { if (!engine.Running) StatusTitle.Text = "Нужно ваше внимание"; Footer.Text = ex.Message; Store.Log(ex.ToString()); MessageBox.Show(this, ex.Message, "Не удалось завершить", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { operation.Dispose(); operation = null; SetBusy(false); Progress.IsIndeterminate = false; Progress.Visibility = Visibility.Collapsed; if (allowExit) _ = Dispatcher.BeginInvoke(new Action(Close)); Refresh(); wasRunning = engine.Running; ToggleButton.Content = engine.Running ? "Выключить" : "Включить"; UpdateActive(); }
    }
    void SetBusy(bool busy)
    {
        AppCheckButton.IsEnabled = !busy;
        AppInstallButton.IsEnabled = !busy && appRelease != null;
        foreach (var button in new[] { AutoButton, ToggleButton, ManualButton, SingleButton, SaveButton, CheckButton, DiagnoseButton, AdminButton }) button.IsEnabled = !busy;
        GameBox.IsEnabled = IpsetBox.IsEnabled = AutoConnectBox.IsEnabled = UpdatesBox.IsEnabled = StartupBox.IsEnabled = TrayBox.IsEnabled = !busy;
        StatusDetail.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; LaterButton.IsEnabled = !busy; Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; CancelButton.IsEnabled = busy; CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed; UpdateButton.IsEnabled = !busy && release != null && release.Version != Updater.Version;
    }
    void Connected()
    {
        StatusTitle.Text = "Соединение готово"; StatusDetail.Text = settings.CloseToTray ? "Можно свернуть окно — обход останется в трее." : "Обход работает, пока приложение открыто.";
        ToggleButton.Content = "Выключить"; Footer.Text = ""; wasRunning = true; Refresh(); BackToActiveClick(this, new RoutedEventArgs());
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
    async void AutoClick(object sender, RoutedEventArgs e) => await Work(ct => Scan(Strategies.List(Store.Engine), true, ct));
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
            AppNotice.Visibility = appRelease != null ? Visibility.Visible : Visibility.Collapsed;
            AppNotice.Content = "Обновить Razret";
            AppNotice.ToolTip = appRelease == null ? null : "Доступен Razret " + appRelease.Version + ". Нажмите, чтобы посмотреть изменения и установить.";
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
    void AppNoticeClick(object sender, RoutedEventArgs e) => Pages.SelectedIndex = 2;
    async void AppInstallClick(object sender, RoutedEventArgs e) => await Work(async ct =>
    {
        if (appRelease == null) return;
        AppUpdateText.Text = "Скачиваем и проверяем Razret…";
        Progress.IsIndeterminate = true;
        var area = await AppUpdater.Stage(appRelease, ct);
        ct.ThrowIfCancellationRequested();
        AppUpdater.Launch(area);
        engine.Stop(); allowExit = true;
    });
    async void SingleClick(object sender, RoutedEventArgs e)
    {
        SelectedExpander.IsOpen = false;
        if (ResultsGrid.SelectedItem is Row row) await Work(ct => Scan([row.Name], false, ct));
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
                catch (IOException ex) { Store.Log(ex.Message); results.Add(new(names[i], [new("Запуск", false, 0, ex.Message)])); }
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
    });
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
        await Work(ct => ActivateStrategy(row.Name, ct));
    }
    void CancelClick(object sender, RoutedEventArgs e) => operation?.Cancel();
    async void DiagnoseClick(object sender, RoutedEventArgs e) => await Work(Diagnose);
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
            Store.Save(settings); Refresh(); ToggleButton.Content = engine.Running ? "Выключить" : "Включить";
            Footer.Text = filtersChanged ? "Настройки сохранены. Подберите стратегию заново." : "Настройки сохранены" + (startup ? " · обход включится при входе в Windows" : "");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Настройки Razret", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
    void ShowRelease()
    {
        if (release == null) return;
        bool available = release.Version != Updater.Version;
        UpdateBadgeText.Text = available ? (settings.PostponedVersion == release.Version ? "Доступна " : "Новая ") + release.Version : "Движок " + release.Version;
        UpdateBadge.ToolTip = available ? "Доступно обновление движка обхода. Открыть изменения и установку." : "Движок обхода актуален. Открыть обновления.";
        UpdateBadge.SetResourceReference(Control.BackgroundProperty, available ? "Notice" : "Button");
        UpdateText.Text = available ? $"Доступна версия {release.Version}. Сохраним ваши списки и предыдущую версию." : $"Установлена актуальная версия {release.Version}.";
        ReleaseNotes.Text = ReleaseNotesRu.Summarize(release.Notes);
        LaterButton.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
        LaterButton.Content = "Остаться на " + Updater.Version;
        UpdateButton.IsEnabled = operation == null && available;
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
        catch (Exception ex) { UpdateBadgeText.Text = "Повторить проверку"; UpdateText.Text = "Проверка недоступна. Установленный комплект сохранён. " + ex.Message; }
    }
    async void UpdateBadgeClick(object sender, RoutedEventArgs e) { Pages.SelectedIndex = 2; if (release == null && operation == null) await CheckUpdate(); }
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
        var candidate = await Updater.Stage(release, ct); ct.ThrowIfCancellationRequested();
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
            Updater.Commit(); Refresh(); ShowRelease(); if (previous != null) Connected(); UpdateText.Text = "Установлена версия " + Updater.Version + ". Предыдущий комплект сохранён в backups.";
        }
        catch
        {
            engine.Stop(); if (backup != null) Updater.Rollback(backup);
            if (previous != null) await engine.Start(previous, settings, CancellationToken.None);
            throw;
        }
    });
    void Elevate(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = Store.Root }); allowExit = true; Close(); }
        catch (System.ComponentModel.Win32Exception) { Footer.Text = "Windows не предоставила права администратора. Можно продолжать просмотр."; }
    }
    void ListsClick(object sender, RoutedEventArgs e) { Process.Start(new ProcessStartInfo("explorer.exe", Path.Combine(Store.Engine, "lists")) { UseShellExecute = true }); }
    void LogClick(object sender, RoutedEventArgs e) { Store.Log("Открытие журнала"); Process.Start(new ProcessStartInfo("notepad.exe", Path.Combine(Store.Data, "app.log")) { UseShellExecute = true }); }
}
