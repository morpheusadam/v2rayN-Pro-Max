using AwesomeAssertions;
using ServiceLib.Manager;
using Xunit;

namespace ServiceLib.Tests.Manager;

/// <summary>
/// Hits the network against the user's real source list. Skipped by default; run with
///   dotnet test --filter FullyQualifiedName~AutoModeLiveHarness -e AUTOMODE_LIVE=1
/// to check that source selection and fetching behave against live data.
/// </summary>
public class AutoModeLiveHarness(ITestOutputHelper output)
{
    private static bool Enabled => Environment.GetEnvironmentVariable("AUTOMODE_LIVE") == "1";

    [Fact]
    public async Task SelectSources_SpreadsAcrossRunsAndFavoursWinners()
    {
        if (!Enabled)
        {
            return;
        }

        var subFile = @"C:\myApp\V2rayN\sub.txt";
        File.Exists(subFile).Should().BeTrue("the harness reads the user's real list");

        var urls = AutoModeSourceManager.ParseUrls(
            await File.ReadAllTextAsync(subFile, TestContext.Current.CancellationToken));
        output.WriteLine($"parsed {urls.Count} urls from sub.txt");
        urls.Count.Should().BeGreaterThan(40);

        await AutoModeSourceManager.Instance.SetUrls(urls);
        var store = await AutoModeSourceManager.Instance.GetStore();

        // Give three sources a strong record and confirm the sampler concentrates on
        // them without ever fully starving the rest.
        var favoured = store.Sources.Take(3).ToList();
        foreach (var s in favoured)
        {
            s.Alpha = 40;
            s.Beta = 2;
            s.Tried = 5;
        }
        foreach (var s in store.Sources.Skip(3))
        {
            s.Tried = 1;
        }

        var favouredHits = 0;
        var distinct = new HashSet<string>();
        const int runs = 40;
        for (var i = 0; i < runs; i++)
        {
            var picked = await AutoModeSourceManager.Instance.SelectSources();
            favouredHits += picked.Count(p => favoured.Contains(p));
            foreach (var p in picked)
            {
                distinct.Add(p.Url);
            }
            // SelectSources decays evidence each call; keep the favoured ones strong so
            // the test measures selection, not decay.
            foreach (var s in favoured)
            {
                s.Alpha = 40;
                s.Beta = 2;
            }
        }

        output.WriteLine($"favoured picked {favouredHits} times over {runs} runs; " +
                         $"{distinct.Count} distinct sources touched");

        favouredHits.Should().BeGreaterThan((int)(runs * 1.5),
            "a proven source should be picked in most runs");
        distinct.Count.Should().BeGreaterThan(10,
            "exploration must keep reaching beyond the favoured few");
    }
}
