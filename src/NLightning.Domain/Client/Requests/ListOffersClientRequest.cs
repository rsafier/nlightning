namespace NLightning.Domain.Client.Requests;

/// <summary>
/// Lists our BOLT 12 offers, newest first (<c>listoffers</c>).
/// </summary>
public sealed class ListOffersClientRequest
{
    /// <summary>Only offers that still answer invoice_requests.</summary>
    public bool ActiveOnly { get; init; }

    /// <summary>How many of the newest offers to skip.</summary>
    public int Skip { get; init; }

    /// <summary>The most offers to return.</summary>
    public int Take { get; init; } = 100;
}