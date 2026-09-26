using System.Globalization;

namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class ListChannelsPrinter : IPrinter<ListChannelsIpcResponse>
{
    private readonly TextWriter _output;

    public ListChannelsPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(ListChannelsIpcResponse item)
    {
        _output.WriteLine("Channels:");
        if (item.Channels.Count == 0)
        {
            _output.WriteLine("  None");
            return;
        }

        _output.WriteLine(PaymentsPrintFormat.Separator);
        foreach (var channel in item.Channels)
        {
            _output.WriteLine("  Id:                 {0}", channel.ChannelId);
            _output.WriteLine("  Peer:               {0} ({1})", channel.PeerId,
                              channel.IsPeerConnected ? "connected" : "disconnected");
            _output.WriteLine("  State:              {0}", channel.State);
            _output.WriteLine("  Reestablished:      {0}", channel.IsReestablished ? "Yes" : "No");
            _output.WriteLine("  Initiator:          {0}", channel.IsInitiator ? "Yes" : "No");
            _output.WriteLine("  Short Channel Id:   {0}", FormatShortChannelId(channel.ShortChannelId));
            // The funding txid in the display (bitcoind, block explorer) byte order, not TxId.ToString()'s internal one.
            _output.WriteLine("  Funding Output:     {0}",
                              channel.FundingTxId is { } fundingTxId
                                  ? $"{DisplayOrder.ToHex(fundingTxId)}:{channel.FundingOutputIndex}"
                                  : "-");
            _output.WriteLine("  Capacity (sat):     {0}", Invariant(channel.Capacity.Satoshi));
            _output.WriteLine("  Local (msat):       {0}", Invariant(channel.LocalBalance.MilliSatoshi));
            _output.WriteLine("  Remote (msat):      {0}", Invariant(channel.RemoteBalance.MilliSatoshi));
            _output.WriteLine("  Commitment (l/r):   {0}/{1}", Invariant(channel.LocalCommitmentNumber),
                              Invariant(channel.RemoteCommitmentNumber));
            _output.WriteLine("  HTLCs (out/in):     {0}/{1}", Invariant(channel.OfferedHtlcCount),
                              Invariant(channel.ReceivedHtlcCount));
            _output.WriteLine("  Fee (base/ppm):     {0} msat/{1}", Invariant(channel.FeeBaseMsat),
                              Invariant(channel.FeePpm));
            if (channel.DataLossDetected)
                _output.WriteLine("  DATA LOSS DETECTED: do not force-close this channel");
            _output.WriteLine(PaymentsPrintFormat.Separator);
        }
    }

    private static string FormatShortChannelId(ulong? shortChannelId) =>
        shortChannelId is { } scid ? $"{scid >> 40}x{(scid >> 16) & 0xFFFFFF}x{scid & 0xFFFF}" : "-";

    private static string Invariant(IFormattable value) => value.ToString(null, CultureInfo.InvariantCulture);
}