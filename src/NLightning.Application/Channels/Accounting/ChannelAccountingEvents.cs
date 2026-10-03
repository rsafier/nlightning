using System.Globalization;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Channels.Accounting;

using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Labels;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using Splicing;

/// <summary>
/// The accounting feed's channel lifecycle events (plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §4, NL-602 A1):
/// <see cref="AccountingEventKind.ChannelFunded"/> with <see cref="AccountingEventKind.PushSent"/> or
/// <see cref="AccountingEventKind.PushReceived"/>, <see cref="AccountingEventKind.SpliceLocked"/> and
/// <see cref="AccountingEventKind.ChannelClosedMutual"/>. Each is staged on the unit of work that commits its transition,
/// before that save, by the transition's "happens once" path; nothing here throws (a value that cannot be derived is
/// left out and named in the details).
/// </summary>
/// <remarks>
/// <para>The three kinds move value between the <c>wallet</c> and the <c>channel</c> buckets, with one convention:
/// <see cref="AccountingEventModel.AmountMsat"/> is the change of the channel bucket and
/// <see cref="AccountingEventModel.FeeMsat"/> what we paid in fees, so the wallet bucket changes by
/// <c>-(AmountMsat + FeeMsat)</c> (a funding: the wallet paid our contribution and our fee share; a splice-out: the
/// channel balance lost the amount out and our fee, the wallet output got the amount out; a mutual close: the channel
/// balance left, our closing output reached the wallet).</para>
/// <para>A splice's <see cref="ChannelFunding.LocalBalanceDeltaMsat"/> already has our fee share taken out (D16: our
/// wallet inputs minus our wallet outputs minus our fee), so <c>AmountMsat</c> is that delta and the fee is not counted
/// twice: the books post the delta to the channel and the fee to expenses, and the wallet side follows from the
/// identity above.</para>
/// <para>Liquidity ads (NL-850): a purchase moves the fee (mining plus service) from the buyer's balance to the
/// seller's in the new commitment, with no output of its own. <see cref="AccountingEventKind.ChannelFunded"/> and
/// <see cref="AccountingEventKind.SpliceLocked"/> book our contribution without it (their <c>liquidityFeeMsat</c>
/// argument, + when we paid, − when we earned, is the part of the balance that is the fee) and
/// <see cref="RecordLiquidityPurchaseAsync"/> books the fee itself
/// (<see cref="AccountingEventKind.LiquidityFeePaid"/> or <see cref="AccountingEventKind.LiquidityFeeEarned"/>), so
/// the channels account holds the balance and the fee is an expense or income.</para>
/// </remarks>
internal static class ChannelAccountingEvents
{
    private const string WalletBucket = AccountingDetailKeys.WalletBucket;
    private const string ChannelBucket = AccountingDetailKeys.ChannelBucket;

    /// <summary>
    /// Stages <see cref="AccountingEventKind.ChannelFunded"/> (and the push, when one was recorded and is not zero) for a
    /// channel whose funding just reached its depth for us (V1FundingSigned or ReadyForThem, called before the
    /// confirmation's save). The channel must hold the confirmed funding outpoint, its short channel id and, in
    /// <see cref="ChannelModel.FundingCreatedAtBlockHeight"/>, the funding's block.
    /// </summary>
    /// <param name="liquidityFeeMsat">The liquidity fee of a purchase made in this funding (NL-850): + when we bought,
    /// − when we sold, 0 for none. Only used when the attempt's contribution is not stored (the channel balance, which
    /// includes the fee, stands in for it); stage the fee itself with <see cref="RecordLiquidityPurchaseAsync"/>.</param>
    public static async Task StageChannelFundedAsync(IUnitOfWork unitOfWork, ChannelModel channel,
                                                     DateTimeOffset occurredAt, ILogger logger,
                                                     long liquidityFeeMsat = 0)
    {
        try
        {
            if (unitOfWork.AccountingEventDbRepository is not { } events)
                return;

            foreach (var accountingEvent in await BuildChannelFundedAsync(unitOfWork, channel, occurredAt,
                                                                          liquidityFeeMsat: liquidityFeeMsat))
                events.Add(accountingEvent);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Could not record the funding of channel {ChannelId} in the accounting feed",
                            channel.ChannelId);
        }
    }

    /// <summary>
    /// Records <see cref="AccountingEventKind.ChannelFunded"/> (and the push) for a channel that failed or went on chain
    /// before its funding reached its depth (NL-617): the confirmation handler only runs for a channel awaiting it, so
    /// without this its force close would take our balance out of a channel bucket that never received it. Once per
    /// funding (the event's key), in its own save; never throws.
    /// </summary>
    /// <param name="liquidityFeeMsat">As for <see cref="StageChannelFundedAsync"/>.</param>
    public static async Task RecordLateChannelFundedAsync(IUnitOfWork unitOfWork, ChannelModel channel, uint height,
                                                          ShortChannelId shortChannelId, DateTimeOffset occurredAt,
                                                          ILogger logger, long liquidityFeeMsat = 0)
    {
        try
        {
            if (unitOfWork.AccountingEventDbRepository is not { } events
             || channel.FundingOutput is not { TransactionId: { } fundingTxId }
             || await events.ExistsAsync(AccountingEventKeys.ChannelFunded(channel.ChannelId, fundingTxId)))
                return;

            var built = await BuildChannelFundedAsync(unitOfWork, channel, occurredAt, (height, shortChannelId),
                                                      liquidityFeeMsat);
            foreach (var accountingEvent in built)
                if (!await events.ExistsAsync(accountingEvent.EventKey))
                    events.Add(accountingEvent);

            await unitOfWork.SaveChangesAsync();
            logger.LogInformation("Recorded the funding of channel {ChannelId}, {State} before its funding confirmed",
                                  channel.ChannelId, Enum.GetName(channel.State));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Could not record the late funding of channel {ChannelId} in the accounting feed",
                            channel.ChannelId);
        }
    }

    /// <summary>
    /// The events <see cref="StageChannelFundedAsync"/> stages (<see cref="AccountingEventKind.ChannelFunded"/>, then the
    /// push when one was recorded and is not zero), read from <paramref name="unitOfWork"/> but not staged; empty when
    /// the channel has no funding outpoint. The backfill writes them as memo events (NL-602 A1-T6). May throw.
    /// </summary>
    /// <param name="confirmedAt">The funding's block and short channel id when the channel does not hold them (a
    /// channel that failed before its funding confirmed, NL-617); null takes the channel's.</param>
    /// <param name="liquidityFeeMsat">As for <see cref="StageChannelFundedAsync"/>.</param>
    public static async Task<IReadOnlyList<AccountingEventModel>> BuildChannelFundedAsync(
        IUnitOfWork unitOfWork, ChannelModel channel, DateTimeOffset occurredAt,
        (uint Height, ShortChannelId ShortChannelId)? confirmedAt = null, long liquidityFeeMsat = 0)
    {
        if (channel.FundingOutput is not { TransactionId: { } fundingTxId } funding)
            return [];

        var capacityMsat = checked((long)funding.Amount.MilliSatoshi);
        var isDualFunded = channel.Version == ChannelVersion.V2;
        var contributionMsat = 0L;
        long? feeMsat = null;
        ulong? totalFeeSat = null;
        if (isDualFunded)
        {
            // Our share of the funding output: never the balance, which a liquidity fee moved (NL-850)
            var attempt = await FindAttemptAsync(unitOfWork, channel.ChannelId, fundingTxId);
            contributionMsat = attempt?.LocalFundingSatoshis is { } localSat
                                   ? checked(localSat * 1_000)
                                   : checked((long)channel.LocalBalance.MilliSatoshi + liquidityFeeMsat);
            if (attempt?.ConstructedTx is { } transaction)
            {
                totalFeeSat = SpliceService.GetTotalFee(transaction);
                feeMsat = GetLocalFeeShareMsat(transaction, contributionMsat);
            }
        }
        else if (channel.IsInitiator)
        {
            contributionMsat = capacityMsat;
            if (unitOfWork.BroadcastTransactionDbRepository is { } broadcasts
             && await broadcasts.GetByTransactionIdAsync(fundingTxId) is { Fee: { } fee })
            {
                feeMsat = checked((long)fee.MilliSatoshi);
                totalFeeSat = (ulong)fee.Satoshi;
            }
        }
        else
        {
            // A v1 fundee adds nothing and pays nothing
            feeMsat = 0;
        }

        var push = await GetPushAmountAsync(unitOfWork, channel.ChannelId);
        var height = confirmedAt?.Height
                  ?? (channel.FundingCreatedAtBlockHeight > 0 ? channel.FundingCreatedAtBlockHeight : (uint?)null);
        var shortChannelId = confirmedAt?.ShortChannelId
                          ?? (IsSet(channel.ShortChannelId) ? channel.ShortChannelId : (ShortChannelId?)null);
        var funded = new AccountingEventModel
        {
            EventKey = AccountingEventKeys.ChannelFunded(channel.ChannelId, fundingTxId),
            Kind = AccountingEventKind.ChannelFunded,
            OccurredAt = occurredAt,
            BlockHeight = height,
            ChannelId = channel.ChannelId,
            ShortChannelId = shortChannelId,
            TxId = fundingTxId,
            OutputIndex = funding.Index,
            Counterparty = channel.RemoteNodeId,
            AmountMsat = contributionMsat,
            FeeMsat = feeMsat ?? 0,
            Finality = AccountingFinality.Confirmed,
            Details = AccountingDetailsCodec.Create(
            [
                (AccountingDetailKeys.BucketFrom, WalletBucket),
                (AccountingDetailKeys.BucketTo, ChannelBucket),
                ("capacitySat", Format(funding.Amount.Satoshi)),
                ("isInitiator", Format(channel.IsInitiator)),
                (AccountingDetailKeys.DualFunded, Format(isDualFunded)),
                ("public", Format(channel.AnnounceChannel)),
                ("anchors", Format(channel.ChannelParams.OptionAnchorOutputs)),
                ("scidAlias", Format(channel.ChannelParams.UseScidAlias > FeatureSupport.No)),
                ("fundingFeeSat", totalFeeSat is { } total ? Format(total) : null),
                ("feeUnknown", feeMsat is null ? "true" : null),
                (AccountingDetailKeys.LiquidityFeeMsat, liquidityFeeMsat != 0 ? Format(liquidityFeeMsat) : null),
                ("pushMsat", push is null ? null : Format(push.MilliSatoshi)),
                ("pushUnknown", push is null && !isDualFunded ? "true" : null),
                // NL-602 A3-T1: the operator's label and tags of the open (openchannel --label/--tag)
                .. SourceLabels.FromStored(channel.Label, channel.Tags).ToDetailPairs()
            ])
        };
        List<AccountingEventModel> built = [funded];

        // The push is its own fact: value that changed hands at the open (NL-605); a dual-funded open has none
        if (push is not { IsZero: false })
            return built;

        var pushMsat = checked((long)push.MilliSatoshi);
        built.Add(new AccountingEventModel
        {
            EventKey = AccountingEventKeys.Push(channel.ChannelId),
            Kind = channel.IsInitiator ? AccountingEventKind.PushSent : AccountingEventKind.PushReceived,
            OccurredAt = occurredAt,
            BlockHeight = height,
            ChannelId = channel.ChannelId,
            ShortChannelId = shortChannelId,
            TxId = fundingTxId,
            OutputIndex = funding.Index,
            Counterparty = channel.RemoteNodeId,
            AmountMsat = channel.IsInitiator ? -pushMsat : pushMsat,
            FeeMsat = 0,
            Finality = AccountingFinality.Confirmed,
            Details = AccountingDetailsCodec.Create(channel.IsInitiator
                                                        ? (AccountingDetailKeys.BucketFrom, ChannelBucket)
                                                        : (AccountingDetailKeys.BucketTo, ChannelBucket))
        });
        return built;
    }

    /// <summary>
    /// Stages the amount the opener of a v1 channel pushed at the open (NL-605; 0 when it pushed nothing, so null keeps
    /// meaning "opened before it was recorded") in the channel's first save. The channel may be staged in that unit of
    /// work. Never throws.
    /// </summary>
    public static async Task StagePushAmountAsync(IUnitOfWork unitOfWork, ChannelId channelId, LightningMoney push,
                                                  ILogger logger)
    {
        try
        {
            if (unitOfWork.ChannelFundingDbRepository is { } fundings)
                await fundings.SetPushAmountAsync(channelId, push);
        }
        catch (NotSupportedException)
        {
            // A unit of work that stores no fundings (test doubles)
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Could not record the push amount of channel {ChannelId}", channelId);
        }
    }

    /// <summary>
    /// Stages <see cref="AccountingEventKind.SpliceLocked"/> for <paramref name="locked"/>, the pending funding that just
    /// locked (both <c>splice_locked</c>, before the lock's save), with its deltas relative to
    /// <paramref name="previous"/>, the funding it replaces.
    /// </summary>
    /// <param name="liquidityFeeMsat">The liquidity fee of a purchase made in this splice (NL-850) that
    /// <see cref="ChannelFunding.LocalBalanceDeltaMsat"/> includes: + when we bought (the delta is our contribution
    /// less the fee), − when we sold (our contribution plus the fee), 0 for none. The event's amount and our fee share
    /// leave it out; stage the fee itself with <see cref="RecordLiquidityPurchaseAsync"/>.</param>
    public static async Task StageSpliceLockedAsync(IUnitOfWork unitOfWork, ChannelModel channel,
                                                    ChannelFunding locked, ChannelFunding previous,
                                                    DateTimeOffset occurredAt, ILogger logger,
                                                    long liquidityFeeMsat = 0)
    {
        try
        {
            if (unitOfWork.AccountingEventDbRepository is not { } events)
                return;

            // The change of our balance less the liquidity fee it includes: what our wallet put in or took out
            var deltaMsat = checked(locked.LocalBalanceDeltaMsat + liquidityFeeMsat);
            var attempt = await FindAttemptAsync(unitOfWork, channel.ChannelId, locked.FundingTxId);
            long? feeMsat = null;
            ulong? totalFeeSat = null;
            long? walletInputsSat = null;
            long? walletOutputsSat = null;
            if (attempt?.ConstructedTx is { } transaction)
            {
                totalFeeSat = SpliceService.GetTotalFee(transaction);
                feeMsat = GetLocalFeeShareMsat(transaction, deltaMsat);
                (walletInputsSat, walletOutputsSat) = GetLocalWalletAmounts(transaction);
            }

            events.Add(new AccountingEventModel
            {
                EventKey = AccountingEventKeys.SpliceLocked(channel.ChannelId, locked.FundingTxId),
                Kind = AccountingEventKind.SpliceLocked,
                OccurredAt = occurredAt,
                BlockHeight = locked.ConfirmedHeight ?? locked.ShortChannelId?.BlockHeight,
                ChannelId = channel.ChannelId,
                ShortChannelId = locked.ShortChannelId,
                TxId = locked.FundingTxId,
                OutputIndex = locked.OutputIndex,
                Counterparty = channel.RemoteNodeId,
                AmountMsat = deltaMsat,
                FeeMsat = feeMsat ?? 0,
                Finality = AccountingFinality.Confirmed,
                Details = AccountingDetailsCodec.Create(
                    (AccountingDetailKeys.BucketFrom, deltaMsat >= 0 ? WalletBucket : ChannelBucket),
                    (AccountingDetailKeys.BucketTo, deltaMsat >= 0 ? ChannelBucket : WalletBucket),
                    (AccountingDetailKeys.Kind, locked.Kind.ToString()),
                    ("capacitySat", Format(locked.CapacitySatoshis)),
                    ("previousCapacitySat", Format(previous.CapacitySatoshis)),
                    ("previousFundingTxId", previous.FundingTxId.ToString()),
                    ("grossDeltaMsat", Format(locked.LocalBalanceDeltaMsat)),
                    ("deltaIncludesFee", "true"),
                    (AccountingDetailKeys.LiquidityFeeMsat, liquidityFeeMsat != 0 ? Format(liquidityFeeMsat) : null),
                    ("ourFeeMsat", feeMsat is { } fee ? Format(fee) : null),
                    ("spliceFeeSat", totalFeeSat is { } total ? Format(total) : null),
                    ("walletInputsSat", walletInputsSat is { } inputs ? Format(inputs) : null),
                    ("walletOutputsSat", walletOutputsSat is { } outputs ? Format(outputs) : null),
                    ("isInitiator", attempt is null ? null : Format(attempt.IsInitiator)),
                    ("feeUnknown", feeMsat is null ? "true" : null))
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Could not record the splice {TxId} of channel {ChannelId} in the accounting feed",
                            locked.FundingTxId, channel.ChannelId);
        }
    }

    /// <summary>
    /// Stages the liquidity fee of a purchase (liquidity ads, NL-850) made in the funding or splice
    /// <paramref name="fundingTxId"/> of <paramref name="channelId"/>: <see cref="AccountingEventKind.LiquidityFeePaid"/>
    /// (AmountMsat −fee, FeeMsat fee) when we bought, <see cref="AccountingEventKind.LiquidityFeeEarned"/> (AmountMsat
    /// +fee) when we sold, the fee being the mining fee plus the service fee. Keyed by channel and funding
    /// (<see cref="AccountingEventKeys.LiquidityFee"/>): nothing is staged while that purchase's event stands, and a
    /// purchase whose event an RBF replaced (<see cref="RecordLiquidityPurchaseReplacedAsync"/>) is recorded again
    /// under its next key (that attempt confirmed after all). Nothing for a zero fee. Staged on
    /// <paramref name="unitOfWork"/>, whose save commits it; never throws.
    /// </summary>
    /// <remarks>
    /// Stage it in the save that books the funding (with <see cref="StageChannelFundedAsync"/>) or the splice's lock
    /// (with <see cref="StageSpliceLockedAsync"/>), passing them the same fee: the reconcile counts a channel's balance
    /// only once its funding confirmed and a splice's new balance only once it locked, so a fee booked earlier shows as
    /// a drift of the channels account until then. Booked earlier (in the negotiation's save), an RBF that replaces the
    /// attempt must reverse it with <see cref="RecordLiquidityPurchaseReplacedAsync"/>.
    /// </remarks>
    /// <param name="weBought">True when we bought the liquidity (the fee left our balance), false when we sold it.
    /// </param>
    /// <param name="peer">The other node of the purchase.</param>
    /// <param name="requestedSat">The amount the buyer requested.</param>
    /// <param name="contributedSat">The amount the seller contributed.</param>
    /// <param name="miningFeeSat">The mining fee part (<c>LiquidityFees.MiningFeeSat</c>).</param>
    /// <param name="serviceFeeSat">The service fee part (<c>LiquidityFees.ServiceFeeSat</c>).</param>
    /// <param name="kind">When the purchase was made (open, RBF of the open, splice).</param>
    /// <param name="blockHeight">The funding's block, when staged with its confirmation.</param>
    /// <param name="shortChannelId">The funding's short channel id, when known.</param>
    public static async Task RecordLiquidityPurchaseAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                          TxId fundingTxId, bool weBought, CompactPubKey? peer,
                                                          ulong requestedSat, ulong contributedSat, ulong miningFeeSat,
                                                          ulong serviceFeeSat, AccountingLiquidityKind kind,
                                                          DateTimeOffset occurredAt, ILogger logger,
                                                          uint? blockHeight = null,
                                                          ShortChannelId? shortChannelId = null)
    {
        try
        {
            if (unitOfWork.AccountingEventDbRepository is not { } events
             || BuildLiquidityPurchase(channelId, fundingTxId, weBought, peer, requestedSat, contributedSat,
                                       miningFeeSat, serviceFeeSat, kind, occurredAt, blockHeight,
                                       shortChannelId) is not { } built)
                return;

            var existing = await events.GetByKeyPrefixAsync(built.EventKey) ?? [];
            if (NextLiquidityKey(built.EventKey, existing) is not { } key)
                return;

            events.Add(key == built.EventKey ? built : WithKey(built, key));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Could not record the liquidity purchase of channel {ChannelId} in funding {TxId} in "
                             + "the accounting feed", channelId, fundingTxId);
        }
    }

    /// <summary>
    /// Stages the <see cref="AccountingEventKind.Reversal"/> of the standing liquidity fee event of
    /// <paramref name="replacedFundingTxId"/> (NL-850), an attempt of a dual-funded open or a splice that an RBF
    /// replaced before it confirmed: the books take the old fee back (the new attempt's purchase is recorded on its own
    /// key). Keyed <see cref="AccountingEventKeys.Replaced"/> of the reversed event, so a repeat is a duplicate; nothing
    /// when no event of that funding stands. Staged on <paramref name="unitOfWork"/>; never throws.
    /// </summary>
    /// <param name="replacementFundingTxId">The attempt that replaced it, when known (a detail).</param>
    public static async Task RecordLiquidityPurchaseReplacedAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                                  TxId replacedFundingTxId, DateTimeOffset occurredAt,
                                                                  ILogger logger, TxId? replacementFundingTxId = null)
    {
        try
        {
            if (unitOfWork.AccountingEventDbRepository is not { } events)
                return;

            var baseKey = AccountingEventKeys.LiquidityFee(channelId, replacedFundingTxId);
            var existing = await events.GetByKeyPrefixAsync(baseKey) ?? [];
            if (FindStandingLiquidity(baseKey, existing) is not { } standing)
                return;

            events.Add(BuildLiquidityReplacement(standing, occurredAt, replacementFundingTxId));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Could not reverse the replaced liquidity purchase of channel {ChannelId} in funding "
                             + "{TxId} in the accounting feed", channelId, replacedFundingTxId);
        }
    }

    /// <summary>
    /// The event <see cref="RecordLiquidityPurchaseAsync"/> stages under the purchase's first key, built but not
    /// staged; null for a zero fee. May throw (an overflow).
    /// </summary>
    public static AccountingEventModel? BuildLiquidityPurchase(ChannelId channelId, TxId fundingTxId, bool weBought,
                                                               CompactPubKey? peer, ulong requestedSat,
                                                               ulong contributedSat, ulong miningFeeSat,
                                                               ulong serviceFeeSat, AccountingLiquidityKind kind,
                                                               DateTimeOffset occurredAt, uint? blockHeight = null,
                                                               ShortChannelId? shortChannelId = null)
    {
        var miningFeeMsat = checked((long)miningFeeSat * 1_000);
        var serviceFeeMsat = checked((long)serviceFeeSat * 1_000);
        var totalMsat = checked(miningFeeMsat + serviceFeeMsat);
        if (totalMsat == 0)
            return null;

        return new AccountingEventModel
        {
            EventKey = AccountingEventKeys.LiquidityFee(channelId, fundingTxId),
            Kind = weBought ? AccountingEventKind.LiquidityFeePaid : AccountingEventKind.LiquidityFeeEarned,
            OccurredAt = occurredAt,
            BlockHeight = blockHeight,
            ChannelId = channelId,
            ShortChannelId = shortChannelId,
            TxId = fundingTxId,
            Counterparty = peer,
            AmountMsat = weBought ? -totalMsat : totalMsat,
            FeeMsat = weBought ? totalMsat : 0,
            Finality = blockHeight is null ? AccountingFinality.Final : AccountingFinality.Confirmed,
            Details = AccountingDetailsCodec.Create(
                (weBought ? AccountingDetailKeys.BucketFrom : AccountingDetailKeys.BucketTo, ChannelBucket),
                (AccountingDetailKeys.LiquidityRole,
                 weBought ? AccountingDetailKeys.LiquidityBuyer : AccountingDetailKeys.LiquiditySeller),
                (AccountingDetailKeys.Kind, kind switch
                {
                    AccountingLiquidityKind.Open => AccountingDetailKeys.LiquidityKindOpen,
                    AccountingLiquidityKind.Rbf => AccountingDetailKeys.LiquidityKindRbf,
                    AccountingLiquidityKind.Splice => AccountingDetailKeys.LiquidityKindSplice,
                    _ => null
                }),
                (AccountingDetailKeys.PurchaseChannelId, channelId.ToString()),
                (AccountingDetailKeys.PurchaseFundingTxId, fundingTxId.ToString()),
                (AccountingDetailKeys.PurchasePeer, peer?.ToString()),
                (AccountingDetailKeys.RequestedSat, Format(requestedSat)),
                (AccountingDetailKeys.ContributedSat, Format(contributedSat)),
                (AccountingDetailKeys.MiningFeeMsat, Format(miningFeeMsat)),
                (AccountingDetailKeys.ServiceFeeMsat, Format(serviceFeeMsat)))
        };
    }

    /// <summary>The reversal <see cref="RecordLiquidityPurchaseReplacedAsync"/> stages for <paramref name="standing"/>.
    /// </summary>
    public static AccountingEventModel BuildLiquidityReplacement(AccountingEventModel standing,
                                                                 DateTimeOffset occurredAt,
                                                                 TxId? replacementFundingTxId = null)
    {
        ArgumentNullException.ThrowIfNull(standing);
        return new AccountingEventModel
        {
            EventKey = AccountingEventKeys.Replaced(standing.EventKey),
            Kind = AccountingEventKind.Reversal,
            OccurredAt = occurredAt,
            ChannelId = standing.ChannelId,
            ShortChannelId = standing.ShortChannelId,
            TxId = standing.TxId,
            Counterparty = standing.Counterparty,
            AmountMsat = -standing.AmountMsat,
            FeeMsat = -standing.FeeMsat,
            Finality = AccountingFinality.Final,
            Details = AccountingDetailsCodec.Create(
                (AccountingConfirmations.ReversesDetail, standing.EventKey),
                (AccountingConfirmations.OriginalKindDetail, standing.Kind.ToString()),
                (AccountingDetailKeys.ReplacedBy, replacementFundingTxId?.ToString()))
        };
    }

    // The first confirmation key of the purchase that is free; null while one stands (neither replaced nor reversed by
    // a reorg)
    private static string? NextLiquidityKey(string baseKey, IReadOnlyCollection<AccountingEventModel> existing)
    {
        var keys = existing.Select(e => e.EventKey).ToHashSet(StringComparer.Ordinal);
        for (var generation = 1; ; generation++)
        {
            var key = generation == 1 ? baseKey : AccountingEventKeys.Reconfirmed(baseKey, generation);
            var recorded = existing.Where(e => e.Kind != AccountingEventKind.Reversal
                                            && string.Equals(e.EventKey, key, StringComparison.Ordinal))
                                   .ToList();
            if (recorded.Count == 0)
                return key;

            if (recorded.Any(e => !IsLiquidityReversed(e, keys)))
                return null;
        }
    }

    private static AccountingEventModel? FindStandingLiquidity(string baseKey,
                                                               IReadOnlyCollection<AccountingEventModel> existing)
    {
        var keys = existing.Select(e => e.EventKey).ToHashSet(StringComparer.Ordinal);
        return existing.LastOrDefault(e => e.Kind != AccountingEventKind.Reversal
                                        && AccountingConfirmations.IsConfirmationKey(baseKey, e.EventKey)
                                        && !IsLiquidityReversed(e, keys));
    }

    private static bool IsLiquidityReversed(AccountingEventModel accountingEvent, IReadOnlySet<string> keys) =>
        keys.Contains(AccountingEventKeys.Replaced(accountingEvent.EventKey))
     || AccountingConfirmations.IsReversed(accountingEvent, keys);

    private static AccountingEventModel WithKey(AccountingEventModel e, string key) => new()
    {
        EventKey = key,
        Kind = e.Kind,
        OccurredAt = e.OccurredAt,
        BlockHeight = e.BlockHeight,
        ChannelId = e.ChannelId,
        ShortChannelId = e.ShortChannelId,
        PaymentHash = e.PaymentHash,
        TxId = e.TxId,
        OutputIndex = e.OutputIndex,
        Counterparty = e.Counterparty,
        AmountMsat = e.AmountMsat,
        FeeMsat = e.FeeMsat,
        Finality = e.Finality,
        Flags = e.Flags,
        Details = e.Details
    };

    /// <summary>
    /// Stages <see cref="AccountingEventKind.ChannelClosedMutual"/> for a channel whose agreed closing transaction
    /// reached its depth (Closing or Failed, before the save that makes it Closed).
    /// </summary>
    /// <remarks>
    /// The fee we paid is what our balance lost on the way to our closing output: the closing fee when we paid it (the
    /// funder with <c>closing_signed</c>, the closer with <c>option_simple_close</c>), our output when it was too small
    /// to exist, and otherwise only the msat our output could not carry. Who paid the closing fee
    /// (<c>feePaidByUs</c>, with <c>closeProtocol</c> and <c>closer</c>) comes from the protocol recorded with the
    /// closing transaction (NL-610, <see cref="ChannelModel.CloseProtocol"/>); a close agreed before it was recorded
    /// infers it from a loss of 1,000 msat or more (<c>feePayerInferred</c>).
    /// </remarks>
    public static void StageMutualClose(IUnitOfWork unitOfWork, ChannelModel channel, uint? blockHeight,
                                        DateTimeOffset occurredAt, ILogger logger)
    {
        try
        {
            if (unitOfWork.AccountingEventDbRepository is { } events
             && BuildMutualClose(channel, blockHeight, occurredAt, logger) is { } accountingEvent)
                events.Add(accountingEvent);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Could not record the mutual close of channel {ChannelId} in the accounting feed",
                            channel.ChannelId);
        }
    }

    /// <summary>
    /// Records <see cref="AccountingEventKind.ChannelClosedMutual"/> again for a Closed channel whose closing transaction
    /// confirmed again at <paramref name="blockHeight"/> after a reorg (NL-607): the chain monitor reversed the first
    /// one when it rewound the closing watch. Under the next confirmation key
    /// (<see cref="AccountingConfirmations.NextConfirmationKey"/>), so nothing is written while a confirmation stands;
    /// in its own save; never throws.
    /// </summary>
    public static async Task RecordMutualCloseAgainAsync(IUnitOfWork unitOfWork, ChannelModel channel,
                                                         uint blockHeight, DateTimeOffset occurredAt, ILogger logger)
    {
        try
        {
            if (unitOfWork.AccountingEventDbRepository is not { } events
             || BuildMutualClose(channel, blockHeight, occurredAt, logger) is not { } built)
                return;

            // A memo close of the backfill (a channel closed before the cutover) is never recorded again as a real
            // one: the opening balances left that channel out (NL-737)
            var existing = await events.GetByKeyPrefixAsync(built.EventKey);
            if (existing.Count == 0 || existing.Any(e => e.Details.ContainsKey(AccountingDetailKeys.Memo))
             || AccountingConfirmations.NextConfirmationKey(built.EventKey, existing) is not { } key)
                return;

            events.Add(AccountingConfirmations.CreateReconfirmation(built, key, blockHeight, null, occurredAt));
            await unitOfWork.SaveChangesAsync();
            logger.LogInformation("Recorded the mutual close of channel {ChannelId} again: its closing transaction "
                                + "confirmed again at block {Height}", channel.ChannelId, blockHeight);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Could not record the mutual close of channel {ChannelId} again in the accounting feed",
                            channel.ChannelId);
        }
    }

    /// <summary>
    /// The event <see cref="StageMutualClose"/> stages, built but not staged; null when the channel has no closing
    /// transaction. The backfill writes it as a memo event (NL-602 A1-T6). May throw.
    /// </summary>
    public static AccountingEventModel? BuildMutualClose(ChannelModel channel, uint? blockHeight,
                                                         DateTimeOffset occurredAt, ILogger logger)
    {
        if (channel.ClosingTransaction is not { } closingTransaction)
            return null;

        var balanceMsat = checked((long)channel.LocalBalance.MilliSatoshi);
        long? ourOutputSat = null;
        uint? ourOutputIndex = null;
        long? closingFeeSat = null;
        try
        {
            var transaction = Transaction.Load(closingTransaction.RawTxBytes, Network.Main);
            var outputsSat = transaction.Outputs.Sum(o => o.Value.Satoshi);
            if (channel.FundingOutput is { } funding)
                closingFeeSat = Math.Max(0, funding.Amount.Satoshi - outputsSat);

            if (channel.LocalShutdownScript is { } script)
            {
                var scriptBytes = (byte[])script;
                ourOutputSat = 0;
                for (var i = 0; i < transaction.Outputs.Count; i++)
                {
                    if (!transaction.Outputs[i].ScriptPubKey.ToBytes().AsSpan().SequenceEqual(scriptBytes))
                        continue;

                    ourOutputSat += transaction.Outputs[i].Value.Satoshi;
                    ourOutputIndex ??= (uint)i;
                }
            }
        }
        catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException
                                      or EndOfStreamException)
        {
            logger.LogWarning(e, "The closing transaction {TxId} of channel {ChannelId} could not be read for the "
                               + "accounting feed", closingTransaction.TxId, channel.ChannelId);
        }

        long? feeMsat = ourOutputSat is { } output ? Math.Max(0, balanceMsat - checked(output * 1_000)) : null;

        // NL-610: who paid the closing fee follows from the recorded protocol (the funder with closing_signed, the
        // closer with option_simple_close); only a close agreed before it was recorded falls back to our loss
        var feePaidByUs = channel.CloseProtocol switch
        {
            MutualCloseProtocol.Legacy => (bool?)channel.IsInitiator,
            MutualCloseProtocol.Simple => channel.LocalIsCloser,
            _ => null
        };
        var feePayerInferred = feePaidByUs is null && feeMsat is not null;
        feePaidByUs ??= feeMsat is { } lost ? lost >= 1_000 : null;
        return new AccountingEventModel
        {
            EventKey = AccountingEventKeys.ChannelClosedMutual(channel.ChannelId, closingTransaction.TxId),
            Kind = AccountingEventKind.ChannelClosedMutual,
            OccurredAt = occurredAt,
            BlockHeight = blockHeight,
            ChannelId = channel.ChannelId,
            ShortChannelId = IsSet(channel.ShortChannelId) ? channel.ShortChannelId : (ShortChannelId?)null,
            TxId = closingTransaction.TxId,
            OutputIndex = ourOutputIndex,
            Counterparty = channel.RemoteNodeId,
            AmountMsat = -balanceMsat,
            FeeMsat = feeMsat ?? 0,
            Finality = AccountingFinality.Confirmed,
            Details = AccountingDetailsCodec.Create(
                (AccountingDetailKeys.BucketFrom, ChannelBucket),
                (AccountingDetailKeys.BucketTo, WalletBucket),
                ("balanceMsat", Format(balanceMsat)),
                ("ourOutputSat", ourOutputSat is { } ours ? Format(ours) : null),
                ("closingFeeSat", closingFeeSat is { } fee ? Format(fee) : null),
                ("feePaidByUs", feePaidByUs is { } paid ? Format(paid) : null),
                ("feePayerInferred", feePayerInferred ? "true" : null),
                ("closeProtocol", channel.CloseProtocol switch
                {
                    MutualCloseProtocol.Legacy => "legacy",
                    MutualCloseProtocol.Simple => "simple",
                    _ => null
                }),
                ("closer", channel.LocalIsCloser switch
                {
                    true => "us",
                    false => "peer",
                    null => null
                }),
                ("isInitiator", Format(channel.IsInitiator)),
                ("feeUnknown", feeMsat is null ? "true" : null))
        };
    }

    /// <summary>
    /// Our share of an interactive transaction's fee in msat: what our wallet inputs bring, less our own outputs (change,
    /// a splice-out) and less what we added to the shared funding output (<paramref name="localContributionMsat"/>: our
    /// share of a dual-funded open's output, or a splice's change of our balance). The shared input and output belong to
    /// both and are not counted. Never negative.
    /// </summary>
    public static long GetLocalFeeShareMsat(ConstructedInteractiveTx transaction, long localContributionMsat)
    {
        var (inputsSat, outputsSat) = GetLocalWalletAmounts(transaction);
        return Math.Max(0, checked((inputsSat - outputsSat) * 1_000 - localContributionMsat));
    }

    /// <summary>What our own (not shared) inputs spend and our own outputs pay, in satoshis.</summary>
    private static (long InputsSat, long OutputsSat) GetLocalWalletAmounts(ConstructedInteractiveTx transaction)
    {
        var inputs = transaction.Inputs.Where(i => i is { AddedBy: InteractiveTxParty.Local, IsShared: false })
                                .Sum(i => i.Amount.Satoshi);
        var outputs = transaction.Outputs.Where(o => o is { AddedBy: InteractiveTxParty.Local, IsShared: false })
                                 .Sum(o => o.Amount.Satoshi);
        return (inputs, outputs);
    }

    /// <summary>The push recorded at the open (NL-605), or null when none was (or this unit of work stores none).</summary>
    private static async Task<LightningMoney?> GetPushAmountAsync(IUnitOfWork unitOfWork, ChannelId channelId)
    {
        try
        {
            return unitOfWork.ChannelFundingDbRepository is { } fundings
                       ? await fundings.GetPushAmountAsync(channelId)
                       : null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The latest stored negotiation of <paramref name="channelId"/> that built <paramref name="txId"/>.</summary>
    private static async Task<InteractiveTxSessionModel?> FindAttemptAsync(IUnitOfWork unitOfWork, ChannelId channelId,
                                                                          TxId txId)
    {
        IReadOnlyList<InteractiveTxSessionModel>? sessions;
        try
        {
            sessions = unitOfWork.InteractiveTxSessionDbRepository is { } repository
                           ? await repository.GetByChannelIdAsync(channelId)
                           : null;
        }
        catch (Exception e) when (e is NotSupportedException or NotImplementedException)
        {
            return null;
        }

        return sessions?.LastOrDefault(s => s.ConstructedTx?.TxId == txId);
    }

    private static bool IsSet(ShortChannelId shortChannelId) => ((byte[]?)shortChannelId)?.Length > 0;

    private static string Format(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Format(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Format(bool value) => value ? "true" : "false";
}