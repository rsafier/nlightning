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
}