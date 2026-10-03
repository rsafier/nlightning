using System.Globalization;

namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class GetRoutePrinter : IPrinter<GetRouteIpcResponse>
{
    private readonly TextWriter _output;

    public GetRoutePrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(GetRouteIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Route: {item.Hops.Count} hop(s), {item.Description}"));
        _output.WriteLine("  Our Channel Id:     {0}", item.ChannelId);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Amount (msat):      {item.AmountMsat}"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Fee (msat):         {item.FeeMsat}"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                        $"  CLTV Expiry:        {item.CltvExpiry} (+{item.CltvExpiry - item.BlockHeight} blocks from {item.BlockHeight})"));
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Probability:        {item.Probability:0.####}"));
        _output.WriteLine(PaymentsPrintFormat.Separator);
        for (var i = 0; i < item.Hops.Count; i++)
        {
            var hop = item.Hops[i];
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Hop {i}:              {hop.NodeId}"));
            _output.WriteLine("    Channel:          {0}", ListGraphChannelsPrinter.FormatShortChannelId(hop.ShortChannelId));
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                                            $"    Amount (msat):    {hop.AmountMsat}, fee {hop.FeeMsat}"));
            _output.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    CLTV Expiry:      {hop.CltvExpiry}"));
        }
    }
}