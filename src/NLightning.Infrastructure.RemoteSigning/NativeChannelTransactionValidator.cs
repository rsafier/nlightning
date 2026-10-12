using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.Transactions.Enums;
using NLightning.Domain.Bitcoin.Transactions.Interfaces;
using NLightning.Domain.Bitcoin.Transactions.Outputs;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.Commitments;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Channels.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Enums;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Models;
using NLightning.Infrastructure.Bitcoin.Builders;
using NLightning.Infrastructure.Bitcoin.Builders.Interfaces;
using NLightning.Infrastructure.Crypto.Hashes;

namespace NLightning.Infrastructure.RemoteSigning;

/// <summary>
/// Reconstructs commitments from independently enrolled channel terms and authorized millisatoshi state.
/// Node transaction bytes and registration counters never establish the trusted financial state.
/// </summary>
public sealed class NativeChannelTransactionValidator(ILightningSigner signerKeys,
                                                      ICommitmentTransactionModelFactory factory,
                                                      ICommitmentTransactionBuilder builder,
                                                      IAuthenticatedNativeChainEvidence chain,
                                                      INativeAuthorizedChannelStateStore state)
{
    public void ValidateCommitment(NativeSignerBinding binding, ChannelId channelId, CommitmentSide holder,
                                   ulong number, SignedTransaction transaction)
    {
        var expected = Reconstruct(binding, channelId, holder, number);
        if (!transaction.RawTxBytes.AsSpan().SequenceEqual(expected.RawTxBytes))
            throw new UnauthorizedAccessException("Commitment transaction differs from independently authorized BOLT 3 content.");
        if (transaction.TxId != expected.TxId)
            throw new UnauthorizedAccessException("Commitment transaction identity differs from its reconstructed bytes.");
    }

    public SignedTransaction Reconstruct(NativeSignerBinding binding, ChannelId channelId,
                                         CommitmentSide holder, ulong number)
    {
        if (holder is not (CommitmentSide.Local or CommitmentSide.Remote) || number > CommitmentNumber.MaxValue)
            throw new ArgumentException("Invalid commitment holder or number.");
        var enrollment = state.GetEnrollment(binding, channelId);
        ValidateEnrollment(binding, enrollment);
        var approved = state.GetCommitment(binding, channelId, holder, number);
        if (approved.Holder != holder || approved.Number != number
         || approved.FeeratePerKw < enrollment.MinimumFeeratePerKw
         || approved.FeeratePerKw > enrollment.MaximumFeeratePerKw)
            throw new UnauthorizedAccessException("Commitment identity or feerate violates enrolled policy.");
        var spec = new CommitmentSpec(holder, approved.FeeratePerKw, approved.LocalMsat,
            approved.RemoteMsat, approved.Htlcs);
        if (spec.TotalMsat != checked(enrollment.FundingSatoshis * 1000UL)
         || approved.Htlcs.Any(h => h.AmountMsat == 0 || h.CltvExpiry >= ChannelCommitments.MaxCltvExpiry
             || h.Direction is not (HtlcDirection.Outgoing or HtlcDirection.Incoming))
         || approved.Htlcs.Select(h => (h.Direction, h.Id)).Distinct().Count() != approved.Htlcs.Count)
            throw new UnauthorizedAccessException("Authorized commitment does not conserve exact millisatoshi funding.");
        if (holder == CommitmentSide.Remote && approved.RemotePerCommitmentPoint is null
         || holder == CommitmentSide.Local && approved.RemotePerCommitmentPoint is not null)
            throw new UnauthorizedAccessException("Commitment per-commitment point does not match its holder.");
        NativeChannelStaticPolicy.Validate(enrollment, spec, enrollment.MaximumDustExposureMsat);
        var channel = Channel(enrollment, approved);
        var model = factory.CreateCommitmentTransactionModel(channel, CommitmentTxSpec.FromCommitmentSpec(spec),
            holder, number, approved.RemotePerCommitmentPoint);
        return builder.Build(model);
    }

    /// <summary>
    /// Fresh registration cannot raise revocation counters. Recovery restores counters from signer history;
    /// node-provided advancement and splice snapshots require separate independently validated transitions.
    /// </summary>
    public void ValidateInitialRegistration(NativeSignerBinding binding, ChannelId channelId,
                                            ChannelSigningInfo proposed)
    {
        var enrolled = state.GetEnrollment(binding, channelId);
        ValidateEnrollment(binding, enrolled);
        if (proposed.FundingTxId != enrolled.FundingTransactionId
         || proposed.FundingOutputIndex != enrolled.FundingOutputIndex
         // ChannelSigningInfo's legacy numeric funding field carries millisatoshis despite its name.
         || proposed.FundingSatoshis != checked(enrolled.FundingSatoshis * 1000UL)
         || proposed.ChannelKeyIndex != enrolled.ChannelKeyIndex
         || proposed.LocalFundingPubKey != enrolled.LocalBasepoints.FundingPubKey
         || proposed.RemoteFundingPubKey != enrolled.RemoteBasepoints.FundingPubKey
         || proposed.RemoteHtlcBasepoint != enrolled.RemoteBasepoints.HtlcBasepoint
         || proposed.LocalCommitmentNumber != 0 || proposed.LocalFundingKeyIndex != 0
         || proposed.FundingKeysUnknown || proposed.IsSimpleTaproot || proposed.IsDualFunded
         || proposed.BroadcastSignedCommitmentNumber is not null
         || proposed.Fundings is { Count: > 0 } || proposed.PersistedSpliceCommitments is { Count: > 0 }
         || proposed.RemoteNodeId is { } peer && peer != enrolled.RemoteNodePublicKey)
            throw new UnauthorizedAccessException("Node registration differs from independently enrolled channel authority.");
    }

    private void ValidateEnrollment(NativeSignerBinding binding, NativeChannelEnrollment enrollment)
    {
        if (enrollment.Binding != binding || signerKeys.GetNodePublicKey().ToString() != binding.PublicKey
         || signerKeys.GetChannelBasepoints(enrollment.ChannelKeyIndex) != enrollment.LocalBasepoints
         || enrollment.FundingSatoshis == 0 || enrollment.MinimumFeeratePerKw > enrollment.MaximumFeeratePerKw)
            throw new UnauthorizedAccessException("Channel enrollment does not belong to the installed signer.");
        chain.RequireFresh(binding);
        var transactionId = enrollment.FundingTransactionId.ToString();
        var proof = chain.GetOutput(binding, transactionId, enrollment.FundingOutputIndex);
        var expectedScript = new FundingOutputBuilder().Build(Funding(enrollment)).BitcoinScriptPubKey;
        // A channel output is jointly owned: wallet-only NodeId/OwnerId ownership labels are not authority here.
        if (proof.TransactionId != transactionId || proof.OutputIndex != enrollment.FundingOutputIndex
         || !proof.Unspent || proof.AmountSatoshis < 0
         || (ulong)proof.AmountSatoshis != enrollment.FundingSatoshis
         || !proof.ScriptPubKey.AsSpan().SequenceEqual((byte[])expectedScript))
            throw new UnauthorizedAccessException("Funding does not match independently verified enrolled channel output.");
        chain.RequireUnchanged(binding);
    }

    private static FundingOutputInfo Funding(NativeChannelEnrollment enrollment) => new(
        LightningMoney.Satoshis(enrollment.FundingSatoshis), enrollment.LocalBasepoints.FundingPubKey,
        enrollment.RemoteBasepoints.FundingPubKey, enrollment.FundingTransactionId, enrollment.FundingOutputIndex);

    private static ChannelModel Channel(NativeChannelEnrollment enrollment, NativeAuthorizedCommitment state)
    {
        var parameters = new ChannelParams(Party(enrollment.Local), Party(enrollment.Remote),
            LightningMoney.Satoshis((ulong)state.FeeratePerKw), 1, enrollment.HasAnchors, FeatureSupport.No);
        var local = enrollment.LocalBasepoints;
        var remote = enrollment.RemoteBasepoints;
        var obscure = enrollment.IsInitiator
            ? new CommitmentNumber(local.PaymentBasepoint, remote.PaymentBasepoint, new Sha256())
            : new CommitmentNumber(remote.PaymentBasepoint, local.PaymentBasepoint, new Sha256());
        var localSet = new ChannelKeySetModel(enrollment.ChannelKeyIndex, local.FundingPubKey,
            local.RevocationBasepoint, local.PaymentBasepoint, local.DelayedPaymentBasepoint,
            local.HtlcBasepoint, local.FundingPubKey);
        var remoteSet = new ChannelKeySetModel(0, remote.FundingPubKey, remote.RevocationBasepoint,
            remote.PaymentBasepoint, remote.DelayedPaymentBasepoint, remote.HtlcBasepoint,
            state.RemotePerCommitmentPoint ?? remote.FundingPubKey);
        return new ChannelModel(parameters, enrollment.ChannelId, obscure, Funding(enrollment),
            enrollment.IsInitiator, null, null, LightningMoney.Zero, localSet, 0, 0,
            LightningMoney.Zero, remoteSet, 0, enrollment.RemoteNodePublicKey, 0,
            ChannelState.Open, ChannelVersion.V1);
    }

    private static ChannelParty Party(NativeChannelParty party) => new(
        LightningMoney.Satoshis(party.DustLimitSatoshis), LightningMoney.Satoshis(party.ReserveSatoshis),
        LightningMoney.MilliSatoshis(party.HtlcMinimumMsat), party.MaximumHtlcs,
        LightningMoney.MilliSatoshis(party.MaximumInFlightMsat), party.ToSelfDelay);
}