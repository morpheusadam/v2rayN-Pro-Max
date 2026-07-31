namespace ServiceLib.Manager;

/// <summary>
/// Owns the Auto Mode subscription-source list and the health evidence attached to it.
/// Persisted as JSON in guiConfigs rather than a SQLite table so it needs no schema
/// migration against an existing database and stays hand-editable.
/// </summary>
public class AutoModeSourceManager
{
    private static readonly Lazy<AutoModeSourceManager> _instance = new(() => new());
    public static AutoModeSourceManager Instance => _instance.Value;

    private static readonly string _tag = "AutoModeSourceManager";
    private const string FileName = "autoMode.json";

    /// <summary>Evidence older than a few runs should not decide today's picks.</summary>
    private const double DecayFactor = 0.92;

    private const int DeadStreakLimit = 5;
    private const int ResurrectEveryRuns = 5;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private AutoModeStore? _store;

    private static string StorePath => Utils.GetConfigPath(FileName);

    public async Task<AutoModeStore> GetStore()
    {
        if (_store is not null)
        {
            return _store;
        }

        await _lock.WaitAsync();
        try
        {
            _store ??= Load();
            SeedFromSubFileIfEmpty(_store);
            return _store;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// First run with no sources configured: pick up a sub.txt sitting next to the app,
    /// which is where a hand-maintained list of links usually already lives.
    /// </summary>
    private static void SeedFromSubFileIfEmpty(AutoModeStore store)
    {
        if (store.Sources.Count > 0)
        {
            return;
        }

        try
        {
            var path = Path.Combine(Utils.StartupPath(), "sub.txt");
            if (!File.Exists(path))
            {
                return;
            }

            foreach (var url in ParseUrls(File.ReadAllText(path)).Distinct())
            {
                store.Sources.Add(new AutoModeSource { Url = url });
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    private static AutoModeStore Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                var store = JsonUtils.Deserialize<AutoModeStore>(File.ReadAllText(StorePath));
                if (store is not null)
                {
                    store.Sources ??= [];
                    return store;
                }
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }

        return new AutoModeStore();
    }

    public async Task Save()
    {
        await _lock.WaitAsync();
        try
        {
            if (_store is null)
            {
                return;
            }
            File.WriteAllText(StorePath, JsonUtils.Serialize(_store, true));
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Replace the source list with the URLs the user typed, keeping the accumulated
    /// health of every URL that survived the edit.
    /// </summary>
    public async Task<int> SetUrls(IEnumerable<string> urls)
    {
        var store = await GetStore();
        var existing = store.Sources.ToDictionary(t => t.Url, t => t);
        var result = new List<AutoModeSource>();
        var seen = new HashSet<string>();

        foreach (var raw in urls)
        {
            var url = NormalizeUrl(raw);
            if (url is null || !seen.Add(url))
            {
                continue;
            }
            result.Add(existing.TryGetValue(url, out var old) ? old : new AutoModeSource { Url = url });
        }

        store.Sources = result;
        await Save();
        return result.Count;
    }

    /// <summary>
    /// Accepts the shape of a real sub list: one URL per line, commas, stray labels such
    /// as "Pack 3: https://..." and github blob links that need the raw host.
    /// </summary>
    public static List<string> ParseUrls(string? text)
    {
        var list = new List<string>();
        if (text.IsNullOrEmpty())
        {
            return list;
        }

        // Angle brackets stay inside the match on purpose: a template row such as
        // ".../countries/<CODE>.sub.txt" must be rejected whole, not silently truncated
        // into the valid-looking but useless ".../countries/".
        foreach (Match m in Regex.Matches(text, @"https?://[^\s,;'""]+"))
        {
            var url = NormalizeUrl(m.Value);
            if (url is not null)
            {
                list.Add(url);
            }
        }
        return list;
    }

    /// <summary>
    /// Turns free text such as "vless, vmess" into EConfigType names. Unknown words are
    /// dropped rather than guessed at, so a typo narrows nothing.
    /// </summary>
    public static List<string> ParseProtocols(string? text)
    {
        var result = new List<string>();
        if (text.IsNullOrEmpty())
        {
            return result;
        }

        var known = Enum.GetValues<EConfigType>()
            .ToDictionary(t => t.ToString(), t => t.ToString(), StringComparer.OrdinalIgnoreCase);

        foreach (var part in text!.Split([',', ';', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim();
            // "ss" and "shadowsocks" are the same thing to everyone except the enum.
            if (token.Equals("ss", StringComparison.OrdinalIgnoreCase))
            {
                token = nameof(EConfigType.Shadowsocks);
            }
            if (token.Equals("hy2", StringComparison.OrdinalIgnoreCase))
            {
                token = nameof(EConfigType.Hysteria2);
            }

            if (known.TryGetValue(token, out var name) && !result.Contains(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static string? NormalizeUrl(string? raw)
    {
        var url = raw?.Trim().TrimEnd('.', ',', ';', ')', ']');
        if (url.IsNullOrEmpty())
        {
            return null;
        }

        if (!url.StartsWith(Global.HttpsProtocol) && !url.StartsWith(Global.HttpProtocol))
        {
            return null;
        }

        // Placeholder rows in hand-maintained lists, e.g. .../<CODE>.sub.txt
        if (url.Contains("YOUR_USERNAME") || url.Contains("YOUR_REPOSITORY")
            || url.Contains('<') || url.Contains('>'))
        {
            return null;
        }

        // A github blob page is HTML; the raw host serves the actual list.
        if (url.StartsWith("https://github.com/") && url.Contains("/blob/"))
        {
            url = url.Replace("https://github.com/", "https://raw.githubusercontent.com/")
                     .Replace("/blob/", "/");
        }

        return Utils.TryUri(url) is null ? null : url;
    }

    /// <summary>
    /// Pick the sources to spend this run on: Thompson sampling over the Beta evidence,
    /// plus guaranteed slots for unexplored links and an occasional resurrection probe
    /// for links that were auto-disabled after going dead.
    /// </summary>
    public async Task<List<AutoModeSource>> SelectSources()
    {
        var store = await GetStore();
        var wanted = Math.Clamp(store.SourcesPerRun, 3, 24);

        // Age out old evidence first, so a source that worked months ago is not still
        // trusted on the strength of that.
        foreach (var s in store.Sources)
        {
            s.Alpha = 1 + ((s.Alpha - 1) * DecayFactor);
            s.Beta = 1 + ((s.Beta - 1) * DecayFactor);
        }

        var live = store.Sources.Where(t => t.Enabled).ToList();
        var picked = new List<AutoModeSource>();

        // Unexplored links first — a brand new link deserves a look before the sampler
        // starts preferring whatever already has a track record.
        var fresh = live.Where(t => t.Tried == 0).Take(2).ToList();
        picked.AddRange(fresh);

        var pool = live.Except(picked).ToList();
        foreach (var s in pool.OrderByDescending(SampleBeta).Take(Math.Max(0, wanted - picked.Count)))
        {
            picked.Add(s);
        }

        // A link that died in March may be alive again in July.
        if (store.RunCount % ResurrectEveryRuns == 0)
        {
            var candidate = store.Sources
                .Where(t => !t.Enabled && t.AutoDisabled)
                .OrderBy(t => t.LastTriedTicks)
                .FirstOrDefault();
            if (candidate is not null)
            {
                picked.Add(candidate);
            }
        }

        return picked;
    }

    private static double SampleBeta(AutoModeSource s)
    {
        return BetaSampler.Sample(s.Alpha, s.Beta);
    }

    /// <summary>Record what a source produced this run and let it decay, die or recover.</summary>
    public void ApplyResult(AutoModeSource source, int realPingTested, int realPingOk, int winners, bool fetchFailed, string? hash, int configCount, double bestSpeedMbps)
    {
        source.Tried++;
        source.LastTriedTicks = DateTime.UtcNow.Ticks;

        if (fetchFailed)
        {
            source.Beta += 2;
            source.DeadStreak++;
        }
        else
        {
            source.LastConfigCount = configCount;
            source.StaleRuns = hash is not null && hash == source.LastHash ? source.StaleRuns + 1 : 0;
            source.LastHash = hash;

            source.TestedTotal += realPingTested;
            source.GreenTotal += realPingOk;
            source.WinnerTotal += winners;

            source.Alpha += realPingOk + (winners * 2);
            source.Beta += Math.Max(0, realPingTested - realPingOk);

            if (realPingOk > 0)
            {
                source.LastGreenTicks = DateTime.UtcNow.Ticks;
                source.DeadStreak = 0;
            }
            else
            {
                // Contributed nothing usable. Only a soft penalty when it was never
                // actually reached by the real-ping stage.
                if (realPingTested == 0)
                {
                    source.Beta += 1;
                }
                source.DeadStreak++;
            }

            if (bestSpeedMbps > source.BestSpeedMbps)
            {
                source.BestSpeedMbps = bestSpeedMbps;
            }
        }

        if (source.DeadStreak >= DeadStreakLimit && source.Enabled)
        {
            source.Enabled = false;
            source.AutoDisabled = true;
        }
        else if (source.DeadStreak == 0 && source.AutoDisabled)
        {
            source.Enabled = true;
            source.AutoDisabled = false;
        }
    }

    /// <summary>
    /// Widen the net when a run came up short, narrow it when it did not have to try hard.
    /// </summary>
    public static void AdaptSourceCount(AutoModeStore store, int passers, int target)
    {
        store.SourcesPerRun = passers < target
            ? Math.Min(store.SourcesPerRun + 4, 20)
            : Math.Max(store.SourcesPerRun - 2, 4);
    }
}
