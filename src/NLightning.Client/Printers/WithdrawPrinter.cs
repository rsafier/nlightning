using System.Globalization;

namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

public sealed class WithdrawPrinter : IPrinter<WithdrawIpcResponse>
{
    private readonly TextWriter _output;

    public WithdrawPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(WithdrawIpcResponse item)
    {
        var inv = CultureInfo.InvariantCulture;
        _output.WriteLine(item.Published ? "Withdrawal broadcast" : "Withdrawal stored, broadcast refused");
        _output.WriteLine(string.Format(inv, "  TxId:           {0}", DisplayOrder.ToHex(item.TxId)));
        _output.WriteLine(string.Format(inv, "  Amount:         {0} sats", item.AmountSat));
        _output.WriteLine(string.Format(inv, "  Fee:            {0} sats ({1} sat/kw, {2} vB)", item.FeeSat,
                                        item.FeeRatePerKw, (item.Weight + 3) / 4));
        _output.WriteLine(string.Format(inv, "  Change:         {0} sats", item.ChangeSat));
        _output.WriteLine(string.Format(inv, "  Inputs:         {0}", item.InputCount));
        if (item.AnchorReserveSat > 0)
            _output.WriteLine(string.Format(inv, "  Anchor reserve: {0} sats kept in the wallet",
                                            item.AnchorReserveSat));
        if (!item.Published)
        {
            _output.WriteLine("  bitcoind refused it for now (see the daemon log for why); the node sends it again");
            _output.WriteLine("  after every block. Do not run withdraw again to retry: a second withdrawal spends");
            _output.WriteLine("  other outputs, and both could confirm and pay twice.");
        }
    }
}