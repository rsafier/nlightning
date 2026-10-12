namespace NLightning.Client.Printers;

using Domain.Channels.Enums;
using Transport.Ipc.Responses;

public sealed class CloseChannelPrinter : IPrinter<CloseChannelIpcResponse>
{
    private readonly TextWriter _output;

    public CloseChannelPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(CloseChannelIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _output.WriteLine("Close:");
        _output.WriteLine($"  Channel ID: {item.ChannelId}");
        _output.WriteLine($"  State: {item.State}");
        if (item.ClosingTxId is not null)
            _output.WriteLine($"  Closing TxId: {item.ClosingTxId}");
        if (item.State is ChannelState.ShuttingDown or ChannelState.Negotiating)
            _output.WriteLine("  The close is still in progress; check it later with listchannels.");
    }
}