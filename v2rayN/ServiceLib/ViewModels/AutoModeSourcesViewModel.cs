namespace ServiceLib.ViewModels;

/// <summary>
/// Row in the source health grid. The statistics are read-only — editing the list itself
/// happens in the textbox, because pasting a thousand links into a grid is not something
/// anyone wants to do — but the tick box writes straight through to the source, so a link
/// can be parked without deleting it and losing everything learned about it.
/// </summary>
public class AutoModeSourceModel
{
    private readonly AutoModeSource? _source;

    public AutoModeSourceModel()
    {
    }

    public AutoModeSourceModel(AutoModeSource source)
    {
        _source = source;
    }

    public bool Enabled
    {
        get => _source?.Enabled ?? true;
        set
        {
            if (_source is null)
            {
                return;
            }
            _source.Enabled = value;
            // Turning a link back on by hand clears the automatic write-off, otherwise
            // the next run would just switch it off again.
            if (value)
            {
                _source.AutoDisabled = false;
                _source.DeadStreak = 0;
            }
        }
    }

    public string Url { get; set; } = string.Empty;
    public string Quality { get; set; } = string.Empty;
    public string Green { get; set; } = string.Empty;
    public string Configs { get; set; } = string.Empty;
    public string LastTried { get; set; } = string.Empty;
    public string State { get; set; } = string.Empty;
}

public class AutoModeSourcesViewModel : MyReactiveObject
{
    public Interaction<string, bool> ShowYesNoInteraction { get; } = new();
    public Interaction<Unit, string?> BrowseFileInteraction { get; } = new();

    public IObservableCollection<AutoModeSourceModel> Stats { get; } = new ObservableCollectionExtended<AutoModeSourceModel>();

    /// <summary>One URL per line. Commas and pasted noise are tolerated on save.</summary>
    [Reactive]
    public string SourceText { get; set; } = string.Empty;

    [Reactive]
    public string Summary { get; set; } = string.Empty;

    /// <summary>How many servers a run keeps. Saved with the source list.</summary>
    [Reactive]
    public int TopCount { get; set; } = 10;

    public ReactiveCommand<Unit, Unit> SaveCmd { get; }
    public ReactiveCommand<Unit, Unit> ImportFileCmd { get; }
    public ReactiveCommand<Unit, Unit> ResetStatsCmd { get; }

    public bool IsModified { get; set; }

    public AutoModeSourcesViewModel()
    {
        _config = AppManager.Instance.Config;

        SaveCmd = ReactiveCommand.CreateFromTask(SaveAsync);
        ImportFileCmd = ReactiveCommand.CreateFromTask(ImportFileAsync);
        ResetStatsCmd = ReactiveCommand.CreateFromTask(ResetStatsAsync);

        _ = Init();
    }

    private async Task Init()
    {
        var store = await AutoModeSourceManager.Instance.GetStore();

        BuildChoices(ProtocolChoices,
            Global.ProtocolShares.Keys.Select(t => new FilterChoice(t.ToString(), t.ToString())),
            store.ProtocolFilter,
            () => ProtocolSummary = Summarise(ProtocolChoices));

        BuildChoices(CountryChoices,
            CountryHint.PickerOptions.Select(t =>
            {
                var parts = t.Split('—', 2);
                return new FilterChoice(parts[0].Trim(), parts.Length > 1 ? parts[1].Trim() : parts[0].Trim());
            }),
            store.CountryFilter,
            () => CountrySummary = Summarise(CountryChoices));

        SourceText = string.Join(Environment.NewLine, store.Sources.Select(t => t.Url));
        TopCount = store.TopCount;

        await RefreshStats();
    }

    private void BuildChoices(IObservableCollection<FilterChoice> target, IEnumerable<FilterChoice> choices, List<string> selected, Action onChanged)
    {
        target.Clear();
        foreach (var choice in choices)
        {
            choice.IsSelected = selected.Contains(choice.Value, StringComparer.OrdinalIgnoreCase);
            // Recompute the dropdown's label the moment a box is ticked, so the control
            // reads as its own state rather than needing a save to catch up.
            choice.WhenAnyValue(t => t.IsSelected).Subscribe(_ => onChanged());
            target.Add(choice);
        }
        onChanged();
    }

    private static string Summarise(IEnumerable<FilterChoice> choices)
    {
        var picked = choices.Where(t => t.IsSelected).Select(t => t.Label).ToList();
        return picked.Count == 0 ? AnyOption : string.Join(", ", picked);
    }

    private async Task RefreshStats()
    {
        var store = await AutoModeSourceManager.Instance.GetStore();

        Stats.Clear();
        Stats.AddRange(store.Sources
            .OrderByDescending(t => t.Enabled)
            .ThenByDescending(t => t.Score)
            .Select(ToModel));

        var alive = store.Sources.Count(t => t.Enabled);
        Summary = $"{store.Sources.Count} sources ({alive} active) · run #{store.RunCount} · "
                + $"{store.SourcesPerRun} sampled per run · keeping top {store.TopCount}";
    }

    /// <summary>
    /// Shown when nothing is ticked. Most people do not care which protocol or country
    /// they end up on as long as it is fast, so "any" is the default and it says so
    /// rather than leaving an empty box to be interpreted.
    /// </summary>
    public const string AnyOption = "Any";

    public IObservableCollection<FilterChoice> ProtocolChoices { get; } = new ObservableCollectionExtended<FilterChoice>();

    public IObservableCollection<FilterChoice> CountryChoices { get; } = new ObservableCollectionExtended<FilterChoice>();

    /// <summary>Label on the protocol dropdown: "Any", or what is ticked.</summary>
    [Reactive]
    public string ProtocolSummary { get; set; } = AnyOption;

    [Reactive]
    public string CountrySummary { get; set; } = AnyOption;

    private static AutoModeSourceModel ToModel(AutoModeSource s)
    {
        string state;
        if (!s.Enabled)
        {
            state = s.AutoDisabled ? "dead (auto-off)" : "off";
        }
        else if (s.Tried == 0)
        {
            state = "untried";
        }
        else if (s.StaleRuns >= 3)
        {
            state = $"stale x{s.StaleRuns}";
        }
        else if (s.DeadStreak > 0)
        {
            state = $"failing x{s.DeadStreak}";
        }
        else
        {
            state = "ok";
        }

        return new AutoModeSourceModel(s)
        {
            Url = s.Url,
            Quality = s.Tried == 0 ? "-" : $"{s.Score * 100:0.#}%",
            Green = s.TestedTotal == 0 ? "-" : $"{s.GreenTotal}/{s.TestedTotal}" + (s.WinnerTotal > 0 ? $" ★{s.WinnerTotal}" : ""),
            Configs = s.LastConfigCount > 0 ? s.LastConfigCount.ToString() : "-",
            LastTried = s.LastTried?.ToString("MM-dd HH:mm") ?? "-",
            State = state,
        };
    }

    private async Task SaveAsync()
    {
        var urls = AutoModeSourceManager.ParseUrls(SourceText);
        if (urls.Count == 0)
        {
            NoticeManager.Instance.Enqueue("No valid http(s) link found.");
            return;
        }

        var store = await AutoModeSourceManager.Instance.GetStore();
        // Bounded: below 1 there is nothing to keep, and every extra slot costs a real
        // speed test, which is the slowest stage of a run.
        store.TopCount = Math.Clamp(TopCount, 1, 50);
        TopCount = store.TopCount;
        store.ProtocolFilter = ProtocolChoices.Where(t => t.IsSelected).Select(t => t.Value).ToList();
        store.CountryFilter = CountryChoices.Where(t => t.IsSelected).Select(t => t.Value).ToList();

        var count = await AutoModeSourceManager.Instance.SetUrls(urls);
        await RefreshStats();
        IsModified = true;

        var scope = store.ProtocolFilter.Count == 0 && store.CountryFilter.Count == 0
            ? "any protocol, anywhere"
            : $"{(store.ProtocolFilter.Count == 0 ? "any protocol" : string.Join("/", store.ProtocolFilter))}"
              + $" in {(store.CountryFilter.Count == 0 ? "any country" : string.Join("/", store.CountryFilter))}";
        NoticeManager.Instance.Enqueue($"Saved {count} sources — keeping the top {store.TopCount} ({scope}).");
    }

    private async Task ImportFileAsync()
    {
        var file = await BrowseFileInteraction.Handle(Unit.Default);
        if (file.IsNullOrEmpty() || !File.Exists(file))
        {
            return;
        }

        var text = await File.ReadAllTextAsync(file!);
        var urls = AutoModeSourceManager.ParseUrls(text);
        if (urls.Count == 0)
        {
            NoticeManager.Instance.Enqueue("No valid http(s) link found in that file.");
            return;
        }

        // Append rather than replace — importing a second list should add to the first.
        var merged = AutoModeSourceManager.ParseUrls(SourceText).Concat(urls).Distinct().ToList();
        SourceText = string.Join(Environment.NewLine, merged);
        NoticeManager.Instance.Enqueue($"Added {urls.Count} links, {merged.Count} total. Press Save to keep them.");
    }

    /// <summary>
    /// Persist tick-box changes. The grid writes straight through to the source objects,
    /// so this only has to flush them; called when the window closes so a stray click is
    /// not written to disk on every keystroke.
    /// </summary>
    public async Task PersistEnabledFlags()
    {
        await AutoModeSourceManager.Instance.Save();
    }

    private async Task ResetStatsAsync()
    {
        if (await ShowYesNoInteraction.Handle("Reset the learned quality of every source?") == false)
        {
            return;
        }

        var store = await AutoModeSourceManager.Instance.GetStore();
        foreach (var s in store.Sources)
        {
            s.Alpha = 1;
            s.Beta = 1;
            s.Tried = 0;
            s.GreenTotal = 0;
            s.TestedTotal = 0;
            s.WinnerTotal = 0;
            s.DeadStreak = 0;
            s.StaleRuns = 0;
            s.Enabled = true;
            s.AutoDisabled = false;
        }
        store.RunCount = 0;
        store.SourcesPerRun = 8;

        await AutoModeSourceManager.Instance.Save();
        await RefreshStats();
        IsModified = true;
        NoticeManager.Instance.Enqueue(ResUI.OperationSuccess);
    }
}
