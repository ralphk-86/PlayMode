using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PlayMode.Core.Platform.Windows;

public sealed class WindowsProcessSource : IProcessSource
{
    public int CurrentProcessId { get; } = Environment.ProcessId;
    public int CurrentSessionId { get; } = Process.GetCurrentProcess().SessionId;

    public IReadOnlyList<ProcessInfo> Snapshot()
    {
        var list = new List<ProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            using (p)
            {
                try
                {
                    list.Add(new ProcessInfo(p.Id, p.ProcessName, ImagePath(p.Id), p.SessionId, p.WorkingSet64));
                }
                catch (InvalidOperationException)
                {
                    // Exited while we were looking.
                }
            }
        }
        return list;
    }

    public bool IsAlive(int pid)
    {
        var handle = Native.OpenProcess(Native.SYNCHRONIZE, false, (uint)pid);
        if (handle == 0)
            return Marshal.GetLastPInvokeError() == Native.ERROR_ACCESS_DENIED;   // exists, just not ours to open
        try
        {
            return Native.WaitForSingleObject(handle, 0) == Native.WAIT_TIMEOUT;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }

    /// <summary>Works for most processes without admin rights, unlike Process.MainModule.</summary>
    internal static string? ImagePath(int pid)
    {
        var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (handle == 0)
            return null;
        try
        {
            var name = new StringBuilder(1024);
            var size = (uint)name.Capacity;
            return Native.QueryFullProcessImageName(handle, 0, name, ref size) ? name.ToString() : null;
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }
}

public sealed class WindowsProcessControl : IProcessControl
{
    public void RequestClose(int pid)
    {
        foreach (var hWnd in Native.TopLevelWindows((_, owner) => owner == pid))
            Native.PostMessage(hWnd, Native.WM_CLOSE, 0, 0);
    }

    public void Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill();
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception)
        {
            // Already gone, or not ours to kill; the caller re-checks.
        }
    }

    public bool Suspend(int pid) => WithSuspendHandle(pid, h => Native.NtSuspendProcess(h) == 0);

    public bool Resume(int pid) => WithSuspendHandle(pid, h => Native.NtResumeProcess(h) == 0);

    public string? Start(string target, string? args)
    {
        var isShell = target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase);
        if (!isShell && !File.Exists(target))
            return $"not found: {target}";
        try
        {
            var info = isShell
                ? new ProcessStartInfo("explorer.exe", target)
                : new ProcessStartInfo(target)
                {
                    UseShellExecute = true,   // also handles .lnk
                    Arguments = args ?? "",
                    WorkingDirectory = Path.GetDirectoryName(target) ?? "",
                };
            using var _ = Process.Start(info);
            return null;
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException)
        {
            return e.Message;
        }
    }

    static bool WithSuspendHandle(int pid, Func<nint, bool> action)
    {
        var handle = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, (uint)pid);
        if (handle == 0)
            return false;
        try
        {
            return action(handle);
        }
        finally
        {
            Native.CloseHandle(handle);
        }
    }
}

public sealed class WindowsWindowControl : IWindowControl
{
    public IReadOnlyList<WindowInfo> VisibleWindows(IReadOnlyCollection<int> pids)
    {
        var owners = pids.ToHashSet();
        return [.. Native.TopLevelWindows((hWnd, owner) => owners.Contains((int)owner) && Native.IsWindowVisible(hWnd))
            .Select(hWnd => new WindowInfo(hWnd, Native.WindowText(hWnd)))
            .Where(w => w.Title.Length > 0)];
    }

    public void Close(nint handle) => Native.PostMessage(handle, Native.WM_CLOSE, 0, 0);

    public void Hide(nint handle) => Native.ShowWindow(handle, Native.SW_HIDE);
}
