using System.IO;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZapretApp;

// Only aggregate technical outcomes. There is deliberately no installation ID,
// session, activation event, raw error, IP, device information or precise time.
public sealed record Statistic(string Kind, string Outcome, string? Strategy = null,
    string? Service = null, string? Error = null, string? TargetVersion = null, int Count = 1);
public sealed record StatisticalReport(int Schema, string ReportId, string Day, string AppVersion,
    string EngineVersion, List<Statistic> Metrics);
public sealed class Statistics : IDisposable
{
    public static string Endpoint => typeof(Statistics).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "StatisticsEndpoint")?.Value ?? "";
    public static bool Configured => Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == "https";
    readonly string path;
    readonly Uri? endpoint;
    readonly HttpClient client;
    readonly object gate = new();
    readonly SemaphoreSlim sending = new(1);
    CancellationTokenSource consentLifetime = new();
    List<StatisticalReport> reports = [];
    StatisticalReport? active;
    bool enabled;
    public Statistics(bool enabled, bool disabled = false) : this(disabled ? Path.Combine(Path.GetTempPath(), "razret-statistics-disabled-" + Guid.NewGuid().ToString("N") + ".json") : Path.Combine(Store.Data, "statistics.json"),
        !disabled && Configured ? new Uri(Endpoint) : null, new HttpClient { Timeout = TimeSpan.FromSeconds(15) }, enabled && !disabled) { }
    internal Statistics(string path, Uri? endpoint, HttpClient client, bool enabled)
    {
        this.path = path; this.endpoint = endpoint; this.client = client;
        try { if (File.Exists(path) && new FileInfo(path).Length <= 1_000_000) reports = (JsonSerializer.Deserialize<List<StatisticalReport>>(File.ReadAllText(path), Store.Json) ?? []).Where(r => r != null && SafeReport(r)).TakeLast(30).ToList(); }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { reports = []; }
        SetEnabled(enabled);
    }
    public void SetEnabled(bool value)
    {
        lock (gate)
        {
            enabled = value;
            if (!value)
            {
                consentLifetime.Cancel(); reports.Clear(); active = null;
                try { File.Delete(path); File.Delete(path + ".tmp"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
            else if (consentLifetime.IsCancellationRequested) { consentLifetime.Dispose(); consentLifetime = new(); }
        }
    }
    internal static string? SafeStrategy(string? value) => value != null &&
        Regex.IsMatch(value, @"^general(?: \([A-Za-z0-9 _+.,-]{1,100}\))?\.bat$") ? value : null;
    internal static string ErrorCode(Exception ex) => ex switch
    {
        InvalidDataException => "invalid_package", HttpRequestException => "network",
        UnauthorizedAccessException => "permission", TimeoutException => "timeout",
        IOException => "io", InvalidOperationException => "operation", _ => "unexpected"
    };
    internal static bool SafeReport(StatisticalReport r) => r.Schema == 1 && Guid.TryParse(r.ReportId, out _)
        && Regex.IsMatch(r.AppVersion ?? "", @"^\d{1,4}\.\d{1,4}\.\d{1,4}$") && Regex.IsMatch(r.EngineVersion ?? "", @"^\d{1,4}\.\d{1,4}\.\d{1,4}$")
        && DateOnly.TryParseExact(r.Day, "yyyy-MM-dd", out var day) && day >= DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-30)) && day <= DateOnly.FromDateTime(DateTime.UtcNow)
        && r.Metrics is { Count: >= 1 and <= 100 } && r.Metrics.All(m => m != null && m.Count is >= 1 and <= 10000
            && m.Kind is "strategy_test" or "service_test" or "error" or "app_download" or "app_install" or "engine_download" or "engine_install"
            && m.Outcome is "success" or "failure" && (m.Strategy == null || SafeStrategy(m.Strategy) != null)
            && m.Service is null or "discord" or "youtube" or "launch"
            && m.Error is null or "invalid_package" or "network" or "permission" or "timeout" or "io" or "operation" or "unexpected" or "engine_exit"
            && (m.TargetVersion == null || Regex.IsMatch(m.TargetVersion, @"^\d{1,4}\.\d{1,4}\.\d{1,4}$")));
    public void Test(Result result)
    {
        if (SafeStrategy(result.Name) is not string strategy) return;
        Record(new("strategy_test", result.Complete ? "success" : "failure", strategy));
        foreach (var group in result.Probes.GroupBy(p => p.Service.StartsWith("Discord", StringComparison.Ordinal) ? "discord"
            : p.Service.StartsWith("YouTube", StringComparison.Ordinal) ? "youtube" : "launch"))
            Record(new("service_test", group.All(p => p.Ok) ? "success" : "failure", strategy, group.Key));
    }
    public void Update(string kind, string outcome, string version, Exception? error = null) =>
        Record(new(kind, outcome, Error: error == null ? null : ErrorCode(error),
            TargetVersion: Regex.IsMatch(version, @"^\d{1,4}\.\d{1,4}\.\d{1,4}$") ? version : null));
    public void Failure(Exception ex, string? strategy = null) => Record(new("error", "failure", SafeStrategy(strategy), Error: ErrorCode(ex)));
    public void UnexpectedExit(string? strategy) => Record(new("error", "failure", SafeStrategy(strategy), Error: "engine_exit"));
    internal void Record(Statistic metric)
    {
        lock (gate)
        {
            if (!enabled) return;
            var day = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var version = Updater.Version;
            if (active == null || active.Day != day || active.EngineVersion != version || active.Metrics.Count >= 100)
            {
                active = new(1, Guid.NewGuid().ToString("D"), day, AppUpdater.Version, version, []);
                reports.Add(active);
            }
            int index = active.Metrics.FindIndex(m => m with { Count = 1 } == metric with { Count = 1 });
            if (index < 0) active.Metrics.Add(metric);
            else active.Metrics[index] = active.Metrics[index] with { Count = Math.Min(10000, active.Metrics[index].Count + 1) };
            reports.RemoveAll(r => string.CompareOrdinal(r.Day, DateTime.UtcNow.AddDays(-30).ToString("yyyy-MM-dd")) < 0);
            if (reports.Count > 30) reports.RemoveRange(0, reports.Count - 30);
            Save();
        }
    }
    void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(reports, Store.Json));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public async Task Flush(CancellationToken ct = default)
    {
        if (endpoint == null) return;
        bool acquired = false;
        try
        {
            acquired = await sending.WaitAsync(0, ct);
            if (!acquired) return;
            StatisticalReport? report; CancellationToken consentToken;
            lock (gate)
            {
                if (!enabled || reports.Count == 0) return;
                reports.RemoveAll(r => r == null || !SafeReport(r));
                Save();
                if (reports.Count == 0) return;
                // Seal before sending; retries keep identical content and ID.
                active = null; report = reports[0]; consentToken = consentLifetime.Token;
            }
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, consentToken);
            using var response = await client.PostAsJsonAsync(endpoint, report, Store.Json, linked.Token);
            if (response.IsSuccessStatusCode || response.StatusCode is System.Net.HttpStatusCode.BadRequest or System.Net.HttpStatusCode.RequestEntityTooLarge)
                lock (gate) { reports.RemoveAll(r => r.ReportId == report.ReportId); if (enabled) Save(); }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or ObjectDisposedException) { }
        finally { if (acquired) sending.Release(); }
    }
    public void Dispose() { consentLifetime.Cancel(); client.Dispose(); }
}
