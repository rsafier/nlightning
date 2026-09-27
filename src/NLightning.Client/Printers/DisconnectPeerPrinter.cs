namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class DisconnectPeerPrinter : IPrinter<DisconnectPeerIpcResponse>
{
    private readonly TextWriter _output;

    public DisconnectPeerPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(DisconnectPeerIpcResponse item)
    {
        _output.WriteLine("Disconnected peer {0}", item.NodeId);
        _output.WriteLine("  Channels:          {0}", item.ChannelCount);
        if (item.HtlcsInFlight > 0)
            _output.WriteLine("  HTLCs in flight:   {0} (forced)", item.HtlcsInFlight);
        if (item.ChannelCount > 0)
            _output.WriteLine("  Not reconnected by the node until the next connect or restart.");
    }
}