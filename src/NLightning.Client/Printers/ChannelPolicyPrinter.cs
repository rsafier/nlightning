using System.Globalization;

namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

/// <summary>
/// Prints the result of <c>setchannelpolicy</c>/<c>getchannelpolicy</c>: the policy in force, each value marked
/// <c>(channel)</c> when it comes from the channel's override and <c>(node)</c> when it is <c>Node:Routing</c>'s (the
/// HTLC range is also bounded by the channel's own limits).
/// </summary>
public sealed class ChannelPolicyPrinter : IPrinter<ChannelPolicyIpcResponse>
{
    private readonly TextWriter _output;

    public ChannelPolicyPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(ChannelPolicyIpcResponse item)
    {
        _output.WriteLine("Channel Policy{0}:", item.WasReset ? " (reset to the node's values)" : string.Empty);
        _output.WriteLine("  Id:                 {0}", item.ChannelId);
        _output.WriteLine("  Short Channel Id:   {0}", FormatShortChannelId(item.ShortChannelId));
        _output.WriteLine("  Fee Base (msat):    {0} {1}", Invariant(item.FeeBaseMsat),
                          Source(item.IsFeeBaseMsatOverridden));
        _output.WriteLine("  Fee Rate (ppm):     {0} {1}", Invariant(item.FeeProportionalMillionths),
                          Source(item.IsFeeProportionalMillionthsOverridden));
        _output.WriteLine("  CLTV Delta:         {0} {1}", Invariant(item.CltvExpiryDelta),
                          Source(item.IsCltvExpiryDeltaOverridden));
        _output.WriteLine("  HTLC Min (msat):    {0} {1}", Invariant(item.HtlcMinimumMsat),
                          Source(item.IsHtlcMinimumMsatOverridden));
        _output.WriteLine("  HTLC Max (msat):    {0} {1}", Invariant(item.HtlcMaximumMsat),
                          Source(item.IsHtlcMaximumMsatOverridden));
        if (item.OverrideUpdatedAt is { } updatedAt)
            _output.WriteLine("  Override Set At:    {0}",
                              DateTimeOffset.FromUnixTimeSeconds(updatedAt).UtcDateTime
                                            .ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture));
    }

    private static string Source(bool overridden) => overridden ? "(channel)" : "(node)";

    private static string FormatShortChannelId(ulong? shortChannelId) =>
        shortChannelId is { } scid ? $"{scid >> 40}x{(scid >> 16) & 0xFFFFFF}x{scid & 0xFFFF}" : "-";

    private static string Invariant(IFormattable value) => value.ToString(null, CultureInfo.InvariantCulture);
}