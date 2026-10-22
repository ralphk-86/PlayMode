using PlayMode.Core.Model;
using PlayMode.Core.Platform.Windows;
using PlayMode.Shared;

namespace PlayMode.Core.Tests;

public class ServiceAllowlistTests
{
    [Fact]
    public void Merges_services_from_all_profiles_once_each_in_name_order()
    {
        var config = new PlayModeConfig
        {
            Profiles =
            [
                new Profile { Name = "A", Services = [new ServiceRule { Name = "WSearch" }, new ServiceRule { Name = "Everything" }] },
                new Profile { Name = "B", Services = [new ServiceRule { Name = "wsearch" }, new ServiceRule { Name = "AUEPLauncher" }] },
            ],
        };
        Assert.Equal(["AUEPLauncher", "Everything", "WSearch"], ServiceHelperClient.AllowlistFor(config).Select(s => s.Name));
    }

    [Fact]
    public void A_service_is_restarted_only_if_every_profile_says_so()
    {
        // e.g. a VPN client restarts its own service, so one profile marking it restart=false must win.
        var config = new PlayModeConfig
        {
            Profiles =
            [
                new Profile { Name = "A", Services = [new ServiceRule { Name = "ProtonVPN Service" }] },
                new Profile { Name = "B", Services = [new ServiceRule { Name = "ProtonVPN Service", Restart = false }] },
            ],
        };
        Assert.Equal(new AllowedService("ProtonVPN Service", Restart: false), ServiceHelperClient.AllowlistFor(config).Single());
    }

    [Fact]
    public void No_services_means_no_allowlist()
    {
        Assert.Empty(ServiceHelperClient.AllowlistFor(new PlayModeConfig { Profiles = [new Profile { Name = "A" }] }));
    }
}
