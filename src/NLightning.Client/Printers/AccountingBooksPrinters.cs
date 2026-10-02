using System.Globalization;

namespace NLightning.Client.Printers;

using Transport.Ipc.Responses;

/// <summary>
/// Prints an <c>accounting report</c> (NL-602 A2). Amounts are msat with their exact value in satoshis.
/// </summary>
public sealed class AccountingReportPrinter : IPrinter<AccountingReportIpcResponse>
{
    private const int NameWidth = 40;

    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    private readonly TextWriter _output;

    public AccountingReportPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(AccountingReportIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Financial is { } financial)
        {
            // The financial book and the risk-weighted capital (NL-602 A3-T6)
            new AccountingFinancialReportPrinter(_output).Print(item, financial);
            return;
        }

        if (item.BalanceSheet is { } sheet)
            PrintBalanceSheet(item, sheet);
        else if (item.IncomeStatement is { } statement)
            PrintIncomeStatement(item, statement);
        else if (item.Channels is { } channels)
            PrintChannels(item, channels);
        else if (item.Peers is { } peers)
            PrintPeers(item, peers);
        else if (item.Fees is { } fees)
            PrintFees(item, fees);
        else if (item.Register is { } register)
            PrintRegister(item, register);
        else
            _output.WriteLine(string.Format(s_inv, "{0}: nothing to report", item.KindName));
    }

    /// <summary>An amount as <c>msat (sat)</c>, exact.</summary>
    internal static string Amount(long msat) =>
        string.Format(s_inv, "{0} msat ({1} sat)", msat, (msat / 1_000m).ToString("0.###", s_inv));

    internal static string Time(long? unixMilliseconds) =>
        unixMilliseconds is { } ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToString("yyyy-MM-dd HH:mm:ss 'UTC'", s_inv)
            : "-";

    private void PrintBalanceSheet(AccountingReportIpcResponse item, AccountingBalanceSheetIpcResponse sheet)
    {
        _output.WriteLine(string.Format(s_inv, "Balance sheet at {0} ({1})",
                                        item.UntilUnixMilliseconds is null ? "now" : Time(item.UntilUnixMilliseconds),
                                        BalanceSheetEntries(item, sheet)));
        Section("Assets", sheet.Assets, sheet.TotalAssetsMsat);
        Section("Liabilities", sheet.Liabilities, sheet.TotalLiabilitiesMsat);
        Section("Equity", sheet.Equity, sheet.TotalEquityMsat);
        _output.WriteLine(string.Format(s_inv, "  {0}{1}", "Retained earnings (net income)".PadRight(NameWidth + 2),
                                        Amount(sheet.RetainedEarningsMsat)));
        _output.WriteLine(string.Format(s_inv, "  Assets = liabilities + equity + earnings: {0}",
                                        sheet.IsBalanced ? "yes" : "NO (the books are inconsistent)"));
    }

    private void PrintIncomeStatement(AccountingReportIpcResponse item, AccountingIncomeStatementIpcResponse statement)
    {
        _output.WriteLine(string.Format(s_inv, "Income statement {0} (books through #{1})", Period(item),
                                        item.ProjectedLedgerSeq));
        Section("Income", statement.Income, statement.TotalIncomeMsat);
        Section("Expenses", statement.Expenses, statement.TotalExpensesMsat);
        _output.WriteLine(string.Format(s_inv, "  {0}{1}", "Net income".PadRight(NameWidth + 2),
                                        Amount(statement.NetIncomeMsat)));
    }

    private void PrintChannels(AccountingReportIpcResponse item, List<AccountingChannelIpcResponse> channels)
    {
        _output.WriteLine(string.Format(s_inv, "Channels {0} (books through #{1}): {2}", Period(item),
                                        item.ProjectedLedgerSeq, channels.Count));
        foreach (var channel in channels)
        {
            _output.WriteLine(string.Format(s_inv, "  {0}{1}", channel.ShortChannelId ?? channel.ChannelId,
                                            channel.ShortChannelId is null ? string.Empty : $" ({channel.ChannelId})"));
            if (channel.Counterparty is { } peer)
                _output.WriteLine(string.Format(s_inv, "    Peer:            {0}", peer));
            _output.WriteLine(string.Format(s_inv, "    Capacity:        {0}{1}",
                                            channel.CapacityMsat is { } capacity ? Amount(capacity) : "unknown",
                                            channel.IsInitiator is { } initiator
                                                ? initiator ? ", opened by us" : ", opened by the peer"
                                                : string.Empty));
            _output.WriteLine(string.Format(s_inv, "    Open:            {0} to {1}", OpenedAt(channel),
                                            channel.ClosedAtUnixMilliseconds is null
                                                ? "now"
                                                : Time(channel.ClosedAtUnixMilliseconds)));
            _output.WriteLine(string.Format(s_inv, "    Routing earned:  in {0} ({1} fwd), out {2} ({3} fwd)",
                                            Amount(channel.RoutingInMsat), channel.ForwardsIn,
                                            Amount(channel.RoutingOutMsat), channel.ForwardsOut));
            _output.WriteLine(string.Format(s_inv, "    Payments:        sent {0} ({1}), received {2} ({3}), route fees "
                                                 + "{4}", Amount(channel.PaymentsSentMsat), channel.PaymentsSent,
                                            Amount(channel.PaymentsReceivedMsat), channel.PaymentsReceived,
                                            Amount(channel.RoutingFeesPaidMsat)));
            if (channel.RebalancedOutMsat != 0 || channel.RebalanceCostMsat != 0)
                _output.WriteLine(string.Format(s_inv, "    Rebalance:       {0} out, cost {1}",
                                                Amount(channel.RebalancedOutMsat), Amount(channel.RebalanceCostMsat)));
            if (channel.PushMsat != 0)
                _output.WriteLine(string.Format(s_inv, "    Push:            {0}", Amount(channel.PushMsat)));
            _output.WriteLine(string.Format(s_inv, "    On-chain fees:   {0} (funding {1}, splice {2}, close {3}, "
                                                 + "commitment {4}, sweep {5}, cpfp {6})",
                                            Amount(channel.OnchainFeesMsat), channel.FundingFeeMsat,
                                            channel.SpliceFeeMsat, channel.CloseFeeMsat, channel.CommitmentFeeMsat,
                                            channel.SweepFeeMsat, channel.CpfpFeeMsat));
            if (channel.OnchainLossMsat != 0)
                _output.WriteLine(string.Format(s_inv, "    On-chain loss:   {0}", Amount(channel.OnchainLossMsat)));
            // NL-625: the yield next to Net is Net's; the routing yield is labelled as before costs
            _output.WriteLine(string.Format(s_inv, "    Net:             {0}; net yield {1}, annualized {2}",
                                            Amount(channel.NetMsat), Percent(channel.NetYieldOnCapacity),
                                            Percent(channel.NetAnnualizedYield)));
            _output.WriteLine(string.Format(s_inv, "    Routing yield:   {0}, annualized {1} (routing out only, before "
                                                 + "costs)", Percent(channel.YieldOnCapacity),
                                            Percent(channel.AnnualizedYield)));
        }
    }

    private void PrintPeers(AccountingReportIpcResponse item, List<AccountingPeerIpcResponse> peers)
    {
        _output.WriteLine(string.Format(s_inv, "Peers {0} (books through #{1}): {2}", Period(item),
                                        item.ProjectedLedgerSeq, peers.Count));
        foreach (var peer in peers)
        {
            _output.WriteLine(string.Format(s_inv, "  {0}: {1} channel(s), capacity {2}",
                                            peer.Counterparty ?? "(unknown peer)", peer.ChannelCount,
                                            Amount(peer.CapacityMsat)));
            _output.WriteLine(string.Format(s_inv, "    Routing earned:  in {0} ({1} fwd), out {2} ({3} fwd)",
                                            Amount(peer.RoutingInMsat), peer.ForwardsIn, Amount(peer.RoutingOutMsat),
                                            peer.ForwardsOut));
            _output.WriteLine(string.Format(s_inv, "    Payments:        sent {0}, received {1}, route fees {2}",
                                            Amount(peer.PaymentsSentMsat), Amount(peer.PaymentsReceivedMsat),
                                            Amount(peer.RoutingFeesPaidMsat)));
            _output.WriteLine(string.Format(s_inv, "    Costs:           rebalance {0}, on-chain fees {1}, loss {2}",
                                            Amount(peer.RebalanceCostMsat), Amount(peer.OnchainFeesMsat),
                                            Amount(peer.OnchainLossMsat)));
            _output.WriteLine(string.Format(s_inv, "    Net:             {0}", Amount(peer.NetMsat)));
        }
    }

    private void PrintFees(AccountingReportIpcResponse item, AccountingFeesIpcResponse fees)
    {
        _output.WriteLine(string.Format(s_inv, "Fees {0} (books through #{1})", Period(item), item.ProjectedLedgerSeq));
        Section("Fee accounts", fees.Accounts, fees.TotalMsat);
        _output.WriteLine(string.Format(s_inv, "  Sweep fee bumps (already in the sweep fees): {0}, {1}",
                                        fees.SweepFeeBumps.Count, Amount(fees.SweepFeeBumpTotalMsat)));
        foreach (var bump in fees.SweepFeeBumps)
            _output.WriteLine(string.Format(s_inv, "    #{0}  {1}  {2}  {3}{4}", bump.LedgerSeq,
                                            Time(bump.OccurredAtUnixMilliseconds), bump.TxId ?? "-",
                                            Amount(bump.FeeMsat),
                                            bump.Purpose is null ? string.Empty : $"  {bump.Purpose}"));
    }

    private void PrintRegister(AccountingReportIpcResponse item, AccountingRegisterIpcResponse register)
    {
        _output.WriteLine(string.Format(s_inv, "Register (books through #{0}):", item.ProjectedLedgerSeq));
        if (register.Entries.Count == 0)
            _output.WriteLine("  None");

        foreach (var entry in register.Entries)
        {
            _output.WriteLine(string.Format(s_inv, "  #{0}  {1}  {2}  {3}", entry.LedgerSeq,
                                            Time(entry.OccurredAtUnixMilliseconds), entry.KindName, entry.EventKey));
            if (entry.Postings.Count == 0)
                _output.WriteLine("      (no postings)");
            foreach (var posting in entry.Postings)
                _output.WriteLine(string.Format(s_inv, "      {0}{1}", posting.Name.PadRight(NameWidth),
                                                Amount(posting.AmountMsat)));
            if (entry.Note is { } note)
                _output.WriteLine(string.Format(s_inv, "      ; {0}", note));
        }

        _output.WriteLine(string.Format(s_inv, "Next page: --after {0}{1}", register.NextAfter,
                                        register.HasMore ? string.Empty : " (nothing more yet)"));
    }

    private void Section(string title, List<AccountingAccountIpcResponse> accounts, long totalMsat)
    {
        _output.WriteLine(string.Format(s_inv, "  {0}:", title));
        foreach (var account in accounts)
            _output.WriteLine(string.Format(s_inv, "    {0}{1}", account.Name.PadRight(NameWidth),
                                            Amount(account.AmountMsat)));
        _output.WriteLine(string.Format(s_inv, "    {0}{1}", "Total".PadRight(NameWidth), Amount(totalMsat)));
    }

    // NL-627: a past balance names the last entry it counts, not only the books' present tip
    private static string BalanceSheetEntries(AccountingReportIpcResponse item,
                                              AccountingBalanceSheetIpcResponse sheet)
    {
        if (item.UntilUnixMilliseconds is null)
            return string.Format(s_inv, "books through #{0}", item.ProjectedLedgerSeq);

        return sheet.LastLedgerSeqAt switch
        {
            null => string.Format(s_inv, "books now through #{0}", item.ProjectedLedgerSeq),
            0 => string.Format(s_inv, "no entries before it; books through #{0}", item.ProjectedLedgerSeq),
            { } last => string.Format(s_inv, "last entry #{0}; books through #{1}", last, item.ProjectedLedgerSeq)
        };
    }

    // NL-623: a channel open before the feed began is dated by its funding block, or said to be open since the feed
    // began, never at the cutover
    internal static string OpenedAt(AccountingChannelIpcResponse channel)
    {
        var block = channel.OpenedAtBlockHeight is { } height
                        ? string.Format(s_inv, "block {0}", height)
                        : null;
        if (channel.OpenedAtUnixMilliseconds is { } opened)
            return block is null ? Time(opened) : string.Format(s_inv, "{0} ({1})", Time(opened), block);

        if (channel.TrackedSinceUnixMilliseconds is { } tracked)
            return string.Format(s_inv, "before the feed began ({0}{1})", Time(tracked),
                                 block is null ? string.Empty : ", funded at " + block);

        return block ?? "-";
    }

    private static string Period(AccountingReportIpcResponse item) =>
        string.Format(s_inv, "from {0} to {1}",
                      item.SinceUnixMilliseconds is null ? "the start" : Time(item.SinceUnixMilliseconds),
                      item.UntilUnixMilliseconds is null ? "now" : Time(item.UntilUnixMilliseconds));

    private static string Percent(double? fraction) =>
        fraction is { } value ? (value * 100).ToString("0.####", s_inv) + " %" : "-";
}

/// <summary>
/// Prints <c>accounting reconcile|rebuild|verify</c> (NL-602 A2).
/// </summary>
public sealed class AccountingAdminPrinter : IPrinter<AccountingAdminIpcResponse>
{
    private static readonly CultureInfo s_inv = CultureInfo.InvariantCulture;

    private readonly TextWriter _output;

    public AccountingAdminPrinter(TextWriter? output = null)
    {
        _output = output ?? Console.Out;
    }

    public void Print(AccountingAdminIpcResponse item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (item.Reconcile is { } reconcile)
        {
            // NL-621: amounts that transactions in flight explain are outstanding, never DRIFT
            var outstanding = reconcile.Lines.Sum(l => l.OutstandingMsat);
            _output.WriteLine(string.Format(s_inv, "Reconcile at {0}, block {1}, books through #{2}: {3}",
                                            AccountingReportPrinter.Time(reconcile.TakenAtUnixMilliseconds),
                                            reconcile.BlockHeight, reconcile.LedgerSeq,
                                            !reconcile.IsClean ? "DRIFT"
                                            : outstanding != 0 ? $"clean, {outstanding} msat outstanding (in flight)"
                                            : "clean"));
            foreach (var line in reconcile.Lines)
                _output.WriteLine(string.Format(s_inv, "  {0}  books {1} msat, node {2} msat, {3}drift {4} msat{5}",
                                                line.Name.PadRight(32), line.BooksMsat, line.NodeMsat,
                                                line.OutstandingMsat == 0
                                                    ? string.Empty
                                                    : $"outstanding {line.OutstandingMsat} msat, ",
                                                line.DriftMsat, line.Note is null ? string.Empty : $" ({line.Note})"));
        }

        if (item.RebuiltEntries is { } rebuilt)
            _output.WriteLine(string.Format(s_inv, "Rebuilt the books: {0} entries projected from the feed", rebuilt));

        if (item.Verification is { } verification)
        {
            if (verification.IsIntact)
                _output.WriteLine(string.Format(s_inv, "Hash chain OK: {0} events verified, tip #{1} {2}",
                                                verification.VerifiedCount, verification.TipLedgerSeq,
                                                verification.TipHash));
            else
                _output.WriteLine(string.Format(s_inv, "Hash chain BROKEN at #{0}: {1} ({2} events verified before it, "
                                                     + "last good #{3} {4})", verification.BreakLedgerSeq,
                                                verification.BreakReason, verification.VerifiedCount,
                                                verification.TipLedgerSeq, verification.TipHash));
        }

        if (item.Classify is { } classify)
            new AccountingClassifyPrinter(_output).Print(classify);

        if (item.PeriodVerifications is { } closes)
        {
            if (closes.Count == 0)
                _output.WriteLine("Period closes: none");
            foreach (var close in closes)
                _output.WriteLine(close.IsIntact
                                      ? string.Format(s_inv, "Close {0} OK: digest, signature and chain hash verified "
                                                           + "({1} entries, {2} reliefs, {3} open lots)",
                                                      close.PeriodId, close.EntryCount, close.ReliefCount,
                                                      close.OpenLotCount)
                                      : string.Format(s_inv, "Close {0} BROKEN: {1}", close.PeriodId, close.Problem));
        }

        if (item.Periods is { } periods)
        {
            if (periods.Count == 0)
                _output.WriteLine("No accounting periods closed yet");
            foreach (var period in periods)
                _output.WriteLine(string.Format(s_inv, "{0}  {1}  {2} to {3}  {4}  through #{5}  digest {6}{7}",
                                                period.PeriodId.PadRight(22), State(period.State),
                                                Day(period.StartUnixSeconds), LastDay(period.EndUnixSeconds),
                                                period.ClosedAtUnixMilliseconds is { } at
                                                    ? "closed " + AccountingReportPrinter.Time(at)
                                                    : "open",
                                                period.LastLedgerSeq, period.Digest ?? "-",
                                                period.Forced ? "  FORCED" : string.Empty));
        }

        if (item.Period is { } shown)
            PrintPeriod(shown);

        if (item.LotImport is { } lotImport)
        {
            _output.WriteLine(string.Format(s_inv, "Imported {0} lot(s): {1} msat for {2} {3} in place of the opening "
                                                 + "balances' lots ({4} msat){5}", lotImport.Imported,
                                            lotImport.ImportedMsat, lotImport.ImportedCost, lotImport.Currency,
                                            lotImport.OpeningMsat,
                                            lotImport.ReplacedLots > 0
                                                ? $"; {lotImport.ReplacedLots} lot(s) of an earlier import replaced"
                                                : string.Empty));
            if (lotImport.AdjustedMsat != 0)
                _output.WriteLine(string.Format(s_inv, "The last lot took {0} msat so the lots hold the opening "
                                                     + "balances exactly", lotImport.AdjustedMsat));
            _output.WriteLine(string.Format(s_inv, "Rebuilt the financial book: {0} entries projected",
                                            lotImport.ProjectedEntries));
        }
    }

    private void PrintPeriod(AccountingPeriodIpcResponse period)
    {
        _output.WriteLine(string.Format(s_inv, "Period {0} ({1}): {2} to {3}{4}", period.PeriodId,
                                        State(period.State), Day(period.StartUnixSeconds),
                                        LastDay(period.EndUnixSeconds), period.Forced ? ", closed with --force" : ""));
        if (period.ClosedAtUnixMilliseconds is { } closedAt)
            _output.WriteLine(string.Format(s_inv, "  closed at      {0}", AccountingReportPrinter.Time(closedAt)));
        _output.WriteLine(string.Format(s_inv, "  through        #{0} (chain hash {1})", period.LastLedgerSeq,
                                        period.ChainHash ?? "-"));
        _output.WriteLine(string.Format(s_inv, "  digest         {0}", period.Digest ?? "-"));
        _output.WriteLine(string.Format(s_inv, "  signature      {0}", period.Signature ?? "-"));
        _output.WriteLine(string.Format(s_inv, "  node id        {0}", period.NodeId ?? "-"));
        if (period.EntryCount is { } entries)
            _output.WriteLine(string.Format(s_inv, "  covers         {0} entries, {1} reliefs, {2} lots open at its end",
                                            entries, period.ReliefCount ?? 0, period.OpenLotCount ?? 0));
        if (period.UnvaluedPostings is > 0 || period.UnclassifiedEntries is > 0)
            _output.WriteLine(string.Format(s_inv, "  left open      {0} unvalued postings, {1} unclassified entries",
                                            period.UnvaluedPostings ?? 0, period.UnclassifiedEntries ?? 0));
        if (period.ReplayAfterLedgerSeq is { } replayAfter)
            _output.WriteLine(string.Format(s_inv, "  rebuild from   after #{0}", replayAfter));
        if (period.Balances is not { Count: > 0 } balances)
            return;

        _output.WriteLine("  balances at the close:");
        foreach (var balance in balances)
            _output.WriteLine(string.Format(s_inv, "    {0}  {1} msat  {2}",
                                            (balance.AccountName ?? balance.Account.ToString(s_inv)).PadRight(40),
                                            balance.BalanceMsat.ToString(s_inv).PadLeft(20), balance.FiatAmount));
    }

    private static string State(int state) => state == 1 ? "closed" : "open";

    /// <summary>The last day of a period that ends (exclusive) at <paramref name="endUnixSeconds"/>.</summary>
    private static string LastDay(long endUnixSeconds) => Day(endUnixSeconds - 1);

    private static string Day(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString("yyyy-MM-dd", s_inv);
}