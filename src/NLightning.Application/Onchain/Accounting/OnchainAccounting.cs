using System.Globalization;

namespace NLightning.Application.Onchain.Accounting;

using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;

/// <summary>
/// The accounting events of a force close and of its on-chain resolution (NL-602 A1-T2, plan
/// <c>docs/agents/ACCOUNTING_PLAN.md</c> §4: <see cref="AccountingEventKind.ChannelForceClosed"/>,
/// <see cref="AccountingEventKind.OutputResolved"/>, <see cref="AccountingEventKind.PenaltyClaimed"/>,
/// <see cref="AccountingEventKind.BreachLoss"/> and their <see cref="AccountingEventKind.Reversal"/>s).
/// </summary>
/// <remarks>
/// <para><b>Buckets.</b> A force close moves our channel balance out of the channel bucket into the
/// <see cref="PendingBucket"/> of the close; every output row then takes its share out of that bucket again, into the
/// wallet (a sweep, claim or penalty of ours), into a new pending output (our HTLC transaction's second-level output) or
/// as a loss (the peer took it, or it was given up). Every event states its bucket flows in its details
/// (<see cref="PendingOutKey"/>, <see cref="PendingInKey"/>, <see cref="WalletKey"/>), so once every output of the close
/// is resolved (or given up) the pending bucket of the close nets to zero:
/// <c>close.pendingMsat + Σ (pendingInMsat - pendingOutMsat) = 0</c>.</para>
/// <para><b>What is pending.</b> An output's value is in the pending bucket ("counted") when it was ours per the
/// commitment that confirmed: our main output (<c>to_local</c> on ours, <c>to_remote</c> on theirs, also on a revoked
/// one), the HTLC outputs we offered, our anchor when we funded the channel (its 330 sat came out of our balance), and
/// later the second-level output of our HTLC transaction (its value moved there from the HTLC output). The peer's
/// offered HTLCs, the outputs of a revoked commitment that were the peer's, the second level of the peer's HTLC
/// transactions and other rows a resolver added after the close (the outputs of a commitment it could not map) are not
/// counted: claiming them is a gain (<see cref="AccountingEventKind.PenaltyClaimed"/> for a revoked output), the peer
/// taking them changes nothing of ours (a <see cref="AccountingEventKind.BreachLoss"/> of 0 for a revoked output; every
/// resolution event carries the output's value as <see cref="ValueKey"/>). The close event lists the counted outputs (<see cref="CountedVoutsKey"/>), and the resolution events read
/// them from it, so a close recorded before the feed (no close event) counts nothing.</para>
/// <para><b>The close.</b> <c>AmountMsat</c> = −B, B = our balance per the commitment that confirmed (our net balance
/// plus the HTLCs we offered, trimmed ones included), except for a revoked commitment, where B is our latest local
/// commitment's balance (what the books hold, NL-616; the revoked state's is <see cref="RevokedStateBalanceKey"/>); for
/// a commitment we cannot rebuild (a future commitment, an unknown
/// spend, a revoked one without its revocation log entry) our latest local commitment's balance stands in
/// (<see cref="BalanceSourceKey"/>). B = <c>pendingMsat</c> + <c>FeeMsat</c> + <c>lostMsat</c>: <c>FeeMsat</c> is what
/// our balance paid for the commitment beyond its outputs when we fund it (the commitment fee, the peer's anchor and
/// sub-satoshi rounding; the commitment transaction's own fee, capacity minus outputs, is
/// <see cref="CommitmentFeeKey"/>), <c>lostMsat</c> the rest (our trimmed HTLCs, <see cref="TrimmedHtlcKey"/>, an
/// output below dust, rounding; signed only when B stood in).</para>
/// <para><b>A resolution by us</b> (the spender is the row's resolving transaction or a transaction we stored for
/// broadcast): <c>walletMsat</c> = the row's share of the spender's outputs to our wallet (outputs that are not a new
/// pending row; several rows spent together share them pro rata, the remainder to the first input), <c>pendingInMsat</c>
/// = the second-level output paired with the row's input, <c>FeeMsat</c> = the row's value minus both. <c>AmountMsat</c>
/// = <c>walletMsat</c>, plus <c>pendingInMsat</c> for a row that was not counted (a gain into pending). Our anchors HTLC
/// transaction pays its fee from wallet inputs (O7, NL-748): its stored fee beyond the row's own is
/// <see cref="WalletFeeKey"/>, included in <c>FeeMsat</c> and posted against the clearing account, where the wallet
/// events book the inputs and the change. A spender that
/// also spends inputs that are not rows of the channel (our anchor CPFP child with wallet inputs) merges the row's value
/// with them: the row leaves the pending bucket with <c>AmountMsat</c> = −value and the wallet events book the rest.
/// <b>A resolution by someone else</b>: <c>AmountMsat</c> = −value when it was counted (our offered HTLC claimed with
/// the preimage, our anchor swept by anyone), else 0. <b>An output given up</b> (<see cref="OutputResolutionState.Ignored"/>
/// by a resolver) leaves the bucket the same way, only when it was counted.</para>
/// <para><b>HTLCs: who owns the value</b> (coordinator rule, NL-602 A1-T2). The off-chain payment events own the
/// economic value of an HTLC; these events own the bucket movements and the on-chain fees. (a) An incoming HTLC we
/// claim on chain with the preimage is already income through <c>InvoiceSettled</c> or <c>ForwardSettled</c>: its
/// event moves the value reaching our wallet (or our second-level output) with <see cref="HtlcDirectionKey"/> =
/// <see cref="IncomingHtlc"/> and <see cref="ValueBookedByKey"/> = invoice or forward, and the books take its source to
/// be that income, not new income. (b) An offered HTLC of ours the peer claims with the preimage was paid out through
/// <c>PaymentSucceeded</c>, <c>ForwardSettled</c> or <c>ForwardLostOnchain</c>: its event writes the HTLC off the
/// pending bucket (<c>AmountMsat</c> = −value, <see cref="BucketKey"/> = <see cref="PendingBucket"/>,
/// <see cref="ClaimedByKey"/> = peer, <see cref="ValueBookedByKey"/> = payment or forward), which the books do not
/// count as a second loss. Only a spend whose witness carries the HTLC's preimage is such a claim (NL-612,
/// <see cref="AccountingDetailKeys.ClaimPath"/>): the peer taking it by the revocation path of our own revoked
/// commitment (or any spend without the preimage) gets no <see cref="ValueBookedByKey"/>, so the books post a loss. (c) An offered HTLC that times out back to us is an ordinary movement into the wallet, with
/// its fees: it was in our gross balance at the close. (d) An incoming HTLC that times out to the peer is 0,
/// informational; when the off-chain side had booked it (a settled forward, NL-608, or a settled invoice of ours, NL-688)
/// its own event (<c>ForwardLostOnchain</c>, <c>InvoiceLostOnchain</c>) books the loss in the same save. The books match the HTLC by <see cref="PaymentHashKey"/>/<see cref="HtlcIdKey"/>.</para>
/// <para><b>Reorgs.</b> A resolution whose spend was reorged out, and a close replaced by another transaction, are
/// negated by <see cref="AccountingEventKind.Reversal"/> events (key
/// <see cref="AccountingEventKeys.Reversal(string, uint)"/>, amount and fee negated, details
/// <see cref="ReversesKey"/> and <see cref="OriginalKindKey"/>, the shape of the chain monitor's reversals). A fact
/// written again after its reversal takes its next confirmation key (<see cref="AccountingEventKeys.Reconfirmed"/>,
/// <see cref="AccountingConfirmations.NextConfirmationKey"/>, the wallet writers' scheme; NL-613).</para>
/// <para><b>Fee bumps.</b> A resolution's <c>FeeMsat</c> is the whole fee its spender paid out of the output, also when
/// the spender is an RBF replacement of an earlier sweep (<see cref="IncludesFeeBumpKey"/> = true): the books take the
/// bump itself out of it against the <see cref="AccountingEventKind.SweepFeeBump"/> of the replacement.</para>
/// </remarks>
internal static class OnchainAccounting
{
    public const string ChannelBucket = AccountingDetailKeys.ChannelBucket;
    public const string PendingBucket = AccountingDetailKeys.PendingBucket;
    public const string WalletBucket = AccountingDetailKeys.WalletBucket;

    public const string BucketFromKey = AccountingDetailKeys.BucketFrom;
    public const string BucketToKey = AccountingDetailKeys.BucketTo;
    public const string CloseKindKey = AccountingDetailKeys.CloseKind;
    public const string CommitmentNumberKey = "commitmentNumber";
    public const string FunderKey = "funder";
    public const string BalanceSourceKey = "balanceSource";
    public const string PendingKey = AccountingDetailKeys.PendingMsat;
    public const string LostKey = AccountingDetailKeys.LostMsat;
    public const string TrimmedHtlcKey = "trimmedHtlcMsat";
    public const string OurOutputsKey = "ourOutputsSat";
    public const string CommitmentFeeKey = "commitmentFeeSat";
    public const string LatestBalanceKey = "latestLocalBalanceMsat";
    public const string RevokedStateBalanceKey = "revokedStateBalanceMsat";
    public const string FundingTxIdKey = "fundingTxId";
    public const string CountedVoutsKey = "countedVouts";
    public const string DescriptorKey = AccountingDetailKeys.Descriptor;
    public const string HtlcIdKey = "htlcId";
    public const string HtlcDirectionKey = AccountingDetailKeys.HtlcDirection;
    public const string PaymentHashKey = "paymentHash";
    public const string SpenderTxIdKey = "spenderTxId";
    public const string ResolvedByKey = AccountingDetailKeys.ResolvedBy;
    public const string PendingOutKey = AccountingDetailKeys.PendingOutMsat;
    public const string PendingInKey = AccountingDetailKeys.PendingInMsat;
    public const string WalletKey = AccountingDetailKeys.WalletMsat;
    public const string WalletFeeKey = AccountingDetailKeys.WalletFeeMsat;
    public const string CountedKey = AccountingDetailKeys.Counted;
    public const string ValueKey = AccountingDetailKeys.ValueMsat;
    public const string CloseTxIdKey = AccountingDetailKeys.CloseTxId;
    public const string NoteKey = AccountingDetailKeys.Note;
    public const string IncludesFeeBumpKey = AccountingDetailKeys.IncludesFeeBump;
    public const string ValueBookedByKey = AccountingDetailKeys.ValueBookedBy;
    public const string ClaimedByKey = AccountingDetailKeys.ClaimedBy;
    public const string BucketKey = AccountingDetailKeys.Bucket;
    public const string OfferedHtlc = "offered";
    public const string IncomingHtlc = "incoming";
    public const string ReversesKey = AccountingConfirmations.ReversesDetail;
    public const string OriginalKindKey = AccountingConfirmations.OriginalKindDetail;
    public const string OpeningBalanceKey = AccountingDetailKeys.OpeningBalance;

    public const string BalanceFromCommitment = "commitment";
    public const string BalanceFromLatestLocal = "latest-local-commitment";

    /// <summary>Whether a descriptor of the close counts in the pending bucket (see the remarks).</summary>
    public static bool CountsAtClose(CommitmentOutputDescriptor descriptor, bool weFund)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        return CountsAtClose(descriptor.Kind, descriptor.Htlc?.Direction, weFund);
    }

    /// <summary>Whether an output of the close of this kind (and HTLC direction, for an HTLC output) counts in the
    /// pending bucket (see the remarks): the rule of <see cref="CountsAtClose(CommitmentOutputDescriptor, bool)"/> for a
    /// stored row (the backfill's cutover, NL-602 A1-T6).</summary>
    public static bool CountsAtClose(OutputDescriptorKind kind, HtlcDirection? htlcDirection, bool weFund) =>
        kind switch
        {
            OutputDescriptorKind.DelayedToLocal or OutputDescriptorKind.PaymentToRemote
                or OutputDescriptorKind.LocalOfferedHtlc or OutputDescriptorKind.RemoteReceivedHtlc => true,
            OutputDescriptorKind.RevokedHtlc => htlcDirection == HtlcDirection.Outgoing,
            OutputDescriptorKind.OurAnchor => weFund,
            _ => false
        };

    /// <summary>Our gross balance per a commitment spec: our net balance plus the HTLCs we offered.</summary>
    public static long OurBalanceMsat(CommitmentTxSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var balance = (long)spec.ToLocalMsat;
        foreach (var htlc in spec.Htlcs)
            if (htlc.Direction == HtlcDirection.Outgoing)
                balance = checked(balance + (long)htlc.Amount.MilliSatoshi);
        return balance;
    }

    /// <summary>
    /// The <see cref="AccountingEventKind.ChannelForceClosed"/> event of a recorded funding spend (see the remarks).
    /// </summary>
    /// <param name="channel">The channel.</param>
    /// <param name="key">The event key (<see cref="NewKeyAsync"/>).</param>
    /// <param name="closeKind">What the spend is.</param>
    /// <param name="spend">The spend.</param>
    /// <param name="commitmentNumber">The commitment number it carries.</param>
    /// <param name="height">Its block.</param>
    /// <param name="descriptors">Its mapped outputs.</param>
    /// <param name="spec">The spec of the commitment that confirmed, when it could be rebuilt.</param>
    /// <param name="latestSpec">Our latest local commitment's spec.</param>
    /// <param name="spentFunding">The funding it spends.</param>
    /// <param name="occurredAt">When it was recorded.</param>
    public static AccountingEventModel ForceClosed(ChannelModel channel, string key, ChannelCloseKind closeKind,
                                                   ChainTx spend, ulong? commitmentNumber, uint height,
                                                   IReadOnlyList<CommitmentOutputDescriptor> descriptors,
                                                   CommitmentTxSpec? spec, CommitmentTxSpec? latestSpec,
                                                   ChannelFunding spentFunding, DateTimeOffset occurredAt)
    {
        var weFund = channel.IsInitiator;
        var latest = latestSpec is null ? (long?)null : OurBalanceMsat(latestSpec);
        var stateBalance = spec is not null ? OurBalanceMsat(spec) : latest ?? 0;

        // NL-616: a revoked commitment carries an old state's balance, while the books' Channels account holds our
        // latest one (every payment since was booked); the close takes the latest out, and the difference to the
        // revoked state is part of lostMsat (the penalty's gains come back through the resolution events)
        var revokedWithLatest = closeKind == ChannelCloseKind.RevokedCommitment && latest is not null;
        var balance = revokedWithLatest ? latest!.Value : stateBalance;
        var counted = descriptors.Where(d => CountsAtClose(d, weFund)).OrderBy(d => d.Vout).ToList();
        var pending = counted.Sum(d => checked((long)d.AmountSat * 1_000));

        long trimmed = 0;
        if (spec is not null)
            foreach (var htlc in spec.Htlcs.Where(h => h.Direction == HtlcDirection.Outgoing))
                if (!descriptors.Any(d => d.Htlc is { Direction: HtlcDirection.Outgoing } output
                                       && output.Id == htlc.Id))
                    trimmed += (long)htlc.Amount.MilliSatoshi;

        var fee = spec is not null && weFund ? Math.Max(0, stateBalance - pending - trimmed) : 0;
        var lost = balance - pending - fee;
        var outputsSat = spend.Outputs.Aggregate(0UL, (sum, o) => sum + o.AmountSat);
        var commitmentFee = spentFunding.CapacitySatoshis >= outputsSat
                                ? (spentFunding.CapacitySatoshis - outputsSat).ToString(CultureInfo.InvariantCulture)
                                : null;
        var ours = descriptors.Where(d => d.IsOurs).Aggregate(0UL, (sum, d) => sum + d.AmountSat);

        return new AccountingEventModel
        {
            EventKey = key,
            Kind = AccountingEventKind.ChannelForceClosed,
            OccurredAt = occurredAt,
            BlockHeight = height,
            ChannelId = channel.ChannelId,
            ShortChannelId = spentFunding.ShortChannelId ?? ScidOf(channel),
            TxId = spend.TxId,
            Counterparty = channel.RemoteNodeId,
            AmountMsat = -balance,
            FeeMsat = fee,
            Finality = AccountingFinality.Confirmed,
            Details = AccountingDetailsCodec.Create(
                (BucketFromKey, ChannelBucket), (BucketToKey, PendingBucket),
                (CloseKindKey, closeKind.ToString()), (CommitmentNumberKey, Text(commitmentNumber)),
                (FunderKey, weFund ? "true" : "false"),
                (BalanceSourceKey, spec is not null && !revokedWithLatest ? BalanceFromCommitment : BalanceFromLatestLocal),
                (RevokedStateBalanceKey, revokedWithLatest ? Text(stateBalance) : null),
                (PendingKey, Text(pending)), (LostKey, Text(lost)), (TrimmedHtlcKey, Text(trimmed)),
                (OurOutputsKey, ours.ToString(CultureInfo.InvariantCulture)), (CommitmentFeeKey, commitmentFee),
                (LatestBalanceKey, latest is { } value ? Text(value) : null),
                (FundingTxIdKey, spentFunding.FundingTxId.ToString()),
                (CountedVoutsKey, string.Join(",", counted.Select(d => d.Vout.ToString(CultureInfo.InvariantCulture)))))
        };
    }

    /// <summary>
    /// The vouts the close event <paramref name="close"/> counted in the pending bucket; empty when there is none.
    /// </summary>
    public static IReadOnlySet<uint> CountedVouts(AccountingEventModel? close)
    {
        var vouts = new HashSet<uint>();
        if (close is null || !close.Details.TryGetValue(CountedVoutsKey, out var text))
            return vouts;

        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries))
            if (uint.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var vout))
                vouts.Add(vout);
        return vouts;
    }

    /// <summary>The flows of one output's resolution (see the remarks).</summary>
    /// <param name="PendingOutMsat">What left the pending bucket (its value when counted).</param>
    /// <param name="PendingInMsat">What entered it (a second-level output).</param>
    /// <param name="WalletMsat">What reached our wallet.</param>
    /// <param name="FeeMsat">The fee paid out of the output's value, plus <paramref name="WalletFeeMsat"/>.</param>
    /// <param name="AmountMsat">The event's amount.</param>
    /// <param name="ResolvedBy">"us", "peer" or "ignored".</param>
    /// <param name="Note">Why a flow could not be told, if so.</param>
    /// <param name="WalletFeeMsat">The part of the fee the spender's wallet inputs paid (NL-748).</param>
    public sealed record ResolutionFlows(long PendingOutMsat, long PendingInMsat, long WalletMsat, long FeeMsat,
                                         long AmountMsat, string ResolvedBy, string? Note = null,
                                         long WalletFeeMsat = 0);

    /// <summary>The flows of an output another transaction than ours took, or that was given up.</summary>
    public static ResolutionFlows Lost(long valueMsat, bool counted, string resolvedBy)
    {
        var pendingOut = counted ? valueMsat : 0;
        return new ResolutionFlows(pendingOut, 0, 0, 0, -pendingOut, resolvedBy);
    }

    /// <summary>
    /// The flows of <paramref name="row"/> spent by our <paramref name="spender"/> (see the remarks).
    /// </summary>
    /// <param name="row">The output.</param>
    /// <param name="valueMsat">Its value.</param>
    /// <param name="counted">Whether it is in the pending bucket.</param>
    /// <param name="spender">Our transaction that spent it.</param>
    /// <param name="rows">Every row of the channel (after this round's changes), by outpoint.</param>
    /// <param name="isHtlcTransaction">The spender is our HTLC transaction (its output paired with the row's input is a
    /// second-level output, even before its row exists).</param>
    /// <param name="spenderFeeMsat">The spender's whole fee, when it is a stored transaction of ours whose fee we know.
    /// For a sweep (never with wallet inputs) its inputs that are not rows (the peer's anchor in our anchor sweep,
    /// NL-611) are then valued as the outputs plus the fee less the rows, and that value is a gain booked with the first
    /// row's event (with its share of the fee), instead of merging the rows into the wallet events. For our HTLC
    /// transaction with wallet fee inputs (anchors, NL-748) the fee beyond the row's own is the wallet's part
    /// (<see cref="ResolutionFlows.WalletFeeMsat"/>).</param>
    public static ResolutionFlows Ours(OutputResolutionModel row, long valueMsat, bool counted, ChainTx spender,
                                       IReadOnlyDictionary<(TxId, uint), OutputResolutionModel> rows,
                                       bool isHtlcTransaction, long? spenderFeeMsat = null)
    {
        var pendingOut = counted ? valueMsat : 0;
        var inputIndex = spender.IndexOfInputSpending(row.TransactionId, row.OutputIndex);
        if (inputIndex < 0)
            return new ResolutionFlows(pendingOut, 0, 0, 0, -pendingOut, AccountingDetailKeys.ResolvedByUs,
                                       "the spender does not spend it");

        bool IsPendingOutput(int vout) => rows.ContainsKey((spender.TxId, (uint)vout))
                                       || (isHtlcTransaction && vout == inputIndex);

        if (inputIndex < spender.Outputs.Count && IsPendingOutput(inputIndex))
        {
            var pendingIn = checked((long)spender.Outputs[inputIndex].AmountSat * 1_000);
            var htlcFee = Math.Max(0, valueMsat - pendingIn);

            // NL-748: the wallet inputs of our anchors HTLC transaction paid the rest of its fee
            var walletFee = isHtlcTransaction && spenderFeeMsat is { } whole && HasInputsOutsideRows(spender, rows)
                                ? Math.Max(0, whole - htlcFee)
                                : 0;
            return new ResolutionFlows(pendingOut, pendingIn, 0, checked(htlcFee + walletFee), counted ? 0 : pendingIn,
                                       AccountingDetailKeys.ResolvedByUs, WalletFeeMsat: walletFee);
        }

        var externalInputsFeeMsat = isHtlcTransaction ? null : spenderFeeMsat;

        // The rows the spender spends (its other inputs must be rows too, or the row's value is merged with them)
        var merged = new ResolutionFlows(pendingOut, 0, 0, 0, -pendingOut, AccountingDetailKeys.ResolvedByUs,
                                         AccountingDetailKeys.MergedNote);
        var shares = new List<(int Index, long ValueMsat)>();
        var externalIndex = -1;
        for (var i = 0; i < spender.Inputs.Count; i++)
        {
            var input = spender.Inputs[i];
            if (i < spender.Outputs.Count && i != inputIndex && IsPendingOutput(i))
                continue;

            if (i == inputIndex)
            {
                shares.Add((i, valueMsat));
                continue;
            }

            if (!rows.TryGetValue((input.PreviousTxId, input.PreviousVout), out var other)
             || OutputDescriptorData.TryDecode(other) is not { } data)
            {
                if (externalInputsFeeMsat is null)
                    return merged;

                if (externalIndex < 0)
                    externalIndex = i;
                continue;
            }

            shares.Add((i, checked((long)data.AmountSat * 1_000)));
        }

        long walletMsat = 0;
        long outputsMsat = 0;
        for (var vout = 0; vout < spender.Outputs.Count; vout++)
        {
            outputsMsat = checked(outputsMsat + (long)spender.Outputs[vout].AmountSat * 1_000);
            if (!IsPendingOutput(vout))
                walletMsat = checked(walletMsat + (long)spender.Outputs[vout].AmountSat * 1_000);
        }

        long externalMsat = 0;
        if (externalIndex >= 0)
        {
            // NL-611: the inputs that are not rows are what the outputs and the fee hold beyond the rows
            externalMsat = outputsMsat + externalInputsFeeMsat!.Value - shares.Sum(s => s.ValueMsat);
            if (externalMsat < 0 || walletMsat != outputsMsat)
                return merged;
        }

        var total = shares.Sum(s => s.ValueMsat) + externalMsat;
        long ours = 0;
        long externalWallet = 0;
        if (total > 0)
        {
            long allocated = 0;
            foreach (var (index, share) in shares)
            {
                var part = (long)((Int128)walletMsat * share / total);
                allocated += part;
                if (index == inputIndex)
                    ours = part;
            }

            externalWallet = (long)((Int128)walletMsat * externalMsat / total);
            allocated += externalWallet;

            // The rounding remainder goes to the first input (with external inputs, to the row that books them)
            if (externalIndex < 0 && shares[0].Index == inputIndex)
                ours += walletMsat - allocated;
            else if (externalIndex >= 0)
                externalWallet += walletMsat - allocated;
        }

        var fee = valueMsat - ours;
        string? note = null;
        if (externalIndex >= 0 && shares.Min(s => s.Index) == inputIndex)
        {
            // This row books the external inputs: their part of the wallet output and of the fee, so the gain is their
            // whole value (the books post walletMsat + fee - pendingOut beyond the row's own flows to OnchainGain)
            ours += externalWallet;
            fee += externalMsat - externalWallet;
            note = AccountingDetailKeys.ExternalInputsNote;
        }

        return fee >= 0
                   ? new ResolutionFlows(pendingOut, 0, ours, fee, ours, AccountingDetailKeys.ResolvedByUs, note)
                   : new ResolutionFlows(pendingOut, 0, ours, 0, ours, AccountingDetailKeys.ResolvedByUs,
                                         "pays out more than its value");
    }

    /// <summary>
    /// The event of an output's resolution or abandonment (<paramref name="flows"/>): <see cref="AccountingEventKind.PenaltyClaimed"/>
    /// or <see cref="AccountingEventKind.BreachLoss"/> for an output of a revoked commitment, else
    /// <see cref="AccountingEventKind.OutputResolved"/>.
    /// </summary>
    public static AccountingEventModel Resolution(ChannelModel channel, ChannelCloseModel close,
                                                  OutputResolutionModel row, OutputDescriptorData? data,
                                                  AccountingEventKind kind, string key, ResolutionFlows flows,
                                                  bool counted, TxId? spenderTxId, uint height,
                                                  DateTimeOffset occurredAt, bool includesFeeBump = false,
                                                  IReadOnlyList<(string Key, string? Value)>? extraDetails = null)
    {
        var htlc = data?.Htlc;
        var details = new List<(string Key, string? Value)>
        {
            (BucketFromKey, counted ? PendingBucket : null),
            (BucketToKey, flows.WalletMsat > 0 ? WalletBucket : flows.PendingInMsat > 0 ? PendingBucket : null),
            (DescriptorKey, row.Descriptor.ToString()), (HtlcIdKey, htlc is { } h ? Text((long)h.Id) : null),
            (HtlcDirectionKey, htlc is { } d ? d.Direction == HtlcDirection.Outgoing ? OfferedHtlc : IncomingHtlc
                                   : null),
            (PaymentHashKey, htlc?.PaymentHash.ToString()), (SpenderTxIdKey, spenderTxId?.ToString()),
            (ResolvedByKey, flows.ResolvedBy), (PendingOutKey, Text(flows.PendingOutMsat)),
            (PendingInKey, Text(flows.PendingInMsat)), (WalletKey, Text(flows.WalletMsat)),
            (WalletFeeKey, flows.WalletFeeMsat != 0 ? Text(flows.WalletFeeMsat) : null),
            (CountedKey, counted ? "true" : "false"),
            (ValueKey, data is null ? null : Text(checked((long)data.AmountSat * 1_000))),
            (CloseKindKey, close.Kind.ToString()),
            (CloseTxIdKey, close.CommitmentTransactionId.ToString()),
            (IncludesFeeBumpKey, includesFeeBump ? "true" : null), (NoteKey, flows.Note)
        };
        if (extraDetails is not null)
            details.AddRange(extraDetails);

        return new AccountingEventModel
        {
            EventKey = key,
            Kind = kind,
            OccurredAt = occurredAt,
            BlockHeight = height,
            ChannelId = channel.ChannelId,
            ShortChannelId = ScidOf(channel),
            PaymentHash = htlc?.PaymentHash,
            TxId = row.TransactionId,
            OutputIndex = row.OutputIndex,
            Counterparty = channel.RemoteNodeId,
            AmountMsat = flows.AmountMsat,
            FeeMsat = flows.FeeMsat,
            Finality = AccountingFinality.Confirmed,
            Details = AccountingDetailsCodec.Create(details.ToArray())
        };
    }

    /// <summary>Whether a descriptor is an output of a revoked commitment (or of the cheater's HTLC transaction).</summary>
    public static bool IsRevoked(OutputDescriptorKind kind) =>
        kind is OutputDescriptorKind.RevokedToLocal or OutputDescriptorKind.RevokedHtlc
             or OutputDescriptorKind.RevokedSecondLevel;

    /// <summary>The base keys a resolution of this outpoint may have been written under.</summary>
    public static IEnumerable<string> ResolutionKeys(TxId txId, uint vout) =>
    [
        AccountingEventKeys.OutputResolved(txId, vout), AccountingEventKeys.PenaltyClaimed(txId, vout),
        AccountingEventKeys.BreachLoss(txId, vout)
    ];

    /// <summary>
    /// The key to write a new fact under (NL-613: the generation scheme of the wallet writers,
    /// <see cref="AccountingConfirmations.NextConfirmationKey"/>): <paramref name="baseKey"/>, or after its reversal by a
    /// reorg <see cref="AccountingEventKeys.Reconfirmed"/>(<paramref name="baseKey"/>, 2), 3 and so on; null when a
    /// confirmation of the fact is written and stands.
    /// </summary>
    public static async Task<string?> NewKeyAsync(IAccountingEventDbRepository repository, string baseKey,
                                                  CancellationToken cancellationToken)
    {
        var existing = await repository.GetByKeyPrefixAsync(baseKey, cancellationToken);
        return AccountingConfirmations.NextConfirmationKey(baseKey, existing);
    }

    /// <summary>
    /// The event of a fact written under <paramref name="baseKey"/> that a block at <paramref name="height"/> holds: its
    /// confirmation at that height when there is one, else its latest standing one, else the first row
    /// (<see cref="AccountingConfirmations.FindAt"/>).
    /// </summary>
    public static async Task<AccountingEventModel?> FindAsync(IAccountingEventDbRepository repository, string baseKey,
                                                              uint? height, CancellationToken cancellationToken)
    {
        var existing = await repository.GetByKeyPrefixAsync(baseKey, cancellationToken);
        return AccountingConfirmations.FindAt(baseKey, height, existing);
    }

    /// <summary>
    /// Stages the <see cref="AccountingEventKind.Reversal"/> of <paramref name="original"/> for the disconnected block
    /// at <paramref name="height"/>, unless it is already written. True when staged.
    /// </summary>
    public static async Task<bool> StageReversalAsync(IAccountingEventDbRepository repository,
                                                      AccountingEventModel original, uint height,
                                                      DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        // Keyed by the original's own block, as AccountingConfirmations.IsReversed reads it
        var key = AccountingEventKeys.Reversal(original.EventKey, original.BlockHeight ?? height);
        if (key.Length > AccountingEventKeys.MaxLength || await repository.ExistsAsync(key, cancellationToken))
            return false;

        repository.Add(new AccountingEventModel
        {
            EventKey = key,
            Kind = AccountingEventKind.Reversal,
            OccurredAt = occurredAt,
            BlockHeight = original.BlockHeight ?? height,
            ChannelId = original.ChannelId,
            ShortChannelId = original.ShortChannelId,
            PaymentHash = original.PaymentHash,
            TxId = original.TxId,
            OutputIndex = original.OutputIndex,
            Counterparty = original.Counterparty,
            AmountMsat = -original.AmountMsat,
            FeeMsat = -original.FeeMsat,
            Finality = AccountingFinality.Final,
            Details = AccountingDetailsCodec.Create((ReversesKey, original.EventKey),
                                                    (OriginalKindKey, original.Kind.ToString()))
        });
        return true;
    }

    /// <summary>Whether <paramref name="spender"/> spends an output that is not a row of the channel (a wallet input).
    /// </summary>
    private static bool HasInputsOutsideRows(ChainTx spender,
                                             IReadOnlyDictionary<(TxId, uint), OutputResolutionModel> rows) =>
        spender.Inputs.Any(i => !rows.ContainsKey((i.PreviousTxId, i.PreviousVout)));

    private static ShortChannelId? ScidOf(ChannelModel channel) =>
        ((byte[]?)channel.ShortChannelId)?.Length > 0 ? channel.ShortChannelId : (ShortChannelId?)null;

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string? Text(ulong? value) => value?.ToString(CultureInfo.InvariantCulture);
}