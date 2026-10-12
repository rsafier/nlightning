using System.Globalization;

namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

/// <summary>
/// Prints a <c>listaccountingevents</c> page (NL-602): one block per event in ledger order, then the cursor of the
/// next page.
/// </summary>
public sealed class ListAccountingEventsPrinter : IPrinter<ListAccountingEventsIpcResponse>
{
    private readonly TextWriter _output;

    public ListAccountingEventsPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(ListAccountingEventsIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var inv = CultureInfo.InvariantCulture;
        _output.WriteLine("Accounting events:");
        if (item.Events.Count == 0)
            _output.WriteLine("  None");

        var separator = new string('-', 96);
        foreach (var accountingEvent in item.Events)
        {
            _output.WriteLine(separator);
            _output.WriteLine(string.Format(inv, "  #{0}  {1:yyyy-MM-dd HH:mm:ss.fff} UTC  {2}  {3}{4}",
                                            accountingEvent.LedgerSeq,
                                            DateTimeOffset.FromUnixTimeMilliseconds(
                                                accountingEvent.OccurredAtUnixMilliseconds),
                                            accountingEvent.KindName, accountingEvent.FinalityName,
                                            accountingEvent.BlockHeight is { } height
                                                ? string.Format(inv, " at block {0}", height)
                                                : string.Empty));
            _output.WriteLine(string.Format(inv, "    Amount:      {0} msat   Fee: {1} msat", SignedMsat(
                                                accountingEvent.AmountMsat), accountingEvent.FeeMsat));
            _output.WriteLine(string.Format(inv, "    Key:         {0}", accountingEvent.EventKey));
            if (accountingEvent.ChannelId is { } channelId)
                _output.WriteLine(string.Format(inv, "    Channel:     {0}{1}",
                                                accountingEvent.ShortChannelId ?? channelId,
                                                accountingEvent.ShortChannelId is null
                                                    ? string.Empty
                                                    : $" ({channelId})"));
            else if (accountingEvent.ShortChannelId is { } scid)
                _output.WriteLine(string.Format(inv, "    Channel:     {0}", scid));
            if (accountingEvent.PaymentHash is { } paymentHash)
                _output.WriteLine(string.Format(inv, "    Hash:        {0}", paymentHash));
            if (accountingEvent.TxId is { } txId)
                _output.WriteLine(string.Format(inv, "    Outpoint:    {0}{1}", txId,
                                                accountingEvent.OutputIndex is { } index
                                                    ? string.Format(inv, ":{0}", index)
                                                    : string.Empty));
            if (accountingEvent.Counterparty is { } counterparty)
                _output.WriteLine(string.Format(inv, "    Peer:        {0}", counterparty));
            if (accountingEvent.Flags != 0)
                _output.WriteLine(string.Format(inv, "    Flags:       {0}", FlagsText(accountingEvent.Flags)));
            if (accountingEvent.Details.Count > 0)
                _output.WriteLine(string.Format(inv, "    Details:     {0}",
                                                string.Join(", ", accountingEvent.Details
                                                                                 .OrderBy(d => d.Key,
                                                                                      StringComparer.Ordinal)
                                                                                 .Select(d => $"{d.Key}={d.Value}"))));
        }

        _output.WriteLine(separator);
        _output.WriteLine(string.Format(inv, "  Sealed up to #{0}. Next page: --after {1}{2}",
                                        item.ChainTipLedgerSeq, item.NextAfter,
                                        item.HasMore ? string.Empty : " (nothing more yet)"));
    }

    private static string SignedMsat(long msat) =>
        msat > 0 ? "+" + msat.ToString(CultureInfo.InvariantCulture) : msat.ToString(CultureInfo.InvariantCulture);

    // AccountingEventFlags: 1 backfilled, 2 duplicate
    private static string FlagsText(int flags)
    {
        var names = new List<string>();
        if ((flags & 1) != 0)
            names.Add("backfilled");
        if ((flags & 2) != 0)
            names.Add("duplicate");
        var rest = flags & ~3;
        if (rest != 0)
            names.Add(string.Format(CultureInfo.InvariantCulture, "0x{0:x}", rest));
        return string.Join(", ", names);
    }
}

/// <summary>
/// Prints <c>accountingsnapshot</c> (NL-602): the totals, the wallet and one line per channel bucket.
/// </summary>
public sealed class AccountingSnapshotPrinter : IPrinter<AccountingSnapshotIpcResponse>
{
    private readonly TextWriter _output;

    public AccountingSnapshotPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(AccountingSnapshotIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var inv = CultureInfo.InvariantCulture;
        _output.WriteLine(string.Format(inv, "Accounting snapshot at {0:yyyy-MM-dd HH:mm:ss} UTC, block {1}",
                                        DateTimeOffset.FromUnixTimeMilliseconds(item.TakenAtUnixMilliseconds),
                                        item.BlockHeight));
        _output.WriteLine(string.Format(inv, "  Total:               {0} msat", item.TotalMsat));
        _output.WriteLine(string.Format(inv, "  Channels (ours):     {0} msat", item.ChannelLocalMsat));
        _output.WriteLine(string.Format(inv, "  Pending on chain:    {0} msat ({1} msat in HTLC outputs, {2} output(s))",
                                        item.PendingOnchainMsat + item.PendingHtlcOnchainMsat,
                                        item.PendingHtlcOnchainMsat, item.PendingSweepCount));
        _output.WriteLine(string.Format(inv, "  Wallet:              {0} msat confirmed, {1} msat unconfirmed, {2} msat "
                                           + "locked",
                                        item.WalletConfirmedMsat, item.WalletUnconfirmedMsat, item.WalletLockedMsat));
        _output.WriteLine(string.Format(inv, "Channels ({0}):", item.Channels.Count));
        foreach (var channel in item.Channels)
        {
            _output.WriteLine(string.Format(inv, "  {0}{1}  {2}{3}", channel.ShortChannelId ?? channel.ChannelId,
                                            channel.ShortChannelId is null ? string.Empty : $" ({channel.ChannelId})",
                                            channel.StateName, channel.IsLoaded ? string.Empty : " (not loaded)"));
            if (channel.Counterparty is { } peer)
                _output.WriteLine(string.Format(inv, "    Peer:        {0}", peer));
            _output.WriteLine(string.Format(inv, "    Balance:     local {0} msat, remote {1} msat, capacity {2} msat",
                                            channel.LocalBalanceMsat, channel.RemoteBalanceMsat,
                                            channel.CapacityMsat));
            if (channel.LocalInFlightMsat != 0 || channel.RemoteInFlightMsat != 0)
                _output.WriteLine(string.Format(inv, "    In flight:   ours {0} msat, theirs {1} msat",
                                                channel.LocalInFlightMsat, channel.RemoteInFlightMsat));
            if (channel.PendingSweepCount > 0)
                _output.WriteLine(string.Format(inv, "    On chain:    {0} msat pending, {1} msat in HTLC outputs "
                                                   + "({2} output(s))",
                                                channel.PendingOnchainMsat, channel.PendingHtlcOnchainMsat,
                                                channel.PendingSweepCount));
        }
    }
}