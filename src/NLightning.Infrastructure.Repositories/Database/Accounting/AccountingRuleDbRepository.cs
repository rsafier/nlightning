using System.Globalization;
using Microsoft.EntityFrameworkCore;

namespace NLightning.Infrastructure.Repositories.Database.Accounting;

using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Persistence.Contexts;
using Persistence.Entities.Accounting;

/// <summary>
/// The classification rules (<c>AccountingRules</c>, NL-602 A3-T3, D-A10; migration <c>AddAccountingFinancial</c>).
/// Writes are staged on the unit of work and committed by its save.
/// </summary>
public class AccountingRuleDbRepository : IAccountingRuleDbRepository
{
    private readonly NLightningDbContext _context;

    public AccountingRuleDbRepository(NLightningDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        _context = context;
    }

    /// <inheritdoc />
    public void Add(AccountingRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.TargetAccount);

        _context.AccountingRules.Add(new AccountingRuleEntity
        {
            Priority = rule.Priority,
            Kinds = EncodeKinds(rule.Kinds),
            LabelPattern = rule.LabelPattern,
            TagKey = rule.TagKey,
            TagValue = rule.TagValue,
            Counterparty = rule.Counterparty,
            OfferId = rule.OfferId,
            ChannelId = rule.ChannelId,
            TargetAccount = rule.TargetAccount,
            Enabled = rule.Enabled,
            CreatedAt = rule.CreatedAt,
            Description = rule.Description
        });
    }

    /// <inheritdoc />
    public async Task<AccountingRule?> GetByIdAsync(long id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.AccountingRules.AsNoTracking()
                                   .FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        return entity is null ? null : MapEntityToDomain(entity);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<AccountingRule>> ListAsync(bool enabledOnly,
                                                               CancellationToken cancellationToken = default)
    {
        var rules = _context.AccountingRules.AsNoTracking();
        if (enabledOnly)
            rules = rules.Where(r => r.Enabled);

        var entities = await rules.OrderBy(r => r.Priority).ThenBy(r => r.Id).ToListAsync(cancellationToken);
        return entities.Select(MapEntityToDomain).ToList();
    }

    /// <inheritdoc />
    public async Task<bool> SetEnabledAsync(long id, bool enabled, CancellationToken cancellationToken = default)
    {
        var entity = await _context.AccountingRules.FindAsync([id], cancellationToken);
        if (entity is null)
            return false;

        entity.Enabled = enabled;
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> RemoveAsync(long id, CancellationToken cancellationToken = default)
    {
        var entity = await _context.AccountingRules.FindAsync([id], cancellationToken);
        if (entity is null)
            return false;

        _context.AccountingRules.Remove(entity);
        return true;
    }

    private static string? EncodeKinds(IReadOnlyList<AccountingEventKind>? kinds) =>
        kinds is { Count: > 0 }
            ? string.Join(',', kinds.Select(k => ((int)k).ToString(CultureInfo.InvariantCulture)))
            : null;

    private static IReadOnlyList<AccountingEventKind>? DecodeKinds(string? kinds) =>
        string.IsNullOrEmpty(kinds)
            ? null
            : kinds.Split(',').Select(k => (AccountingEventKind)int.Parse(k, CultureInfo.InvariantCulture)).ToList();

    private static AccountingRule MapEntityToDomain(AccountingRuleEntity entity) =>
        new(entity.Id, entity.Priority, DecodeKinds(entity.Kinds), entity.LabelPattern, entity.TagKey,
            entity.TagValue, entity.Counterparty, entity.OfferId, entity.ChannelId, entity.TargetAccount,
            entity.Enabled, entity.CreatedAt, entity.Description);
}