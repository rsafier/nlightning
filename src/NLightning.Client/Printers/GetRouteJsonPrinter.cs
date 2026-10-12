using System.Text.Json;
using System.Text.Json.Serialization;

namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

/// <summary>
/// <c>getroute --json</c> (NL-1085): the route as JSON, short channel ids as <c>BLOCKxTXxOUTPUT</c>. Every hop carries
/// both views of it: what its HTLC arrives with (<c>shortChannelId</c>, <c>amountMsat</c>, <c>cltvExpiry</c>, as the
/// text form prints them) and what it forwards, in <c>payroute</c>'s terms (<c>outgoingShortChannelId</c>, omitted on
/// the final hop, <c>amountToForwardMsat</c>, <c>outgoingCltvValue</c>), so the answer maps onto a <c>--routes</c>
/// entry key by key.
/// </summary>
public sealed class GetRouteJsonPrinter : IPrinter<GetRouteIpcResponse>
{
    private readonly TextWriter _output;

    public GetRouteJsonPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(GetRouteIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _output.WriteLine(JsonSerializer.Serialize(ToJson(item), GetRouteJsonContext.Default.GetRouteJson));
    }

    internal static GetRouteJson ToJson(GetRouteIpcResponse item)
    {
        var hops = new List<GetRouteHopJson>(item.Hops.Count);
        for (var i = 0; i < item.Hops.Count; i++)
        {
            var hop = item.Hops[i];

            // A hop forwards what the next one receives; the final hop's HTLC is the one it is paid with
            var next = i + 1 < item.Hops.Count ? item.Hops[i + 1] : null;
            hops.Add(new GetRouteHopJson
            {
                NodeId = hop.NodeId.ToString(),
                ShortChannelId = ListGraphChannelsPrinter.FormatShortChannelId(hop.ShortChannelId),
                AmountMsat = hop.AmountMsat,
                CltvExpiry = hop.CltvExpiry,
                FeeMsat = hop.FeeMsat,
                OutgoingShortChannelId = next is null
                                             ? null
                                             : ListGraphChannelsPrinter.FormatShortChannelId(next.ShortChannelId),
                AmountToForwardMsat = next?.AmountMsat ?? hop.AmountMsat,
                OutgoingCltvValue = next?.CltvExpiry ?? hop.CltvExpiry
            });
        }

        return new GetRouteJson
        {
            ChannelId = item.ChannelId.ToString(),
            AmountMsat = item.AmountMsat,
            FeeMsat = item.FeeMsat,
            CltvExpiry = item.CltvExpiry,
            BlockHeight = item.BlockHeight,
            Probability = item.Probability,
            Description = item.Description,
            Hops = hops,
            Trampoline = item.Trampoline is { } t
                             ? new GetRouteTrampolineJson
                             {
                                 TrampolineNode = t.TrampolineNode.ToString(),
                                 Payee = t.Payee.ToString(),
                                 AmountMsat = t.AmountMsat,
                                 PayeeCltvExpiry = t.PayeeCltvExpiry,
                                 FeeBaseMsat = t.FeeBaseMsat,
                                 FeeProportionalMillionths = t.FeeProportionalMillionths,
                                 CltvExpiryDelta = t.CltvExpiryDelta,
                                 FeeMsat = t.FeeMsat,
                                 PolicyLearnt = t.PolicyLearnt
                             }
                             : null
        };
    }
}

/// <summary>The <c>getroute --json</c> answer.</summary>
internal sealed class GetRouteJson
{
    /// <summary>Our channel of the first HTLC (64 hex characters): <c>payroute</c>'s <c>firstHopChannel</c>.</summary>
    public required string ChannelId { get; init; }

    /// <summary>What our first HTLC carries: <c>payroute</c>'s <c>firstHopAmountMsat</c>.</summary>
    public ulong AmountMsat { get; init; }

    public ulong FeeMsat { get; init; }

    /// <summary>Our first HTLC's <c>cltv_expiry</c>: <c>payroute</c>'s <c>firstHopCltv</c>.</summary>
    public uint CltvExpiry { get; init; }

    public uint BlockHeight { get; init; }

    public double Probability { get; init; }

    public required string Description { get; init; }

    public required List<GetRouteHopJson> Hops { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public GetRouteTrampolineJson? Trampoline { get; init; }
}

/// <summary>One hop of the <c>getroute --json</c> answer.</summary>
internal sealed class GetRouteHopJson
{
    public required string NodeId { get; init; }

    /// <summary>The channel its HTLC arrives on.</summary>
    public required string ShortChannelId { get; init; }

    /// <summary>What its HTLC carries.</summary>
    public ulong AmountMsat { get; init; }

    /// <summary>Its HTLC's <c>cltv_expiry</c>.</summary>
    public uint CltvExpiry { get; init; }

    public ulong FeeMsat { get; init; }

    /// <summary>The channel it forwards over (the next hop's <see cref="ShortChannelId"/>); omitted on the final hop.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OutgoingShortChannelId { get; init; }

    /// <summary>What it forwards (the next hop's <see cref="AmountMsat"/>), or what the final hop is paid.</summary>
    public ulong AmountToForwardMsat { get; init; }

    /// <summary>Its <c>outgoing_cltv_value</c> (the next hop's <see cref="CltvExpiry"/>; the final hop's own).</summary>
    public uint OutgoingCltvValue { get; init; }
}

/// <summary>The trampoline layer of a <c>getroute --json</c> answer (NL-940).</summary>
internal sealed class GetRouteTrampolineJson
{
    public required string TrampolineNode { get; init; }

    public required string Payee { get; init; }

    public ulong AmountMsat { get; init; }

    public uint PayeeCltvExpiry { get; init; }

    public uint FeeBaseMsat { get; init; }

    public uint FeeProportionalMillionths { get; init; }

    public ushort CltvExpiryDelta { get; init; }

    public ulong FeeMsat { get; init; }

    public bool PolicyLearnt { get; init; }
}

/// <summary>The source-generated JSON contract of <c>getroute --json</c> (reflection-free, for NativeAOT).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(GetRouteJson))]
internal partial class GetRouteJsonContext : JsonSerializerContext;