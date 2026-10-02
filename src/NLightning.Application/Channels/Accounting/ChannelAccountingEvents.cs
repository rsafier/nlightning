using System.Globalization;
using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Application.Channels.Accounting;

using Domain.Accounting.Constants;
using Domain.Accounting.Enums;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Channels.ValueObjects;
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
    public static async Task StageChannelFundedAsync(IUnitOfWork unitOfWork, ChannelModel channel,
                                                     DateTimeOffset occurredAt, ILogger logger)
    {
        try
        {
            if (unitOfWork.AccountingEventDbRepository is not { } events
             || channel.FundingOutput is not { TransactionId: { } fundingTxId } funding)
                return;

            var capacityMsat = checked((long)funding.Amount.MilliSatoshi);
            var isDualFunded = channel.Version == ChannelVersion.V2;
            var contributionMsat = 0L;
            long? feeMsat = null;
            ulong? totalFeeSat = null;
            if (isDualFunded)
            {
                var attempt = await FindAttemptAsync(unitOfWork, channel.ChannelId, fundingTxId);
                contributionMsat = attempt?.LocalFundingSatoshis is { } localSat
                                       ? checked(localSat * 1_000)
                                       : checked((long)channel.LocalBalance.MilliSatoshi);
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
            var height = channel.FundingCreatedAtBlockHeight > 0 ? channel.FundingCreatedAtBlockHeight : (uint?)null;
            var shortChannelId = IsSet(channel.ShortChannelId) ? channel.ShortChannelId : (ShortChannelId?)null;
            events.Add(new AccountingEventModel
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
                    ("pushMsat", push is null ? null : Format(push.MilliSatoshi)),
                    ("pushUnknown", push is null && !isDualFunded ? "true" : null))
            });

            // The push is its own fact: value that changed hands at the open (NL-605); a dual-funded open has none
            if (push is not { IsZero: false })
                return;

            var pushMsat = checked((long)push.MilliSatoshi);
            events.Add(new AccountingEventModel
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
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Could not record the funding of channel {ChannelId} in the accounting feed",
                            channel.ChannelId);
        }
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
    public static async Task StageSpliceLockedAsync(IUnitOfWork unitOfWork, ChannelModel channel,
                                                    ChannelFunding locked, ChannelFunding previous,
                                                    DateTimeOffset occurredAt, ILogger logger)
    {
        try
        {
            if (unitOfWork.AccountingEventDbRepository is not { } events)
                return;

            var attempt = await FindAttemptAsync(unitOfWork, channel.ChannelId, locked.FundingTxId);
            long? feeMsat = null;
            ulong? totalFeeSat = null;
            long? walletInputsSat = null;
            long? walletOutputsSat = null;
            if (attempt?.ConstructedTx is { } transaction)
            {
                totalFeeSat = SpliceService.GetTotalFee(transaction);
                feeMsat = GetLocalFeeShareMsat(transaction, locked.LocalBalanceDeltaMsat);
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
                AmountMsat = locked.LocalBalanceDeltaMsat,
                FeeMsat = feeMsat ?? 0,
                Finality = AccountingFinality.Confirmed,
                Details = AccountingDetailsCodec.Create(
                    (AccountingDetailKeys.BucketFrom,
                     locked.LocalBalanceDeltaMsat >= 0 ? WalletBucket : ChannelBucket),
                    (AccountingDetailKeys.BucketTo,
                     locked.LocalBalanceDeltaMsat >= 0 ? ChannelBucket : WalletBucket),
                    (AccountingDetailKeys.Kind, locked.Kind.ToString()),
                    ("capacitySat", Format(locked.CapacitySatoshis)),
                    ("previousCapacitySat", Format(previous.CapacitySatoshis)),
                    ("previousFundingTxId", previous.FundingTxId.ToString()),
                    ("grossDeltaMsat", Format(locked.LocalBalanceDeltaMsat)),
                    ("deltaIncludesFee", "true"),
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
    /// Stages <see cref="AccountingEventKind.ChannelClosedMutual"/> for a channel whose agreed closing transaction
    /// reached its depth (Closing or Failed, before the save that makes it Closed).
    /// </summary>
    /// <remarks>
    /// The fee we paid is what our balance lost on the way to our closing output: the closing fee when we paid it (the
    /// funder with <c>closing_signed</c>, the closer with <c>option_simple_close</c>), our output when it was too small
    /// to exist, and otherwise only the msat our output could not carry.
    /// </remarks>
    public static void StageMutualClose(IUnitOfWork unitOfWork, ChannelModel channel, uint? blockHeight,
                                        DateTimeOffset occurredAt, ILogger logger)
    {
        try
        {
            if (unitOfWork.AccountingEventDbRepository is not { } events
             || channel.ClosingTransaction is not { } closingTransaction)
                return;

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
            events.Add(new AccountingEventModel
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
                    ("feePaidByUs", feeMsat is { } paid ? Format(paid >= 1_000) : null),
                    ("isInitiator", Format(channel.IsInitiator)),
                    ("feeUnknown", feeMsat is null ? "true" : null))
            });
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Could not record the mutual close of channel {ChannelId} in the accounting feed",
                            channel.ChannelId);
        }
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