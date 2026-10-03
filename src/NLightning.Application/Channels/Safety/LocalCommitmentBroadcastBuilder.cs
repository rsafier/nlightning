namespace NLightning.Application.Channels.Safety;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Models;
using Domain.Crypto.ValueObjects;
using Infrastructure.Bitcoin.Builders.Interfaces;

/// <summary>
/// Our latest local commitment, fully signed for broadcast.
/// </summary>
/// <param name="CommitmentNumber">Its commitment number.</param>
/// <param name="Transaction">The signed transaction (both signatures in the witness).</param>
/// <param name="HtlcOutputCount">How many untrimmed HTLC outputs it has (each needs a BOLT 5 resolution).</param>
public sealed record SignedLocalCommitment(ulong CommitmentNumber, SignedTransaction Transaction, int HtlcOutputCount);

/// <summary>
/// Builds our latest local commitment transaction and has the signer sign it for broadcast with the peer's stored
/// signature (BOLT2 plan N9-T4). Used only by <see cref="ChannelFailureService"/>.
/// </summary>
/// <remarks>
/// With a commitment snapshot the commitment is <see cref="ChannelCommitments.LocalCommit"/> (its spec, number and the
/// peer's signature from the <c>commitment_signed</c> we accepted); a channel without one (before channel_ready) uses
/// commitment 0 from the channel (<see cref="ChannelModel.LastReceivedSignature"/>, the funding_created/signed
/// signature). The transaction is built exactly as <c>CommitmentSigningService.VerifyLocalCommitment</c> built it when
/// the signature was checked, so the signature matches. The signer refuses a revoked commitment and anything after data
/// loss (I4, I12).
/// </remarks>
public sealed class LocalCommitmentBroadcastBuilder
{
    private readonly ICommitmentTransactionModelFactory _commitmentTransactionModelFactory;
    private readonly ICommitmentTransactionBuilder _commitmentTransactionBuilder;
    private readonly ILightningSigner _lightningSigner;

    public LocalCommitmentBroadcastBuilder(ICommitmentTransactionModelFactory commitmentTransactionModelFactory,
                                           ICommitmentTransactionBuilder commitmentTransactionBuilder,
                                           ILightningSigner lightningSigner)
    {
        _commitmentTransactionModelFactory = commitmentTransactionModelFactory;
        _commitmentTransactionBuilder = commitmentTransactionBuilder;
        _lightningSigner = lightningSigner;
    }

    /// <summary>
    /// Builds and signs the latest local commitment of <paramref name="channel"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">There is no stored signature of the peer for it, or a channel
    /// without a snapshot has HTLCs.</exception>
    /// <exception cref="Domain.Exceptions.SignerException">The signer refused (data loss, revoked commitment, the
    /// stored signature does not verify).</exception>
    public SignedLocalCommitment Build(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);

        ulong number;
        CommitmentTxSpec spec;
        CompactSignature remoteSignature;
        if (channel.Commitments is { } commitments)
        {
            var localCommit = commitments.LocalCommit;
            if (localCommit.RemoteSignatures is null)
                throw new InvalidOperationException(
                    $"Local commitment {localCommit.Number} of channel {channel.ChannelId} has no peer signature");

            number = localCommit.Number;
            spec = CommitmentTxSpec.FromCommitmentSpec(localCommit.Spec);
            remoteSignature = localCommit.RemoteSignatures.Signature;
        }
        else
        {
            if (channel.LastReceivedSignature is null)
                throw new InvalidOperationException(
                    $"Channel {channel.ChannelId} has no peer signature of its local commitment");

            if (channel.LocalOfferedHtlcs is { Count: > 0 } || channel.RemoteOfferedHtlcs is { Count: > 0 })
                throw new InvalidOperationException(
                    $"Channel {channel.ChannelId} has HTLCs but no commitment snapshot");

            number = channel.LocalCommitmentNumber;
            spec = CommitmentTxSpec.FromChannel(channel);
            remoteSignature = channel.LastReceivedSignature;
        }

        var model = _commitmentTransactionModelFactory.CreateCommitmentTransactionModel(
            channel, spec, CommitmentSide.Local, number);
        var built = _commitmentTransactionBuilder.BuildWithOutputMap(model);
        var signed = _lightningSigner.SignLocalCommitmentForBroadcast(channel.ChannelId, number, built.Transaction,
                                                                      remoteSignature);

        return new SignedLocalCommitment(number, signed, built.HtlcOutputsInTxOrder.Count);
    }
}