using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZapretApp;

// The app update archive contains only the self-contained Razret.exe.
// Engine, user lists, settings and startup paths stay in place.
public static class AppUpdater
{
    public static string Version => typeof(AppUpdater).Assembly.GetName().Version!.ToString(3);
    public static string Repository => typeof(AppUpdater).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "UpdateRepository")?.Value ?? "";
    public static bool Configured => Regex.IsMatch(Repository, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$");
    static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Razret/" + Version);
        return client;
    }
    internal static bool IsNewer(string version) => System.Version.TryParse(version.TrimStart('v'), out var parsed)
        && parsed > System.Version.Parse(Version);
    public static async Task<Release?> Latest(CancellationToken ct)
    {
        if (!Configured) return null;
        using var client = Client();
        using var response = await client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", ct);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        var version = root.GetProperty("tag_name").GetString()!;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean() || !IsNewer(version)) return null;
        var asset = root.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == "Razret-app-update-win-x64.zip");
        var url = asset.GetProperty("browser_download_url").GetString()!;
        var digest = asset.TryGetProperty("digest", out var hash) ? hash.GetString() ?? "" : "";
        if (!url.StartsWith($"https://github.com/{Repository}/releases/download/", StringComparison.Ordinal)
            || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$")) throw new InvalidDataException("Не удалось проверить источник обновления Razret.");
        return new(version.TrimStart('v'), url, digest[7..], asset.GetProperty("size").GetInt64(), root.GetProperty("body").GetString() ?? "");
    }
    public static async Task<string> Stage(Release release, CancellationToken ct)
    {
        if (!Configured || !IsNewer(release.Version) || release.Size <= 0 || release.Size > 180_000_000)
            throw new InvalidDataException("Недопустимое обновление Razret.");
        var area = Path.Combine(Store.Root, "updates", "app-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(area);
        var archive = Path.Combine(area, "release.zip");
        using (var client = Client())
        using (var response = await client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(archive);
            var buffer = new byte[65536]; long total = 0; int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += count;
                if (total > release.Size) throw new InvalidDataException("Архив больше заявленного размера.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            if (total != release.Size) throw new InvalidDataException("Архив скачан не полностью.");
        }
        using (var input = File.OpenRead(archive))
            if (!Convert.ToHexString(await SHA256.HashDataAsync(input, ct)).Equals(release.Digest, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма Razret не совпала.");
        Extract(archive, area);
        var candidate = Path.Combine(area, "Razret.exe");
        if (FileVersionInfo.GetVersionInfo(candidate).FileVersion != System.Version.Parse(release.Version).ToString(3) + ".0")
            throw new InvalidDataException("Версия приложения не совпала с релизом.");
        return area;
    }
    internal static void Extract(string archive, string area)
    {
        using var zip = ZipFile.OpenRead(archive);
        if (zip.Entries.Count != 1 || zip.Entries[0].FullName != "Razret.exe" || zip.Entries[0].Length is <= 0 or > 250_000_000)
            throw new InvalidDataException("Обновление должно содержать только Razret.exe.");
        zip.Entries[0].ExtractToFile(Path.Combine(area, "Razret.exe"));
    }
    public static void Launch(string area)
    {
        // Release builds are self-contained single-file applications, so the helper
        // can run independently after the old app closes and releases its executable.
        var helper = Path.Combine(area, "installer.exe");
        File.Copy(Environment.ProcessPath!, helper);
        var info = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = area };
        info.ArgumentList.Add("--apply-app-update"); info.ArgumentList.Add(Environment.ProcessId.ToString()); info.ArgumentList.Add(Store.Root);
        if (Process.Start(info) == null) throw new IOException("Не удалось запустить установку обновления.");
    }
    internal static void Replace(string candidate, string target, string backup) => File.Replace(candidate, target, backup);
    public static int Apply(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[1], out var pid)) return 1;
        var root = Path.GetFullPath(args[2]);
        var area = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(area), Path.Combine(root, "updates").TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(area).StartsWith("app-", StringComparison.Ordinal)) return 1;
        Store.Root = root;
        var target = Path.Combine(root, "Razret.exe");
        var backup = Path.Combine(area, "Razret.previous.exe");
        bool replaced = false;
        try
        {
            try { using var parent = Process.GetProcessById(pid); if (!parent.WaitForExit(60000)) throw new IOException("Razret не завершил работу. Повторите обновление."); }
            catch (ArgumentException) { }
            using var mutex = new Mutex(false, "Local\\TihoZapretPrototype");
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); } catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new IOException("Другая копия Razret ещё работает.");
            try
            {
                Replace(Path.Combine(area, "Razret.exe"), target, backup); replaced = true;
                Store.Log("Razret обновлён. Резервная копия: " + backup);
            }
            finally { mutex.ReleaseMutex(); }
            if (Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, WorkingDirectory = root }) == null)
                throw new IOException("Не удалось перезапустить Razret.");
            return 0;
        }
        catch (Exception ex)
        {
            if (replaced) File.Copy(backup, target, true);
            Store.Log("Обновление Razret: " + ex);
            System.Windows.MessageBox.Show("Не удалось обновить Razret. Предыдущая версия сохранена.\n" + ex.Message, "Обновление Razret");
            return 1;
        }
    }
}
