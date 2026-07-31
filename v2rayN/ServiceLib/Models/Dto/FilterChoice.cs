namespace ServiceLib.Models.Dto;

/// <summary>
/// One tickable entry in a multi-select filter, such as a protocol or a country.
///
/// Nothing ticked means "any", which is both the default and the honest description of
/// what most people want: the fastest server, wherever it is.
/// </summary>
public class FilterChoice : ReactiveObject
{
    public FilterChoice(string value, string label)
    {
        Value = value;
        Label = label;
    }

    /// <summary>What gets stored, e.g. "VLESS" or "NL".</summary>
    public string Value { get; }

    /// <summary>What the user reads, e.g. "Netherlands".</summary>
    public string Label { get; }

    [Reactive]
    public bool IsSelected { get; set; }
}
