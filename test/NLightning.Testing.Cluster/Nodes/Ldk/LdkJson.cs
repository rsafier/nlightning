using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NLightning.Testing.Cluster.Nodes.Ldk;

/// <summary>
/// Reads ldk-server-cli's JSON (the gRPC responses, snake_case proto fields) into the facade's types (pure, so the unit
/// tests feed it captured output).
/// </summary>
public static class LdkJson
{
    /// <summary>A number that the CLI prints as a JSON number or, for 64-bit fields, possibly as a string.</summary>
    public static long ReadLong(JsonNode? value) =>
        value switch
        {
            null => 0,
            JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.GetValue<long>(),
            JsonValue v when v.GetValueKind() == JsonValueKind.String =>
                long.Parse(v.GetValue<string>(), NumberStyles.None, CultureInfo.InvariantCulture),
            _ => throw new FormatException($"Not a number: {value.ToJsonString()}")
        };

    /// <summary>
    /// A short channel id as <c>block x tx x output</c> (LDK prints the 64-bit value), or null before confirmation.
    /// </summary>
    public static string? FormatShortChannelId(JsonNode? value)
    {
        if (value is null)
            return null;

        var scid = (ulong)ReadLong(value);
        return scid == 0 ? null : $"{scid >> 40}x{(scid >> 16) & 0xFFFFFF}x{scid & 0xFFFF}";
    }

    /// <summary>One entry of <c>list-channels</c>.</summary>
    /// <remarks>
    /// LDK reports no balance as such: ours is what we may send (<c>outbound_capacity_msat</c>) plus the reserve the
    /// peer makes us keep (<c>unspendable_punishment_reserve</c>, satoshis), so it is exact only up to in-flight HTLCs
    /// and the commitment fee the funder pays.
    /// </remarks>
    public static TestChannel ToTestChannel(JsonNode channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var funding = channel["funding_txo"];
        return new TestChannel(channel["counterparty_node_id"]?.GetValue<string>().ToLowerInvariant() ?? string.Empty,
                               funding?["txid"]?.GetValue<string>().ToLowerInvariant() ?? string.Empty,
                               funding?["vout"] is { } vout ? (int)ReadLong(vout) : null,
                               FormatShortChannelId(channel["short_channel_id"]),
                               ReadLong(channel["channel_value_sats"]),
                               ReadLong(channel["outbound_capacity_msat"])
                             + ReadLong(channel["unspendable_punishment_reserve"]) * 1000,
                               channel["is_usable"]?.GetValue<bool>() == true);
    }

    /// <summary>Every channel of a <c>list-channels</c> result.</summary>
    public static IReadOnlyList<TestChannel> ToTestChannels(JsonNode listChannels) =>
        listChannels["channels"]?.AsArray().Where(c => c is not null).Select(c => ToTestChannel(c!)).ToList() ?? [];

    /// <summary>The node's tip height from <c>get-node-info</c>.</summary>
    public static long BlockHeight(JsonNode nodeInfo) => ReadLong(nodeInfo["current_best_block"]?["height"]);

    /// <summary>The spendable (confirmed) on-chain balance from <c>get-balances</c>, in satoshis.</summary>
    public static long SpendableOnchainSat(JsonNode balances) => ReadLong(balances["spendable_onchain_balance_sats"]);

    /// <summary>
    /// The outcome of <c>pay --wait</c>: succeeded with the preimage found anywhere in the payment's details.
    /// </summary>
    public static TestPaymentResult ToPaymentResult(JsonNode result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var details = result["payment"] ?? result;
        var status = details["status"]?.ToString();
        var preimage = FindString(details, "preimage");
        return string.Equals(status, "SUCCEEDED", StringComparison.OrdinalIgnoreCase) && preimage is not null
                   ? new TestPaymentResult(true, preimage.ToLowerInvariant(), null)
                   : new TestPaymentResult(false, null, $"status {status ?? "unknown"}: {details.ToJsonString()}");
    }

    /// <summary>Whether <c>list-peers</c> lists <paramref name="nodeId"/> as connected.</summary>
    public static bool IsConnected(JsonNode listPeers, string nodeId) =>
        FindPeer(listPeers, nodeId)?["is_connected"]?.GetValue<bool>() == true;

    /// <summary>The <c>list-peers</c> entry of <paramref name="nodeId"/>, or null.</summary>
    public static JsonNode? FindPeer(JsonNode listPeers, string nodeId) =>
        (listPeers["peers"]?.AsArray() ?? [])
       .FirstOrDefault(p => string.Equals(p?["node_id"]?.GetValue<string>(), nodeId,
                                          StringComparison.OrdinalIgnoreCase));

    /// <summary>The first string property named <paramref name="name"/> anywhere in <paramref name="node"/>.</summary>
    public static string? FindString(JsonNode? node, string name)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, value) in obj)
                {
                    if (key == name && value is JsonValue v && v.TryGetValue<string>(out var s))
                        return s;

                    if (FindString(value, name) is { } found)
                        return found;
                }

                return null;
            case JsonArray array:
                return array.Select(item => FindString(item, name)).FirstOrDefault(found => found is not null);
            default:
                return null;
        }
    }
}