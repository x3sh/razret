namespace ZapretApp;

internal static class Quality
{
    internal static string Ping(Result? result)
    {
        var values = result?.Probes.Where(p => p.Ok && p.Ping.HasValue).Select(p => p.Ping!.Value).ToArray() ?? [];
        return values.Length == 0 ? "unknown" : values.Average() <= 80 ? "good" : values.Average() <= 150 ? "warning" : "bad";
    }
    internal static string Response(Result? result) => result == null || result.Probes.Count == 0 ? "unknown"
        : result.Passed == 0 ? "bad" : result.Delay <= 500 ? "good" : result.Delay <= 1500 ? "warning" : "bad";
    internal static string Checks(Result? result)
    {
        if (result == null || result.Probes.Count == 0) return "unknown";
        if (result.Complete) return "good";
        bool critical = result.Probes.Any(p => !p.Ok && p.Service is "Discord API" or "Discord Gateway" or "YouTube");
        var groups = result.Probes.GroupBy(p => p.Service).ToList();
        return critical || groups.Count(g => g.All(p => p.Ok)) * 2 < groups.Count ? "bad" : "warning";
    }
    internal static string Overall(Result? result) => Checks(result) switch
    {
        "unknown" => "unknown", "bad" => "bad", "warning" => "warning",
        _ => (Ping(result) is "good" or "unknown") && Response(result) == "good" ? "good" : "warning"
    };
    internal static string Brush(string state) => state switch { "good" => "MetricGood", "warning" => "MetricWarning", "bad" => "MetricBad", _ => "Muted" };
}
