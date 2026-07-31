namespace ServiceLib.Models.Dto;

/// <summary>
/// Health record for a single subscription source used by Auto Mode.
/// Free subscription links decay: they stop updating, then stop working entirely.
/// The alpha/beta pair is the Beta-distribution evidence a Thompson sampler draws from,
/// and both are decayed on every run so old evidence stops dominating.
/// </summary>
[Serializable]
public class AutoModeSource
{
    public string Url { get; set; } = string.Empty;

    /// <summary>Set false by the user, or automatically after too many dead runs.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>True when Auto Mode disabled it, so a resurrection probe may re-enable it.</summary>
    public bool AutoDisabled { get; set; }

    /// <summary>Beta(alpha, beta) evidence. Both start at 1 = uniform prior.</summary>
    public double Alpha { get; set; } = 1;

    public double Beta { get; set; } = 1;

    /// <summary>Times this source was selected for a run.</summary>
    public int Tried { get; set; }

    /// <summary>Configs from this source that passed the real-ping stage, all time.</summary>
    public int GreenTotal { get; set; }

    /// <summary>Configs from this source that reached the real-ping stage, all time.</summary>
    public int TestedTotal { get; set; }

    /// <summary>Configs from this source that made the final top list, all time.</summary>
    public int WinnerTotal { get; set; }

    public long LastTriedTicks { get; set; }
    public long LastGreenTicks { get; set; }

    /// <summary>Hash of the last fetched body, used to detect a source that stopped updating.</summary>
    public string? LastHash { get; set; }

    public int LastConfigCount { get; set; }

    /// <summary>Consecutive fetches that returned byte-identical content.</summary>
    public int StaleRuns { get; set; }

    /// <summary>Consecutive runs that produced no working config (or failed to fetch).</summary>
    public int DeadStreak { get; set; }

    public double BestSpeedMbps { get; set; }

    public DateTime? LastTried => LastTriedTicks > 0 ? new DateTime(LastTriedTicks, DateTimeKind.Utc).ToLocalTime() : null;

    public DateTime? LastGreen => LastGreenTicks > 0 ? new DateTime(LastGreenTicks, DateTimeKind.Utc).ToLocalTime() : null;

    /// <summary>Posterior mean of the success rate — what the UI shows as "quality".</summary>
    public double Score => Alpha / (Alpha + Beta);
}

/// <summary>
/// Persisted Auto Mode state: the source list plus the tuning that adapts between runs.
/// Stored as JSON in guiConfigs so it survives upgrades and can be hand-edited.
/// </summary>
[Serializable]
public class AutoModeStore
{
    public int Version { get; set; } = 1;

    /// <summary>How many servers the final list keeps.</summary>
    public int TopCount { get; set; } = 10;

    /// <summary>How many sources to fetch per run. Adapts to how well recent runs went.</summary>
    public int SourcesPerRun { get; set; } = 8;

    public int RunCount { get; set; }

    public long LastRunTicks { get; set; }

    /// <summary>
    /// Protocols worth testing, by <see cref="EConfigType"/> name. Empty means any.
    /// </summary>
    public List<string> ProtocolFilter { get; set; } = [];

    /// <summary>
    /// Wanted countries as ISO codes. Empty means any. A remark's label decides what is
    /// worth testing; the country measured through the tunnel decides what is kept.
    /// </summary>
    public List<string> CountryFilter { get; set; } = [];

    public List<AutoModeSource> Sources { get; set; } = [];
}

/// <summary>Result of one Auto Mode run, for the summary message.</summary>
public class AutoModeRunResult
{
    public bool Success { get; set; }
    public int SourcesUsed { get; set; }
    public int Fetched { get; set; }
    public int Candidates { get; set; }
    public int TcpAlive { get; set; }
    public int RealPingOk { get; set; }
    public int SpeedTested { get; set; }
    public int TopCount { get; set; }
    public string? Message { get; set; }
}
