using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ZapretApp;

internal static class StatisticsTests
{
    sealed class Wire : HttpMessageHandler
    {
        public List<string> Bodies = [];
        public HttpStatusCode Status = HttpStatusCode.ServiceUnavailable;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new(Status);
        }
    }
    internal static async Task Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "razret-statistics-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); var originalRoot = Store.Root; Store.Root = root;
        try
        {
            Directory.CreateDirectory(Store.Engine); File.WriteAllText(Path.Combine(Store.Engine, "version.txt"), "1.10.3");
            var path = Path.Combine(root, "reports.json"); var wire = new Wire();
            using var stats = new Statistics(path, new Uri("https://example.invalid/collect"), new HttpClient(wire), false);
            var result = new Result("general.bat", [new("Discord API", true, 1, "private-address@example.com"), new("YouTube", false, 2, "C:\\Users\\Secret")]);
            stats.Test(result); await stats.Flush();
            Check(!File.Exists(path) && wire.Bodies.Count == 0, "no collection or request without consent");
            stats.SetEnabled(true); stats.Test(result); stats.Test(result); stats.Failure(new IOException("secret error path"));
            var supportCode = stats.SupportCode;
            using (var reopened = new Statistics(path, new Uri("https://example.invalid/collect"), new HttpClient(new Wire()), true))
                Check(reopened.SupportCode == supportCode, "support ID persists across restarts after consent");
            await stats.Flush();
            var body = wire.Bodies.Single();
            Check(!body.Contains("private-address") && !body.Contains("Secret") && !body.Contains("secret error"), "no raw probe details or error messages");
            using (var doc = JsonDocument.Parse(body))
            {
                Check(doc.RootElement.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "Schema", "ReportId", "Day", "AppVersion", "EngineVersion", "Metrics", "TesterId" }.Order()), "report has random support ID but no device fingerprint, session or exact time fields");
                Check(doc.RootElement.GetProperty("Metrics")[0].GetProperty("Count").GetInt32() == 2, "repeated tests aggregated");
            }
            wire.Status = HttpStatusCode.Accepted; await stats.Flush();
            Check(wire.Bodies.Count == 2 && wire.Bodies[1] == body, "failed request retries immutable report for server deduplication");
            stats.Test(result); stats.SetEnabled(false); await stats.Flush();
            Check(!File.Exists(path) && wire.Bodies.Count == 2, "revoking consent erases queue and stops requests");
            stats.SetEnabled(true); await stats.Flush(); Check(wire.Bodies.Count == 2, "re-enabling does not replay old activity");
            Check(stats.SupportCode != supportCode, "re-enabling creates a new support ID after revocation");
            Check(Statistics.TechnicalCause(new HttpRequestException("private-host", new System.Net.Sockets.SocketException(10061))) == "socket:10061", "inner socket error retained without message");
            Check(Statistics.TechnicalCause(new EngineFailure(-1073741819)) == "engine:-1073741819", "engine exit code preserved safely");
            using var canceled = new CancellationTokenSource(); canceled.Cancel(); await stats.Flush(canceled.Token);
            Check(Statistics.SafeStrategy("C:\\Users\\Person\\strategy.bat") == null && Statistics.SafeStrategy("general (ALT11).bat") != null, "only published strategy names accepted");
            Check(!Statistics.SafeReport(new(1, Guid.NewGuid().ToString(), DateTime.UtcNow.ToString("yyyy-MM-dd"), "0.7.3", "1.10.3", [new("error", "failure", Error: "private-user@example.com")])), "corrupt queue cannot send arbitrary text");
            Store.Write("statistics-test.json", new { Passed = true, Checks = new[] { "Consent", "Aggregation", "No personal fields or raw details", "Immutable retries", "Revocation", "Cancellation", "Safe strategy names" } });
        }
        finally { Store.Root = originalRoot; }
    }
    static void Check(bool value, string description) { if (!value) throw new Exception("Statistics test failed: " + description); }
}
