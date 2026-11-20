using PlayMode.Core.Platform.Windows;

namespace PlayMode.Core.Tests;

/// <summary>Read-only: never calls Set, so it cannot change the machine's pointer settings.</summary>
public sealed class WindowsMouseTests
{
    [Fact]
    public void Reads_the_real_mouse_settings()
    {
        var mouse = new WindowsMouseSettings().Get();
        Assert.NotNull(mouse);
        Assert.InRange(mouse.Speed, 0, 2);
        Assert.Equal(mouse, new WindowsMouseSettings().Get());
    }
}
