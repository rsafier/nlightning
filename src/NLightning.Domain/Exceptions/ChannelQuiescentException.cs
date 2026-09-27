using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Exceptions;

using Channels.ValueObjects;

/// <summary>
/// A local update (<c>IChannelOperations</c>) is refused because the channel is quiescing or quiescent (BOLT 2
/// "Channel Quiescence": after sending <c>stfu</c> the sender MUST NOT send update messages, Q-S-04; after receiving
/// one it SHOULD NOT, Q-R-02).
/// </summary>
/// <remarks>
/// <para>It derives from <see cref="CommitmentRefusedException"/> so every existing caller keeps treating it as a
/// refusal with nothing persisted or sent (the switch fails a forward back with <c>temporary_channel_failure</c>,
/// payments avoid the channel). Callers that can wait tell it apart by type: the refusal is temporary and ends with the
/// quiescence (the dependent protocol finishes, or the connection closes). A fulfill refused this way must be
/// re-queued, never dropped (splicing plan Q1-T4).</para>
/// <para><see cref="CommitmentRefusedException.RequirementId"/> is <c>Q-S-04</c> when we sent <c>stfu</c>, else
/// <c>Q-R-02</c>.</para>
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class ChannelQuiescentException : CommitmentRefusedException
{
    /// <summary>The quiescing or quiescent channel.</summary>
    public ChannelId ChannelId { get; }

    public ChannelQuiescentException(ChannelId channelId, string requirementId, string message)
        : base(requirementId, message)
    {
        ChannelId = channelId;
    }
}