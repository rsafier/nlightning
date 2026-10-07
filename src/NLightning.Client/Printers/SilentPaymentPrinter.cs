namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class SilentPaymentPrinter
{
    private readonly TextWriter _output;

    public SilentPaymentPrinter(TextWriter? output = null) => _output = output ?? Console.Out;

    public void Print(SilentPaymentIpcResponse response)
    {
        if (response.Address is { } address)
        {
            _output.WriteLine(address);
            _output.WriteLine($"recoverable_elsewhere: {response.RecoverableElsewhere.ToString().ToLowerInvariant()}");
            if (response.Label is { } label)
                _output.WriteLine($"Label {label}: {response.LabelName}");
        }
        if (response.Labels is { } labels)
            foreach (var label in labels)
                _output.WriteLine($"{label.M} {label.Name} {label.Address}");
        if (response.Status is { } status)
        {
            _output.WriteLine($"Enabled: {status.Enabled}; send: {status.Send}; receive: {status.Receive}");
            _output.WriteLine($"recoverable_elsewhere: {status.RecoverableElsewhere.ToString().ToLowerInvariant()}");
            _output.WriteLine($"Birthday: {status.BirthdayHeight}; live cursor: {status.LiveCursorHeight}; source: {status.PrevoutSource}");
            _output.WriteLine(status.RescanTargetHeight is { } target
                ? $"Rescan: {status.RescanCursorHeight?.ToString() ?? "not started"} / {target}; recovery labels: {status.RecoveryLabelCount}"
                : "Rescan: idle");
            _output.WriteLine($"Outputs: {status.FoundOutputs} found, {status.UnspentOutputs} unspent, {status.IgnoredOutputs} ignored");
            if (status.Unspent is { Count: > 0 } unspent)
                foreach (var output in unspent)
                    _output.WriteLine($"  {output.TxId}:{output.Index} {output.AmountSats} sat, block {output.BlockHeight}"
                                    + (output.Label is { } m ? $", label {m}" : ", no label"));
            if (status.LastScanMilliseconds is { } milliseconds)
                _output.WriteLine($"Last scan: {milliseconds:F2} ms");
            if (status.LastError is { } error)
                _output.WriteLine($"Last error: {error}");
        }
    }
}