namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class ChainStatusPrinter : IPrinter<ChainStatusIpcResponse>
{
    private readonly TextWriter _output;

    public ChainStatusPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(ChainStatusIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _output.WriteLine("Chain status:");
        _output.WriteLine($"  Processing: {(item.IsChainProcessingHalted ? "HALTED" : "running")}");
        if (item.HaltReason is not null)
            _output.WriteLine($"  Halt reason: {item.HaltReason}");
        _output.WriteLine($"  Last processed block: {item.LastProcessedBlockHeight}");
        _output.WriteLine($"  bitcoind tip: {(item.ChainTipHeight is { } tip ? tip.ToString() : "unknown")}");
        if (item.RefusedOperations.Count == 0)
            return;

        _output.WriteLine("  Refused while halted:");
        foreach (var operation in item.RefusedOperations)
            _output.WriteLine($"    {operation}");
    }
}