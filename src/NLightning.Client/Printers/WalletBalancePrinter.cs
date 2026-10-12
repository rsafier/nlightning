using NLightning.Transport.Ipc.Responses;

namespace NLightning.Client.Printers;

public sealed class WalletBalancePrinter : IPrinter<WalletBalanceIpcResponse>
{
    public void Print(WalletBalanceIpcResponse item)
    {
        Console.WriteLine("Balances:");
        Console.WriteLine("  Confirmed:   {0} sats", item.ConfirmedBalance.Satoshi);
        Console.WriteLine("               {0} Bitcoin", item.ConfirmedBalance);
        Console.WriteLine("  Unconfirmed: {0} sats", item.UnconfirmedBalance.Satoshi);
        Console.WriteLine("               {0} Bitcoin", item.UnconfirmedBalance);
        Console.WriteLine("  Available:   {0} sats (confirmed, not locked, reserved or spent by a pending broadcast)",
                          item.AvailableBalance.Satoshi);
        Console.WriteLine("  Anchor reserve: {0} sats for {1} anchors channel(s)", item.AnchorReserve.Satoshi,
                          item.AnchorsChannelCount);
        Console.WriteLine("  Spendable:   {0} sats (available minus the anchor reserve)", item.SpendableBalance.Satoshi);
    }
}