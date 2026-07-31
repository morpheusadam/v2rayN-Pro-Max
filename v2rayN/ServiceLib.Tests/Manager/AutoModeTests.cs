using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Manager;
using ServiceLib.Models.Dto;
using Xunit;

namespace ServiceLib.Tests.Manager;

public class AutoModeUrlParsingTests
{
    [Fact]
    public void ParseUrls_RealWorldSubList_ExtractsLinksAndDropsNoise()
    {
        // The shape a hand-maintained list actually has: labels, table columns, blank
        // lines and trailing punctuation around the links.
        var text = """
            https://raw.githubusercontent.com/user/repo/main/config.txt
            Subscription 1: https://raw.githubusercontent.com/a/b/main/Sub1.txt

            Pack 3	https://raw.githubusercontent.com/c/d/refs/heads/main/Sub3.txt,
            📦 Main — 2000 configs
            https://raw.githubusercontent.com/e/f/main/config.txt
            """;

        var urls = AutoModeSourceManager.ParseUrls(text);

        urls.Should().HaveCount(4);
        urls.Should().Contain("https://raw.githubusercontent.com/a/b/main/Sub1.txt");
        urls.Should().Contain("https://raw.githubusercontent.com/c/d/refs/heads/main/Sub3.txt");
    }

    [Fact]
    public void ParseUrls_GithubBlobLink_RewrittenToRaw()
    {
        // A blob URL serves an HTML page, not the config list, so it has to be rewritten
        // or the source silently yields zero configs forever.
        var urls = AutoModeSourceManager.ParseUrls("https://github.com/user/repo/blob/main/sub.txt");

        urls.Should().ContainSingle()
            .Which.Should().Be("https://raw.githubusercontent.com/user/repo/main/sub.txt");
    }

    [Fact]
    public void ParseUrls_PlaceholderRows_AreSkipped()
    {
        var text = """
            https://raw.githubusercontent.com/YOUR_USERNAME/YOUR_REPOSITORY/main/all.txt
            https://raw.githubusercontent.com/user/repo/main/countries/<CODE>.sub.txt
            ftp://example.com/list.txt
            https://raw.githubusercontent.com/good/repo/main/ok.txt
            """;

        var urls = AutoModeSourceManager.ParseUrls(text);

        urls.Should().ContainSingle()
            .Which.Should().Be("https://raw.githubusercontent.com/good/repo/main/ok.txt");
    }

    [Fact]
    public void ParseUrls_EmptyInput_ReturnsEmpty()
    {
        AutoModeSourceManager.ParseUrls(null).Should().BeEmpty();
        AutoModeSourceManager.ParseUrls("   ").Should().BeEmpty();
    }
}

public class BetaSamplerTests
{
    [Fact]
    public void Sample_AlwaysInUnitInterval()
    {
        foreach (var (a, b) in new[] { (1.0, 1.0), (0.05, 40.0), (200.0, 0.05), (7.5, 2.5) })
        {
            for (var i = 0; i < 500; i++)
            {
                BetaSampler.Sample(a, b).Should().BeInRange(0, 1);
            }
        }
    }

    [Fact]
    public void Sample_MeanTracksAlphaOverAlphaPlusBeta()
    {
        // The sampler is only useful if its draws actually centre on the success rate the
        // evidence implies; a broken gamma draw would still return values in [0,1].
        foreach (var (a, b) in new[] { (2.0, 8.0), (30.0, 10.0), (1.0, 1.0) })
        {
            var mean = Enumerable.Range(0, 20000).Average(_ => BetaSampler.Sample(a, b));
            mean.Should().BeApproximately(a / (a + b), 0.02);
        }
    }

    [Fact]
    public void Sample_ProvenSourceBeatsDeadOne()
    {
        // Beta(30,5) and Beta(1,30) barely overlap, so a source with a real track record
        // should essentially always outdraw one that has only ever failed.
        var proven = 0;
        for (var i = 0; i < 2000; i++)
        {
            if (BetaSampler.Sample(30, 5) > BetaSampler.Sample(1, 30))
            {
                proven++;
            }
        }

        proven.Should().BeGreaterThan(1980);
    }

    [Fact]
    public void Sample_ThinEvidenceStillLosesSometimes()
    {
        // Exploration lives here: two sources with the same posterior mean but different
        // confidence must trade wins, otherwise a source that got unlucky early would
        // never be retried. Beta(2,2) and Beta(20,20) both centre on 0.5.
        var wins = 0;
        for (var i = 0; i < 4000; i++)
        {
            if (BetaSampler.Sample(2, 2) > BetaSampler.Sample(20, 20))
            {
                wins++;
            }
        }

        wins.Should().BeInRange(1700, 2300);
    }

    [Fact]
    public void Sample_UntestedSourceUsuallyOutdrawsMediocreOne()
    {
        // Beta(1,1) is uniform with mean 0.5 against a mediocre 0.3, so a brand new link
        // wins most draws and reliably gets its first try.
        var fresh = 0;
        for (var i = 0; i < 2000; i++)
        {
            if (BetaSampler.Sample(1, 1) > BetaSampler.Sample(3, 7))
            {
                fresh++;
            }
        }

        fresh.Should().BeInRange(1200, 1800);
    }
}

public class AutoModeMergeTests
{
    private static ProfileItem Server(string address, int port = 443) => new()
    {
        IndexId = Guid.NewGuid().ToString(),
        ConfigType = ServiceLib.Enums.EConfigType.VLESS,
        Address = address,
        Port = port,
        Password = "pw",
    };

    [Fact]
    public void MergeForSpeedTest_ChampionsAlwaysGetToDefendTheirSlot()
    {
        // Regression guard: champions used to be appended after the newcomers and then
        // truncated away, so a run that turned up plenty of mediocre servers would evict
        // a fast one without ever re-testing it.
        var champions = Enumerable.Range(0, 10).Select(i => Server($"champion{i}.example")).ToList();
        var working = Enumerable.Range(0, 60).Select(i => Server($"new{i}.example")).ToList();

        var merged = AutoModeService.MergeForSpeedTest(working, champions);

        foreach (var champion in champions)
        {
            merged.Should().Contain(champion, "every champion must be re-tested, not dropped");
        }
        merged.Count.Should().BeGreaterThan(champions.Count, "newcomers still get tested too");
    }

    [Fact]
    public void MergeForSpeedTest_DuplicateEndpoint_CountsOnce()
    {
        // The same server usually reappears in a fresh fetch; it must not occupy two of
        // the ten slots.
        var champion = Server("dup.example");
        var rediscovered = Server("dup.example");
        var other = Server("other.example");

        var merged = AutoModeService.MergeForSpeedTest([rediscovered, other], [champion]);

        merged.Should().HaveCount(2);
        merged.Should().Contain(champion);
        merged.Should().Contain(other);
        merged.Should().NotContain(rediscovered);
    }

    [Fact]
    public void MergeForSpeedTest_SkipsUntestableEntries()
    {
        var custom = Server("custom.example");
        custom.ConfigType = ServiceLib.Enums.EConfigType.Custom;
        var noPort = Server("noport.example", port: 0);
        var good = Server("good.example");

        var merged = AutoModeService.MergeForSpeedTest([custom, noPort, good], []);

        merged.Should().ContainSingle().Which.Should().Be(good);
    }

    [Fact]
    public void MergeForSpeedTest_NoChampionsYet_StillReturnsNewcomers()
    {
        var working = Enumerable.Range(0, 5).Select(i => Server($"new{i}.example")).ToList();

        AutoModeService.MergeForSpeedTest(working, []).Should().HaveCount(5);
    }
}

public class AutoModeSourceHealthTests
{
    private static AutoModeSource NewSource() => new() { Url = "https://example.com/sub.txt" };

    [Fact]
    public void ApplyResult_WorkingConfigs_RaiseScoreAndClearDeadStreak()
    {
        var source = NewSource();
        source.DeadStreak = 2;

        AutoModeSourceManager.Instance.ApplyResult(source, realPingTested: 10, realPingOk: 4, winners: 1, fetchFailed: false, hash: "h1", configCount: 300, bestSpeedMbps: 12);

        source.Alpha.Should().Be(1 + 4 + 2);   // greens plus double credit for the winner
        source.Beta.Should().Be(1 + 6);        // the six that failed
        source.DeadStreak.Should().Be(0);
        source.GreenTotal.Should().Be(4);
        source.WinnerTotal.Should().Be(1);
        source.BestSpeedMbps.Should().Be(12);
        source.Enabled.Should().BeTrue();
    }

    [Fact]
    public void ApplyResult_RepeatedFailure_EventuallyAutoDisables()
    {
        var source = NewSource();

        for (var i = 0; i < 5; i++)
        {
            AutoModeSourceManager.Instance.ApplyResult(source, 0, 0, 0, fetchFailed: true, null, 0, 0);
        }

        source.DeadStreak.Should().Be(5);
        source.Enabled.Should().BeFalse();
        source.AutoDisabled.Should().BeTrue();
    }

    [Fact]
    public void ApplyResult_AutoDisabledSourceThatWorksAgain_IsReEnabled()
    {
        // The resurrection path: a link that died months ago and came back must be able
        // to rejoin the rotation on its own.
        var source = NewSource();
        source.Enabled = false;
        source.AutoDisabled = true;
        source.DeadStreak = 6;

        AutoModeSourceManager.Instance.ApplyResult(source, realPingTested: 5, realPingOk: 2, winners: 0, fetchFailed: false, hash: "h", configCount: 80, bestSpeedMbps: 0);

        source.DeadStreak.Should().Be(0);
        source.Enabled.Should().BeTrue();
        source.AutoDisabled.Should().BeFalse();
    }

    [Fact]
    public void ApplyResult_IdenticalBody_CountsAsStale()
    {
        var source = NewSource();

        AutoModeSourceManager.Instance.ApplyResult(source, 3, 1, 0, false, "same-hash", 100, 0);
        source.StaleRuns.Should().Be(0);

        AutoModeSourceManager.Instance.ApplyResult(source, 3, 1, 0, false, "same-hash", 100, 0);
        AutoModeSourceManager.Instance.ApplyResult(source, 3, 1, 0, false, "same-hash", 100, 0);
        source.StaleRuns.Should().Be(2);

        AutoModeSourceManager.Instance.ApplyResult(source, 3, 1, 0, false, "new-hash", 100, 0);
        source.StaleRuns.Should().Be(0);
    }

    [Fact]
    public void AdaptSourceCount_WidensWhenShort_NarrowsWhenPlentiful()
    {
        var store = new AutoModeStore { SourcesPerRun = 8 };

        AutoModeSourceManager.AdaptSourceCount(store, passers: 2, target: 20);
        store.SourcesPerRun.Should().Be(12);

        AutoModeSourceManager.AdaptSourceCount(store, passers: 25, target: 20);
        store.SourcesPerRun.Should().Be(10);
    }

    [Fact]
    public void AdaptSourceCount_StaysWithinBounds()
    {
        var store = new AutoModeStore { SourcesPerRun = 4 };
        for (var i = 0; i < 10; i++)
        {
            AutoModeSourceManager.AdaptSourceCount(store, passers: 99, target: 20);
        }
        store.SourcesPerRun.Should().Be(4);

        for (var i = 0; i < 20; i++)
        {
            AutoModeSourceManager.AdaptSourceCount(store, passers: 0, target: 20);
        }
        store.SourcesPerRun.Should().Be(20);
    }
}
