using System.Globalization;
using System.Text.Json.Nodes;

namespace NLightning.Integration.Tests.Docker.Interop.Eclair;

/// <summary>
/// Readers for the parts of Eclair 0.14.3's JSON the interop proofs check (its <c>JsonSerializers</c> at the tag): a
/// channel's commitments (<c>data.commitments.active</c>, newest first, each with <c>fundingInput</c> as
/// <c>txid:index</c>, <c>fundingAmount</c> in satoshis and <c>localFunding.shortChannelId</c> once confirmed) and
/// short channel ids printed as <c>BLOCKxTXxOUTPUT</c>.
/// </summary>
public static class EclairJson
{
    /// <summary>Eclair's newest active commitment of a <c>channel</c> answer.</summary>
    public static JsonNode? Active(JsonNode channel) => channel["data"]?["commitments"]?["active"]?[0];

    /// <summary>How many active commitments (fundings) the channel has: more than one while a splice is pending.</summary>
    public static int ActiveCount(JsonNode channel) =>
        channel["data"]?["commitments"]?["active"]?.AsArray().Count ?? 0;

    /// <summary>Every active commitment as <c>index:outpoint:amount</c>, for the log.</summary>
    public static string DescribeActive(JsonNode channel) =>
        string.Join(",", channel["data"]?["commitments"]?["active"]?.AsArray()
                                .Select(c => $"{c?["fundingTxIndex"]}:{c?["fundingInput"]}:{c?["fundingAmount"]}")
                         ?? []);

    /// <summary>The txid (display order) of Eclair's newest active funding.</summary>
    public static string? FundingTxId(JsonNode channel) =>
        Active(channel)?["fundingInput"]?.GetValue<string>().Split(':')[0];

    /// <summary>The real short channel id of Eclair's newest funding as a u64, or null before it confirmed.</summary>
    public static ulong? ShortChannelId(JsonNode channel) =>
        Active(channel)?["localFunding"]?["shortChannelId"]?.GetValue<string>() is { } text
            ? ParseShortChannelId(text)
            : null;

    /// <summary><c>BLOCKxTXxOUTPUT</c> as the BOLT 7 u64.</summary>
    public static ulong ParseShortChannelId(string text)
    {
        var parts = text.Split('x');
        return (ulong.Parse(parts[0], CultureInfo.InvariantCulture) << 40)
             | (ulong.Parse(parts[1], CultureInfo.InvariantCulture) << 16)
             | ulong.Parse(parts[2], CultureInfo.InvariantCulture);
    }
}