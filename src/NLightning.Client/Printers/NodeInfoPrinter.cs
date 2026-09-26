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

        _output.WriteLine();
        _output.WriteLine("Network Information:");
        _output.WriteLine("  Network:           {0}", item.Network);
        _output.WriteLine("  Best Block Height: {0}", item.BestBlockHeight);
        // The block hash in the display (bitcoind, block explorer) byte order, not Hash.ToString()'s internal one
        _output.WriteLine("  Best Block Hash:   {0}", DisplayOrder.ToHex(item.BestBlockHash));
        if (item.BestBlockTime is not null)
            _output.WriteLine($"  Best Block Time:   {item.BestBlockTime:O}");
        _output.WriteLine("  Implementation:    {0}", item.Implementation);
        _output.WriteLine("  Version:           {0}", item.Version);
    }
}