using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Principal;
using System.Net.NetworkInformation;
using System.Net.WebSockets;
using System.Net.Http;
using System.Runtime.InteropServices;

namespace ZapretApp;

public sealed class Settings
{
    public string? Strategy { get; set; }
    public string GameMode { get; set; } = "off";
    public string Ipset { get; set; } = "none";
    public bool AutoConnect { get; set; } = true;
    public bool CheckUpdates { get; set; } = true;
    public bool SetupComplete { get; set; }
    public bool StartWithWindows { get; set; }
    public bool CloseToTray { get; set; } = true;
    public string? PostponedVersion { get; set; }
    public string Theme { get; set; } = "dark";
}
public record Probe(string Service, bool Ok, double Milliseconds, string Detail = "", double? Ping = null);
public record Result(string Name, List<Probe> Probes)
{
    public int Passed => Probes.Count(x => x.Ok);
    public bool Complete => Probes.Count > 0 && Probes.All(x => x.Ok);
    public double Delay => Probes.Where(x => x.Ok).Select(x => x.Milliseconds).DefaultIfEmpty(999999).Average();
    public string Summary => $"{Passed}/{Probes.Count}  ·  {(Passed > 0 ? $"{Delay:0} мс" : "—")}";
}

public static class Store
{
    static readonly object logLock = new();
    public static string Root = AppContext.BaseDirectory;
    public static string Engine => Path.Combine(Root, "engine");
    public static string Data => Path.Combine(Root, "data");
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static Settings Load()
    {
        Directory.CreateDirectory(Data);
        var path = Path.Combine(Data, "settings.json");
        if (!File.Exists(path)) return new();
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new(); }
        catch (JsonException) { File.Copy(path, path + ".corrupt-" + DateTime.UtcNow.Ticks); return new(); }
    }
    public static void Save(Settings settings) => Write("settings.json", settings);
    public static void Write(string name, object value)
    {
        Directory.CreateDirectory(Data);
        var path = Path.Combine(Data, name);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(value, Json));
        File.Move(path + ".tmp", path, true);
    }
    public static void Log(string message)
    {
        lock (logLock)
        {
            Directory.CreateDirectory(Data);
            var path = Path.Combine(Data, "app.log");
            if (File.Exists(path) && new FileInfo(path).Length > 2_000_000) File.Move(path, path + ".previous", true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:O} {message}\n");
        }
    }
}

public static class Strategies
{
    public static string[] List(string root) => Directory.GetFiles(root, "general*.bat").Select(Path.GetFileName).Cast<string>().OrderBy(x => Regex.Replace(x, @"\d+", m => m.Value.PadLeft(5, '0'))).ToArray();
    public static List<string> Parse(string text, string root, Settings settings)
    {
        var command = Regex.Match(text, @"(?im)^start\s+[^\r\n]*?winws\.exe\""\s+([\s\S]+)$");
        if (!command.Success) throw new InvalidDataException("Неизвестный формат стратегии: команда winws не найдена.");
        var raw = Regex.Replace(command.Groups[1].Value, @"\^\r?\n", " ").Trim();
        if (raw.Contains('\n') || raw.IndexOfAny(['&', '|', '<', '>']) >= 0) throw new InvalidDataException("Стратегия содержит неподдерживаемые команды.");
        string tcp = settings.GameMode is "tcp" or "all" ? "1024-65535" : "12";
        string udp = settings.GameMode is "udp" or "all" ? "1024-65535" : "12";
        raw = raw.Replace("%BIN%", Path.Combine(root, "bin") + Path.DirectorySeparatorChar)
            .Replace("%LISTS%", Path.Combine(root, "lists") + Path.DirectorySeparatorChar)
            .Replace("%GameFilterTCP%", tcp).Replace("%GameFilterUDP%", udp).Replace("%GameFilter%", udp);
        if (raw.Contains('%')) throw new InvalidDataException("Неизвестная переменная в стратегии.");
        var args = Regex.Matches(raw, "(?:[^\\s\"]+|\"[^\"]*\")+").Select(m => m.Value.Replace("\"", "")).ToList();
        if (args.Count == 0 || args.Any(x => !x.StartsWith("--"))) throw new InvalidDataException("Неизвестный формат аргументов.");
        foreach (var arg in args)
        {
            int eq = arg.IndexOf('=');
            if (eq < 0) continue;
            var value = arg[(eq + 1)..];
            if (Path.IsPathRooted(value))
            {
                var full = Path.GetFullPath(value);
                if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Путь стратегии выходит за папку движка.");
            }
        }
        return args;
    }
    public static void Prepare(string root, Settings settings)
    {
        var lists = Path.Combine(root, "lists");
        foreach (var pair in new[] { ("list-general-user.txt", "domain.example.abc\n"), ("list-exclude-user.txt", "domain.example.abc\n"), ("ipset-exclude-user.txt", "203.0.113.113/32\n") })
            if (!File.Exists(Path.Combine(lists, pair.Item1))) File.WriteAllText(Path.Combine(lists, pair.Item1), pair.Item2);
        var ipset = Path.Combine(lists, "ipset-all.txt");
        var backup = ipset + ".backup";
        if (settings.Ipset == "loaded")
        {
            if (!File.Exists(backup)) throw new IOException("Отсутствует исходный IPSet.");
            File.Copy(backup, ipset, true);
        }
        else File.WriteAllText(ipset, settings.Ipset == "any" ? "" : "203.0.113.113/32\n");
    }
    public static void Validate(string root)
    {
        foreach (var file in new[] { "bin/winws.exe", "bin/WinDivert.dll", "bin/WinDivert64.sys", "bin/cygwin1.dll", "lists/list-general.txt", "lists/list-google.txt", "lists/list-exclude.txt", "lists/ipset-exclude.txt", "lists/ipset-all.txt.backup" })
            if (!File.Exists(Path.Combine(root, file))) throw new InvalidDataException("В комплекте отсутствует " + file);
        if (List(root).Length == 0) throw new InvalidDataException("В комплекте нет стратегий.");
        foreach (var file in List(root))
        {
            var args = Parse(File.ReadAllText(Path.Combine(root, file)), root, new());
            foreach (var arg in args)
            {
                int equals = arg.IndexOf('=');
                if (equals < 0) continue;
                var value = arg[(equals + 1)..];
                if (Path.IsPathRooted(value) && !value.EndsWith("-user.txt") && !File.Exists(value))
                    throw new InvalidDataException("Нет файла " + Path.GetFileName(value));
            }
        }
    }
}

public sealed class Engine : IDisposable
{
    Process? child;
    readonly nint job;
    public string? Active { get; private set; }
    public bool Running => child is { HasExited: false };
    public static bool Admin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public Engine()
    {
        job = CreateJobObject(0, null);
        var info = new JobInfo(); info.Basic.LimitFlags = 0x2000;
        if (job == 0 || !SetInformationJobObject(job, 9, ref info, (uint)Marshal.SizeOf<JobInfo>())) throw new IOException("Не удалось создать изолированную группу процессов.");
    }
    public string[] Conflicts() => Process.GetProcessesByName("winws").Select(p => { using (p) return p.Id; }).Where(id => child == null || id != child.Id).Select(id => $"winws, PID {id}").ToArray();
    public async Task Start(string name, Settings settings, CancellationToken ct)
    {
        if (!Admin) throw new InvalidOperationException("Для включения нужны права администратора. Нажмите «Права администратора».");
        if (Conflicts().Length > 0) throw new InvalidOperationException("Обнаружен другой zapret. Остановите его через программу, которая его запустила. Приложение его не завершает.");
        if (!Strategies.List(Store.Engine).Contains(name)) throw new InvalidDataException("Стратегия не найдена.");
        Stop();
        Strategies.Prepare(Store.Engine, settings);
        var args = Strategies.Parse(File.ReadAllText(Path.Combine(Store.Engine, name)), Store.Engine, settings);
        var info = new ProcessStartInfo(Path.Combine(Store.Engine, "bin", "winws.exe")) { WorkingDirectory = Path.Combine(Store.Engine, "bin"), UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        child = new Process { StartInfo = info };
        child.OutputDataReceived += (_, e) => { if (e.Data != null) Store.Log(e.Data); };
        child.ErrorDataReceived += (_, e) => { if (e.Data != null) Store.Log(e.Data); };
        child.Start();
        if (!AssignProcessToJobObject(job, child.Handle)) { child.Kill(); throw new IOException("Не удалось привязать процесс к приложению."); }
        child.BeginOutputReadLine(); child.BeginErrorReadLine();
        try
        {
            await Task.Delay(1200, ct);
            if (child.HasExited) throw new IOException($"Движок завершился с кодом {child.ExitCode}. Подробности в журнале.");
            Active = name;
        }
        catch { Stop(); throw; }
    }
    public void Stop()
    {
        if (child != null) { if (!child.HasExited) { child.Kill(); child.WaitForExit(4000); } child.Dispose(); child = null; }
        Active = null;
    }
    public void Dispose() { Stop(); CloseHandle(job); }
    [StructLayout(LayoutKind.Sequential)] struct BasicInfo { public long ProcessTime, JobTime; public uint LimitFlags; public nuint Min, Max; public uint ActiveLimit; public nuint Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct IoInfo { public ulong A,B,C,D,E,F; }
    [StructLayout(LayoutKind.Sequential)] struct JobInfo { public BasicInfo Basic; public IoInfo Io; public nuint ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] static extern nint CreateJobObject(nint attr, string? name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(nint job, int type, ref JobInfo info, uint length);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(nint job, nint process);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(nint handle);
}

public static class Checks
{
    static readonly (string Name, string Url)[] Targets = [("Discord API", "https://discord.com/api/v10/gateway"), ("Discord CDN", "https://cdn.discordapp.com/embed/avatars/0.png"), ("YouTube", "https://www.youtube.com/"), ("YouTube изображения", "https://i.ytimg.com/vi/jNQXAC9IVRw/hqdefault.jpg")];
    public static async Task<List<Probe>> Run(CancellationToken ct)
    {
        var results = await Task.WhenAll(Targets.Select(t => Retry(() => Http(t.Name, t.Url, ct), ct)));
        var list = results.ToList(); list.Add(await Retry(() => Gateway(ct), ct)); return list;
    }
    internal static async Task<Probe> Retry(Func<Task<Probe>> probe, CancellationToken ct)
    {
        var first = await probe();
        if (first.Ok) return first;
        await Task.Delay(800, ct);
        var second = await probe();
        return second with { Detail = second.Detail + (second.Ok ? " · со второй попытки" : " · две неудачные попытки") + " (первая: " + first.Detail + ")" };
    }
    internal static async Task<bool> ReadHello(WebSocket socket, CancellationToken ct)
    {
        using var message = new MemoryStream();
        var buffer = new byte[4096];
        WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (part.MessageType != WebSocketMessageType.Text || message.Length + part.Count > 65536) return false;
            message.Write(buffer, 0, part.Count);
        } while (!part.EndOfMessage);
        try
        {
            using var json = JsonDocument.Parse(message.ToArray());
            return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("op", out var op) && op.ValueKind == JsonValueKind.Number && op.TryGetInt32(out var code) && code == 10;
        }
        catch (JsonException) { return false; }
    }
    static async Task<Probe> Http(string name, string url, CancellationToken ct)
    {
        using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = true, ConnectTimeout = TimeSpan.FromSeconds(4) }) { Timeout = TimeSpan.FromSeconds(7) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(7000);
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            bool ok = response.IsSuccessStatusCode;
            if (name == "Discord API" && ok)
            {
                var json = await response.Content.ReadAsStringAsync(timeout.Token);
                ok = json.Contains("wss://gateway.discord.gg", StringComparison.Ordinal);
            }
            if (ok && (name == "Discord CDN" || name == "YouTube изображения"))
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                var bytes = new byte[4096];
                ok = (response.Content.Headers.ContentType?.MediaType?.StartsWith("image/") == true) && await stream.ReadAsync(bytes, timeout.Token) > 0;
            }
            double elapsed = sw.Elapsed.TotalMilliseconds;
            double? pingMs = null;
            try { using var ping = new Ping(); var reply = await ping.SendPingAsync(new Uri(url).Host, TimeSpan.FromMilliseconds(700), cancellationToken: ct); if (reply.Status == IPStatus.Success) pingMs = reply.RoundtripTime; } catch (PingException) { }
            return new(name, ok, elapsed, $"HTTP {(int)response.StatusCode}", pingMs);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new(name, false, 7000, "Тайм-аут"); }
        catch (HttpRequestException ex) { return new(name, false, sw.Elapsed.TotalMilliseconds, ex.HttpRequestError.ToString()); }
        catch (IOException) { return new(name, false, sw.Elapsed.TotalMilliseconds, "Ошибка чтения ответа"); }
    }
    static async Task<Probe> Gateway(CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.Proxy = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(7000);
        var sw = Stopwatch.StartNew();
        try
        {
            await socket.ConnectAsync(new Uri("wss://gateway.discord.gg/?v=10&encoding=json"), timeout.Token);
            bool hello = await ReadHello(socket, timeout.Token);
            return new("Discord Gateway", hello, sw.Elapsed.TotalMilliseconds, hello ? "WebSocket Hello" : "Нет Hello");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new("Discord Gateway", false, 7000, "Тайм-аут"); }
        catch (WebSocketException) { return new("Discord Gateway", false, sw.Elapsed.TotalMilliseconds, "Ошибка WebSocket"); }
    }
    public static IEnumerable<Result> Rank(IEnumerable<Result> results) => results.OrderByDescending(r => r.Complete).ThenByDescending(r => r.Passed).ThenBy(r => r.Delay).ThenBy(r => r.Probes.Where(p => p.Ping.HasValue).Select(p => p.Ping!.Value).DefaultIfEmpty(double.PositiveInfinity).Average()).ThenBy(r => r.Name, StringComparer.Ordinal);
}
