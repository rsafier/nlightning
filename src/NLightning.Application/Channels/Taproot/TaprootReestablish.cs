using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Taproot;

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
    private readonly IMessageSerializer _messageSerializer;
    private readonly ChannelStateTransitionService _transitions;

    public TaprootReestablish(ILogger logger, IMessageSerializer messageSerializer,
                              ChannelStateTransitionService transitions)
    {
        _logger = logger;
        _messageSerializer = messageSerializer;
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
    public async Task ReceiveNoncesAsync(ChannelModel channel, ChannelReestablishMessage message)
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
            foreach (var txId in TaprootChannelNonces.GetActiveFundingTxIds(channel))
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

        var stored = await SentCommitDiffCodec.DecodeAsync(_messageSerializer, diff);
        var updates = stored.TakeWhile(m => m is not (CommitmentSignedMessage or StartBatchMessage))
                            .Select(m => m as IChannelMessage
                                      ?? throw new InvalidOperationException(
                                             $"The stored diff of channel {channel.ChannelId} holds a {m.Type}"))
                            .ToList();

        var result = _transitions.ResignRemoteNextCommit(commitments);
        var signed = result.Outbound.Select(o => _transitions.ToWireMessage(channel, o)).ToList();
        var newDiff = await SentCommitDiffCodec.EncodeAsync(_messageSerializer, updates.Concat(signed));
        await _transitions.CommitAsync(channel, result, new ChannelStateExtras { SentCommitDiff = newDiff });

        _logger.LogInformation(
            "Signed remote commitment {Number} of simple taproot channel {ChannelId} again for its retransmission",
            commitments.RemoteNextCommit?.Commit.Number, channel.ChannelId);
        return [.. updates, .. signed];
    }

    private static ChannelFailedException Fail(ChannelModel channel, string reason) =>
        new(channel.ChannelId, $"[TAPROOT-RE-R01] {reason}", "channel_reestablish without next_local_nonces")
        {
            RequirementId = "TAPROOT-RE-R01"
        };
}