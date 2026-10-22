using PlayMode.Core.Storage;

namespace PlayMode.Core.Platform.Windows;

public static class WindowsPlatform
{
    public static PlatformServices Create(bool echoLog = false, Func<string, CancellationToken, Task<ServiceResult?>>? serviceHelper = null)
    {
        var log = new FileLog(AppPaths.LogFile, echoLog);
        return new PlatformServices(
            new WindowsProcessSource(),
            new WindowsProcessControl(),
            new WindowsWindowControl(),
            new WindowsServiceControl(serviceHelper, log),
            new TailscaleCli(log),
            new WslCli(),
            new WindowsSystemInfo(),
            new JsonStateStore(AppPaths.StateFile, log),
            new SystemClock(),
            log);
    }
}
