using System.Runtime.InteropServices;

namespace ZapretApp;

// Query only the upstream service name. No process inventory, stopping services,
// registry changes or network access is needed for this diagnostic.
public static class HappDiagnostics
{
    public record Result(int? State, int Error = 0);

    public static Result Check()
    {
        var manager = OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT
        if (manager == 0) return new(null, Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, "HappService", 4); // SERVICE_QUERY_STATUS
            if (service == 0) return new(null, Marshal.GetLastWin32Error());
            try
            {
                return QueryServiceStatus(service, out var status)
                    ? new(status.CurrentState) : new(null, Marshal.GetLastWin32Error());
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    public static string Describe(Result result) => result switch
    {
        { Error: 1060 } => "Happ: служба HappService не установлена. Это не исключает работу клиента без службы.",
        { Error: not 0 } => $"Happ: не удалось проверить службу (код Windows {result.Error}). Наличие конфликта не определено.",
        { State: 1 } => "Happ: служба установлена и остановлена.",
        { State: 4 } => "Happ: служба работает. Happ — клиент для прокси и туннелей; его маршрутизация может влиять на zapret и результаты тестов. При проблемах отключите подключение в Happ и повторите проверку. Работающая служба сама по себе не доказывает конфликт.",
        { State: >= 2 and <= 7 } => "Happ: служба установлена и находится в переходном или приостановленном состоянии. Если доступ нестабилен, проверьте подключение в Happ и повторите диагностику.",
        _ => "Happ: состояние службы неизвестно. Наличие конфликта не определено."
    };

    [StructLayout(LayoutKind.Sequential)]
    struct ServiceStatus { public int Type, CurrentState, Controls, Win32Exit, ServiceExit, CheckPoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern nint OpenService(nint manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryServiceStatus(nint service, out ServiceStatus status);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CloseServiceHandle(nint handle);
}
