using System.Globalization;

namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class ListGraphChannelsPrinter : IPrinter<ListGraphChannelsIpcResponse>
{
    private readonly TextWriter _output;

    public ListGraphChannelsPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(ListGraphChannelsIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _output.WriteLine("Graph channels: {0}", item.Channels.Count.ToString(CultureInfo.InvariantCulture));
        if (item.Channels.Count == 0)
            return;

        _output.WriteLine(PaymentsPrintFormat.Separator);
        foreach (var channel in item.Channels)
        {
            _output.WriteLine("  Short Channel Id:   {0}", FormatShortChannelId(channel.ShortChannelId));
            _output.WriteLine("  Node 1:             {0}", channel.NodeId1);
            _output.WriteLine("  Node 2:             {0}", channel.NodeId2);
            _output.WriteLine("  Capacity (sat):     {0}",
                              channel.CapacitySat is { } capacity
                                  ? capacity.ToString(CultureInfo.InvariantCulture)
                                  : "unknown");
            _output.WriteLine("  Verification:       {0}", channel.Verification);
            if (channel.SpentAtHeight is { } spentAt)
                _output.WriteLine("  Spent at block:     {0} (closed)", spentAt.ToString(CultureInfo.InvariantCulture));
            PrintPolicy("Policy 1 -> 2", channel.Policy1);
            PrintPolicy("Policy 2 -> 1", channel.Policy2);
            _output.WriteLine(PaymentsPrintFormat.Separator);
        }
    }

    internal static string FormatShortChannelId(ulong shortChannelId) =>
        string.Create(CultureInfo.InvariantCulture,
                      $"{shortChannelId >> 40}x{(shortChannelId >> 16) & 0xFFFFFF}x{shortChannelId & 0xFFFF}");

    private void PrintPolicy(string label, GraphPolicyIpcInfo? policy)
    {
        if (policy is null)
        {
            _output.WriteLine("  {0}:      -", label);
            return;
        }

        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                        $"  {label}:      fee {policy.FeeBaseMsat} msat + {policy.FeeProportionalMillionths} ppm, cltv delta {policy.CltvExpiryDelta}, htlc {policy.HtlcMinimumMsat}-{policy.HtlcMaximumMsat} msat{(policy.IsDisabled ? ", DISABLED" : "")}, updated {ListNodesPrinter.FormatTimestamp(policy.Timestamp)}"));
    }
}