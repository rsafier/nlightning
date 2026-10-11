namespace NLightning.Domain.Payments.Models;

/// <summary>
/// A page of invoices or payments by LND index (<c>add_index</c> / <c>payment_index</c>, NL-1163, NL-1165): the rows
/// whose index is strictly after <paramref name="After"/> and strictly before <paramref name="Before"/>, created in
/// <paramref name="CreatedFrom"/>..<paramref name="CreatedUntil"/> (inclusive), lowest index first when
/// <paramref name="Ascending"/>, highest first otherwise, at most <paramref name="Take"/>. Rows without an index (saved
/// by a unit of work without the allocator) are not listed.
/// </summary>
/// <param name="After">The exclusive lower index bound, or null.</param>
/// <param name="Before">The exclusive upper index bound, or null.</param>
/// <param name="Ascending">Lowest index first (true) or highest first (false).</param>
/// <param name="Take">The most rows to return.</param>
/// <param name="CreatedFrom">The earliest creation time, or null.</param>
/// <param name="CreatedUntil">The latest creation time, or null.</param>
public sealed record LndIndexQuery(ulong? After, ulong? Before, bool Ascending, int Take,
                                   DateTimeOffset? CreatedFrom = null, DateTimeOffset? CreatedUntil = null)
{
    /// <summary>Whether a row with <paramref name="index"/> created at <paramref name="createdAt"/> matches.</summary>
    public bool Contains(ulong? index, DateTimeOffset createdAt) =>
        index is { } value
     && (After is not { } after || value > after) && (Before is not { } before || value < before)
     && (CreatedFrom is not { } from || createdAt >= from) && (CreatedUntil is not { } until || createdAt <= until);
}