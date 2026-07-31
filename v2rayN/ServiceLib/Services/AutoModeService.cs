namespace ServiceLib.Services;

/// <summary>
/// One button press: fetch a few subscription sources, find the servers that actually
/// work, and leave the best ones in a dedicated group.
///
/// The funnel is deliberately adaptive rather than fixed-width. Measured against the
/// user's real source list, only ~5% of TCP-reachable endpoints survive a real tunnel
/// test, and that rate swings with which sources happen to be healthy this week — so the
/// real-ping stage keeps pulling batches until it has enough survivors or spends its
/// budget, instead of testing a guessed number of candidates.
///
/// Two findings from the same measurement are baked in:
///  - TCP reachability is used only to drop dead endpoints, never to rank. Ranking by
///    lowest tcping performed *worse* than random (2.1% vs 7.5% pass), because the
///    fastest-answering hosts are CDN edges fronting dead proxies.
///  - Which sources a run spends itself on matters far more than which configs it picks,
///    which is why source selection is a decayed Thompson sampler.
/// </summary>
public class AutoModeService
{
    private static readonly string _tag = "AutoModeService";

    /// <summary>Group holding the surviving best servers. Fixed id so runs find it again.</summary>
    public const string TopGroupId = "automode-top";

    /// <summary>
    /// Prefix for this run's scratch groups. One group per source, so a server's Subid
    /// says exactly which link produced it — attribution with no guesswork. All of them
    /// are deleted once the run picks its winners.
    /// </summary>
    private const string PoolGroupPrefix = "automode-pool-";

    public const string TopGroupRemarks = "⚡ Auto Mode";

    /// <summary>Configs imported per run. Kept modest so the database stays small.</summary>
    private const int MaxPoolSize = 1400;

    /// <summary>Cap on the cheap liveness stage.</summary>
    private const int MaxTcping = 1200;

    /// <summary>Real-ping batch size. Small enough that one poisoned config is cheap.</summary>
    private const int RealPingBatch = 120;

    /// <summary>Upper bound on real-ping tests when survivors are scarce.</summary>
    private const int MaxRealPing = 600;

    /// <summary>
    /// Rounds of real-ping per run. Bounded so a run with poor sources still finishes in
    /// a predictable time rather than grinding through the whole live pool.
    /// </summary>
    private const int MaxRealPingRounds = 4;

    /// <summary>New servers taken into the speed-test stage, on top of the champions.</summary>
    private const int MaxSpeedTest = 18;

    /// <summary>Existing top-list servers re-tested each run so they can defend a slot.</summary>
    private const int MaxChampionsRetested = 12;

    /// <summary>Beyond this a server is unusable however fast it downloads.</summary>
    private const int MaxAcceptableDelay = 2500;

    private readonly Config _config;
    private readonly Func<string, Task>? _progress;
    private readonly Action<TimeSpan>? _estimate;
    private SpeedtestService? _speedtest;
    private bool _stopped;

    public AutoModeService(Config config, Func<string, Task>? progress = null, Action<TimeSpan>? estimate = null)
    {
        _config = config;
        _progress = progress;
        _estimate = estimate;
    }

    /// <summary>
    /// Publish how much longer the run should take. Every figure below comes from timing
    /// the stage that is actually running rather than from a fixed guess, so the estimate
    /// corrects itself on a slow network instead of counting down to zero and sitting there.
    /// </summary>
    private void Estimate(TimeSpan remaining)
    {
        _estimate?.Invoke(remaining < TimeSpan.Zero ? TimeSpan.Zero : remaining);
    }

    public void Stop()
    {
        _stopped = true;
        _speedtest?.ExitLoop();
    }

    public async Task<AutoModeRunResult> RunAsync()
    {
        var result = new AutoModeRunResult();
        var sources = new List<AutoModeSource>();
        var fetched = new Dictionary<AutoModeSource, FetchResult>();
        var poolBySource = new Dictionary<AutoModeSource, List<ProfileItem>>();
        var realPingTested = new HashSet<string>();
        var workingIds = new HashSet<string>();
        var winnerIds = new HashSet<string>();

        try
        {
            // Every stage of a run is a core-backed test, so without a core the run cannot
            // do anything except emit one "core not found" line per candidate — hundreds of
            // them — and finish with an empty list. Say it once, up front, and stop.
            var coreProblem = MissingCoreMessage();
            if (coreProblem is not null)
            {
                result.Message = coreProblem;
                await Report(result.Message);
                return result;
            }

            var store = await AutoModeSourceManager.Instance.GetStore();
            if (store.Sources.Count == 0)
            {
                result.Message = "No subscription sources. Open the list beside the button and paste some links.";
                await Report(result.Message);
                return result;
            }

            store.RunCount++;
            store.LastRunTicks = DateTime.UtcNow.Ticks;

            sources = await AutoModeSourceManager.Instance.SelectSources();
            result.SourcesUsed = sources.Count;
            await Report($"Auto Mode: run #{store.RunCount}, trying {sources.Count} of {store.Sources.Count} sources.");

            // ---- fetch -----------------------------------------------------------
            fetched = await FetchSources(sources);
            result.Fetched = fetched.Count(t => !t.Value.Failed);
            if (result.Fetched == 0)
            {
                result.Message = "No source could be downloaded. Check the connection.";
                await Report(result.Message);
                return result;
            }
            await Report($"Downloaded {result.Fetched} of {sources.Count} sources.");

            if (_stopped)
            {
                return Cancelled(result);
            }

            // ---- import candidates ----------------------------------------------
            poolBySource = await ImportCandidates(fetched);

            // The downloaded bodies are megabytes each and nothing after this point reads
            // them — only the hash and the failure flag. Holding them for the rest of a
            // multi-minute run would keep tens of megabytes alive for no reason.
            foreach (var key in fetched.Keys.ToList())
            {
                fetched[key] = fetched[key] with { Text = string.Empty };
            }

            var candidates = poolBySource.Values.SelectMany(t => t).ToList();
            var beforeFilter = candidates.Count;
            candidates = ApplyFilters(candidates, store);
            result.Candidates = candidates.Count;

            await Report(candidates.Count == beforeFilter
                ? $"Imported {candidates.Count} candidate servers."
                : $"Imported {beforeFilter} candidates, {candidates.Count} match the filter.");
            if (candidates.Count == 0)
            {
                result.Message = "Sources downloaded but contained no usable server.";
                await Report(result.Message);
                return result;
            }

            if (_stopped)
            {
                return Cancelled(result);
            }

            // ---- liveness (drop dead endpoints; never used for ranking) ----------
            // First projection, from the work now known to be ahead. The later stages
            // replace it with figures measured from this machine and this network.
            Estimate(TimeSpan.FromSeconds(
                (Math.Min(candidates.Count, MaxTcping) / 24.0)
                + (RealPingBatch * MaxRealPingRounds / 3.0)
                + (MaxSpeedTest * 3.0)));

            var live = await TcpingStage(candidates);
            result.TcpAlive = live.Count;
            await Report($"{live.Count} of {Math.Min(candidates.Count, MaxTcping)} endpoints answered.");
            if (live.Count == 0)
            {
                result.Message = "No endpoint answered.";
                await Report(result.Message);
                return result;
            }

            if (_stopped)
            {
                return Cancelled(result);
            }

            // ---- real ping: the stage that actually proves a proxy works ---------
            var target = Math.Max(store.TopCount * 2, 12);
            var working = await RealPingStage(live, target, realPingTested);
            workingIds = working.Select(t => t.IndexId).ToHashSet();
            result.RealPingOk = working.Count;
            await Report($"{working.Count} servers completed a real request through the tunnel.");

            // Champions from the previous run are re-tested rather than grandfathered,
            // so a server that has since died loses its slot on its own.
            var champions = await AppManager.Instance.ProfileItems(TopGroupId) ?? [];
            var speedInput = MergeForSpeedTest(working, champions);

            if (speedInput.Count == 0)
            {
                result.Message = "Nothing survived the tunnel test this run. Try again — different sources are sampled each time.";
                await Report(result.Message);
                return result;
            }

            if (_stopped)
            {
                return Cancelled(result);
            }

            // ---- speed test ------------------------------------------------------
            await Report($"Speed testing {speedInput.Count} servers...");
            await GetSpeedtest().RunAwaitableAsync(ESpeedActionType.Mixedtest, speedInput);
            result.SpeedTested = speedInput.Count;

            // ---- keep the best ---------------------------------------------------
            var winners = await SelectWinners(speedInput, store);
            winnerIds = winners.Select(t => t.IndexId).ToHashSet();
            result.TopCount = winners.Count;

            await PublishWinners(winners);
            AutoModeSourceManager.AdaptSourceCount(store, working.Count, target);

            result.Success = winners.Count > 0;
            result.Message = winners.Count > 0
                ? $"Auto Mode done: {winners.Count} servers kept in \"{TopGroupRemarks}\"."
                : "Auto Mode found no server fast enough to keep.";
            await Report(result.Message);
            return result;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            result.Message = $"Auto Mode failed: {ex.Message}";
            await Report(result.Message);
            return result;
        }
        finally
        {
            // Runs even when a stage bailed out, so the scratch groups never survive a
            // run and the source health always reflects what actually happened.
            try
            {
                await FinishSources(sources, fetched, poolBySource, realPingTested, workingIds, winnerIds);
                await AutoModeSourceManager.Instance.Save();
                await CleanupPools();
            }
            catch (Exception ex)
            {
                Logging.SaveLog(_tag, ex);
            }
        }
    }

    private AutoModeRunResult Cancelled(AutoModeRunResult result)
    {
        result.Message = "Auto Mode stopped.";
        return result;
    }

    /// <summary>
    /// Xray is what every speed test falls back to, so if its executable is not on disk
    /// there is nothing to test with and the run should not start. sing_box only carries
    /// the protocols mapped to it, so its absence is worth saying but not worth aborting
    /// for. Returns null when the run can proceed.
    /// </summary>
    public static string? MissingCoreMessage()
    {
        if (CoreExists(ECoreType.Xray))
        {
            if (!CoreExists(ECoreType.sing_box))
            {
                NoticeManager.Instance.SendMessageEx(
                    $"sing_box is missing from {Utils.GetBinPath("")} — Hysteria2, TUIC, WireGuard and AnyTLS servers cannot be tested this run.");
            }
            return null;
        }

        return $"Auto Mode cannot test anything: the Xray core is not in {Utils.GetBinPath("")}. "
             + "Use a release that ships the bin folder, or download the cores from Check Update, then try again.";
    }

    private static bool CoreExists(ECoreType coreType)
    {
        var info = CoreInfoManager.Instance.GetCoreInfo(coreType);
        if (info?.CoreExes is null)
        {
            return false;
        }

        // GetCoreExecFile logs a "not found" line of its own on every miss, which is the
        // very noise this check exists to prevent — so probe the files directly.
        return info.CoreExes.Any(name => File.Exists(Utils.GetBinPath(Utils.GetExeName(name), coreType.ToString())));
    }

    #region stages

    private sealed record FetchResult(string Text, string? Hash, bool Failed);

    private async Task<Dictionary<AutoModeSource, FetchResult>> FetchSources(List<AutoModeSource> sources)
    {
        var results = new ConcurrentDictionary<AutoModeSource, FetchResult>();
        using var gate = new SemaphoreSlim(8);

        var tasks = sources.Select(async source =>
        {
            await gate.WaitAsync();
            try
            {
                var downloader = new DownloadService();
                var text = await downloader.TryDownloadString(Utils.GetPunycode(source.Url), false, string.Empty);
                results[source] = text.IsNullOrEmpty()
                    ? new FetchResult(string.Empty, null, true)
                    : new FetchResult(text!, Utils.GetMd5(text!), false);
            }
            catch (Exception ex)
            {
                Logging.SaveLog(_tag, ex);
                results[source] = new FetchResult(string.Empty, null, true);
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks);
        return new Dictionary<AutoModeSource, FetchResult>(results);
    }

    /// <summary>
    /// Turn the downloaded bodies into ProfileItems, one scratch group per source.
    /// Each source gets an equal quota so one huge list cannot crowd out the rest, and
    /// the quota is filled by random sampling — the "try a few of them each time"
    /// behaviour, and also the only selection shown to beat ranking by latency.
    /// </summary>
    private async Task<Dictionary<AutoModeSource, List<ProfileItem>>> ImportCandidates(Dictionary<AutoModeSource, FetchResult> fetched)
    {
        var result = new Dictionary<AutoModeSource, List<ProfileItem>>();
        var usable = fetched.Where(t => !t.Value.Failed && t.Value.Text.IsNotEmpty()).ToList();
        if (usable.Count == 0)
        {
            return result;
        }

        var quota = Math.Max(40, MaxPoolSize / usable.Count);
        var index = 0;

        foreach (var (source, fetch) in usable)
        {
            var poolId = $"{PoolGroupPrefix}{index++}";
            await EnsureGroup(poolId, $"pool {index}", int.MaxValue);

            var body = Utils.IsBase64String(fetch.Text) ? Utils.Base64Decode(fetch.Text) : fetch.Text;
            var uris = ExtractUris(body);

            string payload;
            if (uris.Count == 0)
            {
                // Not a URI list — Clash YAML, SIP008 JSON and friends. Hand the whole
                // body to the existing parser rather than trying to sample it.
                payload = fetch.Text;
            }
            else
            {
                Shuffle(uris);
                payload = string.Join(Environment.NewLine, uris.Take(quota));
            }

            // isSub: true clears whatever the previous run left in this group.
            await ConfigHandler.AddBatchServers(_config, payload, poolId, true);

            var items = await AppManager.Instance.ProfileItems(poolId) ?? [];
            result[source] = items.Where(t => t.ConfigType != EConfigType.Custom && t.Port > 0).ToList();
        }

        return result;
    }

    /// <summary>
    /// Narrow the candidate pool to what the user asked for.
    ///
    /// Protocol is exact — it comes from the parsed config. Country is not: at this point
    /// the only evidence is the remark the provider wrote, which is a claim rather than a
    /// measurement. So a country filter is applied here only to decide what is worth
    /// spending tests on, and is checked again against the measured exit country when the
    /// winners are chosen. Candidates whose remark says nothing about a country are kept
    /// rather than dropped — an unlabelled server in the right country is still a good
    /// server, and the measurement will settle it.
    /// </summary>
    private static List<ProfileItem> ApplyFilters(List<ProfileItem> candidates, AutoModeStore store)
    {
        var filtered = candidates;

        if (store.ProtocolFilter.Count > 0)
        {
            var wanted = store.ProtocolFilter.ToHashSet(StringComparer.OrdinalIgnoreCase);
            filtered = filtered.Where(t => wanted.Contains(t.ConfigType.ToString())).ToList();
        }

        if (store.CountryFilter.Count > 0)
        {
            var wanted = store.CountryFilter.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var labelled = filtered
                .Select(t => (Item: t, Country: CountryHint.FromRemark(t.Remarks)))
                .ToList();

            var matching = labelled.Where(t => t.Country is not null && wanted.Contains(t.Country)).ToList();
            var unlabelled = labelled.Where(t => t.Country is null).ToList();

            // Prefer the ones that claim the right country, then top up with unlabelled
            // ones so a filter never starves the run of anything to test.
            filtered = matching.Select(t => t.Item)
                .Concat(unlabelled.Select(t => t.Item))
                .ToList();
        }

        return filtered;
    }

    private static readonly string[] _uriSchemes = [.. Global.ProtocolShares.Values];

    private static List<string> ExtractUris(string body)
    {
        var list = new List<string>();
        foreach (var line in body.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 12 && _uriSchemes.Any(p => trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(trimmed);
            }
        }
        return list;
    }

    private static void Shuffle<T>(IList<T> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>
    /// Drop endpoints that answer nothing. The resulting delays are deliberately *not*
    /// used to order the next stage: measured pass rate for the lowest-tcping candidates
    /// was 2.1% against 7.5% for a random draw from the same pool.
    /// </summary>
    private async Task<List<ProfileItem>> TcpingStage(List<ProfileItem> candidates)
    {
        var subset = candidates.Count > MaxTcping
            ? candidates.OrderBy(_ => Random.Shared.Next()).Take(MaxTcping).ToList()
            : candidates;

        await GetSpeedtest().RunAwaitableAsync(ESpeedActionType.Tcping, subset);

        var delays = await GetDelays(subset.Select(t => t.IndexId));
        return subset.Where(t => delays.TryGetValue(t.IndexId, out var d) && d > 0).ToList();
    }

    /// <summary>
    /// Keep testing batches until enough servers have proven they tunnel, or the budget
    /// runs out. Batches are drawn in random order for the reason above.
    /// </summary>
    private async Task<List<ProfileItem>> RealPingStage(List<ProfileItem> live, int target, HashSet<string> testedIds)
    {
        var queue = live.OrderBy(_ => Random.Shared.Next()).ToList();
        var working = new List<ProfileItem>();
        var tested = 0;
        var round = 0;

        while (queue.Count > 0 && tested < MaxRealPing && working.Count < target
               && round < MaxRealPingRounds && !_stopped)
        {
            round++;
            var batch = queue.Take(RealPingBatch).ToList();
            queue = queue.Skip(batch.Count).ToList();

            var batchStarted = DateTime.UtcNow;
            await GetSpeedtest().RunAwaitableAsync(ESpeedActionType.Realping, batch);
            var batchTook = DateTime.UtcNow - batchStarted;

            tested += batch.Count;
            foreach (var item in batch)
            {
                testedIds.Add(item.IndexId);
            }

            var delays = await GetDelays(batch.Select(t => t.IndexId));
            var passed = batch.Where(t => delays.TryGetValue(t.IndexId, out var d) && d is > 0 and <= MaxAcceptableDelay).ToList();
            working.AddRange(passed);

            await Report($"  tunnel test {tested}/{Math.Min(live.Count, MaxRealPing)} — {working.Count} working so far.");

            // Now that a batch has been timed, project from that instead of the prior:
            // how many rounds are still likely, plus the speed test that follows them.
            var roundsLeft = Math.Max(0, MaxRealPingRounds - round);
            if (working.Count >= target)
            {
                roundsLeft = 0;
            }
            Estimate(TimeSpan.FromSeconds((roundsLeft * batchTook.TotalSeconds)
                                          + (Math.Min(working.Count + target, MaxSpeedTest) * 3.0)));
        }

        return working;
    }

    /// <summary>
    /// The current champions plus this run's survivors, deduplicated by endpoint so a
    /// server that reappears in a fresh fetch cannot occupy two slots.
    ///
    /// Champions go in first and are never squeezed out by the newcomer cap: a slot is
    /// only lost by being beaten in the same speed test, not by arriving earlier. Without
    /// that, a run turning up plenty of mediocre servers would silently evict a fast one
    /// that was still working.
    /// </summary>
    public static List<ProfileItem> MergeForSpeedTest(List<ProfileItem> working, List<ProfileItem> champions)
    {
        var merged = new List<ProfileItem>();
        var seen = new HashSet<string>();

        void Add(IEnumerable<ProfileItem> items, int limit)
        {
            var added = 0;
            foreach (var item in items)
            {
                if (added >= limit)
                {
                    return;
                }
                if (item.ConfigType == EConfigType.Custom || item.Port <= 0)
                {
                    continue;
                }
                if (seen.Add(EndpointKey(item)))
                {
                    merged.Add(item);
                    added++;
                }
            }
        }

        Add(champions, MaxChampionsRetested);
        Add(working, MaxSpeedTest);
        return merged;
    }

    private static string EndpointKey(ProfileItem item)
    {
        return $"{item.ConfigType}|{item.Address}|{item.Port}|{item.Password}";
    }

    private async Task<List<ProfileItem>> SelectWinners(List<ProfileItem> tested, AutoModeStore store)
    {
        var stats = (await ProfileExManager.Instance.GetProfileExs())
            .Where(t => t.IndexId.IsNotEmpty())
            .GroupBy(t => t.IndexId!)
            .ToDictionary(g => g.Key, g => g.First());

        var scored = tested
            .Select(item =>
            {
                stats.TryGetValue(item.IndexId, out var ex);
                return new
                {
                    Item = item,
                    Speed = ex?.Speed ?? 0,
                    Delay = ex?.Delay ?? 0,
                    // Where the traffic actually came out, measured through the tunnel.
                    // Falls back to the provider's label only when no measurement exists.
                    Country = CountryHint.FromIpInfo(ex?.IpInfo) ?? CountryHint.FromRemark(item.Remarks),
                };
            })
            .Where(t => t.Speed > 0 && t.Delay is > 0 and <= MaxAcceptableDelay)
            .OrderByDescending(t => t.Speed)
            .ThenBy(t => t.Delay)
            .ToList();

        if (store.CountryFilter.Count == 0)
        {
            return scored.Take(store.TopCount).Select(t => t.Item).ToList();
        }

        var wanted = store.CountryFilter.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inCountry = scored.Where(t => t.Country is not null && wanted.Contains(t.Country)).ToList();

        // Filling the remaining slots from outside the wanted countries beats handing back
        // a half-empty list; the ones that do match are still ranked first.
        var winners = inCountry.Take(store.TopCount).ToList();
        if (winners.Count < store.TopCount)
        {
            winners.AddRange(scored.Except(inCountry).Take(store.TopCount - winners.Count));
        }

        return winners.Select(t => t.Item).ToList();
    }

    /// <summary>
    /// Move the winners into the top group and label them with their measured numbers,
    /// then clear out whatever used to be there.
    /// </summary>
    private async Task PublishWinners(List<ProfileItem> winners)
    {
        if (winners.Count == 0)
        {
            return;
        }

        var topSubId = await EnsureGroup(TopGroupId, TopGroupRemarks, 0);
        var keep = winners.Select(t => t.IndexId).ToHashSet();

        var previous = await AppManager.Instance.ProfileItems(TopGroupId) ?? [];
        var stale = previous.Where(t => !keep.Contains(t.IndexId)).ToList();
        if (stale.Count > 0)
        {
            await ConfigHandler.RemoveServers(_config, stale);
        }

        var stats = (await ProfileExManager.Instance.GetProfileExs())
            .Where(t => t.IndexId.IsNotEmpty())
            .GroupBy(t => t.IndexId!)
            .ToDictionary(g => g.Key, g => g.First());

        for (var i = 0; i < winners.Count; i++)
        {
            var item = winners[i];
            stats.TryGetValue(item.IndexId, out var ex);

            item.Subid = topSubId;
            item.IsSub = true;
            item.Remarks = $"#{i + 1} {FormatSpeed(ex?.Speed ?? 0)} · {ex?.Delay ?? 0}ms · {StripRank(item.Remarks)}";
            ProfileExManager.Instance.SetSort(item.IndexId, i + 1);
        }

        await SQLiteHelper.Instance.UpdateAllAsync(winners);
        await ProfileExManager.Instance.SaveTo();
    }

    private static string FormatSpeed(decimal speedMbPerSecond)
    {
        return speedMbPerSecond <= 0 ? "?" : $"{speedMbPerSecond:0.#}MB/s";
    }

    /// <summary>Strip a label this service added on a previous run before re-labelling.</summary>
    private static string StripRank(string? remarks)
    {
        var text = remarks ?? string.Empty;
        var match = Regex.Match(text, @"^#\d+\s+\S+\s+·\s+\d+ms\s+·\s+(.*)$");
        return match.Success ? match.Groups[1].Value : text;
    }

    #endregion stages

    #region helpers

    private SpeedtestService GetSpeedtest()
    {
        return _speedtest ??= new SpeedtestService(_config, _ => Task.CompletedTask);
    }

    private static async Task<Dictionary<string, int>> GetDelays(IEnumerable<string> indexIds)
    {
        var wanted = indexIds.Where(t => t.IsNotEmpty()).ToHashSet();
        var all = await ProfileExManager.Instance.GetProfileExs();
        return all
            .Where(t => t.IndexId.IsNotEmpty() && wanted.Contains(t.IndexId!))
            .GroupBy(t => t.IndexId!)
            .ToDictionary(g => g.Key, g => g.First().Delay);
    }

    private async Task<string> EnsureGroup(string id, string remarks, int sort)
    {
        var existing = await AppManager.Instance.GetSubItem(id);
        if (existing is not null)
        {
            return existing.Id;
        }

        var item = new SubItem
        {
            Id = id,
            Remarks = remarks,
            Url = string.Empty,
            // Never touched by a normal "update subscription" run.
            Enabled = false,
            Sort = sort,
        };
        await SQLiteHelper.Instance.ReplaceAsync(item);
        return id;
    }

    /// <summary>
    /// Attribute this run's outcomes back to the sources that produced them. Every
    /// candidate still carries the scratch group it was imported into, so which link
    /// produced which working server is exact rather than inferred.
    /// </summary>
    private async Task FinishSources(
        List<AutoModeSource> sources,
        Dictionary<AutoModeSource, FetchResult> fetched,
        Dictionary<AutoModeSource, List<ProfileItem>> poolBySource,
        HashSet<string> realPingTestedIds,
        HashSet<string> workingIds,
        HashSet<string> winnerIds)
    {
        foreach (var source in sources)
        {
            if (!fetched.TryGetValue(source, out var fetch) || fetch.Failed)
            {
                AutoModeSourceManager.Instance.ApplyResult(source, 0, 0, 0, true, null, 0, 0);
                continue;
            }

            var mine = poolBySource.TryGetValue(source, out var items) ? items : [];
            var tested = mine.Count(t => realPingTestedIds.Contains(t.IndexId));
            var ok = mine.Count(t => workingIds.Contains(t.IndexId));
            var won = mine.Count(t => winnerIds.Contains(t.IndexId));

            AutoModeSourceManager.Instance.ApplyResult(source, tested, ok, won, false, fetch.Hash, mine.Count, 0);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// Drop every scratch group and the thousand-odd servers in it. Winners have already
    /// been moved out, so nothing worth keeping is lost and the database stays small.
    /// </summary>
    private async Task CleanupPools()
    {
        var subs = await AppManager.Instance.SubItems() ?? [];
        foreach (var sub in subs.Where(t => t.Id.StartsWith(PoolGroupPrefix, StringComparison.Ordinal)))
        {
            await ConfigHandler.DeleteSubItem(_config, sub.Id);
        }

        // Deleting the profiles leaves their test results behind in the in-memory cache,
        // which would otherwise grow by a thousand-odd entries on every run. Re-init drops
        // the orphans and keeps the entries the surviving servers still need.
        await ProfileExManager.Instance.Init();

        // A run allocates hundreds of megabytes in short-lived strings and profile
        // objects. This is a once-per-click operation on a process that then sits idle in
        // the tray for hours, so it is worth handing the memory back rather than leaving
        // the working set inflated.
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private async Task Report(string message)
    {
        NoticeManager.Instance.SendMessageEx(message);
        if (_progress is not null)
        {
            await _progress(message);
        }
    }

    #endregion helpers
}
