using NLightning.Domain.Channels.Enums;

namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class OpenChannelSubscriptionPrinter : IPrinter<OpenChannelSubscriptionIpcResponse>
{
    private readonly TextWriter _output;

    public OpenChannelSubscriptionPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(OpenChannelSubscriptionIpcResponse item)
    {
        switch (item.ChannelState)
        {
            case ChannelState.V1FundingSigned:
                _output.WriteLine("Peer sent their signature. Sending ours.");
                // The txid in the display (bitcoind, block explorer) byte order, not TxId.ToString()'s internal one.
                _output.WriteLine("Funding transaction published. TxId: {0}, Index: {1}",
                                  item.TxId is { } txId ? DisplayOrder.ToHex(txId) : "-", item.Index);
                _output.WriteLine("Waiting for confirmations.");
                _output.WriteLine("You can either wait for the full confirmation or press CTRL+C to quit.");
                break;
            case ChannelState.ReadyForThem or ChannelState.ReadyForUs:
                _output.WriteLine("Channel is now open!");
                break;
            default:
                _output.WriteLine("We've got an unexpected Channel state update: {0}",
                                  Enum.GetName(typeof(ChannelState), item.ChannelState));
                break;
        }
    }
}