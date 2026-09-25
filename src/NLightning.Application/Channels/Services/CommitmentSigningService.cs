namespace NLightning.Application.Channels.Services;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.Transactions.Models;
using Domain.Channels.Commitments;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>
/// Signs and verifies whole commitments (commitment transaction plus its HTLC transactions) for
/// <c>commitment_signed</c>: model factory → commitment builder (with the HTLC output map) → HTLC transaction
/// models and builder → <see cref="ILightningSigner"/>. HTLC signatures are always in commitment output order.
/// </summary>
public sealed class CommitmentSigningService : ICommitmentSigner, ICommitmentVerifier
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

    /// <inheritdoc />
    public CommitmentTxSignatures SignRemoteCommitment(ChannelModel channel, CommitmentTxSpec spec,
                                                     ulong remoteCommitmentNumber,
                                                     CompactPubKey remotePerCommitmentPoint)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(spec);

        var model = _commitmentTransactionModelFactory.CreateCommitmentTransactionModel(
            channel, spec, CommitmentSide.Remote, remoteCommitmentNumber, remotePerCommitmentPoint);
        var built = _commitmentTransactionBuilder.BuildWithOutputMap(model);

        var signature = _lightningSigner.SignChannelTransaction(channel.ChannelId, built.Transaction);
        var htlcSignatures =
            _lightningSigner.SignRemoteHtlcTransactions(channel.ChannelId, BuildHtlcSigningContexts(model, built));

        return new CommitmentTxSignatures(built.Transaction.TxId, signature, htlcSignatures);
    }

    /// <inheritdoc />
    public CommitmentTxSignatures VerifyLocalCommitment(ChannelModel channel, CommitmentTxSpec spec,
                                                      ulong localCommitmentNumber, CompactSignature signature,
                                                      IReadOnlyList<CompactSignature> htlcSignatures)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(htlcSignatures);

        var model = _commitmentTransactionModelFactory.CreateCommitmentTransactionModel(
            channel, spec, CommitmentSide.Local, localCommitmentNumber);
        var built = _commitmentTransactionBuilder.BuildWithOutputMap(model);

        _lightningSigner.ValidateSignature(channel.ChannelId, signature, built.Transaction);
        _lightningSigner.ValidateLocalHtlcSignatures(channel.ChannelId, BuildHtlcSigningContexts(model, built),
                                                     htlcSignatures);

        return new CommitmentTxSignatures(built.Transaction.TxId, signature, htlcSignatures.ToList());
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