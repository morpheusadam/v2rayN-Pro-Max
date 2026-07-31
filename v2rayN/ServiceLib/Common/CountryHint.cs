namespace ServiceLib.Common;

/// <summary>
/// Works out which country a server claims to be in.
///
/// There are two sources of truth and they disagree often. The remark a provider writes
/// ("🇳🇱 Netherlands", "US-01") is free but frequently marketing rather than fact. The
/// country reported by an IP lookup performed *through the tunnel* is the country the
/// user's traffic actually appears to come from, which is what anyone picking a country
/// really wants — but it only exists after the server has been tested successfully.
///
/// So the label is used to decide what is worth testing, and the measured exit country
/// decides what is worth keeping.
/// </summary>
public static class CountryHint
{
    // Regional indicator symbols: U+1F1E6 is 'A' … U+1F1FF is 'Z'. A flag emoji is just
    // two of them, so a country code can be read straight out of the remark with no table.
    private const int RegionalIndicatorA = 0x1F1E6;
    private const int RegionalIndicatorZ = 0x1F1FF;

    /// <summary>
    /// Country names that turn up in real subscription remarks, mapped to ISO codes.
    /// Deliberately short: the flag emoji and the bare code cover most lists, and a
    /// wrong guess here costs a wasted test.
    /// </summary>
    private static readonly Dictionary<string, string> _namesToCode = new(StringComparer.OrdinalIgnoreCase)
    {
        { "united states", "US" }, { "usa", "US" }, { "america", "US" },
        { "united kingdom", "GB" }, { "england", "GB" }, { "britain", "GB" },
        { "netherlands", "NL" }, { "holland", "NL" },
        { "germany", "DE" }, { "deutschland", "DE" },
        { "france", "FR" }, { "canada", "CA" }, { "japan", "JP" },
        { "singapore", "SG" }, { "korea", "KR" }, { "turkey", "TR" }, { "turkiye", "TR" },
        { "iran", "IR" }, { "russia", "RU" }, { "poland", "PL" }, { "sweden", "SE" },
        { "finland", "FI" }, { "norway", "NO" }, { "denmark", "DK" }, { "ireland", "IE" },
        { "spain", "ES" }, { "italy", "IT" }, { "austria", "AT" }, { "switzerland", "CH" },
        { "belgium", "BE" }, { "romania", "RO" }, { "bulgaria", "BG" }, { "hungary", "HU" },
        { "czech", "CZ" }, { "ukraine", "UA" }, { "lithuania", "LT" }, { "latvia", "LV" },
        { "estonia", "EE" }, { "moldova", "MD" }, { "serbia", "RS" }, { "croatia", "HR" },
        { "greece", "GR" }, { "portugal", "PT" }, { "australia", "AU" },
        { "india", "IN" }, { "china", "CN" }, { "hong kong", "HK" }, { "taiwan", "TW" },
        { "vietnam", "VN" }, { "indonesia", "ID" }, { "malaysia", "MY" }, { "thailand", "TH" },
        { "brazil", "BR" }, { "argentina", "AR" }, { "mexico", "MX" }, { "chile", "CL" },
        { "emirates", "AE" }, { "dubai", "AE" }, { "israel", "IL" }, { "armenia", "AM" },
        { "azerbaijan", "AZ" }, { "kazakhstan", "KZ" }, { "georgia", "GE" },
        { "south africa", "ZA" }, { "egypt", "EG" }, { "cyprus", "CY" }, { "luxembourg", "LU" },
    };

    // Digits count as part of the surrounding word: "500GB monthly" is a traffic quota,
    // not a server in Great Britain.
    private static readonly Regex _isoTokenRegex =
        new(@"(?<![A-Za-z0-9])([A-Z]{2})(?![A-Za-z0-9])", RegexOptions.Compiled);

    /// <summary>
    /// Best guess at the country a remark advertises, as an ISO code, or null.
    /// </summary>
    public static string? FromRemark(string? remark)
    {
        if (remark.IsNullOrEmpty())
        {
            return null;
        }

        // A flag emoji is unambiguous, so it wins over anything spelled out.
        var fromFlag = FromFlagEmoji(remark!);
        if (fromFlag is not null)
        {
            return fromFlag;
        }

        foreach (var (name, code) in _namesToCode)
        {
            if (remark!.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return code;
            }
        }

        // A bare two-letter token, but only when it is a code we recognise — plenty of
        // remarks contain things like "GB" meaning gigabytes or "TV", and treating any
        // pair of capitals as a country produces confident nonsense.
        foreach (Match m in _isoTokenRegex.Matches(remark!))
        {
            var token = m.Groups[1].Value.ToUpperInvariant();
            if (_namesToCode.ContainsValue(token))
            {
                return token;
            }
        }

        return null;
    }

    private static string? FromFlagEmoji(string text)
    {
        for (var i = 0; i < text.Length - 1; i++)
        {
            if (!char.IsHighSurrogate(text[i]))
            {
                continue;
            }

            var first = char.ConvertToUtf32(text, i);
            if (first is < RegionalIndicatorA or > RegionalIndicatorZ)
            {
                continue;
            }

            var next = i + 2;
            if (next >= text.Length - 1 || !char.IsHighSurrogate(text[next]))
            {
                continue;
            }

            var second = char.ConvertToUtf32(text, next);
            if (second is < RegionalIndicatorA or > RegionalIndicatorZ)
            {
                continue;
            }

            return $"{(char)('A' + first - RegionalIndicatorA)}{(char)('A' + second - RegionalIndicatorA)}";
        }

        return null;
    }

    /// <summary>
    /// Reads the country out of the IP info v2rayN records after a successful test,
    /// which is stored in the shape "(NL) 1.2.3.4".
    /// </summary>
    public static string? FromIpInfo(string? ipInfo)
    {
        if (ipInfo.IsNullOrEmpty())
        {
            return null;
        }

        var match = Regex.Match(ipInfo!, @"\(([A-Za-z]{2})\)");
        return match.Success ? match.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>
    /// Countries offered in the Auto Mode picker, as "NL — Netherlands". Ordered by how
    /// often they turn up as exit locations in public subscription lists.
    /// </summary>
    public static IReadOnlyList<string> PickerOptions { get; } =
    [
        "NL — Netherlands", "DE — Germany", "GB — United Kingdom", "US — United States",
        "FR — France", "FI — Finland", "SE — Sweden", "PL — Poland", "AT — Austria",
        "CH — Switzerland", "CA — Canada", "JP — Japan", "SG — Singapore", "KR — Korea",
        "TR — Turkey", "AE — Emirates", "RU — Russia", "IR — Iran", "IN — India",
        "AU — Australia", "BR — Brazil", "IT — Italy", "ES — Spain", "RO — Romania",
        "LT — Lithuania", "LV — Latvia", "EE — Estonia", "CZ — Czechia", "IE — Ireland",
        "HK — Hong Kong", "TW — Taiwan", "AM — Armenia", "AZ — Azerbaijan", "CY — Cyprus",
    ];

    /// <summary>Parses a user-typed filter such as "NL, DE, gb" into ISO codes.</summary>
    public static List<string> ParseFilter(string? text)
    {
        var result = new List<string>();
        if (text.IsNullOrEmpty())
        {
            return result;
        }

        foreach (var part in text!.Split([',', ';', ' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries))
        {
            var token = part.Trim().ToUpperInvariant();
            if (token.Length == 2 && token.All(char.IsAsciiLetterUpper))
            {
                if (!result.Contains(token))
                {
                    result.Add(token);
                }
                continue;
            }

            // Also accept a spelled-out name, so "Netherlands" works as well as "NL".
            if (_namesToCode.TryGetValue(token, out var mapped) && !result.Contains(mapped))
            {
                result.Add(mapped);
            }
        }

        return result;
    }
}
