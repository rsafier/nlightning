namespace NLightning.Client.Printers;

using NLightning.Transport.Ipc.Responses;

public sealed class NodeInfoPrinter : IPrinter<NodeInfoIpcResponse>
{
    private readonly TextWriter _output;

    public NodeInfoPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(NodeInfoIpcResponse item)
    {
        _output.WriteLine("Node Information:");
        _output.WriteLine("  Pubkey:            {0}", item.PubKey);
        _output.WriteLine("  Listening to:");
        foreach (var t in item.ListeningTo)
        {
            _output.WriteLine("                     {0}", t);
        }

        if (item.TorMode is not null && item.TorMode != "Off")
            _output.WriteLine("  Tor:               {0}", item.TorMode);
        if (item.OnionAddress is not null)
            _output.WriteLine("  Onion address:     {0}", item.OnionAddress);

        if (item.PeerCount is not null)
            _output.WriteLine("  Peers:             {0}", item.PeerCount);
        if (item.ActiveChannelCount is not null)
            _output.WriteLine("  Channels:          {0} active, {1} pending, {2} closing", item.ActiveChannelCount,
                              item.PendingChannelCount ?? 0, item.ClosingChannelCount ?? 0);

        _output.WriteLine();
        _output.WriteLine("Network Information:");
        _output.WriteLine("  Network:           {0}", item.Network);
        _output.WriteLine("  Best Block Height: {0}", item.BestBlockHeight);
        // The block hash in the display (bitcoind, block explorer) byte order, not Hash.ToString()'s internal one
        _output.WriteLine("  Best Block Hash:   {0}", DisplayOrder.ToHex(item.BestBlockHash));
        // When we processed the best block, in UTC with a Z (NL-885: ":O" printed the offset the value carried)
        if (item.BestBlockTime is { } bestBlockTime)
            _output.WriteLine("  Best Block Time:   {0}", PaymentsPrintFormat.FormatTime(bestBlockTime));
        _output.WriteLine("  Implementation:    {0}", item.Implementation);
        _output.WriteLine("  Version:           {0}", item.Version);
    }
}