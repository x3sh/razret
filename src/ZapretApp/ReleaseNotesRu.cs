using System.Text.RegularExpressions;

namespace ZapretApp;

public static class ReleaseNotesRu
{
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
            s = Regex.Replace(s, @"(?i)\bnew strateg(?:y|ies)\b", "Новая стратегия");
            s = Regex.Replace(s, @"(?i)\bupdated?\b", "Обновлено");
            s = Regex.Replace(s, @"(?i)\bfixed?\b", "Исправлено");
            s = Regex.Replace(s, @"(?i)\bimproved? diagnostics\b", "Улучшена диагностика");
            s = Regex.Replace(s, @"(?i)\badded?\b", "Добавлено");
            s = s.Trim(' ', '*', '-', '\r');
            if (s.Length < 3) continue;
            // Allow names and protocol/file identifiers, but never display untranslated prose.
            var prose = Regex.Replace(s, @"(?i)\b(?:Discord|YouTube|Google|Telegram|TCP|UDP|TLS|IPSet|GameFilter|hosts|ALT\d*|SIMPLE|FAKE|EXP|AUTO|QUIC|DPI|ip_id|list-general|list-exclude|ipset-all|service\.bat)\b", "");
            if (!Regex.IsMatch(s, @"[А-Яа-яЁё]") || Regex.IsMatch(prose, @"[A-Za-z]{2,}")) { omitted = true; continue; }
            lines.Add("• " + Regex.Replace(s, @"\s+", " ").Trim());
        }
        var shown = lines.Distinct().Take(7).ToList();
        if (shown.Count == 0) return "Автор пока не предоставил изменений, которые приложение может надёжно показать по-русски. Можно остаться на установленной версии.";
        return string.Join("\n\n", shown) + (omitted || lines.Count > 7 ? "\n\nПоказаны основные изменения на русском. Часть подробностей пропущена." : "");
    }
}
