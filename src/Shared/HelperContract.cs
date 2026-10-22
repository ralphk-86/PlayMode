// Shared between PlayMode.Core (the client) and PlayMode.Helper (the elevated side), compiled into both.
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PlayMode.Shared;

public enum HelperAction { Stop, Start }

/// <summary>A service the user approved (through UAC, at setup) for PlayMode to stop.</summary>
public sealed record AllowedService(string Name, bool Restart);

public sealed record HelperRequest(Guid Id, HelperAction Action, IReadOnlyList<string> Services);

public sealed record HelperResult(Guid Id, HelperAction Action, IReadOnlyList<string> Ok, IReadOnlyList<string> Failed, IReadOnlyList<string> Notes);

public static class HelperPaths
{
    /// <summary>Admin-only write (Program Files): the helper binaries and the service allowlist.</summary>
    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PlayMode", "Helper");

    /// <summary>Users can read; only the installing user can write the request file.</summary>
    public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PlayMode");

    public const string ExeName = "PlayMode.Helper.exe";
    public const string DllName = "PlayMode.Helper.dll";
    public static string Exe => Path.Combine(InstallDir, ExeName);
    public static string Allowlist => Path.Combine(InstallDir, "services.json");
    public static string Request => Path.Combine(DataDir, "request.json");
    public static string Result => Path.Combine(DataDir, "result.json");
    public static string State => Path.Combine(DataDir, "svc-state.json");
    public static string Log => Path.Combine(DataDir, "helper.log");

    public static string TaskName(HelperAction action) => $@"\PlayMode\Services{action}";
}

public static class HelperJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };
}
