namespace NLightning.Domain.Client.Requests;

/// <summary>
/// Lists our outgoing payments, newest first (<c>ClientCommand.ListPayments</c>).
/// </summary>
public sealed class ListPaymentsClientRequest
{
    /// <summary>
    /// How many of the newest payments to skip.
    /// </summary>
    public int Skip { get; init; }

    /// <summary>
    /// The most payments to return.
    /// </summary>
    public int Take { get; init; } = 100;

    /// <summary>
    /// Also list the outgoing legs of the trampoline payments we relayed (NL-899). They are not our spending, so
    /// <c>listpayments</c> leaves them out by default and says how many it left out; <c>listforwards</c> lists the
    /// relays.
    /// </summary>
    public bool IncludeRelayLegs { get; init; }
}