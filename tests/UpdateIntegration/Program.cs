using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using ZapretApp;

// An older shell version lets this test fetch the real public app release without
// publishing a fake release or changing the production app's version comparison.
if (args.Length == 0) throw new ArgumentException("Provide an output directory, then --live or --helper <published EXE> <restart fixture EXE>.");
string reportRoot = Path.GetFullPath(args[0]);
Directory.CreateDirectory(reportRoot);
Store.Root = Path.Combine(reportRoot, "isolated-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(Store.Engine, "lists"));
File.WriteAllText(Path.Combine(Store.Engine, "version.txt"), "1.10.3");
File.WriteAllText(Path.Combine(Store.Engine, "lists", "list-general-user.txt"), "example.com\n");
Store.Save(new Settings { Strategy = "general.bat", AutoConnect = false, CheckUpdates = false, StatisticsConsentAsked = true });
byte[] settingsBefore = File.ReadAllBytes(Path.Combine(Store.Data, "settings.json"));
var checks = new List<string>();
void Check(bool ok, string name) { if (!ok) throw new Exception(name); checks.Add(name); Console.WriteLine("PASS: " + name); }
void Preserved() => Check(File.ReadAllBytes(Path.Combine(Store.Data, "settings.json")).SequenceEqual(settingsBefore)
    && File.ReadAllText(Path.Combine(Store.Engine, "lists", "list-general-user.txt")) == "example.com\n", "Settings and user list preserved");
if (args.Contains("--live"))
{
    var appRelease = await AppUpdater.Latest(CancellationToken.None) ?? throw new Exception("No published app newer than test shell 0.7.0");
    var staged = await AppUpdater.Stage(appRelease, CancellationToken.None);
    string target = Path.Combine(Store.Root, "Razret.exe"), backup = Path.Combine(staged, "previous.exe");
    File.WriteAllText(target, "previous shell fixture");
    AppUpdater.Replace(Path.Combine(staged, "Razret.exe"), target, backup);
    Check(File.ReadAllText(backup) == "previous shell fixture" && Updater.Version == "1.10.3", "Real app release: download, SHA256, EXE version, replacement and backup");
    Preserved();
    byte[] appBefore = SHA256.HashData(File.ReadAllBytes(target));
    var engineRelease = await Updater.Latest(CancellationToken.None);
    var engineCandidate = await Updater.Stage(engineRelease, CancellationToken.None);
    var oldEngine = Updater.Swap(engineCandidate);
    Check(Updater.Version == engineRelease.Version && SHA256.HashData(File.ReadAllBytes(target)).SequenceEqual(appBefore), "Real upstream engine installs independently of app version");
    Preserved();
    var engineNotes = ReleaseNotesRu.ForEngine(engineRelease.Notes, "none");
    Check(engineNotes.Contains("TLS") && engineNotes.Contains("не используется") && engineNotes.Contains("не меняет код нашей диагностики"), "Engine notes explain strategy files, inactive IPSet and local Happ diagnostics");
    Updater.Rollback(oldEngine);
    Check(Updater.Version == "1.10.3", "Engine rollback restores previous version");
    Preserved();
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    bool stopped = false;
    try { await AppUpdater.Stage(appRelease, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
    Check(stopped && SHA256.HashData(File.ReadAllBytes(target)).SequenceEqual(appBefore), "Cancelled app download leaves installed EXE unchanged");
    File.WriteAllText(Path.Combine(reportRoot, "live-versions.json"), JsonSerializer.Serialize(new { App = appRelease.Version, Engine = engineRelease.Version, Notes = engineNotes }, Store.Json));
}
else if (Array.IndexOf(args, "--helper") is var helperIndex && helperIndex >= 0)
{
    string publishedExe = Path.GetFullPath(args[helperIndex + 1]), fixture = Path.GetFullPath(args[helperIndex + 2]);
    string area = Path.Combine(Store.Root, "updates", "app-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(area);
    File.Copy(publishedExe, Path.Combine(area, "installer.exe"));
    File.Copy(fixture, Path.Combine(area, "Razret.exe"));
    File.WriteAllText(Path.Combine(area, "target-version.txt"), "99.0.0");
    string target = Path.Combine(Store.Root, "Razret.exe"); File.WriteAllText(target, "old shell fixture");
    var parentStart = new ProcessStartInfo(fixture) { UseShellExecute = false, CreateNoWindow = true };
    parentStart.ArgumentList.Add("--hold"); parentStart.ArgumentList.Add(target);
    using var parent = Process.Start(parentStart) ?? throw new Exception("Locking parent did not start");
    for (int i = 0; i < 60 && !File.Exists(target + ".ready"); i++) await Task.Delay(50);
    Check(File.Exists(target + ".ready") && !parent.HasExited, "Old process holds executable against replacement");
    var start = new ProcessStartInfo(Path.Combine(area, "installer.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = area };
    start.ArgumentList.Add("--apply-app-update"); start.ArgumentList.Add(parent.Id.ToString()); start.ArgumentList.Add(Store.Root);
    using var process = Process.Start(start) ?? throw new Exception("Helper did not start");
    if (!process.WaitForExit(30000)) throw new Exception("Helper did not finish within 30 seconds");
    Check(process.ExitCode == 0, "Actual self-contained installer helper completed");
    Check(parent.HasExited, "Helper waited until old process released executable");
    for (int i = 0; i < 40 && !File.Exists(Path.Combine(Store.Root, "restarted.txt")); i++) await Task.Delay(100);
    Check(File.Exists(Path.Combine(Store.Root, "restarted.txt")) && File.ReadAllText(Path.Combine(area, "Razret.previous.exe")) == "old shell fixture", "App restart and previous EXE backup completed");
    Check(File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(fixture)) && Updater.Version == "1.10.3", "Helper replaced only the app executable");
    Preserved();
}
else throw new ArgumentException("Expected --live or --helper.");
File.WriteAllText(Path.Combine(reportRoot, args.Contains("--live") ? "live-result.json" : "helper-result.json"), JsonSerializer.Serialize(new { Passed = true, Checks = checks, IsolatedRoot = Store.Root, UserInstallationChanged = false }, Store.Json));
