using Microsoft.Extensions.Logging;

namespace NLightning.Application.Channels.Reestablish;

using Domain.Bitcoin.Interfaces;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.Models;
using Domain.Channels.Reestablish;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Messages;
using Domain.Protocol.Models;
using Services;

/// <summary>
/// Builds our <c>channel_reestablish</c> and checks the peer's secret against our own per-commitment points (BOLT2
/// plan N7-T1/T2). Scoped: it reads the peer's shachain through the scope's unit of work.
/// </summary>
public sealed class ReestablishService
{
    private readonly ILightningSigner _lightningSigner;
    private readonly ILogger<ReestablishService> _logger;
    private readonly IMessageFactory _messageFactory;
    private readonly IRevocationVerifier _revocationVerifier;
    private readonly ChannelStateTransitionService _transitions;

    public ReestablishService(ILightningSigner lightningSigner, ILogger<ReestablishService> logger,
                              IMessageFactory messageFactory, IRevocationVerifier revocationVerifier,
                              ChannelStateTransitionService transitions)
    {
        _lightningSigner = lightningSigner;
        _logger = logger;
        _messageFactory = messageFactory;
        _revocationVerifier = revocationVerifier;
        _transitions = transitions;
    }

    /// <summary>
    /// Our side of a channel as the planner sees it. A channel without a commitment snapshot (not Open yet, or opened
    /// before the snapshot existed, NL-246) has no HTLC state: its numbers come from the channel.
    /// </summary>
    public static ReestablishLocalState GetLocalState(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        return channel.Commitments is { } commitments
                   ? ReestablishLocalState.From(commitments, channel.SentCommitDiff is not null,
                                                channel.LastSentCommitmentMessage)
                   : new ReestablishLocalState(channel.LocalCommitmentNumber, channel.RemoteCommitmentNumber, false,
                                               false, LastSentCommitmentMessage.None, false);
    }

    /// <summary>
    /// Our <c>channel_reestablish</c> (B2-RE-08..12): <c>next_commitment_number</c> = L + 1,
    /// <c>next_revocation_number</c> = R, the peer's last secret (R - 1, from its persisted shachain; zeroes when
    /// R = 0), our point for commitment L, and no <c>next_funding</c> (v1 channel).
    /// </summary>
    /// <exception cref="InvalidOperationException">The peer's shachain does not hold secret R - 1.</exception>
    public async Task<ChannelReestablishMessage> CreateOwnAsync(ChannelModel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var own = ReestablishPlanner.CreateOwn(GetLocalState(channel));

        var secret = new byte[ReestablishPlanner.SecretLength];
        if (own.LastReceivedSecretNumber is { } secretNumber)
        {
            using var shachain = await _transitions.LoadRemoteShachainAsync(channel.ChannelId);
            try
            {
                byte[] derived = shachain.DeriveOldSecret(PerCommitmentIndex.From(secretNumber));
                derived.CopyTo(secret, 0);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    $"The peer's shachain of channel {channel.ChannelId} lacks secret {secretNumber}", e);
            }
        }

        var point = _lightningSigner.GetPerCommitmentPoint(channel.ChannelId, own.CurrentPointNumber);
        if (_logger.IsEnabled(LogLevel.Debug))
            _logger.LogDebug(
                "channel_reestablish for {ChannelId}: next_commitment_number {Next}, next_revocation_number {Revocation}",
                channel.ChannelId, own.NextCommitmentNumber, own.NextRevocationNumber);

        return _messageFactory.CreateChannelReestablishMessage(channel.ChannelId, own.NextCommitmentNumber,
                                                              own.NextRevocationNumber, secret, point);
    }

    /// <summary>
    /// True when <paramref name="secret"/> is our per-commitment secret of commitment <paramref name="number"/>:
    /// checked against our point, so nothing is revealed (the peer may claim a number we never revoked).
    /// </summary>
    public bool IsOurSecret(ChannelModel channel, ulong number, ReadOnlyMemory<byte> secret)
    {
        ArgumentNullException.ThrowIfNull(channel);
        try
        {
            var point = _lightningSigner.GetPerCommitmentPoint(channel.ChannelId, number);
            return _revocationVerifier.IsValidSecret(new Secret(secret.ToArray()), point);
        }
        catch (Exception e)
        {
            // Not a valid scalar, or a number we can't derive: not our secret
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug(e, "Secret check {Number} of channel {ChannelId} failed", number, channel.ChannelId);
            return false;
        }
    }
}