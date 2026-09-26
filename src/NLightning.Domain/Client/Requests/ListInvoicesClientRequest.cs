namespace NLightning.Domain.Client.Requests;

/// <summary>
/// Lists our invoices, newest first (<c>ClientCommand.ListInvoices</c>).
/// </summary>
public sealed class ListInvoicesClientRequest
{
    /// <summary>
    /// How many of the newest invoices to skip.
    /// </summary>
    public int Skip { get; init; }

    /// <summary>
    /// The most invoices to return.
    /// </summary>
    public int Take { get; init; } = 100;
}