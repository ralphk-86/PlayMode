using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.ServiceProcess;
using System.Text;
using Microsoft.Win32;
using PlayMode.Core.Discovery;
using PlayMode.Core.Engine;
using PlayMode.Core.Model;
using PlayMode.Core.Storage;

namespace PlayMode.Core.Platform.Windows;

/// <summary>Looks around the machine for the settings window: running apps, startup items, services.</summary>
public static class WindowsDiscovery
{
    static readonly string WindowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    public static MachineScan Scan(PlayModeConfig? config, Catalog catalog)
    {
        var source = new WindowsProcessSource();
        var ctx = new MatchContext(source.CurrentProcessId, source.CurrentSessionId, config?.NeverTouch ?? []);
        var processes = source.Snapshot();
        var startup = StartupEntries();
        var tailscale = new TailscaleCli(new FileLog(AppPaths.LogFile));
        return new MachineScan
        {
            Running = AppGrouper.Group(processes, Roots(), WindowsDir, catalog, ctx, new ProcessDetails(), startup),
            Startup = startup,
            Services = Services(),
            ProcessNames = processes.Where(p => p.SessionId == ctx.SessionId).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase),
            InstalledRecipes = catalog.Apps
                .Where(a => a.Detect.Any(path => File.Exists(Environment.ExpandEnvironmentVariables(path))))
                .Select(a => a.Rule.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            WslRunning = processes.Any(p => string.Equals(p.Name, "vmmemWSL", StringComparison.OrdinalIgnoreCase)),
            TailscaleUp = tailscale.IsInstalled && tailscale.IsUp(),
        };
    }

    /// <summary>Folders that hold many unrelated apps; see <see cref="AppGrouper.Group"/>.</summary>
    static IReadOnlyCollection<string> Roots()
    {
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        var pf = F(Environment.SpecialFolder.ProgramFiles);
        var pf86 = F(Environment.SpecialFolder.ProgramFilesX86);
        var local = F(Environment.SpecialFolder.LocalApplicationData);
        var roaming = F(Environment.SpecialFolder.ApplicationData);
        var home = F(Environment.SpecialFolder.UserProfile);
        var data = F(Environment.SpecialFolder.CommonApplicationData);
        return [.. new[]
        {
            pf, pf86, $@"{pf}\WindowsApps", $@"{pf}\Common Files", $@"{pf86}\Common Files", $@"{pf86}\Microsoft",
            local, $@"{local}\Programs", $@"{local}\Microsoft", $@"{local}\Microsoft\WindowsApps",
            $@"{local}\Microsoft\WinGet\Packages", $@"{local}\Temp", roaming, $@"{roaming}\Microsoft",
            home, $@"{home}\.local\bin", $@"{home}\bin", $@"{home}\.cargo\bin", $@"{home}\go\bin",
            $@"{home}\scoop\apps", $@"{home}\scoop\shims", data, $@"{data}\chocolatey\bin",
        }.Where(r => r.Length > 3).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    // ---------------------------------------------------------------- startup items

    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    public static IReadOnlyList<StartupEntry> StartupEntries()
    {
        var list = new List<StartupEntry>();
        AddRunKey(list, Registry.CurrentUser, RunKey, "Run", "Run (user)");
        AddRunKey(list, Registry.LocalMachine, RunKey, "Run", "Run (all users)");
        AddRunKey(list, Registry.LocalMachine, @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run", "Run32", "Run (all users, 32-bit)");
        AddStartupFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.Startup), Registry.CurrentUser, "Startup folder");
        AddStartupFolder(list, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), Registry.LocalMachine, "Startup folder (all users)");
        return list;
    }

    static void AddRunKey(List<StartupEntry> list, RegistryKey hive, string path, string approvedName, string location)
    {
        using var key = hive.OpenSubKey(path);
        if (key is null)
            return;
        using var approved = hive.OpenSubKey($@"{ApprovedKey}\{approvedName}");
        foreach (var name in key.GetValueNames())
        {
            if (key.GetValue(name) is not string command || command.Trim().Length == 0)
                continue;
            var (exe, args, launches) = CommandLine.Split(Environment.ExpandEnvironmentVariables(command));
            list.Add(new StartupEntry
            {
                Name = name,
                Location = location,
                Command = command,
                Exe = exe,
                Args = args,
                Launches = launches,
                Enabled = IsApproved(approved, name),
            });
        }
    }

    static void AddStartupFolder(List<StartupEntry> list, string folder, RegistryKey hive, string location)
    {
        if (!Directory.Exists(folder))
            return;
        using var approved = hive.OpenSubKey($@"{ApprovedKey}\StartupFolder");
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                continue;
            var isLink = file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase);
            var (target, args) = isLink ? ResolveShortcut(file) : (file, null);
            list.Add(new StartupEntry
            {
                Name = Path.GetFileNameWithoutExtension(file),
                Location = location,
                Command = target is null ? file : $"\"{target}\" {args}".Trim(),
                Exe = target,
                Args = args,
                Shortcut = isLink ? file : null,
                Enabled = IsApproved(approved, fileName),
            });
        }
    }

    /// <summary>Task Manager's on/off switch: the first byte is even when enabled (or there's no record).</summary>
    static bool IsApproved(RegistryKey? approved, string name) =>
        approved?.GetValue(name) is not byte[] { Length: > 0 } flags || (flags[0] & 1) == 0;

    static (string? Target, string? Args) ResolveShortcut(string path)
    {
        try
        {
            var link = (IShellLinkW)(object)new ShellLink();
            try
            {
                ((IPersistFile)link).Load(path, 0);
                var target = new StringBuilder(1024);
                link.GetPath(target, target.Capacity, 0, 0);
                var args = new StringBuilder(2048);
                link.GetArguments(args, args.Capacity);
                return (target.Length == 0 ? null : target.ToString(), args.Length == 0 ? null : args.ToString());
            }
            finally
            {
                Marshal.ReleaseComObject(link);
            }
        }
        catch (COMException)
        {
            return (null, null);
        }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    sealed class ShellLink;

    /// <summary>The leading part of IShellLinkW, up to the methods we call.</summary>
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, nint findData, uint flags);
        void GetIDList(out nint idList);
        void SetIDList(nint idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
    }

    // ---------------------------------------------------------------- services

    public static IReadOnlyList<ServiceEntry> Services()
    {
        var list = new List<ServiceEntry>();
        foreach (var service in ServiceController.GetServices())
        {
            using (service)
            {
                try
                {
                    var exe = ServiceExe(service.ServiceName);
                    list.Add(new ServiceEntry(
                        service.ServiceName,
                        service.DisplayName,
                        service.Status == ServiceControllerStatus.Running,
                        exe,
                        IsWindowsComponent(exe)));
                }
                catch (Exception e) when (e is InvalidOperationException or Win32Exception)
                {
                    // Removed while we were looking, or not ours to query.
                }
            }
        }
        return list;
    }

    /// <summary>Windows itself, or Defender, which updates itself into ProgramData.</summary>
    static bool IsWindowsComponent(string? exe) =>
        exe is null
        || exe.StartsWith(WindowsDir + "\\", StringComparison.OrdinalIgnoreCase)
        || exe.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Microsoft\Windows Defender") + "\\", StringComparison.OrdinalIgnoreCase)
        || exe.StartsWith(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender") + "\\", StringComparison.OrdinalIgnoreCase);

    static string? ServiceExe(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
        return key?.GetValue("ImagePath") is string image
            ? CommandLine.Split(Environment.ExpandEnvironmentVariables(image)).Exe
            : null;
    }

    // ---------------------------------------------------------------- process details

    sealed class ProcessDetails : IProcessDetails
    {
        readonly HashSet<int> _withWindows = [.. Native.TopLevelWindows((hWnd, _) => Native.IsWindowVisible(hWnd) && Native.WindowText(hWnd).Length > 0)
            .Select(hWnd => { Native.GetWindowThreadProcessId(hWnd, out var pid); return (int)pid; })];

        public bool HasWindow(int pid) => _withWindows.Contains(pid);

        public string? Describe(string path)
        {
            try
            {
                var info = FileVersionInfo.GetVersionInfo(path);
                return string.IsNullOrWhiteSpace(info.FileDescription) ? info.ProductName : info.FileDescription;
            }
            catch (Exception e) when (e is FileNotFoundException or IOException or UnauthorizedAccessException)
            {
                return null;
            }
        }

        public string? AppUserModelId(int pid)
        {
            var handle = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
            if (handle == 0)
                return null;
            try
            {
                var id = new StringBuilder(256);
                var length = (uint)id.Capacity;
                return Native.GetApplicationUserModelId(handle, ref length, id) == 0 ? id.ToString() : null;
            }
            finally
            {
                Native.CloseHandle(handle);
            }
        }
    }
}
