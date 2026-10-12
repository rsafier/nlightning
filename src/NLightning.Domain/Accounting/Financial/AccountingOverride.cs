namespace NLightning.Domain.Accounting.Financial;

/// <summary>
/// A manual reclassification of one event (<c>AccountingOverrides</c>, plan §6.2): the financial book sends the event's
/// entry to <see cref="Account"/>, ahead of every rule. It is the only state the books cannot rebuild from the feed.
/// </summary>
/// <param name="EventKey">The event's key (one override per key).</param>
/// <param name="Account">The financial account name.</param>
/// <param name="Note">The operator's note, or null.</param>
/// <param name="CreatedAt">When it was set (or last replaced).</param>
public sealed record AccountingOverride(string EventKey, string Account, string? Note, DateTimeOffset CreatedAt);