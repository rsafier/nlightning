using System.Globalization;
using NLightning.Domain.Bitcoin.ValueObjects;
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

    public void Print(OpenChannelSubscriptionIpcResponse item) => Print(item, null);

    /// <summary>
    /// Prints an answer of the open subscription. <paramref name="previousTxId"/> is the funding transaction printed
    /// before, if any: a new one is an RBF attempt of a dual-funded open (either side's), or the earlier attempt that
    /// confirmed (NL-535).
    /// </summary>
    public void Print(OpenChannelSubscriptionIpcResponse item, TxId? previousTxId)
    {
        switch (item.ChannelState)
        {
            case ChannelState.V1FundingSigned when previousTxId is { } previous:
                _output.WriteLine("The channel's funding transaction changed (replaces {0}).", DisplayOrder.ToHex(previous));
                PrintFunding(item);
                break;
            case ChannelState.V1FundingSigned:
                _output.WriteLine("Peer sent their signature. Sending ours.");
                PrintFunding(item);
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

    private void PrintFunding(OpenChannelSubscriptionIpcResponse item)
    {
        // The txid in the display (bitcoind, block explorer) byte order, as TxId.ToString() prints it since NL-519.
        _output.WriteLine("Funding transaction published. TxId: {0}, Index: {1}",
                          item.TxId is { } txId ? DisplayOrder.ToHex(txId) : "-",
                          item.Index?.ToString(CultureInfo.InvariantCulture) ?? "-");
        _output.WriteLine("Waiting for confirmations.");
        _output.WriteLine("You can either wait for the full confirmation or press CTRL+C to quit; the open continues.");
    }
}