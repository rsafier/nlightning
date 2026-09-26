using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Services;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Interfaces;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;

/// <summary>
/// The commitment state machine's <see cref="ICommitmentVerifier"/> over <see cref="CommitmentSigningService"/>
/// (NL-230): looks the channel up by id, adapts the engine's <see cref="CommitmentSpec"/> with
/// <see cref="CommitmentTxSpec.FromCommitmentSpec"/>, builds our commitment and checks the peer's commitment and HTLC
/// signatures.
/// </summary>
/// <remarks>A <see cref="SignerException"/> (missing, malformed, high-S or invalid signature) becomes <c>false</c>, so
/// the engine rejects the <c>commitment_signed</c> with B2-CS-R01; any other exception is our bug and propagates.
/// </remarks>
public sealed class EngineCommitmentVerifierPort : ICommitmentVerifier
{
    private readonly CommitmentSigningService _commitmentSigningService;
    private readonly IChannelMemoryRepository _channelMemoryRepository;
    private readonly ILogger<EngineCommitmentVerifierPort> _logger;

    public EngineCommitmentVerifierPort(CommitmentSigningService commitmentSigningService,
                                        IChannelMemoryRepository channelMemoryRepository,
                                        ILogger<EngineCommitmentVerifierPort> logger)
    {
        _commitmentSigningService = commitmentSigningService;
        _channelMemoryRepository = channelMemoryRepository;
        _logger = logger;
    }

    /// <inheritdoc />
    /// <exception cref="ArgumentException"><paramref name="spec"/> is not a local commitment.</exception>
    /// <exception cref="InvalidOperationException">The channel is not in memory.</exception>
    public bool VerifyLocalCommitment(ChannelId channelId, ulong number, CommitmentSpec spec,
                                      CommitmentSignatures signatures)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(signatures);
        if (spec.Holder != CommitmentSide.Local)
            throw new ArgumentException($"Only a local commitment can be verified, not a {spec.Holder} one",
                                        nameof(spec));

        if (!_channelMemoryRepository.TryGetChannel(channelId, out var channel))
            throw new InvalidOperationException($"Channel {channelId} is not in memory");

        try
        {
            _commitmentSigningService.VerifyLocalCommitment(channel, CommitmentTxSpec.FromCommitmentSpec(spec), number,
                                                            signatures.Signature, signatures.HtlcSignatures);
            return true;
        }
        catch (SignerException e)
        {
            _logger.LogWarning(e, "Rejected the peer's signatures for local commitment {Number} of channel {ChannelId}",
                               number, channelId);
            return false;
        }
    }
}