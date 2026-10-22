using System.Runtime.InteropServices;
using System.Text;

namespace PlayMode.Core.Platform.Windows;

internal static class Native
{
    public const uint WM_CLOSE = 0x0010;
    public const int SW_HIDE = 0;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint PROCESS_SUSPEND_RESUME = 0x0800;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint WAIT_TIMEOUT = 0x102;
    public const int ERROR_ACCESS_DENIED = 5;

    public delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    public static extern int GetWindowText(nint hWnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(nint hWnd, uint msg, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ShowWindow(nint hWnd, int cmd);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(nint process, uint flags, StringBuilder name, ref uint size);

    [DllImport("kernel32.dll")]
    public static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("ntdll.dll")]
    public static extern int NtSuspendProcess(nint process);

    [DllImport("ntdll.dll")]
    public static extern int NtResumeProcess(nint process);

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "WTSQuerySessionInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WTSQuerySessionInformation(nint server, int sessionId, int infoClass, out nint buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    public static extern void WTSFreeMemory(nint memory);

    public static List<nint> TopLevelWindows(Func<nint, uint, bool> include)
    {
        var list = new List<nint>();
        EnumWindows((hWnd, _) =>
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            if (include(hWnd, pid))
                list.Add(hWnd);
            return true;
        }, 0);
        return list;
    }

    public static string WindowText(nint hWnd)
    {
        var text = new StringBuilder(256);
        GetWindowText(hWnd, text, text.Capacity);
        return text.ToString();
    }
}
