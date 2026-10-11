namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class RotateMacaroonRootKeyPrinter : IPrinter<RotateMacaroonRootKeyIpcResponse>
{
    private readonly TextWriter _output;

    public RotateMacaroonRootKeyPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(RotateMacaroonRootKeyIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        _output.WriteLine("Rotated the LND gRPC macaroon root key: every macaroon baked before no longer works.");
        _output.WriteLine($"  Root key kept in: {(item.InDatabase ? "the node's database" : "macaroons.key")}");
        if (item.RebakedDefaults)
            _output.WriteLine($"  New admin, readonly and invoice macaroons in: {item.DataDirectory}");
    }
}