namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class OpenHoldPrinter : IPrinter<OpenHoldIpcResponse>
{
    private readonly TextWriter _output;

    public OpenHoldPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(OpenHoldIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Released)
            _output.WriteLine("Lifted the hold on channel opens (Node:Backup:HoldOpensUntilRestore).");

        if (!item.Held)
        {
            _output.WriteLine("Channel opens are allowed.");
            return;
        }

        _output.WriteLine($"Channel opens are refused: {item.Reason}");
        if (item.RestoreInProgress)
            _output.WriteLine("  A channel restore is in progress: opens are allowed again once it ends.");
        if (item.AwaitingRestore)
            _output.WriteLine("  The node waits for restorechanbackup; 'openhold --release' lifts the hold without "
                            + "one.");
    }
}