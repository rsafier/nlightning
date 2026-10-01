namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class ShutdownPrinter : IPrinter<ShutdownIpcResponse>
{
    private readonly TextWriter _output;

    public ShutdownPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(ShutdownIpcResponse item)
    {
        _output.WriteLine("Node is shutting down (no HTLCs in flight)");
        _output.WriteLine("  Channels:          {0}", item.ChannelCount);
        if (item.ChannelCount > 0)
            _output.WriteLine("  They reestablish when the node starts again.");
    }
}