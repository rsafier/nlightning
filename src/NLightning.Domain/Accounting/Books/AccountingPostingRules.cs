using System.Globalization;

namespace NLightning.Domain.Accounting.Books;

using Constants;
using Enums;
using Models;
using Services;

/// <summary>
/// The operational posting rules (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.1 "Rules"): one sealed event in, the
/// postings of its entry out. Pure; the result always sums to zero.
/// </summary>
/// <remarks>
/// <para>Every value that moves between the wallet and a channel (a funding, a splice, a mutual close) or out of a
/// force close's pending bucket into the wallet posts that side to <see cref="AccountRole.Clearing"/>; only the wallet
/// events (<see cref="AccountingEventKind.WalletReceived"/>, <see cref="AccountingEventKind.WalletOutputSpent"/>) post to
/// <see cref="AccountRole.Wallet"/>, so the clearing account nets to zero once both sides of a transaction are
/// confirmed. The channel events share one convention (the writers' class remarks): <c>AmountMsat</c> is the change of
/// the channel bucket, <c>FeeMsat</c> what we paid, and the wallet side moves by <c>-(AmountMsat + FeeMsat)</c>.</para>
/// <para>Rules beyond the plan's table: a resolution merged into our anchor CPFP child moves the output's whole value to
/// the clearing account (the wallet events and the CPFP fee book what the child did with it), and a value it adds to the
/// pending bucket's (an output that was not counted) is a gain like any other uncounted output we claim; a resolution
/// or a force close without its flow details (an older writer) falls back to its amount against the pending bucket
/// and says so in the note; the reversal of a wallet fact the feed never recorded
/// (<see cref="AccountingConfirmations.UnrecordedDetail"/>) moves the wallet against the opening balances, which held
/// it.</para>
/// </remarks>
public static class AccountingPostingRules
{
    /// <summary>
    /// The postings of <paramref name="accountingEvent"/>. <paramref name="findEntry"/> returns the entry of an earlier
    /// event key (for a <see cref="AccountingEventKind.Reversal"/>), or null.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rules would not balance (a bug).</exception>
    public static IReadOnlyList<AccountingPosting> Post(AccountingEventModel accountingEvent,
                                                        Func<string, AccountingEntry?> findEntry) =>
        Evaluate(accountingEvent, findEntry).Postings;

    /// <summary>
    /// The postings of <paramref name="accountingEvent"/> with the note of its entry (why the rules fell back, or why
    /// nothing was posted). Lines of the same account are merged and zero lines dropped.
    /// </summary>
    /// <exception cref="InvalidOperationException">The rules would not balance (a bug).</exception>
    public static AccountingPostingResult Evaluate(AccountingEventModel accountingEvent,
                                                   Func<string, AccountingEntry?> findEntry)
    {
        ArgumentNullException.ThrowIfNull(accountingEvent);
        ArgumentNullException.ThrowIfNull(findEntry);

        var lines = new Lines();
        string? note;
        if (IsSet(accountingEvent, AccountingDetailKeys.Memo))
            note = "memo: statistics only";
        else
            note = accountingEvent.Kind switch
            {
                AccountingEventKind.InvoiceSettled => PostInvoiceSettled(accountingEvent, lines),
                AccountingEventKind.PaymentSucceeded => PostPaymentSucceeded(accountingEvent, lines),
                AccountingEventKind.PaymentFailed => null,
                AccountingEventKind.ForwardSettled => PostForwardSettled(accountingEvent, lines),
                AccountingEventKind.TrampolineRelaySettled => PostTrampolineRelaySettled(accountingEvent, lines),
                AccountingEventKind.ForwardLostOnchain or AccountingEventKind.InvoiceLostOnchain =>
                    PostForwardLostOnchain(accountingEvent, lines),
                AccountingEventKind.ChannelFunded => PostWalletToChannel(accountingEvent, AccountRole.FeeFunding, lines),
                AccountingEventKind.PushSent or AccountingEventKind.PushReceived => PostPush(accountingEvent, lines),
                AccountingEventKind.SpliceLocked => PostWalletToChannel(accountingEvent, AccountRole.FeeSplice, lines),
                AccountingEventKind.ChannelClosedMutual =>
                    PostWalletToChannel(accountingEvent, AccountRole.FeeClose, lines),
                AccountingEventKind.ChannelForceClosed => PostForceClosed(accountingEvent, lines),
                AccountingEventKind.OutputResolved or AccountingEventKind.PenaltyClaimed
                    or AccountingEventKind.BreachLoss => PostResolution(accountingEvent, lines),
                AccountingEventKind.AnchorCpfpFee => PostAnchorCpfpFee(accountingEvent, lines),
                AccountingEventKind.SweepFeeBump => null,
                AccountingEventKind.LiquidityFeePaid or AccountingEventKind.LiquidityFeeEarned =>
                    PostLiquidityFee(accountingEvent, lines),
                AccountingEventKind.WalletReceived => PostWalletReceived(accountingEvent, lines),
                AccountingEventKind.WalletOutputSpent => PostWalletOutputSpent(accountingEvent, lines),
                AccountingEventKind.WalletSent => PostWalletSent(accountingEvent, lines),
                AccountingEventKind.OpeningBalance => PostOpeningBalance(accountingEvent, lines),
                AccountingEventKind.Reversal => PostReversal(accountingEvent, findEntry, lines),
                _ => $"unknown kind {(int)accountingEvent.Kind}: nothing posted"
            };

        var postings = lines.ToPostings();
        var sum = 0L;
        foreach (var posting in postings)
            sum = checked(sum + posting.AmountMsat);
        if (sum != 0)
            throw new InvalidOperationException(
                $"The postings of {accountingEvent.Kind} {accountingEvent.EventKey} sum to {sum} msat, not 0");

        return new AccountingPostingResult(postings, note);
    }

    /// <summary>A short human description of <paramref name="accountingEvent"/> for the register and the exports.</summary>
    public static string Describe(AccountingEventModel accountingEvent)
    {
        ArgumentNullException.ThrowIfNull(accountingEvent);

        var description = Text(accountingEvent, AccountingDetailKeys.Description);
        var kind = Text(accountingEvent, AccountingDetailKeys.Kind);
        return accountingEvent.Kind switch
        {
            AccountingEventKind.InvoiceSettled => WithDetail(IsSet(accountingEvent, AccountingDetailKeys.SelfPayment)
                                                                 ? "Rebalance received"
                                                                 : kind == "keysend"
                                                                     ? "Keysend received"
                                                                     : "Invoice settled", description),
            AccountingEventKind.PaymentSucceeded => WithDetail(IsSet(accountingEvent, AccountingDetailKeys.SelfPayment)
                                                                   ? "Rebalance"
                                                                   : kind == "keysend"
                                                                       ? "Keysend sent"
                                                                       : "Payment sent", description),
            AccountingEventKind.PaymentFailed => WithDetail("Payment failed",
                                                            Text(accountingEvent, AccountingDetailKeys.Reason)),
            AccountingEventKind.ForwardSettled => WithDetail("Forward settled", Route(accountingEvent)),
            AccountingEventKind.TrampolineRelaySettled => WithDetail("Trampoline relay settled", Route(accountingEvent)),
            AccountingEventKind.ForwardLostOnchain => WithDetail("Forward lost on chain", Route(accountingEvent)),
            AccountingEventKind.InvoiceLostOnchain => WithDetail("Invoice payment lost on chain", description),
            AccountingEventKind.ChannelFunded => IsSet(accountingEvent, AccountingDetailKeys.DualFunded)
                                                     ? "Channel funded (dual-funded)"
                                                     : "Channel funded",
            AccountingEventKind.PushSent => "Push sent at the open",
            AccountingEventKind.PushReceived => "Push received at the open",
            AccountingEventKind.SpliceLocked => accountingEvent.AmountMsat < 0 ? "Splice out locked" : "Splice in locked",
            AccountingEventKind.ChannelClosedMutual => "Channel closed (mutual)",
            AccountingEventKind.ChannelForceClosed =>
                WithDetail("Channel force-closed", Text(accountingEvent, AccountingDetailKeys.CloseKind)),
            AccountingEventKind.OutputResolved =>
                Text(accountingEvent, AccountingDetailKeys.ResolvedBy) == AccountingDetailKeys.ResolvedByIgnored
                    ? WithDetail("Output given up", Text(accountingEvent, AccountingDetailKeys.Descriptor))
                    : WithDetail("Output resolved", Resolution(accountingEvent)),
            AccountingEventKind.PenaltyClaimed => WithDetail("Penalty claimed",
                                                             Text(accountingEvent, AccountingDetailKeys.Descriptor)),
            AccountingEventKind.BreachLoss => WithDetail("Breach loss",
                                                         Text(accountingEvent, AccountingDetailKeys.Descriptor)),
            AccountingEventKind.AnchorCpfpFee => "Anchor CPFP fee",
            AccountingEventKind.SweepFeeBump => "Sweep fee bump",
            AccountingEventKind.LiquidityFeePaid => WithDetail("Liquidity fee paid", LiquidityPurchase(accountingEvent)),
            AccountingEventKind.LiquidityFeeEarned =>
                WithDetail("Liquidity fee earned", LiquidityPurchase(accountingEvent)),
            AccountingEventKind.WalletReceived =>
                Text(accountingEvent, AccountingDetailKeys.Source) == AccountingDetailKeys.ExternalSource
                    ? "Deposit"
                    : WithDetail("Wallet output received", WalletSource(accountingEvent)),
            AccountingEventKind.WalletOutputSpent => WithDetail("Wallet output spent", WalletSource(accountingEvent)),
            AccountingEventKind.WalletSent => WithDetail("Withdrawal",
                                                         Text(accountingEvent, AccountingDetailKeys.Destination)),
            AccountingEventKind.OpeningBalance => WithDetail("Opening balance", OpeningBucketName(accountingEvent)),
            AccountingEventKind.Reversal =>
                WithDetail($"Reversal of {Text(accountingEvent, AccountingConfirmations.OriginalKindDetail) ?? "an event"}",
                           Text(accountingEvent, AccountingConfirmations.ReversesDetail)),
            _ => accountingEvent.Kind.ToString()
        };
    }

    #region Off-chain

    /// <summary>Dr Channels a; Cr Received a. Our own invoice paid by ourselves (a rebalance's incoming side, flagged
    /// <c>selfPayment</c>, NL-609) is no income: Cr Rebalance a, which the payment's Dr Rebalance (a + fee) offsets, so
    /// the rebalance account keeps only the route fee.</summary>
    private static string? PostInvoiceSettled(AccountingEventModel e, Lines lines)
    {
        lines.Add(AccountRole.Channels, e.AmountMsat);
        lines.Add(IsSet(e, AccountingDetailKeys.SelfPayment) ? AccountRole.Rebalance : AccountRole.Received,
                  -e.AmountMsat);
        return null;
    }

    /// <summary>AmountMsat = −(amount + fee): Cr Channels (a + fee); Dr Sent a; Dr RoutingFees fee. A self-payment (a
    /// rebalance, NL-609): Cr Channels (a + fee); Dr Rebalance (a + fee), the amount coming back through the incoming
    /// side's <c>InvoiceSettled</c> (Cr Rebalance a), so only the route fee stays an expense.</summary>
    private static string? PostPaymentSucceeded(AccountingEventModel e, Lines lines)
    {
        var fee = e.FeeMsat;
        var amount = checked(-e.AmountMsat - fee);
        lines.Add(AccountRole.Channels, e.AmountMsat);
        if (IsSet(e, AccountingDetailKeys.SelfPayment))
        {
            lines.Add(AccountRole.Rebalance, checked(amount + fee));
            return null;
        }

        lines.Add(AccountRole.Sent, amount);
        lines.Add(AccountRole.RoutingFees, fee);
        return null;
    }

    private static string? PostForwardSettled(AccountingEventModel e, Lines lines)
    {
        lines.Add(AccountRole.Channels, e.AmountMsat);
        lines.Add(AccountRole.Routing, -e.AmountMsat);
        return null;
    }

    /// <summary>
    /// A trampoline relay (NL-875), AmountMsat = what the incoming parts brought minus what the outgoing payment took: Dr
    /// Channels a; Cr Routing a, the relay's fee, as a forward's. A relay that cost more than it brought (negative) is an
    /// expense of routing: Cr Channels |a|; Dr RoutingFees |a|.
    /// </summary>
    private static string? PostTrampolineRelaySettled(AccountingEventModel e, Lines lines)
    {
        lines.Add(AccountRole.Channels, e.AmountMsat);
        lines.Add(e.AmountMsat >= 0 ? AccountRole.Routing : AccountRole.RoutingFees, -e.AmountMsat);
        return null;
    }

    /// <summary>AmountMsat = −v: Cr Channels v; Dr LossOnchain v (a gain the other way round). Also the rule of an
    /// <see cref="AccountingEventKind.InvoiceLostOnchain"/> (NL-688): the HTLC amount its <c>InvoiceSettled</c> left in
    /// the channels, which the close never took out (an incoming HTLC is not in our balance at the close).</summary>
    private static string? PostForwardLostOnchain(AccountingEventModel e, Lines lines)
    {
        lines.Add(AccountRole.Channels, e.AmountMsat);
        lines.Add(e.AmountMsat <= 0 ? AccountRole.LossOnchain : AccountRole.OnchainGain, -e.AmountMsat);
        return null;
    }

    #endregion

    #region Channels

    /// <summary>
    /// A funding, a splice or a mutual close: Dr Channels AmountMsat; Dr <paramref name="feeAccount"/> FeeMsat; Cr
    /// Clearing (AmountMsat + FeeMsat). A mutual close (AmountMsat = −balance) so credits the channel and debits the
    /// clearing account with our closing output.
    /// </summary>
    private static string? PostWalletToChannel(AccountingEventModel e, AccountRole feeAccount, Lines lines)
    {
        lines.Add(AccountRole.Channels, e.AmountMsat);
        lines.Add(feeAccount, e.FeeMsat);
        lines.Add(AccountRole.Clearing, checked(-(e.AmountMsat + e.FeeMsat)));
        return null;
    }

    private static string? PostPush(AccountingEventModel e, Lines lines)
    {
        lines.Add(AccountRole.Channels, e.AmountMsat);
        lines.Add(e.Kind == AccountingEventKind.PushSent ? AccountRole.PushSent : AccountRole.PushReceived,
                  -e.AmountMsat);
        return null;
    }

    /// <summary>
    /// A liquidity purchase (liquidity ads, NL-850): AmountMsat is the channel's change, -fee when we bought (Cr Channels
    /// fee; Dr LiquidityFees fee) and +fee when we sold (Dr Channels fee; Cr LiquidityIncome fee). No on-chain value
    /// moves: the fee changed hands in the commitment, and the funding's or splice's own event books our contribution
    /// without it.
    /// </summary>
    private static string? PostLiquidityFee(AccountingEventModel e, Lines lines)
    {
        lines.Add(AccountRole.Channels, e.AmountMsat);
        lines.Add(e.Kind == AccountingEventKind.LiquidityFeePaid ? AccountRole.LiquidityFees : AccountRole.LiquidityIncome,
                  -e.AmountMsat);
        return null;
    }

    #endregion

    #region Force close and resolution

    /// <summary>
    /// AmountMsat = −B: Cr Channels B; Dr Pending pendingMsat; Dr FeeCommitment fee; Dr LossOnchain lostMsat (Cr
    /// OnchainGain when negative). A close of the opening balances posts nothing.
    /// </summary>
    private static string? PostForceClosed(AccountingEventModel e, Lines lines)
    {
        if (IsSet(e, AccountingDetailKeys.OpeningBalance))
            return "the close of an opening balance: its funds are in the opening balances";

        lines.Add(AccountRole.Channels, e.AmountMsat);
        lines.Add(AccountRole.FeeCommitment, e.FeeMsat);
        var pending = Msat(e, AccountingDetailKeys.PendingMsat);
        if (pending is null)
        {
            // An older writer: everything that is not the fee went to the pending bucket
            lines.Add(AccountRole.Pending, checked(-e.AmountMsat - e.FeeMsat));
            return "no pending flow in the details: the balance less the fee is taken as pending";
        }

        lines.Add(AccountRole.Pending, pending.Value);
        var lost = checked(-e.AmountMsat - e.FeeMsat - pending.Value);
        lines.Add(lost >= 0 ? AccountRole.LossOnchain : AccountRole.OnchainGain, lost);
        var stated = Msat(e, AccountingDetailKeys.LostMsat);
        return stated is not null && stated != lost
                   ? string.Create(CultureInfo.InvariantCulture,
                                   $"lostMsat {stated} differs from the balance less pending and fee ({lost})")
                   : null;
    }

    /// <summary>
    /// An output's resolution, written off or claimed: Cr Pending pendingOutMsat; Dr Pending pendingInMsat; Dr Clearing
    /// walletMsat; Dr FeeSweep fee; the difference d = debits − credits goes to the channels when an off-chain event
    /// already booked the HTLC's value (<see cref="AccountingDetailKeys.ValueBookedBy"/>), else to the on-chain gains
    /// (d &gt; 0) or losses (d &lt; 0). The part of the fee that wallet inputs paid
    /// (<see cref="AccountingDetailKeys.WalletFeeMsat"/>, our anchors HTLC transaction, NL-748) is not the output's:
    /// Dr FeeSweep; Cr Clearing, where the wallet events book those inputs and the change.
    /// With an off-chain owner, the HTLC's sub-satoshi part (<see cref="AccountingDetailKeys.HtlcRoundingMsat"/> r,
    /// NL-1007) moves too, since the off-chain events booked the HTLC's msat amount and the output holds only its
    /// satoshis: the commitment took r, which the close booked (in its fee when we fund, else its loss) for an HTLC we
    /// offered (Dr Channels r; Cr that account r: the payment or forward already spent it) and nothing booked for an
    /// HTLC the peer offered (Cr Channels r; Dr that account r).
    /// </summary>
    private static string? PostResolution(AccountingEventModel e, Lines lines)
    {
        var pendingOut = Msat(e, AccountingDetailKeys.PendingOutMsat);
        var pendingIn = Msat(e, AccountingDetailKeys.PendingInMsat);
        var wallet = Msat(e, AccountingDetailKeys.WalletMsat);
        string? note = null;
        if (pendingOut is null || pendingIn is null || wallet is null)
        {
            // An older writer (or unknown flows): the amount reached the wallet, or was lost, out of the pending bucket
            var reached = Math.Max(0, e.AmountMsat);
            pendingOut = checked(reached + e.FeeMsat + Math.Max(0, -e.AmountMsat));
            pendingIn = 0;
            wallet = reached;
            note = "no flows in the details: the amount is taken out of the pending bucket";
        }

        // NL-748: the wallet inputs' part of the fee is paid out of the clearing account, not out of the output
        var walletFee = Math.Clamp(Msat(e, AccountingDetailKeys.WalletFeeMsat) ?? 0, 0, Math.Max(0, e.FeeMsat));
        var fee = e.FeeMsat - walletFee;
        if (Text(e, AccountingDetailKeys.Note) == AccountingDetailKeys.MergedNote)
        {
            // The output's value went into our CPFP child: the wallet events and its fee book what it became
            var value = Msat(e, AccountingDetailKeys.ValueMsat) ?? pendingOut.Value;
            wallet = Math.Max(value, pendingOut.Value);
            fee = 0;
            note = "merged into a transaction with wallet inputs: its value is booked through the clearing account";
        }

        lines.Add(AccountRole.Pending, checked(pendingIn.Value - pendingOut.Value));
        lines.Add(AccountRole.Clearing, checked(wallet.Value - walletFee));
        lines.Add(AccountRole.FeeSweep, checked(fee + walletFee));

        var difference = checked(pendingIn.Value + wallet.Value + fee - pendingOut.Value);
        if (Text(e, AccountingDetailKeys.ValueBookedBy) is not null)
        {
            lines.Add(AccountRole.Channels, -difference);
            PostHtlcRounding(e, lines);
        }
        else
            lines.Add(difference >= 0 ? AccountRole.OnchainGain : AccountRole.LossOnchain, -difference);

        return note;
    }

    /// <summary>The sub-satoshi part of an HTLC an off-chain event owns (see <see cref="PostResolution"/>, NL-1007).
    /// </summary>
    private static void PostHtlcRounding(AccountingEventModel e, Lines lines)
    {
        var rounding = Msat(e, AccountingDetailKeys.HtlcRoundingMsat) ?? 0;
        if (rounding is <= 0 or >= 1_000)
            return;

        var account = IsSet(e, AccountingDetailKeys.Funder) ? AccountRole.FeeCommitment : AccountRole.LossOnchain;
        var offered = Text(e, AccountingDetailKeys.HtlcDirection) == AccountingDetailKeys.OfferedHtlc;
        lines.Add(AccountRole.Channels, offered ? rounding : -rounding);
        lines.Add(account, offered ? -rounding : rounding);
    }

    private static string? PostAnchorCpfpFee(AccountingEventModel e, Lines lines)
    {
        lines.Add(AccountRole.FeeCpfp, e.FeeMsat);
        lines.Add(AccountRole.Clearing, -e.FeeMsat);
        return null;
    }

    #endregion

    #region Wallet

    private static string? PostWalletReceived(AccountingEventModel e, Lines lines)
    {
        lines.Add(AccountRole.Wallet, e.AmountMsat);
        lines.Add(Text(e, AccountingDetailKeys.Source) == AccountingDetailKeys.ExternalSource
                      ? AccountRole.TransfersIn
                      : AccountRole.Clearing, -e.AmountMsat);
        return null;
    }

    /// <summary>AmountMsat = −v: Cr Wallet v; Dr Clearing v.</summary>
    private static string? PostWalletOutputSpent(AccountingEventModel e, Lines lines)
    {
        lines.Add(AccountRole.Wallet, e.AmountMsat);
        lines.Add(AccountRole.Clearing, -e.AmountMsat);
        return null;
    }

    /// <summary>AmountMsat = −x (what left to outputs that are not ours): Dr TransfersOut x; Dr FeeWithdraw fee; Cr
    /// Clearing (x + fee).</summary>
    private static string? PostWalletSent(AccountingEventModel e, Lines lines)
    {
        lines.Add(AccountRole.TransfersOut, -e.AmountMsat);
        lines.Add(AccountRole.FeeWithdraw, e.FeeMsat);
        lines.Add(AccountRole.Clearing, checked(e.AmountMsat - e.FeeMsat));
        return null;
    }

    #endregion

    #region Opening balances and reversals

    private static string? PostOpeningBalance(AccountingEventModel e, Lines lines)
    {
        var bucket = OpeningBucketName(e);
        if (bucket is null)
            return "an opening balance without a bucket: nothing posted";

        if (bucket == AccountingDetailKeys.OpeningCutoverBucket
         || bucket.StartsWith(AccountingDetailKeys.OpeningMemoBucket, StringComparison.Ordinal))
            return "an opening-balance marker: nothing posted";

        AccountRole? account = bucket switch
        {
            AccountingDetailKeys.ChannelBucket => AccountRole.Channels,
            AccountingDetailKeys.WalletBucket => AccountRole.Wallet,
            AccountingDetailKeys.PendingBucket or "pending" => AccountRole.Pending,
            _ when bucket.StartsWith(AccountingDetailKeys.OpeningChannelBucket, StringComparison.Ordinal) =>
                AccountRole.Channels,
            _ when bucket.StartsWith(AccountingDetailKeys.OpeningPendingBucket, StringComparison.Ordinal) =>
                AccountRole.Pending,
            _ => null
        };
        if (account is null)
            return $"an opening balance of an unknown bucket '{bucket}': nothing posted";

        lines.Add(account.Value, e.AmountMsat);
        lines.Add(AccountRole.Opening, -e.AmountMsat);
        return null;
    }

    /// <summary>The exact negation of the entry it reverses; nothing when that entry posted nothing or is unknown.</summary>
    private static string? PostReversal(AccountingEventModel e, Func<string, AccountingEntry?> findEntry, Lines lines)
    {
        var reverses = Text(e, AccountingConfirmations.ReversesDetail);
        if (reverses is null)
            return "a reversal that names no event: nothing posted";

        var original = findEntry(reverses);
        if (original is null)
        {
            if (!IsSet(e, AccountingConfirmations.UnrecordedDetail))
                return $"the reversed event {reverses} is not in the books: nothing posted";

            // A wallet fact from before the feed: the opening balances held it
            var originalKind = Text(e, AccountingConfirmations.OriginalKindDetail);
            if (originalKind is not (nameof(AccountingEventKind.WalletReceived)
                                     or nameof(AccountingEventKind.WalletOutputSpent)))
                return $"the reversal of {reverses}, which predates the feed: nothing posted";

            lines.Add(AccountRole.Wallet, e.AmountMsat);
            lines.Add(AccountRole.Opening, -e.AmountMsat);
            return $"the reversal of {reverses}, which predates the feed, moves the wallet against the opening balances";
        }

        foreach (var posting in original.Postings)
            lines.Add(posting.Account, checked(-posting.AmountMsat));
        return null;
    }

    #endregion

    #region Details

    private static string? Text(AccountingEventModel e, string key) =>
        e.Details.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value) ? value : null;

    private static bool IsSet(AccountingEventModel e, string key) =>
        string.Equals(Text(e, key), AccountingDetailKeys.True, StringComparison.OrdinalIgnoreCase);

    private static long? Msat(AccountingEventModel e, string key) =>
        Text(e, key) is { } text && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture,
                                                  out var value)
            ? value
            : null;

    /// <summary>The bucket of an opening balance: its detail, else the part of its key after <c>open:</c>.</summary>
    private static string? OpeningBucketName(AccountingEventModel e)
    {
        if (Text(e, AccountingDetailKeys.Bucket) is { } bucket)
            return bucket;

        return e.EventKey.StartsWith(AccountingDetailKeys.OpeningKeyPrefix, StringComparison.Ordinal)
                   ? e.EventKey[AccountingDetailKeys.OpeningKeyPrefix.Length..]
                   : null;
    }

    private static string? Route(AccountingEventModel e)
    {
        var incoming = Text(e, AccountingDetailKeys.IncomingScid);
        var outgoing = Text(e, AccountingDetailKeys.OutgoingScid);
        return incoming is null && outgoing is null ? null : $"{incoming ?? "?"} -> {outgoing ?? "?"}";
    }

    private static string? Resolution(AccountingEventModel e)
    {
        var descriptor = Text(e, AccountingDetailKeys.Descriptor);
        var resolvedBy = Text(e, AccountingDetailKeys.ResolvedBy);
        return resolvedBy is null ? descriptor : $"{descriptor ?? "output"} by {resolvedBy}";
    }

    private static string? WalletSource(AccountingEventModel e)
    {
        var source = Text(e, AccountingDetailKeys.Source);
        var purpose = Text(e, AccountingDetailKeys.Purpose);
        return purpose is null ? source : $"{source ?? "?"}, {purpose}";
    }

    private static string? LiquidityPurchase(AccountingEventModel e)
    {
        var kind = Text(e, AccountingDetailKeys.Kind);
        var requested = Text(e, AccountingDetailKeys.RequestedSat);
        return requested is null ? kind : $"{kind ?? "purchase"}, {requested} sat requested";
    }

    private static string WithDetail(string text, string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? text : $"{text}: {detail}";

    #endregion

    /// <summary>The lines of one entry: one per account, in the order they were first added.</summary>
    private sealed class Lines
    {
        private readonly List<AccountRole> _order = [];
        private readonly Dictionary<AccountRole, long> _amounts = [];

        public void Add(AccountRole account, long amountMsat)
        {
            if (_amounts.TryGetValue(account, out var current))
            {
                _amounts[account] = checked(current + amountMsat);
                return;
            }

            _order.Add(account);
            _amounts[account] = amountMsat;
        }

        public IReadOnlyList<AccountingPosting> ToPostings() =>
            _order.Where(a => _amounts[a] != 0).Select(a => new AccountingPosting(a, _amounts[a])).ToList();
    }
}