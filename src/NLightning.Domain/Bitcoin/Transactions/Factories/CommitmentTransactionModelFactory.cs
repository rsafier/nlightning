using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.Transactions.Enums;
using NLightning.Domain.Bitcoin.Transactions.Interfaces;
using NLightning.Domain.Bitcoin.Transactions.Models;
using NLightning.Domain.Bitcoin.Transactions.Outputs;
using NLightning.Domain.Channels.Commitments;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Channels.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Crypto.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Protocol.Models;

namespace NLightning.Domain.Bitcoin.Transactions.Factories;

public class CommitmentTransactionModelFactory : ICommitmentTransactionModelFactory
{
    private readonly ICommitmentKeyDerivationService _commitmentKeyDerivationService;
    private readonly ILightningSigner _lightningSigner;

    public CommitmentTransactionModelFactory(ICommitmentKeyDerivationService commitmentKeyDerivationService,
                                             ILightningSigner lightningSigner)
    {
        _commitmentKeyDerivationService = commitmentKeyDerivationService;
        _lightningSigner = lightningSigner;
    }

    /// <inheritdoc />
    public CommitmentTransactionModel CreateCommitmentTransactionModel(ChannelModel channel, CommitmentSide side,
                                                                       ulong commitmentNumber)
    {
        ArgumentNullException.ThrowIfNull(channel);

        var remotePerCommitmentPoint = side == CommitmentSide.Remote
                                           ? channel.RemoteKeySet?.CurrentPerCommitmentCompactPoint
                                           : null;

        return CreateCommitmentTransactionModel(channel, CommitmentSpec.FromChannel(channel), side, commitmentNumber,
                                                remotePerCommitmentPoint);
    }

    /// <inheritdoc />
    public CommitmentTransactionModel CreateCommitmentTransactionModel(ChannelModel channel, CommitmentSpec spec,
                                                                       CommitmentSide side, ulong commitmentNumber,
                                                                       CompactPubKey? remotePerCommitmentPoint = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(spec);

        if (commitmentNumber > CommitmentNumber.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(commitmentNumber), commitmentNumber,
                                                  "Commitment numbers are 48-bit values");

        // Guarantee we have a RemoteKeySet
        if (channel.RemoteKeySet is null)
            throw new InvalidOperationException(
                "Channel must have a RemoteKeySet to create a commitment transaction model");

        // Guarantee we have a CommitmentNumber
        if (channel.CommitmentNumber is null)
            throw new InvalidOperationException(
                "Channel must have a CommitmentNumber to create a commitment transaction model");

        // Guarantee we have a FundingOutput
        if (channel.FundingOutput is null)
            throw new InvalidOperationException(
                "Channel must have a FundingOutput to create a commitment transaction model");

        // The remote holder's point must be given explicitly; ours comes from the signer by commitment number
        switch (side)
        {
            case CommitmentSide.Remote when remotePerCommitmentPoint is null:
                throw new ArgumentNullException(nameof(remotePerCommitmentPoint),
                                                "A remote commitment needs the remote per-commitment point");
            case CommitmentSide.Local when remotePerCommitmentPoint is not null:
                throw new ArgumentException(
                    "A local commitment derives its per-commitment point from the commitment number",
                    nameof(remotePerCommitmentPoint));
        }

        // Get basepoints from the signer instead of the old key set model
        var localBasepoints = _lightningSigner.GetChannelBasepoints(channel.LocalKeySet.KeyIndex);
        var remoteBasepoints = new ChannelBasepoints(channel.RemoteKeySet.FundingCompactPubKey,
                                                     channel.RemoteKeySet.RevocationCompactBasepoint,
                                                     channel.RemoteKeySet.PaymentCompactBasepoint,
                                                     channel.RemoteKeySet.DelayedPaymentCompactBasepoint,
                                                     channel.RemoteKeySet.HtlcCompactBasepoint);

        // Derive the commitment keys from the holder's perspective
        var commitmentKeys = side switch
        {
            // Our per-commitment point comes from the signer for this commitment number (never an index, NL-187)
            CommitmentSide.Local => _commitmentKeyDerivationService.DeriveLocalCommitmentKeys(
                channel.LocalKeySet.KeyIndex, localBasepoints, remoteBasepoints, commitmentNumber),

            CommitmentSide.Remote => _commitmentKeyDerivationService.DeriveRemoteCommitmentKeys(
                localBasepoints, remoteBasepoints, remotePerCommitmentPoint!.Value),

            _ => throw new ArgumentOutOfRangeException(nameof(side), side,
                                                       "You should use either Local or Remote commitment side.")
        };

        var hasAnchors = channel.ChannelConfig.OptionAnchorOutputs;
        var feeRatePerKw = spec.FeeRatePerKw;

        // "local"/"remote" in the spec are the local node; on a commitment they are the holder and the other side
        var toLocalAmount = LightningMoney.MilliSatoshis(side == CommitmentSide.Local
                                                             ? spec.ToLocalMsat
                                                             : spec.ToRemoteMsat);
        var toRemoteAmount = LightningMoney.MilliSatoshis(side == CommitmentSide.Local
                                                              ? spec.ToRemoteMsat
                                                              : spec.ToLocalMsat);

        // Every output of a commitment transaction is trimmed against the dust limit of its holder
        var dustLimitAmount = side == CommitmentSide.Local
                                  ? channel.ChannelConfig.LocalDustLimitAmount
                                  : channel.ChannelConfig.RemoteDustLimitAmount;

        var offeredHtlcOutputs = new List<OfferedHtlcOutputInfo>();
        var receivedHtlcOutputs = new List<ReceivedHtlcOutputInfo>();
        foreach (var htlc in spec.Htlcs)
        {
            // Offered or received from the perspective of the commitment holder
            var isOffered = side == CommitmentSide.Local
                                ? htlc.Direction == HtlcDirection.Outgoing
                                : htlc.Direction == HtlcDirection.Incoming;

            // Trim the HTLC if its amount minus the second-stage fee is below the holder's dust limit
            if (CommitmentFeeCalculator.IsHtlcTrimmed(htlc.Amount, isOffered, dustLimitAmount, feeRatePerKw,
                                                      hasAnchors))
                continue;

            if (isOffered)
                offeredHtlcOutputs.Add(new OfferedHtlcOutputInfo(htlc, commitmentKeys.LocalHtlcPubKey,
                                                                 commitmentKeys.RemoteHtlcPubKey,
                                                                 commitmentKeys.RevocationPubKey));
            else
                receivedHtlcOutputs.Add(new ReceivedHtlcOutputInfo(htlc, commitmentKeys.LocalHtlcPubKey,
                                                                   commitmentKeys.RemoteHtlcPubKey,
                                                                   commitmentKeys.RevocationPubKey));
        }

        // Base fee: feerate_per_kw * (724 or 1124 + 172 per untrimmed HTLC) / 1000, rounded down
        var untrimmedHtlcCount = offeredHtlcOutputs.Count + receivedHtlcOutputs.Count;
        var fee = CommitmentFeeCalculator.CommitmentBaseFee(feeRatePerKw, hasAnchors, untrimmedHtlcCount);

        // The funder pays the base fee and, with option_anchors, both anchor outputs. Its output may end at zero.
        var funderCost = CommitmentFeeCalculator.FunderCost(feeRatePerKw, hasAnchors, untrimmedHtlcCount);
        ref var feePayerAmount =
            ref GetFeePayerAmount(side, channel.IsInitiator, ref toLocalAmount, ref toRemoteAmount);
        feePayerAmount = feePayerAmount > funderCost
                             ? feePayerAmount - funderCost
                             : LightningMoney.Zero;

        // The channel reserve is an update-validation rule, never a transaction-building one: a commitment whose outputs
        // are both below the reserve (e.g. Appendix C "fee greater than funder amount") must still build (NL-196).

        // Outputs are whole satoshis (rounded down) and omitted below the holder's dust limit
        var toSelfDelay = channel.ChannelConfig.ToSelfDelay;
        ToLocalOutputInfo? toLocalOutput = null;
        if (toLocalAmount.Satoshi >= dustLimitAmount.Satoshi)
            toLocalOutput = new ToLocalOutputInfo(LightningMoney.Satoshis(toLocalAmount.Satoshi),
                                                  commitmentKeys.LocalDelayedPubKey, commitmentKeys.RevocationPubKey,
                                                  toSelfDelay);

        ToRemoteOutputInfo? toRemoteOutput = null;
        if (toRemoteAmount.Satoshi >= dustLimitAmount.Satoshi)
        {
            var remotePubKey = side == CommitmentSide.Local
                                   ? channel.RemoteKeySet.PaymentCompactBasepoint
                                   : channel.LocalKeySet.PaymentCompactBasepoint;

            toRemoteOutput = new ToRemoteOutputInfo(LightningMoney.Satoshis(toRemoteAmount.Satoshi), remotePubKey,
                                                    hasAnchors);
        }

        AnchorOutputInfo? localAnchorOutput = null;
        AnchorOutputInfo? remoteAnchorOutput = null;
        if (hasAnchors)
        {
            // to_local_anchor belongs to the commitment holder, to_remote_anchor to the other side. Each exists only
            // if its side's balance output exists or there are untrimmed HTLCs.
            var holderFundingPubKey = side == CommitmentSide.Local
                                          ? channel.LocalKeySet.FundingCompactPubKey
                                          : channel.RemoteKeySet.FundingCompactPubKey;
            var counterpartyFundingPubKey = side == CommitmentSide.Local
                                                ? channel.RemoteKeySet.FundingCompactPubKey
                                                : channel.LocalKeySet.FundingCompactPubKey;
            if (toLocalOutput is not null || untrimmedHtlcCount > 0)
                localAnchorOutput = new AnchorOutputInfo(holderFundingPubKey, true);
            if (toRemoteOutput is not null || untrimmedHtlcCount > 0)
                remoteAnchorOutput = new AnchorOutputInfo(counterpartyFundingPubKey, false);
        }

        return new CommitmentTransactionModel(channel.CommitmentNumber, commitmentNumber, fee, channel.FundingOutput,
                                              localAnchorOutput, remoteAnchorOutput, toLocalOutput, toRemoteOutput,
                                              offeredHtlcOutputs, receivedHtlcOutputs)
        {
            FeeRatePerKw = feeRatePerKw,
            HasAnchors = hasAnchors,
            ToSelfDelay = toSelfDelay,
            LocalDelayedPubKey = commitmentKeys.LocalDelayedPubKey,
            RevocationPubKey = commitmentKeys.RevocationPubKey
        };
    }

    private static ref LightningMoney GetFeePayerAmount(CommitmentSide side, bool isInitiator,
                                                        ref LightningMoney toLocal,
                                                        ref LightningMoney toRemote)
    {
        // If we're the initiator, and it's our tx, deduct from toLocal
        // If not initiator and our tx, deduct from toRemote
        // For remote tx, logic is reversed
        if ((side == CommitmentSide.Local && isInitiator) || (side == CommitmentSide.Remote && !isInitiator))
            return ref toLocal;

        return ref toRemote;
    }
}