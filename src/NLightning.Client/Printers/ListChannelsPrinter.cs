namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class ListChannelsPrinter : IPrinter<ListChannelsIpcResponse>
{
    public void Print(ListChannelsIpcResponse item)
    {
        Console.WriteLine("Channels:");
        if (item.Channels.Count == 0)
        {
            Console.WriteLine("  None");
            return;
        }

        Console.WriteLine("----------------------------------------------------------------------------------");
        foreach (var channel in item.Channels)
        {
            Console.WriteLine("  Id:                 {0}", channel.ChannelId);
            Console.WriteLine("  Peer:               {0} ({1})", channel.PeerId,
                              channel.IsPeerConnected ? "connected" : "disconnected");
            Console.WriteLine("  State:              {0}", channel.State);
            Console.WriteLine("  Initiator:          {0}", channel.IsInitiator ? "Yes" : "No");
            Console.WriteLine("  Short Channel Id:   {0}", FormatShortChannelId(channel.ShortChannelId));
            Console.WriteLine("  Funding Output:     {0}",
                              channel.FundingTxId is null
                                  ? "-"
                                  : $"{channel.FundingTxId}:{channel.FundingOutputIndex}");
            Console.WriteLine("  Capacity (sat):     {0}", channel.Capacity.Satoshi);
            Console.WriteLine("  Local (msat):       {0}", channel.LocalBalance.MilliSatoshi);
            Console.WriteLine("  Remote (msat):      {0}", channel.RemoteBalance.MilliSatoshi);
            Console.WriteLine("  Commitment (l/r):   {0}/{1}", channel.LocalCommitmentNumber,
                              channel.RemoteCommitmentNumber);
            Console.WriteLine("  HTLCs (out/in):     {0}/{1}", channel.OfferedHtlcCount, channel.ReceivedHtlcCount);
            if (channel.DataLossDetected)
                Console.WriteLine("  DATA LOSS DETECTED: do not force-close this channel");
            Console.WriteLine("----------------------------------------------------------------------------------");
        }
    }

    private static string FormatShortChannelId(ulong? shortChannelId) =>
        shortChannelId is { } scid ? $"{scid >> 40}x{(scid >> 16) & 0xFFFFFF}x{scid & 0xFFFF}" : "-";
}