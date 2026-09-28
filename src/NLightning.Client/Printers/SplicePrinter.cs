using System.Globalization;

namespace NLightning.Client.Printers;

using Domain.Channels.Splicing.Enums;
using Transport.Ipc.Responses;

/// <summary>
/// Prints the outcome of <c>splicein</c>/<c>spliceout</c> (ClientCommand 33/34) and <c>bumpsplice</c> (37).
/// </summary>
/// <remarks>
/// A splice that stopped at <see cref="SpliceNegotiationState.CommitmentSigned"/> with its txid (the peer disconnected
/// before <c>tx_signatures</c>) is kept by the daemon and completes when the peer reconnects: it is printed as waiting
/// for the reconnection, with the daemon's <c>Note</c>, not as a failure.
/// </remarks>
public sealed class SplicePrinter : IPrinter<SpliceIpcResponse>
{
    private readonly TextWriter _output;
    private readonly bool _bump;
    private readonly string _subject;

    /// <param name="output">Where to print; the console when null.</param>
    /// <param name="bump">The response answers <c>bumpsplice</c>: the new RBF attempt is the subject.</param>
    public SplicePrinter(TextWriter? output = null, bool bump = false)
    {
        _output = output ?? Console.Out;
        _bump = bump;
        _subject = bump ? "Splice RBF" : "Splice";
    }

    public void Print(SpliceIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var inv = CultureInfo.InvariantCulture;
        var waitsForReconnection = item is
        {
            State: SpliceNegotiationState.CommitmentSigned, SpliceTxId: not null, Note: not null
        };
        _output.WriteLine(item.State switch
        {
            SpliceNegotiationState.Signed => $"{_subject} signed",
            SpliceNegotiationState.Aborted => $"{_subject} aborted",
            _ when waitsForReconnection => $"{_subject} waiting for the peer to reconnect",
            _ => $"{_subject} in progress"
        });
        _output.WriteLine($"  Channel ID:   {item.ChannelId}");
        _output.WriteLine($"  State:        {item.State}");
        if (item.SpliceTxId is not null)
            _output.WriteLine($"  Splice TxId:  {item.SpliceTxId}");
        if (item.NewCapacitySat is { } capacity)
            _output.WriteLine(string.Format(inv, "  New capacity: {0} sats", capacity));
        if (item.FailureReason is not null)
            _output.WriteLine($"  Reason:       {item.FailureReason}");
        if (item.Note is not null)
            _output.WriteLine($"  Note:         {item.Note}");

        switch (item.State)
        {
            case SpliceNegotiationState.Signed when _bump:
                _output.WriteLine("  The new attempt is broadcast and replaces the previous one; the channel keeps");
                _output.WriteLine("  working until one of the attempts is locked at depth (see listchannels).");
                break;
            case SpliceNegotiationState.Signed:
                _output.WriteLine("  The splice transaction is broadcast; the channel keeps working on both fundings");
                _output.WriteLine("  until it is locked at depth (see listchannels).");
                break;
            case SpliceNegotiationState.Aborted:
                break;
            case SpliceNegotiationState.CommitmentSigned when waitsForReconnection:
                _output.WriteLine("  Both commitments are signed and the splice is kept: it completes (tx_signatures,");
                _output.WriteLine("  broadcast) when the peer reconnects; check it later with listchannels.");
                break;
            default:
                _output.WriteLine("  The negotiation goes on in the daemon; check it later with listchannels.");
                break;
        }
    }
}