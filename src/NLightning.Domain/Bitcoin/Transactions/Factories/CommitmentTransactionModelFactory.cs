using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.Transactions.Enums;
using NLightning.Domain.Bitcoin.Transactions.Interfaces;
using NLightning.Domain.Bitcoin.Transactions.Models;
using NLightning.Domain.Bitcoin.Transactions.Outputs;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Channels.Models;
using NLightning.Domain.Channels.ValueObjects;
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

    public CommitmentTransactionModel CreateCommitmentTransactionModel(ChannelModel channel, CommitmentSide side,
                                                                       ulong commitmentNumber)
    {
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

        // Create base output information
        ToLocalOutputInfo? toLocalOutput = null;
        ToRemoteOutputInfo? toRemoteOutput = null;
        AnchorOutputInfo? localAnchorOutput = null;
        AnchorOutputInfo? remoteAnchorOutput = null;
        var offeredHtlcOutputs = new List<OfferedHtlcOutputInfo>();
        var receivedHtlcOutputs = new List<ReceivedHtlcOutputInfo>();

        // Get the HTLCs based on the commitment side
        var htlcs = new List<Htlc>();
        htlcs.AddRange(channel.LocalOfferedHtlcs?.ToList() ?? []);
        htlcs.AddRange(channel.RemoteOfferedHtlcs?.ToList() ?? []);

        // Get basepoints from the signer instead of the old key set model
        var localBasepoints = _lightningSigner.GetChannelBasepoints(channel.LocalKeySet.KeyIndex);
        var remoteBasepoints = new ChannelBasepoints(channel.RemoteKeySet!.FundingCompactPubKey,
                                                     channel.RemoteKeySet.RevocationCompactBasepoint,
                                                     channel.RemoteKeySet.PaymentCompactBasepoint,
                                                     channel.RemoteKeySet.DelayedPaymentCompactBasepoint,
                                                     channel.RemoteKeySet.HtlcCompactBasepoint);

        // Derive the commitment keys from the appropriate perspective
        var commitmentKeys = side switch
        {
            // Our per-commitment point comes from the signer for this commitment number (never an index, NL-187)
            CommitmentSide.Local => _commitmentKeyDerivationService.DeriveLocalCommitmentKeys(
                channel.LocalKeySet.KeyIndex, localBasepoints, remoteBasepoints, commitmentNumber),

            CommitmentSide.Remote => _commitmentKeyDerivationService.DeriveRemoteCommitmentKeys(
                localBasepoints, remoteBasepoints, channel.RemoteKeySet.CurrentPerCommitmentCompactPoint),

            _ => throw new ArgumentOutOfRangeException(nameof(side), side,
                                                       "You should use either Local or Remote commitment side.")
        };

        var hasAnchors = channel.ChannelConfig.OptionAnchorOutputs;
        var feeRatePerKw = (ulong)channel.ChannelConfig.FeeRateAmountPerKw.Satoshi;

        // Set initial amounts for to_local and to_remote outputs
        var toLocalAmount = side == CommitmentSide.Local
                                ? channel.LocalBalance
                                : channel.RemoteBalance;

        var toRemoteAmount = side == CommitmentSide.Local
                                 ? channel.RemoteBalance
                                 : channel.LocalBalance;

        // Every output of a commitment transaction is trimmed against the dust limit of its holder
        var dustLimitAmount = side == CommitmentSide.Local
                                  ? channel.ChannelConfig.LocalDustLimitAmount
                                  : channel.ChannelConfig.RemoteDustLimitAmount;

        foreach (var htlc in htlcs)
        {
            // Determine if this is an offered or received HTLC from the perspective of the commitment holder
            var isOffered = side == CommitmentSide.Local
                                ? htlc.Direction == HtlcDirection.Outgoing
                                : htlc.Direction == HtlcDirection.Incoming;

            // The HTLC amount comes out of the balance of the side that offered it
            if (isOffered)
                toLocalAmount = toLocalAmount > htlc.Amount ? toLocalAmount - htlc.Amount : LightningMoney.Zero;
            else
                toRemoteAmount = toRemoteAmount > htlc.Amount
                                     ? toRemoteAmount - htlc.Amount
                                     : LightningMoney.Zero;

            // Trim the HTLC if its amount minus the second-stage fee is below the holder's dust limit
            if (CommitmentFeeCalculator.IsHtlcTrimmed(htlc.Amount, isOffered, dustLimitAmount, feeRatePerKw,
                                                      hasAnchors))
                continue;

            if (isOffered)
            {
                offeredHtlcOutputs.Add(new OfferedHtlcOutputInfo(
                                           htlc,
                                           commitmentKeys.LocalHtlcPubKey,
                                           commitmentKeys.RemoteHtlcPubKey,
                                           commitmentKeys.RevocationPubKey));
            }
            else
            {
                receivedHtlcOutputs.Add(new ReceivedHtlcOutputInfo(
                                            htlc,
                                            commitmentKeys.LocalHtlcPubKey,
                                            commitmentKeys.RemoteHtlcPubKey,
                                            commitmentKeys.RevocationPubKey));
            }
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

        if (hasAnchors)
        {
            // to_local_anchor belongs to the commitment holder, to_remote_anchor to the other side
            var holderFundingPubKey = side == CommitmentSide.Local
                                          ? channel.LocalKeySet.FundingCompactPubKey
                                          : channel.RemoteKeySet.FundingCompactPubKey;
            var counterpartyFundingPubKey = side == CommitmentSide.Local
                                                ? channel.RemoteKeySet.FundingCompactPubKey
                                                : channel.LocalKeySet.FundingCompactPubKey;
            localAnchorOutput = new AnchorOutputInfo(holderFundingPubKey, true);
            remoteAnchorOutput = new AnchorOutputInfo(counterpartyFundingPubKey, false);
        }

        // The channel reserve is an update-validation rule, never a transaction-building one: a commitment whose outputs
        // are both below the reserve (e.g. Appendix C "fee greater than funder amount") must still build (NL-196).

        // Only create output if the amount is above the dust limit
        if (toLocalAmount.Satoshi >= dustLimitAmount.Satoshi)
        {
            toLocalOutput = new ToLocalOutputInfo(toLocalAmount, commitmentKeys.LocalDelayedPubKey,
                                                  commitmentKeys.RevocationPubKey,
                                                  channel.ChannelConfig.ToSelfDelay);
        }

        if (toRemoteAmount.Satoshi >= dustLimitAmount.Satoshi)
        {
            var remotePubKey = side == CommitmentSide.Local
                                   ? channel.RemoteKeySet.PaymentCompactBasepoint
                                   : channel.LocalKeySet.PaymentCompactBasepoint;

            toRemoteOutput =
                new ToRemoteOutputInfo(toRemoteAmount, remotePubKey, channel.ChannelConfig.OptionAnchorOutputs);
        }

        if (offeredHtlcOutputs.Count == 0 && receivedHtlcOutputs.Count == 0)
        {
            // If no HTLCs and no to_local, we can remove our anchor output
            if (toLocalOutput is null)
                localAnchorOutput = null;

            // If no HTLCs and no to_remote, we can remove their anchor output
            if (toRemoteOutput is null)
                remoteAnchorOutput = null;
        }

        // Create and return the commitment transaction model
        return new CommitmentTransactionModel(channel.CommitmentNumber!, commitmentNumber, fee, channel.FundingOutput!,
                                              localAnchorOutput, remoteAnchorOutput, toLocalOutput, toRemoteOutput,
                                              offeredHtlcOutputs, receivedHtlcOutputs);
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