namespace NLightning.Client.Printers;

using Domain.Client.Enums;
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
        switch (item.Outcome)
        {
            case ShutdownOutcome.TimedOut:
                PrintTimedOut(item);
                break;
            case ShutdownOutcome.Forced:
                PrintForced(item);
                break;
            default:
                PrintStopped(item);
                break;
        }
    }

    private void PrintStopped(ShutdownIpcResponse item)
    {
        _output.WriteLine(item.HtlcsInFlight > 0
                              ? "Node is shutting down (drained)"
                              : "Node is shutting down (no HTLCs in flight)");
        _output.WriteLine("  Channels:          {0}", item.ChannelCount);
        if (item.ChannelCount > 0)
            _output.WriteLine("  They reestablish when the node starts again.");
    }

    private void PrintTimedOut(ShutdownIpcResponse item)
    {
        _output.WriteLine("Shutdown timed out: the node keeps running (the drain is over)");
        PrintBusy(item);
        _output.WriteLine("  Retry with --force to stop anyway.");
    }

    private void PrintForced(ShutdownIpcResponse item)
    {
        _output.WriteLine("Node is stopping (forced with activity in flight)");
        PrintBusy(item);
        if (item.NearestCltvExpiry > 0)
        {
            if (item.BlocksUntilDeadline >= 0)
                _output.WriteLine("  Nearest HTLC expiry: block {0} (our deadline to act: in {1} block(s))",
                                  item.NearestCltvExpiry, item.BlocksUntilDeadline);
            else
                _output.WriteLine("  Nearest HTLC expiry: block {0} (current height unknown)",
                                  item.NearestCltvExpiry);
        }

        _output.WriteLine("  Nothing was broadcast or force-closed; the HTLC expiry monitor and the on-chain");
        _output.WriteLine("  resolvers run at the next start. Have the node back before the deadlines.");
    }

    private void PrintBusy(ShutdownIpcResponse item)
    {
        _output.WriteLine("  Channels:          {0}", item.ChannelCount);
        _output.WriteLine("  HTLCs in flight:   {0}", item.HtlcsInFlight);
        _output.WriteLine("  Negotiations:      {0}", item.NegotiationCount);
        foreach (var channel in item.BusyChannels)
        {
            _output.WriteLine("    {0}  {1} HTLC(s){2}", channel.ChannelId, channel.HtlcsInFlight,
                              channel.Negotiating ? ", negotiating" : string.Empty);
        }
    }
}