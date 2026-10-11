using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Exceptions;

using Channels.ValueObjects;

/// <summary>
/// The peer broke a BOLT 2 normal-operation rule (an <c>update_*</c>, <c>commitment_signed</c> or
/// <c>revoke_and_ack</c> it sent is invalid).
/// </summary>
/// <remarks>
/// <see cref="RequirementId"/> is the <c>B2-*</c> row of the BOLT 2 plan's traceability matrix
/// (<c>docs/agents/BOLT2_NORMAL_OPERATION_PLAN.md</c> §6). The spec lets most of these be answered with either
/// "<c>warning</c> and close the connection" or "<c>error</c> and fail the channel"; the handler that catches this
/// exception decides which (<see cref="MustFailChannel"/> marks the ones where only failing the channel is allowed).
/// The commitment state is unchanged when this is thrown.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class CommitmentViolationException : ChannelErrorException
{
    public string RequirementId { get; }

    /// <summary>True when BOLT 2 says "MUST send an <c>error</c> and fail the channel" (no warning option).</summary>
    public bool MustFailChannel { get; init; }

    public CommitmentViolationException(string requirementId, string message, ChannelId? channelId)
        : base($"[{requirementId}] {message}", channelId, message)
    {
        RequirementId = requirementId;
    }
}