using System.Text.Json.Nodes;

namespace NLightning.Testing.Cluster.Nodes.Eclair;

/// <summary>
/// Reads Eclair 0.14.3's JSON answers into the facade's types (pure, so the unit tests feed it captured output): a
/// channel's newest active commitment (<c>data.commitments.active[0]</c>: <c>fundingInput</c> as <c>txid:index</c>,
/// <c>fundingAmount</c> in satoshis, <c>localFunding.shortChannelId</c> once confirmed, <c>localCommit.spec.toLocal</c>
/// in msat).
/// </summary>
public static class EclairJson
{
    /// <summary>The state of a channel that can carry payments (Eclair moves it to <c>OFFLINE</c> while disconnected).</summary>
    public const string NormalState = "NORMAL";

    /// <summary>One entry of <c>channels</c>.</summary>
    public static TestChannel ToTestChannel(JsonNode channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var active = channel["data"]?["commitments"]?["active"]?[0];
        var fundingInput = active?["fundingInput"]?.GetValue<string>();
        var fundingTxId = string.Empty;
        int? outputIndex = null;
        if (fundingInput is not null)
        {
            var separator = fundingInput.LastIndexOf(':');
            fundingTxId = (separator < 0 ? fundingInput : fundingInput[..separator]).ToLowerInvariant();
            if (separator >= 0 && int.TryParse(fundingInput[(separator + 1)..], out var index))
                outputIndex = index;
        }

        return new TestChannel(channel["nodeId"]!.GetValue<string>().ToLowerInvariant(), fundingTxId, outputIndex,
                               active?["localFunding"]?["shortChannelId"]?.GetValue<string>(),
                               active?["fundingAmount"]?.GetValue<long>() ?? 0,
                               active?["localCommit"]?["spec"]?["toLocal"]?.GetValue<long>() ?? 0,
                               channel["state"]?.GetValue<string>() == NormalState);
    }

    /// <summary>Every channel of a <c>channels</c> answer.</summary>
    public static IReadOnlyList<TestChannel> ToTestChannels(JsonNode? channels) =>
        channels?.AsArray().Where(c => c is not null).Select(c => ToTestChannel(c!)).ToList() ?? [];

    /// <summary>The outcome of a <c>payinvoice blocking=true</c> answer (<c>payment-sent</c> or <c>payment-failed</c>).</summary>
    public static TestPaymentResult ToPaymentResult(JsonNode? result)
    {
        var type = result?["type"]?.GetValue<string>();
        var preimage = result?["paymentPreimage"]?.GetValue<string>();
        return type == "payment-sent" && preimage is not null
                   ? new TestPaymentResult(true, preimage.ToLowerInvariant(), null)
                   : new TestPaymentResult(false, null, $"{type ?? "no answer"}: {result?["failures"]?.ToJsonString()}");
    }

    /// <summary>The funding txid of an <c>open</c> answer (<c>created channel &lt;id&gt; with fundingTxId=&lt;txid&gt; ...</c>).</summary>
    public static string? FundingTxIdOfOpen(string answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        const string marker = "fundingTxId=";
        var start = answer.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += marker.Length;
        var end = start;
        while (end < answer.Length && Uri.IsHexDigit(answer[end]))
            end++;
        return end - start == 64 ? answer[start..end].ToLowerInvariant() : null;
    }
}