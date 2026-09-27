using System.Globalization;

namespace NLightning.Client.Printers;

using Domain.Channels.Splicing.Enums;
using Transport.Ipc.Responses;

/// <summary>
/// Prints the outcome of <c>splicein</c>/<c>spliceout</c> (ClientCommand 33/34).
/// </summary>
public sealed class SplicePrinter : IPrinter<SpliceIpcResponse>
{
    private readonly TextWriter _output;

    public SplicePrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(SpliceIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var inv = CultureInfo.InvariantCulture;
        _output.WriteLine(item.State switch
        {
            SpliceNegotiationState.Signed => "Splice signed",
            SpliceNegotiationState.Aborted => "Splice aborted",
            _ => "Splice in progress"
        });
        _output.WriteLine($"  Channel ID:   {item.ChannelId}");
        _output.WriteLine($"  State:        {item.State}");
        if (item.SpliceTxId is not null)
            _output.WriteLine($"  Splice TxId:  {item.SpliceTxId}");
        if (item.NewCapacitySat is { } capacity)
            _output.WriteLine(string.Format(inv, "  New capacity: {0} sats", capacity));
        if (item.FailureReason is not null)
            _output.WriteLine($"  Reason:       {item.FailureReason}");

        switch (item.State)
        {
            case SpliceNegotiationState.Signed:
                _output.WriteLine("  The splice transaction is broadcast; the channel keeps working on both fundings");
                _output.WriteLine("  until it is locked at depth (see listchannels).");
                break;
            case SpliceNegotiationState.Aborted:
                break;
            default:
                _output.WriteLine("  The negotiation goes on in the daemon; check it later with listchannels.");
                break;
        }
    }
}