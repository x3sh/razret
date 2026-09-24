using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace ZapretApp;

public static class Startup
{
    static string Sid => WindowsIdentity.GetCurrent().User!.Value;
    static string Name => "Razret-" + Sid;
    const string Description = "Razret: start saved bypass for the current interactive user.";
    static dynamic Service() { dynamic s = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service")!)!; s.Connect(); return s; }
    static dynamic? Find(dynamic folder)
    {
        try { return folder.GetTask(Name); }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070002)) { return null; }
    }
    public static bool Enabled()
    {
        dynamic service = Service(); dynamic task = Find(service.GetFolder("\\"));
        return task != null && task!.Enabled && Owned(task!.Definition);
    }
    static bool Owned(dynamic definition) => definition.RegistrationInfo.Description == Description
        && definition.Actions.Count == 1 && definition.Actions[1].Arguments == "--startup";
    public static string DefinitionXml()
    {
        dynamic service = Service(); return CreateDefinition(service).XmlText;
    }
    static dynamic CreateDefinition(dynamic service)
    {
        dynamic definition = service.NewTask(0);
        definition.RegistrationInfo.Description = Description;
        definition.Principal.UserId = Sid; definition.Principal.LogonType = 3; definition.Principal.RunLevel = 1;
        definition.Settings.DisallowStartIfOnBatteries = false; definition.Settings.StopIfGoingOnBatteries = false;
        definition.Settings.ExecutionTimeLimit = "PT0S"; definition.Settings.MultipleInstances = 2;
        definition.Settings.StartWhenAvailable = true;
        dynamic trigger = definition.Triggers.Create(9); trigger.UserId = Sid; trigger.Delay = "PT20S";
        dynamic action = definition.Actions.Create(0); action.Path = Environment.ProcessPath!;
        action.Arguments = "--startup"; action.WorkingDirectory = Store.Root;
        return definition;
    }
    public static void Set(bool enabled)
    {
        if (!Engine.Admin) throw new InvalidOperationException("Для изменения автозапуска нажмите «Разрешить запуск», затем сохраните настройки ещё раз.");
        dynamic service = Service(); dynamic folder = service.GetFolder("\\"); dynamic task = Find(folder);
        if (task != null && !Owned(task!.Definition)) throw new InvalidOperationException("Имя задачи занято другой программой. Задача не изменена.");
        if (enabled) folder.RegisterTaskDefinition(Name, CreateDefinition(service), 6, Sid, null, 3, null);
        else if (task != null) folder.DeleteTask(Name, 0);
        if (Enabled() != enabled) throw new IOException("Windows не подтвердила изменение автозапуска.");
    }
}

public sealed class TrayIcon : IDisposable
{
    readonly System.Windows.Forms.NotifyIcon icon;
    readonly System.Drawing.Icon drawingIcon;
    public TrayIcon(Action open, Action exit)
    {
        drawingIcon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "razret.ico"), 32, 32);
        icon = new() { Icon = drawingIcon, Text = "Razret", Visible = true };
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Открыть Razret", null, (_, _) => open());
        menu.Items.Add("Выйти и выключить обход", null, (_, _) => exit());
        icon.ContextMenuStrip = menu; icon.DoubleClick += (_, _) => open();
    }
    public void Status(bool running) => icon.Text = running ? "Razret · обход включён" : "Razret · обход выключен";
    public void Hint() => icon.ShowBalloonTip(3000, "Razret работает в трее", "Двойной щелчок по значку откроет окно. Для остановки выберите «Выйти и выключить обход».", System.Windows.Forms.ToolTipIcon.Info);
    public void Dispose() { icon.Visible = false; icon.ContextMenuStrip?.Dispose(); icon.Dispose(); drawingIcon.Dispose(); }
    [DllImport("user32.dll")] static extern bool DestroyIcon(nint icon);
}
