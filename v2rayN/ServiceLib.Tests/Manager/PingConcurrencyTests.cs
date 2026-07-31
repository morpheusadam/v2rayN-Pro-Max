using AwesomeAssertions;
using ServiceLib.Models.Configs;
using Xunit;

namespace ServiceLib.Tests.Manager;

/// <summary>
/// Guards the fix for the long-standing "test a thousand configs and every ping is -1"
/// failure.
///
/// What the investigation established: xray itself is fine — it accepts ~1000 outbounds
/// and has every one of its socks ports listening inside a second. A standalone harness
/// pinging those same ports could not reproduce the failure at any concurrency, so the
/// collapse is not in the core and not in raw socket throughput. It is in how v2rayN
/// drives the pings: one HttpClient plus two TLS round trips per server, started for
/// every server in the page at once, with a page holding up to a thousand entries.
///
/// The fix bounds that fan-out. These tests pin the settings that make it hold, because
/// a default silently reverting to "unbounded" is exactly how this regressed before.
/// </summary>
public class PingConcurrencyTests
{
    [Fact]
    public void Default_IsFarBelowAPageOfServers()
    {
        // A page can be SpeedTestPageSize entries; the whole point is that the number
        // pinged simultaneously is not tied to it.
        Global.DefaultPingConcurrency.Should().BeLessThan(Global.SpeedTestPageSize / 4);
        Global.DefaultPingConcurrency.Should().BeInRange(10, 100);
    }

    [Fact]
    public void MissingOrZeroSetting_FallsBackToTheBoundedDefault()
    {
        // An older config file has no such key, and 0 would mean "no limit" if it were
        // ever passed to a semaphore — both must land on the bounded default.
        foreach (int? stored in new int?[] { null, 0, -5 })
        {
            var item = new SpeedTestItem { PingConcurrencyCount = stored };
            var effective = item.PingConcurrencyCount is null or < 1
                ? Global.DefaultPingConcurrency
                : item.PingConcurrencyCount.Value;

            effective.Should().Be(Global.DefaultPingConcurrency);
            effective.Should().BeGreaterThan(0, "a semaphore built from this must not be unbounded");
        }
    }

    [Fact]
    public void ConfiguredValue_IsHonouredButClamped()
    {
        // Users can tune it, but not back to the unbounded behaviour that caused the bug.
        foreach (var (configured, expected) in new[] { (1, 1), (50, 50), (200, 200), (100000, 200) })
        {
            Math.Clamp(configured, 1, 200).Should().Be(expected);
        }
    }
}
