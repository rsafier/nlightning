namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class PendingSweepsPrinter : IPrinter<PendingSweepsIpcResponse>
{
    private readonly TextWriter _output;

    public PendingSweepsPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(PendingSweepsIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Channels.Count == 0)
        {
            _output.WriteLine("No channel is being resolved on chain.");
            return;
        }

        foreach (var channel in item.Channels)
        {
            _output.WriteLine($"Channel {channel.ChannelId}: {channel.State}");
            _output.WriteLine($"  Closed by: {channel.CloseKind} {channel.CommitmentTxId}"
                            + (channel.CommitmentNumber is { } number ? $" (commitment {number})" : string.Empty)
                            + $" at height {channel.SpentAtHeight}");
            if (channel.Outputs.Count == 0)
                _output.WriteLine("  No output to resolve.");

            foreach (var output in channel.Outputs)
            {
                var line = $"  {output.TransactionId}:{output.OutputIndex} {output.Descriptor} {output.State}";
                if (output.AmountSat is { } amount)
                    line += $" {amount} sat";
                if (output.HtlcId is { } htlcId)
                    line += $" htlc {output.HtlcDirection} {htlcId}";
                if (output.WaitUntilHeight is { } wait)
                    line += $" wait until {wait}";
                if (output.DeadlineHeight is { } deadline)
                    line += $" deadline {deadline}";
                if (output.ResolvingTxId is { } resolving)
                    line += $" by {resolving}";
                if (output.ResolvedHeight is { } resolved)
                    line += $" resolved at {resolved}";
                _output.WriteLine(line);
            }
        }
    }
}