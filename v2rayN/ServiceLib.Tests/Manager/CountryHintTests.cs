using AwesomeAssertions;
using ServiceLib.Common;
using ServiceLib.Manager;
using Xunit;

namespace ServiceLib.Tests.Manager;

/// <summary>
/// Country detection for the Auto Mode filter. Every remark below is the shape that
/// actually turns up in public subscription lists.
/// </summary>
public class CountryHintTests
{
    [Theory]
    [InlineData("🇳🇱 Join+Telegram:@Farah_VPN 🇳🇱", "NL")]
    [InlineData("🇬🇧128185 | ⚡️Telegram = t.me/SOSkeyNET", "GB")]
    [InlineData("🇺🇸 San Jose, US | HYSTERIA2", "US")]
    [InlineData("🇯🇵 tokyo", "JP")]
    public void FromRemark_FlagEmoji_DecodedArithmetically(string remark, string expected)
    {
        // A flag is two regional indicator letters, so no lookup table can fall behind.
        CountryHint.FromRemark(remark).Should().Be(expected);
    }

    [Theory]
    [InlineData("United Kingdom | SS | 82.38.31.48:8080", "GB")]
    [InlineData("Netherlands premium", "NL")]
    [InlineData("germany-01", "DE")]
    public void FromRemark_CountryName_IsRecognised(string remark, string expected)
    {
        CountryHint.FromRemark(remark).Should().Be(expected);
    }

    [Theory]
    [InlineData("500GB monthly")]
    [InlineData("HD TV stream")]
    [InlineData("@ConfigV2rayNG")]
    [InlineData("")]
    [InlineData(null)]
    public void FromRemark_NoCountryPresent_ReturnsNull(string? remark)
    {
        // "GB" here means gigabytes and "TV" is not a country. Guessing on any pair of
        // capitals would quietly send the filter after the wrong servers.
        CountryHint.FromRemark(remark).Should().BeNull();
    }

    [Fact]
    public void FromRemark_FlagBeatsText()
    {
        // Providers relabel servers constantly; the flag is the more deliberate signal.
        CountryHint.FromRemark("🇩🇪 germany relay via netherlands").Should().Be("DE");
    }

    [Theory]
    [InlineData("(NL) 185.2.3.4", "NL")]
    [InlineData("🇬🇧(GB) 82.38.31.48", "GB")]
    [InlineData("unknown", null)]
    [InlineData(null, null)]
    public void FromIpInfo_ReadsMeasuredCountry(string? ipInfo, string? expected)
    {
        CountryHint.FromIpInfo(ipInfo).Should().Be(expected);
    }

    [Fact]
    public void ParseFilter_AcceptsCodesAndNames()
    {
        CountryHint.ParseFilter("nl, DE  gb").Should().BeEquivalentTo(["NL", "DE", "GB"]);
        CountryHint.ParseFilter("Netherlands, Germany").Should().BeEquivalentTo(["NL", "DE"]);
        CountryHint.ParseFilter("NL, nl").Should().ContainSingle().Which.Should().Be("NL");
        CountryHint.ParseFilter("").Should().BeEmpty();
        CountryHint.ParseFilter(null).Should().BeEmpty();
    }
}

public class AutoModeProtocolFilterTests
{
    [Fact]
    public void ParseProtocols_AcceptsTheNamesPeopleActuallyType()
    {
        AutoModeSourceManager.ParseProtocols("vless, vmess").Should().BeEquivalentTo(["VLESS", "VMess"]);

        // "ss" and "hy2" are what the URI schemes call them; the enum disagrees.
        AutoModeSourceManager.ParseProtocols("ss").Should().ContainSingle().Which.Should().Be("Shadowsocks");
        AutoModeSourceManager.ParseProtocols("hy2").Should().ContainSingle().Which.Should().Be("Hysteria2");
    }

    [Fact]
    public void ParseProtocols_UnknownWordsAreDropped()
    {
        // A typo must not narrow the filter to nothing testable without saying so — the
        // caller shows back what was understood, so dropping is safe and visible.
        AutoModeSourceManager.ParseProtocols("vless, nonsense").Should().ContainSingle()
            .Which.Should().Be("VLESS");
        AutoModeSourceManager.ParseProtocols("nonsense").Should().BeEmpty();
        AutoModeSourceManager.ParseProtocols("").Should().BeEmpty();
    }

    [Fact]
    public void ParseProtocols_IsCaseInsensitiveAndDeduplicates()
    {
        AutoModeSourceManager.ParseProtocols("VLESS vless Vless").Should().ContainSingle();
    }
}
