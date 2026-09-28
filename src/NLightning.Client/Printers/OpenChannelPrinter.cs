using System.Globalization;

namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class OpenChannelPrinter : IPrinter<OpenChannelIpcResponse>
{
    private readonly TextWriter _output;

    public OpenChannelPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(OpenChannelIpcResponse item)
    {
        _output.WriteLine("Opening Channel: {0}", item.ChannelId);
        if (item.FundingTxId is not { } txId)
        {
            _output.WriteLine("Peer accepted our Channel. Sending funding data to Peer.");
            return;
        }

        // A dual-funded open returns once both tx_signatures went out and the funding is published (NL-535): print it
        // now, so the operator can bumpopen it before it confirms
        _output.WriteLine("Dual-funded open negotiated with the peer; both signed the funding transaction.");
        _output.WriteLine("Funding transaction published. TxId: {0}, Index: {1}", DisplayOrder.ToHex(txId),
                          item.FundingOutputIndex?.ToString(CultureInfo.InvariantCulture) ?? "-");
        _output.WriteLine("Bump its fee before it confirms with: bumpopen {0} <feerate_per_kw>", item.ChannelId);
    }
}