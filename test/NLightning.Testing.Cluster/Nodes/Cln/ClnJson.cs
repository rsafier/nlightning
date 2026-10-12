using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NLightning.Testing.Cluster.Nodes.Cln;

/// <summary>
/// Reads CLN's JSON results into the facade's types (pure, so the unit tests feed it captured output).
/// </summary>
public static class ClnJson
{
    /// <summary>The state of a channel that can carry payments.</summary>
    public const string NormalState = "CHANNELD_NORMAL";

    /// <summary>
    /// A millisatoshi amount: an integer (CLN since v23) or a string with the <c>msat</c> suffix (older releases).
    /// </summary>
    public static long ReadMsat(JsonNode? value) =>
        value switch
        {
            null => 0,
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.GetValue<long>(),
            JsonValue v when v.GetValueKind() == JsonValueKind.String =>
                long.Parse(v.GetValue<string>().Replace("msat", string.Empty, StringComparison.Ordinal),
                           NumberStyles.None, CultureInfo.InvariantCulture),
            _ => throw new FormatException($"Not a msat amount: {value.ToJsonString()}")
        };

    /// <summary>One entry of <c>listpeerchannels</c>.</summary>
    public static TestChannel ToTestChannel(JsonNode channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var state = channel["state"]?.GetValue<string>();
        var connected = channel["peer_connected"]?.GetValue<bool>() == true;
        return new TestChannel(channel["peer_id"]!.GetValue<string>().ToLowerInvariant(),
                               channel["funding_txid"]?.GetValue<string>().ToLowerInvariant() ?? string.Empty,
                               channel["funding_outnum"]?.GetValue<int>(),
                               channel["short_channel_id"]?.GetValue<string>(),
                               ReadMsat(channel["total_msat"]) / 1000,
                               ReadMsat(channel["to_us_msat"]),
                               state == NormalState && connected);
    }

    /// <summary>Every channel of a <c>listpeerchannels</c> result.</summary>
    public static IReadOnlyList<TestChannel> ToTestChannels(JsonNode listPeerChannels) =>
        listPeerChannels["channels"]?.AsArray().Where(c => c is not null).Select(c => ToTestChannel(c!)).ToList()
     ?? [];

    /// <summary>The confirmed, unreserved wallet outputs of a <c>listfunds</c> result, in satoshis.</summary>
    public static long ConfirmedFundsSat(JsonNode listFunds) =>
        (listFunds["outputs"]?.AsArray() ?? [])
       .Where(o => o?["status"]?.GetValue<string>() == "confirmed" && o["reserved"]?.GetValue<bool>() != true)
       .Sum(o => ReadMsat(o!["amount_msat"])) / 1000;

    /// <summary>The outcome of an <c>xpay</c> (or <c>pay</c>) result.</summary>
    public static TestPaymentResult ToPaymentResult(JsonNode result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var preimage = result["payment_preimage"]?.GetValue<string>();
        var status = result["status"]?.GetValue<string>();
        return preimage is not null && (status is null || status == "complete")
                   ? new TestPaymentResult(true, preimage.ToLowerInvariant(), null)
                   : new TestPaymentResult(false, null, $"no preimage (status {status ?? "unknown"})");
    }
}