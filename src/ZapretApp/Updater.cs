using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZapretApp;

public record Release(string Version, string Url, string Digest, long Size, string Notes = "");
public static class Updater
{
    public static string Version => File.Exists(Path.Combine(Store.Engine, "version.txt")) ? File.ReadAllText(Path.Combine(Store.Engine, "version.txt")).Trim() : "неизвестна";
    static HttpClient Client()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ZapretApp-Prototype/0.1");
        return client;
    }
    public static async Task<Release> Latest(CancellationToken ct)
    {
        using var client = Client();
        using var response = await client.GetAsync("https://api.github.com/repos/Flowseal/zapret-discord-youtube/releases/latest", ct);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) throw new InvalidDataException("Нестабильный релиз.");
        var tag = root.GetProperty("tag_name").GetString()!;
        var asset = root.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == $"zapret-discord-youtube-{tag}.zip");
        var url = asset.GetProperty("browser_download_url").GetString()!;
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        if (!url.StartsWith("https://github.com/Flowseal/zapret-discord-youtube/releases/download/", StringComparison.Ordinal) || !Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$")) throw new InvalidDataException("Релиз не содержит проверяемый официальный ZIP.");
        var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "";
        return new(tag, url, digest[7..], asset.GetProperty("size").GetInt64(), notes.Length > 12000 ? notes[..12000] : notes);
    }
    public static void Extract(string archive, string destination)
    {
        using var zip = ZipFile.OpenRead(archive);
        long size = 0;
        if (zip.Entries.Count > 5000) throw new InvalidDataException("Слишком много файлов.");
        var root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(destination);
        foreach (var entry in zip.Entries)
        {
            size += entry.Length;
            if (size > 200_000_000) throw new InvalidDataException("Слишком большой архив.");
            if (entry.FullName.Contains(':') || ((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000) throw new InvalidDataException("Недопустимый тип файла.");
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь вне папки обновления.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) { Directory.CreateDirectory(target); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target);
        }
    }
    public static async Task<string> Stage(Release release, CancellationToken ct)
    {
        if (release.Size <= 0 || release.Size > 50_000_000) throw new InvalidDataException("Недопустимый размер загрузки.");
        string area = Path.Combine(Store.Root, "updates", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(area);
        string archive = Path.Combine(area, "release.zip");
        using (var client = Client())
        using (var response = await client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            await using var output = File.Create(archive);
            var buffer = new byte[65536]; long total = 0; int count;
            while ((count = await input.ReadAsync(buffer, ct)) > 0)
            {
                total += count; if (total > release.Size) throw new InvalidDataException("Размер загрузки превышает заявленный.");
                await output.WriteAsync(buffer.AsMemory(0, count), ct);
            }
            if (total != release.Size) throw new InvalidDataException("Неполная загрузка.");
        }
        using (var stream = File.OpenRead(archive))
            if (!Convert.ToHexString(await SHA256.HashDataAsync(stream, ct)).Equals(release.Digest, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Контрольная сумма обновления не совпала.");
        ct.ThrowIfCancellationRequested();
        var extracted = Path.Combine(area, "extracted"); Extract(archive, extracted);
        var roots = Directory.GetFiles(extracted, "service.bat", SearchOption.AllDirectories);
        if (roots.Length != 1) throw new InvalidDataException("Неоднозначная структура архива.");
        var candidate = Path.GetDirectoryName(roots[0])!;
        Strategies.Validate(candidate);
        foreach (var name in new[] { "list-general-user.txt", "list-exclude-user.txt", "ipset-exclude-user.txt" })
        {
            var old = Path.Combine(Store.Engine, "lists", name);
            if (File.Exists(old)) File.Copy(old, Path.Combine(candidate, "lists", name), true);
        }
        File.WriteAllText(Path.Combine(candidate, "version.txt"), release.Version);
        return candidate;
    }
    public static string Swap(string candidate)
    {
        var backup = Path.Combine(Store.Root, "backups", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
        // Journal is written before either rename, so an interrupted update can be recovered next launch.
        Store.Write("update-journal.json", new { Backup = backup, Candidate = candidate });
        Directory.Move(Store.Engine, backup);
        try { Directory.Move(candidate, Store.Engine); }
        catch { Directory.Move(backup, Store.Engine); throw; }
        return backup;
    }
    public static void Commit() => File.Delete(Path.Combine(Store.Data, "update-journal.json"));
    public static void Rollback(string backup)
    {
        Directory.CreateDirectory(Path.Combine(Store.Root, "updates"));
        if (Directory.Exists(Store.Engine)) Directory.Move(Store.Engine, Path.Combine(Store.Root, "updates", "rejected-" + Guid.NewGuid().ToString("N")));
        Directory.Move(backup, Store.Engine); Commit();
    }
    public static void Recover()
    {
        var journal = Path.Combine(Store.Data, "update-journal.json"); if (!File.Exists(journal)) return;
        using var json = JsonDocument.Parse(File.ReadAllText(journal));
        var backup = json.RootElement.GetProperty("Backup").GetString()!;
        if (!Path.GetFullPath(backup).StartsWith(Path.Combine(Store.Root, "backups") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Некорректный журнал обновления.");
        if (Directory.Exists(backup)) Rollback(backup); else Commit();
    }
}
