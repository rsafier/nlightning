using System.Diagnostics.CodeAnalysis;

namespace NLightning.Domain.Exceptions;

/// <summary>
/// A local operation (an update we want to send, or signing) is not allowed by BOLT 2 in the current state.
/// </summary>
/// <remarks>
/// Nothing is sent to the peer and the commitment state is unchanged. <see cref="RequirementId"/> is the <c>B2-*</c> row
/// of the BOLT 2 plan's traceability matrix. Callers (payments, forwarding) turn it into a local failure; it must never
/// reach the peer as an <c>error</c>.
/// </remarks>
[ExcludeFromCodeCoverage]
public sealed class CommitmentRefusedException : InvalidOperationException
{
    public string RequirementId { get; }

    public CommitmentRefusedException(string requirementId, string message) : base($"[{requirementId}] {message}")
    {
        RequirementId = requirementId;
    }
}