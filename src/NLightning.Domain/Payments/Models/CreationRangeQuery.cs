namespace NLightning.Domain.Payments.Models;

/// <summary>
/// A page of rows ordered by their creation time (LND's index-offset listings over invoices and payments, NL-1163):
/// the rows created strictly after <paramref name="CreatedAfter"/> and strictly before <paramref name="CreatedBefore"/>,
/// oldest first when <paramref name="Ascending"/>, newest first otherwise, at most <paramref name="Take"/>.
/// </summary>
/// <param name="CreatedAfter">The exclusive lower bound, or null.</param>
/// <param name="CreatedBefore">The exclusive upper bound, or null.</param>
/// <param name="Ascending">Oldest first (true) or newest first (false); ties by payment hash in the same direction.</param>
/// <param name="Take">The most rows to return.</param>
public sealed record CreationRangeQuery(DateTimeOffset? CreatedAfter, DateTimeOffset? CreatedBefore, bool Ascending,
                                        int Take)
{
    /// <summary>Whether <paramref name="createdAt"/> is inside the bounds.</summary>
    public bool Contains(DateTimeOffset createdAt) =>
        (CreatedAfter is not { } after || createdAt > after) && (CreatedBefore is not { } before || createdAt < before);
}