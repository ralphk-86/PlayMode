using System.Diagnostics;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using PlayMode.Shared;

namespace PlayMode.Helper;

/// <summary>One-time setup, run elevated through UAC by the app.</summary>
static class Installer
{
    static readonly SecurityIdentifier System = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    public static int Install(string? allowlistPath, string? userSid)
    {
        if (!IsAdmin())
            return 3;
        if (allowlistPath is null || userSid is null)
            return 2;
        var user = new SecurityIdentifier(userSid);
        var allowlist = Program.ReadJson<List<AllowedService>>(allowlistPath)
            ?? throw new InvalidDataException($"Unreadable allowlist {allowlistPath}");
        foreach (var service in allowlist)
            Validate(service.Name);

        // 1. Binaries and allowlist into Program Files, which only admins can change.
        Directory.CreateDirectory(HelperPaths.InstallDir);
        var source = Path.GetFullPath(AppContext.BaseDirectory);
        if (!string.Equals(source.TrimEnd('\\'), Path.GetFullPath(HelperPaths.InstallDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            // Recursive: dependencies keep their Windows implementations under runtimes\win\.
            foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(HelperPaths.InstallDir, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
        }
        Program.WriteJson(HelperPaths.Allowlist, allowlist);

        // 2. Data folder: admins and SYSTEM full, users read. Only the installing user may write the request.
        Directory.CreateDirectory(HelperPaths.DataDir);
        var folder = new DirectorySecurity();
        folder.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        folder.AddAccessRule(new FileSystemAccessRule(System, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        folder.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        folder.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(HelperPaths.DataDir).SetAccessControl(folder);

        var request = new FileInfo(HelperPaths.Request);
        if (!request.Exists)
            File.WriteAllText(request.FullName, "{}");
        var requestAcl = request.GetAccessControl();
        requestAcl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.Modify, AccessControlType.Allow));
        request.SetAccessControl(requestAcl);

        // 3. On-demand tasks that run the helper as SYSTEM; only this user (and admins) may trigger them.
        foreach (var action in Enum.GetValues<HelperAction>())
            RegisterTask(action, user);

        Program.Log($"installed for {user.Value}; allowlist: {string.Join(", ", allowlist.Select(a => a.Name))}");
        return 0;
    }

    public static int Uninstall()
    {
        if (!IsAdmin())
            return 3;
        foreach (var action in Enum.GetValues<HelperAction>())
            Schtasks("/Delete", "/TN", HelperPaths.TaskName(action), "/F");
        if (Directory.Exists(HelperPaths.InstallDir))
            Directory.Delete(HelperPaths.InstallDir, recursive: true);
        var parent = Path.GetDirectoryName(HelperPaths.InstallDir)!;
        if (Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            Directory.Delete(parent);
        if (Directory.Exists(HelperPaths.DataDir))
            Directory.Delete(HelperPaths.DataDir, recursive: true);
        return 0;
    }

    static void RegisterTask(HelperAction action, SecurityIdentifier user)
    {
        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>PlayMode: {action.ToString().ToLowerInvariant()} the services you approved for play mode</Description>
                <SecurityDescriptor>D:(A;;FA;;;BA)(A;;FA;;;SY)(A;;GRGX;;;{user.Value})</SecurityDescriptor>
              </RegistrationInfo>
              <Principals>
                <Principal id="Author">
                  <UserId>S-1-5-18</UserId>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <ExecutionTimeLimit>PT5M</ExecutionTimeLimit>
                <Enabled>true</Enabled>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{SecurityElement.Escape(HelperPaths.Exe)}"</Command>
                  <Arguments>{action.ToString().ToLowerInvariant()}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
        var temp = Path.Combine(Path.GetTempPath(), $"playmode-task-{action}-{Guid.NewGuid():N}.xml");
        File.WriteAllText(temp, xml, Encoding.Unicode);
        try
        {
            var exit = Schtasks("/Create", "/TN", HelperPaths.TaskName(action), "/XML", temp, "/F");
            if (exit != 0)
                throw new InvalidOperationException($"schtasks /Create failed for {action} (exit {exit})");
        }
        finally
        {
            File.Delete(temp);
        }
    }

    static int Schtasks(params string[] args)
    {
        var info = new ProcessStartInfo("schtasks.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args)
            info.ArgumentList.Add(arg);
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            Program.Log($"schtasks {string.Join(' ', args)} -> {p.ExitCode}: {output.Trim()}");
        return p.ExitCode;
    }

    /// <summary>Plain service names only: no wildcards or paths.</summary>
    static void Validate(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || name.IndexOfAny(['*', '?', '\\', '/', '"', '<', '>', '|']) >= 0 || name.Any(char.IsControl))
            throw new InvalidDataException($"Invalid service name '{name}'");
    }

    static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
}
