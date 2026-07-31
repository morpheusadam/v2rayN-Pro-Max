using AwesomeAssertions;
using ServiceLib.Enums;
using ServiceLib.Manager;
using Xunit;

namespace ServiceLib.Tests.Manager;

/// <summary>
/// Guards the one thing that can silently undo this entire fork.
///
/// Updating the app replaces v2rayN.exe and ServiceLib.dll in place. Auto Mode, the
/// styling, the icons and the fix for the batch that reported every server as -1 are all
/// compiled into those two files, while the settings they read live in guiConfigs and
/// would survive. Accepting an upstream update therefore does not "lose settings" in a
/// way anyone would notice immediately — it leaves the settings sitting there with a
/// stock application that has no idea what they are.
///
/// These tests fail if the app self-update is ever turned back on without the releases
/// to go with it.
/// </summary>
public class UpdateSafetyTests
{
    [Fact]
    public void AppSelfUpdate_IsDisabled()
    {
        CoreInfoManager.Instance.IsCheckUpdateSupported(ECoreType.v2rayN)
            .Should().BeFalse("an app update would replace this build with a stock one");
    }

    [Theory]
    [InlineData(ECoreType.Xray)]
    [InlineData(ECoreType.sing_box)]
    [InlineData(ECoreType.mihomo)]
    public void ProxyCoreUpdates_StayEnabled(ECoreType coreType)
    {
        // The cores are separate downloads that land in bin\ and never touch the app's
        // own assemblies, so they must keep updating — that is where the security fixes
        // that actually matter arrive.
        CoreInfoManager.Instance.IsCheckUpdateSupported(coreType).Should().BeTrue();
    }

    [Fact]
    public void CheckUpdateList_DoesNotOfferTheApp()
    {
        // Belt and braces: even the dialog's list must not contain it, so there is no
        // tick box to leave selected from an earlier config.
        CoreInfoManager.Instance.GetCheckUpdateCoreTypes()
            .Should().NotContain(ECoreType.v2rayN);
    }

    [Fact]
    public void AppReleaseSource_PointsAtThisFork()
    {
        // If self-update is ever re-enabled, it must pull this fork's builds rather than
        // upstream's — which would carry none of this work.
        Global.CoreUrls[ECoreType.v2rayN].Should().NotContain("2dust/");
        Global.CoreUrls[ECoreType.v2rayN].Should().Contain("RayPulse");
    }

    [Fact]
    public void CoreReleaseSources_StayUpstream()
    {
        Global.CoreUrls[ECoreType.Xray].Should().Be("XTLS/Xray-core");
        Global.CoreUrls[ECoreType.sing_box].Should().Be("SagerNet/sing-box");
    }
}
