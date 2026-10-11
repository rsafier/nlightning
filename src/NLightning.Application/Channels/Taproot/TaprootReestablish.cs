using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Taproot;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Models;
using Domain.Exceptions;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Serialization.Interfaces;
using Services;

/// <summary>
/// The simple taproot rules of <c>channel_reestablish</c> (bolt-simple-taproot.md §Message Retransmission, NL-877 T3):
/// the peer's <c>next_local_nonces</c> are required and replace its verification nonces before anything is
/// retransmitted, and a retransmitted <c>commitment_signed</c> is signed again against them with a fresh signing nonce,
/// never replayed byte for byte (the same update messages before it).
/// </summary>
/// <remarks>Called by <c>ChannelReestablishMessageHandler</c> under the channel's lock, in the handler's scope.</remarks>
public sealed class TaprootReestablish
{
    private readonly ILogger _logger;
    private readonly ChannelStateTransitionService _transitions;

    public TaprootReestablish(ILogger logger, IMessageSerializer messageSerializer,
                              ChannelStateTransitionService transitions)
    {
        _logger = logger;
        ArgumentNullException.ThrowIfNull(messageSerializer);
        _transitions = transitions;
    }

    /// <summary>
    /// The peer's <c>next_local_nonces</c> of a simple taproot channel: required, with an entry for every active
    /// funding; with a commitment state they replace the engine's nonces of the peer (saved, then swapped in memory)
    /// before any retransmission. Before <c>channel_ready</c> (no commitment state) only the presence of the funding's
    /// entry is checked: the peer's <c>channel_ready</c> carries the same nonce.
    /// </summary>
    /// <exception cref="ChannelFailedException">The map is absent, misses an active funding or holds a nonce that
    /// does not parse (MUST fail the channel).</exception>
    /// <param name="channel">The channel (under its lock).</param>
    /// <param name="message">The peer's <c>channel_reestablish</c>.</param>
    /// <param name="signedOpenAttempts">For a dual-funded open waiting for its funding, its fully signed attempts
    /// (<c>ReestablishService.GetSignedOpenAttemptsAsync</c>): the map must hold each of them (or, once the peer saw one
    /// of them confirm and dropped the others, at least one, NL-1079), and the channel's funding
    /// output (an RBF attempt still being signed) only when the peer's <c>next_funding</c> names it, as Eclair 0.14.3
    /// checks (<c>Helpers.Syncing.checkCommitNonces</c>; a peer that forgot the attempt has no nonce for it, and the
    /// attempt is then aborted; NL-970). Null for any other channel: every active funding needs its entry.</param>
    public async Task ReceiveNoncesAsync(ChannelModel channel, ChannelReestablishMessage message,
                                         IReadOnlyCollection<TxId>? signedOpenAttempts = null)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(message);
        if (!channel.ChannelParams.OptionSimpleTaproot)
            return;

        if (message.NextLocalNoncesTlv is not { } noncesTlv)
            throw Fail(channel, "channel_reestablish without next_local_nonces on a simple taproot channel");

        TaprootChannelNonces.ThrowIfUnparsable(channel, noncesTlv.Nonces, "channel_reestablish");
        var nonces = TaprootChannelNonces.ToDictionary(noncesTlv.Nonces);
        if (channel.Commitments is not { } commitments)
        {
            var active = TaprootChannelNonces.GetActiveFundingTxIds(channel);
            var required = active;
            if (signedOpenAttempts is not null)
            {
                var namedByPeer = message.NextFundingTlv is { } nextFunding
                                      ? new TxId(nextFunding.NextFundingTxId)
                                      : (TxId?)null;

                // A peer whose chain saw one signed attempt reach its depth keeps only that one active (Eclair 0.14.3
                // deactivates the other RBF candidates on its local lock, and so do we once the channel left
                // V1FundingSigned): one signed attempt's entry is then enough. Nothing is signed against these
                // nonces before channel_ready, which carries the peer's nonce of the confirmed attempt (NL-1079)
                IEnumerable<TxId> signed = signedOpenAttempts;
                if (signedOpenAttempts.Any(nonces.ContainsKey) && !signedOpenAttempts.All(nonces.ContainsKey))
                {
                    _logger.LogInformation("channel_reestablish of the simple taproot open {ChannelId} has nonces for "
                                         + "some of its signed attempts only: the peer saw one confirm",
                                           channel.ChannelId);
                    signed = [];
                }

                required = signed.Concat(active.Where(txId => txId == namedByPeer)).Distinct().ToList();
                if (signedOpenAttempts.Count > 0 && !signedOpenAttempts.Any(nonces.ContainsKey))
                    throw Fail(channel, "channel_reestablish has no next_local_nonces entry for any signed funding "
                                      + "attempt");
            }

            foreach (var txId in required)
                if (!nonces.ContainsKey(txId))
                    throw Fail(channel, $"channel_reestablish has no next_local_nonces entry for funding {txId}");
            return;
        }

        CommitmentsResult result;
        try
        {
            result = commitments.ReceiveRemoteNonces(nonces);
        }
        catch (CommitmentViolationException e)
        {
            throw ChannelStateTransitionService.ToFailure(e, channel.ChannelId);
        }

        await _transitions.CommitAsync(channel, result);
    }

    /// <summary>
    /// Our unacked <c>commitment_signed</c> again for a simple taproot channel: the stored diff's update messages as they
    /// were, then the commitment signed again (<see cref="ChannelCommitments.ResignRemoteNextCommit"/>, against the nonces
    /// <see cref="ReceiveNoncesAsync"/> took). The new signatures and diff are saved before they are returned.
    /// </summary>
    public async Task<IReadOnlyList<IChannelMessage>> ResignCommitDiffAsync(ChannelModel channel,
                                                                            ReadOnlyMemory<byte> diff)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var commitments = channel.Commitments
                       ?? throw new InvalidOperationException($"Channel {channel.ChannelId} has no commitment state");

        var retransmission = await _transitions.ResignRemoteNextCommitAsync(channel, diff);

        _logger.LogInformation(
            "Signed remote commitment {Number} of simple taproot channel {ChannelId} again for its retransmission",
            commitments.RemoteNextCommit?.Commit.Number, channel.ChannelId);
        return retransmission;
    }

    private static ChannelFailedException Fail(ChannelModel channel, string reason) =>
        new(channel.ChannelId, $"[TAPROOT-RE-R01] {reason}", "channel_reestablish without next_local_nonces")
        {
            RequirementId = "TAPROOT-RE-R01"
        };
}