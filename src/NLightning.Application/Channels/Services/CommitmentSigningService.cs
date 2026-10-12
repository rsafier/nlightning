namespace NLightning.Application.Channels.Services;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Extensions;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Channels.Commitments;
using Domain.Channels.Models;
using Domain.Channels.Splicing;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Money;
using Domain.Signing.Vls;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>
/// Signs and verifies whole commitments (commitment transaction plus its HTLC transactions) for
/// <c>commitment_signed</c>: model factory → commitment builder (with the HTLC output map) → HTLC transaction
/// models and builder → <see cref="ILightningSigner"/>. HTLC signatures are always in commitment output order.
/// </summary>
/// <remarks>
/// Works on a <see cref="ChannelModel"/> (static data) and a <see cref="CommitmentTxSpec"/> (content) and returns the
/// commitment txid with the signatures. The commitment state machine reaches it through the engine ports
/// <see cref="EngineCommitmentSignerPort"/> and <see cref="EngineCommitmentVerifierPort"/> (NL-230).
/// </remarks>
public sealed class CommitmentSigningService
{
    private readonly ICommitmentTransactionModelFactory _commitmentTransactionModelFactory;
    private readonly ICommitmentTransactionBuilder _commitmentTransactionBuilder;
    private readonly IHtlcTransactionBuilder _htlcTransactionBuilder;
    private readonly ILightningSigner _lightningSigner;

    public CommitmentSigningService(ICommitmentTransactionModelFactory commitmentTransactionModelFactory,
                                    ICommitmentTransactionBuilder commitmentTransactionBuilder,
                                    IHtlcTransactionBuilder htlcTransactionBuilder, ILightningSigner lightningSigner)
    {
        _commitmentTransactionModelFactory = commitmentTransactionModelFactory;
        _commitmentTransactionBuilder = commitmentTransactionBuilder;
        _htlcTransactionBuilder = htlcTransactionBuilder;
        _lightningSigner = lightningSigner;
    }

    /// <summary>
    /// Builds the remote commitment <paramref name="remoteCommitmentNumber"/> from <paramref name="spec"/> (the local
    /// node's view) and returns our commitment signature and our signatures for its HTLC transactions, in commitment
    /// output order.
    /// </summary>
    /// <param name="channel">The channel (static data: keys, funding output, dust limits, anchors, funder).</param>
    /// <param name="spec">The balances, feerate and HTLCs of the commitment, from our point of view.</param>
    /// <param name="remoteCommitmentNumber">The number of the remote commitment being signed.</param>
    /// <param name="remotePerCommitmentPoint">The peer's per-commitment point for that commitment.</param>
    public CommitmentTxSignatures SignRemoteCommitment(ChannelModel channel, CommitmentTxSpec spec,
                                                     ulong remoteCommitmentNumber,
                                                     CompactPubKey remotePerCommitmentPoint) =>
        SignRemoteCommitment(channel, null, spec, remoteCommitmentNumber, remotePerCommitmentPoint);

    /// <summary>
    /// <see cref="SignRemoteCommitment(ChannelModel, CommitmentTxSpec, ulong, CompactPubKey)"/> for the commitment
    /// spending <paramref name="funding"/> (splicing plan SP1-C: one commitment per active funding, SP-OP-03).
    /// </summary>
    /// <param name="channel">The channel (static data).</param>
    /// <param name="funding">The funding the commitment spends; null, or the channel's current funding, signs exactly
    /// as the single-funding path. A pending splice is signed against its own outpoint, capacity and funding keys (the
    /// anchors too), with the signer's per-funding <c>SignChannelTransaction</c>.</param>
    /// <param name="spec">The commitment content for that funding (its balances already shifted by the engine).</param>
    /// <param name="remoteCommitmentNumber">The number of the remote commitment being signed.</param>
    /// <param name="remotePerCommitmentPoint">The peer's per-commitment point for that commitment.</param>
    /// <param name="remoteVerificationNonce">Simple taproot channels (NL-877 T3): the peer's verification nonce for
    /// that commitment on that funding; the commitment is then signed with a MuSig2 partial signature
    /// (<see cref="ILightningSigner.SignRemoteCommitmentPartial"/>) and the returned <c>Signature</c> is the zero
    /// signature. Ignored (null) for the other channel types.</param>
    /// <exception cref="Domain.Exceptions.SignerException">A simple taproot channel without
    /// <paramref name="remoteVerificationNonce"/>, or the signer refused.</exception>
    public CommitmentTxSignatures SignRemoteCommitment(ChannelModel channel, ChannelFunding? funding,
                                                     CommitmentTxSpec spec, ulong remoteCommitmentNumber,
                                                     CompactPubKey remotePerCommitmentPoint,
                                                     MusigPublicNonce? remoteVerificationNonce = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(spec);
        var isTaproot = channel.ChannelParams.CommitmentFormat.IsTaproot();
        if (isTaproot && remoteVerificationNonce is null)
            throw new SignerException("Signing the peer's simple taproot commitment needs its verification nonce",
                                      channel.ChannelId);

        var model = _commitmentTransactionModelFactory.CreateCommitmentTransactionModel(
            channel, spec, CommitmentSide.Remote, remoteCommitmentNumber, remotePerCommitmentPoint);
        var otherFunding = GetOtherFunding(channel, funding);
        if (otherFunding is not null)
            model = WithFunding(model, otherFunding, CommitmentSide.Remote);
        var built = _commitmentTransactionBuilder.BuildWithOutputMap(model);

        if (_lightningSigner is IVlsChannelSigner vls)
        {
            if (isTaproot || otherFunding is not null)
                throw new NotSupportedException("VLS supports single-funded ECDSA commitments only.");
            var signatures = vls.SignCounterpartyCommitment(channel, model);
            if (signatures.HtlcSignatures.Count != built.HtlcOutputsInTxOrder.Count)
                throw new SignerException("VLS returned an incorrect HTLC signature count", channel.ChannelId);
            return new CommitmentTxSignatures(built.Transaction.TxId, signatures.Signature,
                                              signatures.HtlcSignatures);
        }

        if (isTaproot)
        {
            // MuSig2 (bolt-simple-taproot.md): our partial signature with a fresh signing nonce, against the peer's
            // verification nonce; the 64-byte signature field is all zeros
            var partial = _lightningSigner.SignRemoteCommitmentPartial(channel.ChannelId, otherFunding?.FundingTxId,
                                                                       built.Transaction,
                                                                       remoteVerificationNonce!.Value);
            var taprootHtlcSignatures =
                _lightningSigner.SignRemoteHtlcTransactions(channel.ChannelId, BuildHtlcSigningContexts(model, built));
            return new CommitmentTxSignatures(built.Transaction.TxId, CommitmentSignatures.ZeroSignature,
                                              taprootHtlcSignatures)
            {
                PartialSignature = partial
            };
        }

        var signature = otherFunding is null
                            ? _lightningSigner.SignChannelTransaction(channel.ChannelId, built.Transaction)
                            : _lightningSigner.SignChannelTransaction(channel.ChannelId, otherFunding.FundingTxId,
                                                                      built.Transaction);
        var htlcSignatures =
            _lightningSigner.SignRemoteHtlcTransactions(channel.ChannelId, BuildHtlcSigningContexts(model, built));

        return new CommitmentTxSignatures(built.Transaction.TxId, signature, htlcSignatures);
    }

    /// <summary>
    /// Builds our local commitment <paramref name="localCommitmentNumber"/> from <paramref name="spec"/> and checks the
    /// peer's commitment signature and its HTLC signatures (count, order, low-S, validity).
    /// </summary>
    /// <returns>The verified signatures with the commitment txid, ready to persist.</returns>
    /// <exception cref="Domain.Exceptions.SignerException">A signature is missing, malformed, high-S or
    /// invalid.</exception>
    public CommitmentTxSignatures VerifyLocalCommitment(ChannelModel channel, CommitmentTxSpec spec,
                                                      ulong localCommitmentNumber, CompactSignature signature,
                                                      IReadOnlyList<CompactSignature> htlcSignatures) =>
        VerifyLocalCommitment(channel, null, spec, localCommitmentNumber, signature, htlcSignatures);

    /// <summary>
    /// <see cref="VerifyLocalCommitment(ChannelModel, CommitmentTxSpec, ulong, CompactSignature, IReadOnlyList{CompactSignature})"/>
    /// for our commitment spending <paramref name="funding"/> (null or the current funding: the single-funding path).
    /// </summary>
    /// <exception cref="Domain.Exceptions.SignerException">A signature is missing, malformed, high-S or invalid, or
    /// the funding is not one the signer knows.</exception>
    /// <param name="channel">The channel (static data).</param>
    /// <param name="funding">The funding the commitment spends (null or the current funding: the single-funding path).</param>
    /// <param name="spec">The commitment content.</param>
    /// <param name="localCommitmentNumber">Our commitment number.</param>
    /// <param name="signature">The peer's commitment signature (the zero signature on a simple taproot channel).</param>
    /// <param name="htlcSignatures">The peer's HTLC signatures, in commitment output order.</param>
    /// <param name="partialSignature">Simple taproot channels (NL-877 T3): the peer's MuSig2 partial signature with its
    /// signing nonce, checked against our verification nonce for <paramref name="localCommitmentNumber"/>
    /// (<see cref="ILightningSigner.ValidateLocalCommitmentPartialSignature"/>) instead of
    /// <paramref name="signature"/>; required for such a channel, ignored for the others.</param>
    public CommitmentTxSignatures VerifyLocalCommitment(ChannelModel channel, ChannelFunding? funding,
                                                      CommitmentTxSpec spec, ulong localCommitmentNumber,
                                                      CompactSignature signature,
                                                      IReadOnlyList<CompactSignature> htlcSignatures,
                                                      MusigPartialSignatureWithNonce? partialSignature = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(htlcSignatures);
        var isTaproot = channel.ChannelParams.CommitmentFormat.IsTaproot();
        if (isTaproot && partialSignature is null)
            throw new SignerException("The peer's simple taproot commitment_signed has no partial signature",
                                      channel.ChannelId);

        var model = _commitmentTransactionModelFactory.CreateCommitmentTransactionModel(
            channel, spec, CommitmentSide.Local, localCommitmentNumber);
        var otherFunding = GetOtherFunding(channel, funding);
        if (otherFunding is not null)
            model = WithFunding(model, otherFunding, CommitmentSide.Local);
        var built = _commitmentTransactionBuilder.BuildWithOutputMap(model);

        if (_lightningSigner is IVlsChannelSigner vls)
        {
            if (isTaproot || otherFunding is not null)
                throw new NotSupportedException("VLS supports single-funded ECDSA commitments only.");
            if (htlcSignatures.Count != built.HtlcOutputsInTxOrder.Count)
                throw new SignerException("Incorrect peer HTLC signature count", channel.ChannelId);
            vls.ValidateHolderCommitment(channel, model, signature, htlcSignatures);
            return new CommitmentTxSignatures(built.Transaction.TxId, signature, htlcSignatures.ToList());
        }

        if (isTaproot)
            _lightningSigner.ValidateLocalCommitmentPartialSignature(channel.ChannelId, otherFunding?.FundingTxId,
                                                                     localCommitmentNumber, partialSignature!.Value,
                                                                     built.Transaction);
        else if (otherFunding is null)
            _lightningSigner.ValidateSignature(channel.ChannelId, signature, built.Transaction);
        else
            _lightningSigner.ValidateSignature(channel.ChannelId, otherFunding.FundingTxId, signature,
                                               built.Transaction);
        _lightningSigner.ValidateLocalHtlcSignatures(channel.ChannelId, BuildHtlcSigningContexts(model, built),
                                                     htlcSignatures);

        return new CommitmentTxSignatures(built.Transaction.TxId, signature, htlcSignatures.ToList())
        {
            PartialSignature = isTaproot ? partialSignature : null
        };
    }

    /// <summary>
    /// The funding to sign for when it is not the channel's current one (the model factory builds on
    /// <see cref="ChannelModel.FundingOutput"/>), else null.
    /// </summary>
    private static ChannelFunding? GetOtherFunding(ChannelModel channel, ChannelFunding? funding)
    {
        if (funding is null)
            return null;

        var current = channel.FundingOutput;
        return current?.TransactionId is { } currentTxId && currentTxId == funding.FundingTxId
                                                         && current.Index == funding.OutputIndex
                   ? null
                   : funding;
    }

    /// <summary>
    /// The same commitment spending <paramref name="funding"/> instead: its outpoint, capacity and funding keys (the
    /// 2-of-2 input script and, with anchors, both anchor outputs, which are keyed to the funding keys, BOLT 3). The
    /// other outputs do not depend on the funding.
    /// </summary>
    internal static CommitmentTransactionModel WithFunding(CommitmentTransactionModel model, ChannelFunding funding,
                                                           CommitmentSide holder)
    {
        var fundingOutput = new FundingOutputInfo(LightningMoney.Satoshis(funding.CapacitySatoshis),
                                                  funding.LocalFundingPubKey, funding.RemoteFundingPubKey,
                                                  funding.FundingTxId, funding.OutputIndex)
        {
            // A simple taproot channel's splice funding is the MuSig2 P2TR output too (NL-965)
            IsSimpleTaproot = model.IsSimpleTaproot
        };
        var (holderKey, counterpartyKey) = holder == CommitmentSide.Local
                                               ? (funding.LocalFundingPubKey, funding.RemoteFundingPubKey)
                                               : (funding.RemoteFundingPubKey, funding.LocalFundingPubKey);
        // Simple taproot anchors are keyed to local_delayedpubkey/remotepubkey, not to the funding: they stay
        var localAnchor = model.IsSimpleTaproot ? model.LocalAnchorOutput
                          : model.LocalAnchorOutput is null ? null : new AnchorOutputInfo(holderKey, true);
        var remoteAnchor = model.IsSimpleTaproot ? model.RemoteAnchorOutput
                           : model.RemoteAnchorOutput is null ? null : new AnchorOutputInfo(counterpartyKey, false);

        return new CommitmentTransactionModel(model.CommitmentNumber, model.Number, model.Fee, fundingOutput,
                                              localAnchor, remoteAnchor, model.ToLocalOutput, model.ToRemoteOutput,
                                              model.OfferedHtlcOutputs, model.ReceivedHtlcOutputs)
        {
            FeeRatePerKw = model.FeeRatePerKw,
            HasAnchors = model.HasAnchors,
            Format = model.Format,
            ToSelfDelay = model.ToSelfDelay,
            LocalDelayedPubKey = model.LocalDelayedPubKey,
            RevocationPubKey = model.RevocationPubKey,
            PerCommitmentPoint = model.PerCommitmentPoint
        };
    }

    private List<HtlcSigningContext> BuildHtlcSigningContexts(CommitmentTransactionModel model,
                                                              CommitmentTransactionBuildResult built)
    {
        if (built.HtlcOutputsInTxOrder.Count == 0)
            return [];

        if (model.PerCommitmentPoint is null)
            throw new InvalidOperationException(
                "The commitment model has no per-commitment point; build it with the commitment factory");

        // HtlcTransactionModelFactory keeps the builder's output order, which is the order of htlc_signatures
        return HtlcTransactionModelFactory.CreateHtlcTransactionModels(model, built)
                                          .Select(htlcModel => new HtlcSigningContext(
                                                      _htlcTransactionBuilder.Build(htlcModel),
                                                      model.PerCommitmentPoint.Value, model.HasAnchors))
                                          .ToList();
    }
}