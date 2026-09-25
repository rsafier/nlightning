using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.Transactions.Constants;
using NLightning.Domain.Bitcoin.Transactions.Enums;
using NLightning.Domain.Bitcoin.Transactions.Interfaces;
using NLightning.Domain.Bitcoin.Transactions.Models;
using NLightning.Domain.Bitcoin.Transactions.Outputs;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Channels.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Exceptions;
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
        var feeRatePerKw = channel.ChannelConfig.FeeRateAmountPerKw.Satoshi;

        // BOLT 3 base weight (no HTLC outputs): 724, or 1124 if option_anchors applies
        var weight = hasAnchors
                         ? TransactionConstants.InitialCommitmentTransactionWeightWithAnchor
                         : TransactionConstants.InitialCommitmentTransactionWeightNoAnchor;

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

        if (htlcs is { Count: > 0 })
        {
            // Second-stage HTLC transaction fees; zero when option_anchors applies
            var offeredHtlcFee = hasAnchors
                                     ? LightningMoney.Zero
                                     : LightningMoney.Satoshis(
                                         WeightConstants.HtlcTimeoutWeightNoAnchors * feeRatePerKw / 1000);
            var receivedHtlcFee = hasAnchors
                                      ? LightningMoney.Zero
                                      : LightningMoney.Satoshis(
                                          WeightConstants.HtlcSuccessWeightNoAnchors * feeRatePerKw / 1000);

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
                var htlcFee = isOffered ? offeredHtlcFee : receivedHtlcFee;
                if (htlc.Amount.Satoshi < dustLimitAmount.Satoshi + htlcFee.Satoshi)
                    continue;

                weight += WeightConstants.HtlcOutputWeight;
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
        }

        // Base fee: feerate_per_kw * weight / 1000, rounded down to whole satoshis
        var fee = LightningMoney.Satoshis(weight * feeRatePerKw / 1000);

        // The funder pays the base fee and, with option_anchors, both anchor outputs
        var funderCost = hasAnchors
                             ? fee + 2 * TransactionConstants.AnchorOutputAmount
                             : fee;
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

        // Fail if both amounts are below ChannelReserve
        if (channel.ChannelConfig.ChannelReserveAmount is not null
         && toLocalAmount.Satoshi < channel.ChannelConfig.ChannelReserveAmount.Satoshi
         && toRemoteAmount.Satoshi < channel.ChannelConfig.ChannelReserveAmount.Satoshi)
            throw new ChannelErrorException("Both to_local and to_remote amounts are below the reserve limits.");

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