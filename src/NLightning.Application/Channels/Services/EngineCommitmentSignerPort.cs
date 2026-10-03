namespace NLightning.Application.Channels.Services;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Channels.Splicing;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;

/// <summary>
/// The commitment state machine's <see cref="ICommitmentSigner"/> over <see cref="CommitmentSigningService"/> (NL-230):
/// looks the channel up by id (static data: keys, funding output, dust limits, anchors, funder), adapts the engine's
/// <see cref="CommitmentSpec"/> with <see cref="CommitmentTxSpec.FromCommitmentSpec"/>, and signs the peer's
/// commitment and its HTLC transactions.
/// </summary>
public sealed class EngineCommitmentSignerPort : ICommitmentSigner
{
    private readonly CommitmentSigningService _commitmentSigningService;
    private readonly IChannelMemoryRepository _channelMemoryRepository;

    public EngineCommitmentSignerPort(CommitmentSigningService commitmentSigningService,
                                      IChannelMemoryRepository channelMemoryRepository)
    {
        _commitmentSigningService = commitmentSigningService;
        _channelMemoryRepository = channelMemoryRepository;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="spec"/> is not a remote commitment.</exception>
    /// <exception cref="InvalidOperationException">The channel is not in memory.</exception>
    /// <remarks>A null <paramref name="funding"/>, or the channel's current one, signs against the channel's funding
    /// output exactly as before splicing; a pending splice funding is signed against its own outpoint and keys
    /// (splicing plan SP1-C).</remarks>
    public CommitmentSignatures SignRemoteCommitment(ChannelId channelId, ChannelFunding? funding, ulong number,
                                                     CommitmentSpec spec, CompactPubKey remotePerCommitmentPoint)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Holder != CommitmentSide.Remote)
            throw new ArgumentException($"Only a remote commitment can be signed, not a {spec.Holder} one",
                                        nameof(spec));

        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
            throw new InvalidOperationException($"Channel {channelId} is not in memory");

        return _commitmentSigningService
              .SignRemoteCommitment(channel, funding, CommitmentTxSpec.FromCommitmentSpec(spec), number,
                                    remotePerCommitmentPoint)
              .ToCommitmentSignatures();
    }
}