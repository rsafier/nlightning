namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class ForceCloseChannelPrinter : IPrinter<ForceCloseChannelIpcResponse>
{
    private readonly TextWriter _output;

    public ForceCloseChannelPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(ForceCloseChannelIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _output.WriteLine("Force close:");
        _output.WriteLine($"  Channel ID: {item.ChannelId}");
        _output.WriteLine($"  State: {item.State}");
        _output.WriteLine($"  Broadcast: {item.Status}");
        if (item.CommitmentTxId is not null)
            _output.WriteLine($"  Commitment TxId: {item.CommitmentTxId}");
        _output.WriteLine("  Follow the on-chain resolution with pendingsweeps.");
    }
}