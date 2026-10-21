using System.Text.RegularExpressions;

namespace ZapretApp;

public static class ReleaseNotesRu
{
    public static string ForEngine(string notes, string ipsetMode)
    {
        var direct = new List<string>();
        var external = new List<string>();
        var unconfirmed = new List<string>();
        bool happ = false, ipset = false;
        foreach (var line in Summarize(notes).Split("\n\n"))
        {
            if (!line.StartsWith("• ")) continue;
            var s = line[2..];
            if (Regex.IsMatch(s, @"(?i)Happ") && Regex.IsMatch(s, @"(?i)детект|обнаруж|detect")) { happ = true; continue; }
            if (Regex.IsMatch(s, @"(?i)диагност|service\.bat|меню|интерфейс|\bhosts\b|\bDNS\b|служб|авто.?обновлен"))
                external.Add(line);
            else if (Regex.IsMatch(s, @"(?i)IPSet|списк.*IP")) { ipset = true; direct.Add(line); }
            else if (Regex.IsMatch(s, @"(?i)стратег|strateg|\bTLS\b|\bQUIC\b|фейк|\bwinws\b|движ|GameFilter|HTTP|списк.*домен"))
                direct.Add(line);
            else unconfirmed.Add(line);
        }
        var sections = new List<string> {
            "Razret скачивает официальный комплект целиком: движок, стратегии, списки и файлы TLS/QUIC. Поддерживаемые стратегии и их файлы применяются при запуске без отдельного обновления Razret. Если формат стратегии несовместим, установка отменяется и прежний комплект сохраняется."
        };
        if (direct.Count > 0) sections.Add("Изменения стратегий и компонентов по описанию автора:\n" + string.Join("\n", direct));
        if (ipset) sections.Add(ipsetMode switch {
            "loaded" => "IPSet: обновлённый список будет использоваться в режиме «Адреса из списка проекта».",
            "any" => "IPSet: файл обновится, но сейчас выбран режим «Все адреса» — список не ограничивает обработку.",
            _ => "IPSet: файл обновится, но сейчас выбран режим «Только домены» — список IP-адресов не используется."
        });
        if (direct.Any(s => Regex.IsMatch(s, @"(?i)фейк|\bTLS\b|\bQUIC\b")))
            sections.Add("Файлы TLS/QUIC используются по параметрам выбранной стратегии; наличие новых файлов не означает, что все они включены одновременно.");
        if (happ) sections.Add("Happ: проверка службы уже встроена в этот Razret. Откройте «Настройки → Дополнительно → Диагностика». Обновление zapret не меняет код нашей диагностики.");
        if (external.Count > 0) sections.Add("Изменения утилит автора (не добавляются автоматически в функции Razret):\n" + string.Join("\n", external));
        if (unconfirmed.Count > 0) sections.Add("Другие изменения автора — применимость в Razret не подтверждена:\n" + string.Join("\n", unconfirmed));
        if (direct.Count + external.Count + unconfirmed.Count == 0 && !happ) sections.Add(Summarize(notes));
        sections.Add("Новые команды меню, системные настройки и новые проверки автора могут требовать обновления Razret. Полное исходное описание — на GitHub.");
        return string.Join("\n\n", sections);
    }

    // No translation service or account is required. Unknown English prose is omitted,
    // rather than presenting an invented translation as the author's release notes.
    public static string Summarize(string notes)
    {
        var lines = new List<string>(); bool omitted = false, contributors = false;
        foreach (var original in notes.Split('\n'))
        {
            var s = original.Trim();
            if (Regex.IsMatch(s, @"(?i)^#+\s*(new contributors|contributors|участники)")) { contributors = true; continue; }
            if (s.StartsWith('#')) { contributors = false; continue; }
            if (contributors || string.IsNullOrWhiteSpace(s) || s.StartsWith("**Full Changelog", StringComparison.OrdinalIgnoreCase)) continue;
            s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");
            s = Regex.Replace(s, @"(?i)\s*\(including changes by.*?\)", "");
            s = Regex.Replace(s, @"(?i)\s+(?:by\s+@|in\s+https://).*", "");
            s = Regex.Replace(s, @"\s+от\s+@[\w-]+", "");
            s = Regex.Replace(s, @"https?://\S+|@[\w-]+|\*\*|`", "");
            bool russianOriginal = Regex.IsMatch(s, @"[А-Яа-яЁё]");
            foreach (var (phrase, translated) in new[] {
                (@"updated? strategies", "Обновлены стратегии"), (@"new strategies", "Новые стратегии"),
                (@"fixed? Discord connection issues", "Исправлены проблемы подключения Discord"), (@"fixed? connection issues", "Исправлены проблемы подключения"),
                (@"improved? connection stability", "Улучшена стабильность подключения"),
                (@"updated? (?:IPSet|IP lists)", "Обновлены списки IP-адресов"),
                (@"improved? diagnostics", "Улучшена диагностика"),
                (@"fixed? (?:the )?app updater", "Исправлено обновление приложения") })
                s = Regex.Replace(s, @"(?i)\b" + phrase + @"\b", translated);
            s = Regex.Replace(s, @"(?i)\bnew strateg(?:y|ies)\b", "Новая стратегия");
            s = Regex.Replace(s, @"(?i)\bupdated?\b", "Обновлено");
            s = Regex.Replace(s, @"(?i)\bfixed?\b", "Исправлено");
            s = Regex.Replace(s, @"(?i)\bimproved? diagnostics\b", "Улучшена диагностика");
            s = Regex.Replace(s, @"(?i)\badded?\b", "Добавлено");
            s = s.Trim(' ', '*', '-', '\r');
            if (s.Length < 3) continue;
            // Allow names and protocol/file identifiers, but never display untranslated prose.
            var prose = Regex.Replace(s, @"(?i)\b(?:Discord|YouTube|Google|Telegram|TCP|UDP|TLS|IPSet|GameFilter|hosts|ALT\d*|SIMPLE|FAKE|EXP|AUTO|QUIC|DPI|ip_id|list-general|list-exclude|ipset-all|service\.bat)\b", "");
            if (!Regex.IsMatch(s, @"[А-Яа-яЁё]") || !russianOriginal && Regex.IsMatch(prose, @"[A-Za-z]{2,}")) { omitted = true; continue; }
            lines.Add("• " + Regex.Replace(s, @"\s+", " ").Trim());
        }
        var shown = lines.Distinct().Take(12).ToList();
        if (shown.Count == 0) return string.IsNullOrWhiteSpace(notes) ? "Автор не опубликовал описание изменений этого выпуска." : "Описание автора пока недоступно по-русски. Полный список изменений можно открыть на GitHub.";
        return string.Join("\n\n", shown) + (omitted || lines.Count > 12 ? "\n\nПоказаны изменения на русском. Полный список — на GitHub." : "");
    }
}
