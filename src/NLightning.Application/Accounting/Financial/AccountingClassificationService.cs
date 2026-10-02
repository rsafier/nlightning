using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Reports;
using Domain.Accounting.Constants;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Classification;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Persistence.Interfaces;
using Reports;

/// <summary>
/// The financial book's classification (NL-602 A3-T3, plan <c>docs/agents/ACCOUNTING_PLAN.md</c> §6.2, D-A10): the
/// <c>accounting classify</c> administration of IPC 45 and the engine the financial projector (A3-T4) classifies with.
/// </summary>
/// <remarks>
/// <para>Rules (<c>AccountingRules</c>) and overrides (<c>AccountingOverrides</c>) live in the node's database, so they
/// are in its backups and change without a restart: every call reads them through a scope of its own, and
/// <see cref="CreateEngineAsync"/> builds a <see cref="ClassificationEngine"/> from the enabled rules in effect.
/// They are managed whatever <see cref="AccountingOptions.Profile"/> says (an operator prepares the rules before
/// switching to <see cref="AccountingProfile.Financial"/>); the answers then carry a warning.</para>
/// <para><c>rule test</c> and <c>list --unclassified</c> read the operational entries, so they seal and project
/// first and need the books on. The unclassified listing classifies every projected entry with the rules and
/// overrides in effect now (the same engine as the projector), in ledger order, at most
/// <see cref="MaxScanPerCall"/> entries per call; it is on demand only, never at startup.</para>
/// <para>A change of a rule or an override never rewrites a closed period: the financial book posts it as an
/// adjustment in the open period (D-A8, A3-T5); <c>set</c> warns when the event lies in a closed period.</para>
/// <para><b>The open period</b> (A3-T4): <c>set</c> and <c>unset</c> of an event the financial book projected in the
/// open period lower the financial book's cursor to just before it, and a rule added, removed, enabled or disabled to
/// just before the open period, in the same save and under the period lock's write lock
/// (<see cref="IAccountingAdjustmentSink.EnterAsync"/>), so the financial projector projects those entries again
/// with the classification in effect (their lots and gains included).</para>
/// </remarks>
public sealed class AccountingClassificationService : IAccountingClassificationAdmin
{
    /// <summary>The most entries one unclassified listing call looks at.</summary>
    public const int MaxScanPerCall = 5_000;

    /// <summary>The largest page of a listing.</summary>
    public const int MaxLimit = 1_000;

    private const int ScanPageSize = 500;
    private const string CandidatePlaceholderAccount = "income:candidate";

    private readonly AccountingBooksAccess _access;
    private readonly ILogger<AccountingClassificationService> _logger;
    private readonly AccountingOptions _options;
    private readonly SemaphoreSlim _ruleGate = new(1, 1);
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly TimeProvider _timeProvider;
    private readonly IAccountingAdjustmentSink _sink;

    public AccountingClassificationService(IServiceScopeFactory scopeFactory,
                                           ILogger<AccountingClassificationService> logger,
                                           IOptions<AccountingOptions>? options = null, IAccountingBooks? books = null,
                                           IAccountingEventSealer? sealer = null, TimeProvider? timeProvider = null,
                                           IAccountingAdjustmentSink? adjustmentSink = null)
    {
        _sink = adjustmentSink ?? NullAccountingAdjustmentSink.Instance;
        _scopeFactory = scopeFactory;
        _logger = logger;
        _options = options?.Value ?? new AccountingOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _access = new AccountingBooksAccess(books, sealer, logger);
        Chart = _options.GetFinancialChart();
        foreach (var ignored in Chart.IgnoredOverrides)
            _logger.LogWarning("Accounting:FinancialAccountNames: {Ignored}; the default name is kept", ignored);
    }

    /// <summary>The financial chart in effect.</summary>
    public FinancialChart Chart { get; }

    /// <summary>The profile in effect.</summary>
    public AccountingProfile Profile => _options.Profile;

    /// <summary>
    /// The engine over the enabled rules stored now (A3-T4 builds one per projection batch, so a rule change applies
    /// from the next batch on).
    /// </summary>
    public async Task<ClassificationEngine> CreateEngineAsync(IUnitOfWork unitOfWork,
                                                              CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unitOfWork);
        var rules = await unitOfWork.AccountingRuleDbRepository.ListAsync(true, cancellationToken);
        var engine = new ClassificationEngine(Chart, rules);
        foreach (var id in engine.InvalidRuleIds)
            _logger.LogWarning("Accounting rule {RuleId} can never match (invalid pattern or target); remove it", id);
        return engine;
    }

    /// <inheritdoc />
    public async Task<AccountingClassifyClientResponse> HandleAsync(AccountingClassifyClientRequest request,
                                                                    CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = new AccountingClassifyClientResponse(request.Action, Profile);
        var warnings = new List<string>();
        if (Profile != AccountingProfile.Financial)
            warnings.Add("Accounting:Profile is Operational: the rules and overrides are stored, but no financial "
                       + "book is kept until the profile is Financial.");
        warnings.AddRange(Chart.IgnoredOverrides.Select(i => $"Accounting:FinancialAccountNames {i} (ignored)."));

        response = request.Action switch
        {
            AccountingClassifyAction.RuleAdd => await AddRuleAsync(request, response, cancellationToken),
            AccountingClassifyAction.RuleList => await ListRulesAsync(response, warnings, cancellationToken),
            AccountingClassifyAction.RuleRemove or AccountingClassifyAction.RuleEnable
                or AccountingClassifyAction.RuleDisable => await ChangeRuleAsync(request, response, cancellationToken),
            AccountingClassifyAction.RuleTest => await TestAsync(request, response, warnings, cancellationToken),
            AccountingClassifyAction.Set => await SetAsync(request, response, warnings, cancellationToken),
            AccountingClassifyAction.Unset => await UnsetAsync(request, response, cancellationToken),
            AccountingClassifyAction.ListOverrides => await ListOverridesAsync(request, response, cancellationToken),
            AccountingClassifyAction.ListUnclassified => await ListUnclassifiedAsync(request, response, warnings,
                                                                                    cancellationToken),
            _ => throw Invalid($"Unknown classify action {request.Action}.")
        };

        return response with { Warnings = warnings };
    }

    /// <summary>
    /// A page of the entries that go to an unclassified account: the projected operational entries after
    /// <paramref name="afterLedgerSeq"/>, classified now, at most <paramref name="limit"/> items and
    /// <see cref="MaxScanPerCall"/> entries looked at. Reads the books as they stand (the caller seals and projects).
    /// </summary>
    public async Task<AccountingUnclassifiedPage> ListUnclassifiedAsync(long afterLedgerSeq, int limit,
                                                                        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var engine = await CreateEngineAsync(unitOfWork, cancellationToken);
        var items = new List<AccountingUnclassifiedItem>();
        var after = Math.Max(0, afterLedgerSeq);
        var scanned = 0;
        while (scanned < MaxScanPerCall)
        {
            var take = Math.Min(ScanPageSize, MaxScanPerCall - scanned);
            var entries = await unitOfWork.AccountingBooksDbRepository.ListEntriesAsync(
                              new AccountingEntryQuery(after, take), cancellationToken);
            if (entries.Count == 0)
                return new AccountingUnclassifiedPage(items, after, false, scanned);

            var events = (await unitOfWork.AccountingEventDbRepository.ListAsync(
                              new AccountingEventQuery(after, take), cancellationToken))
                        .Where(e => e.LedgerSeq is not null)
                        .ToDictionary(e => e.LedgerSeq!.Value);
            var overrides = await unitOfWork.AccountingOverrideDbRepository.GetManyAsync(
                                entries.Select(e => e.EventKey).Distinct(StringComparer.Ordinal).ToList(),
                                cancellationToken);

            foreach (var entry in entries)
            {
                scanned++;
                after = entry.LedgerSeq;
                if (!events.TryGetValue(entry.LedgerSeq, out var accountingEvent))
                    continue;

                var classification = engine.Classify(entry, accountingEvent,
                                                     overrides.GetValueOrDefault(entry.EventKey));
                if (!classification.IsUnclassified)
                    continue;

                var amount = entry.Postings.Where(p => FinancialChart.IsClassifiable(p.Account))
                                  .Sum(p => p.AmountMsat);
                items.Add(new AccountingUnclassifiedItem(entry.LedgerSeq, entry.EventKey, entry.Kind,
                                                         entry.OccurredAt, amount, classification.Account!,
                                                         classification.Reason,
                                                         accountingEvent.Details.GetValueOrDefault(
                                                             ClassificationEngine.LabelDetail)));
                if (items.Count >= limit)
                    return new AccountingUnclassifiedPage(items, after, true, scanned);
            }

            if (entries.Count < take)
                return new AccountingUnclassifiedPage(items, after, false, scanned);
        }

        return new AccountingUnclassifiedPage(items, after, true, scanned);
    }

    private async Task<AccountingClassifyClientResponse> AddRuleAsync(AccountingClassifyClientRequest request,
                                                                      AccountingClassifyClientResponse response,
                                                                      CancellationToken cancellationToken)
    {
        var candidate = request.Rule ?? throw Invalid("A rule is required.");
        if (!AccountingRuleValidator.TryValidate(candidate, out var error))
            throw Invalid(error!);

        var rule = candidate with { Id = 0, CreatedAt = _timeProvider.GetUtcNow() };

        // One add at a time, so the highest id after the save is the rule this call added
        await _ruleGate.WaitAsync(cancellationToken);
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            unitOfWork.AccountingRuleDbRepository.Add(rule);
            using (await _sink.EnterAsync(cancellationToken))
            {
                await RequestReprojectionAsync(unitOfWork, null, cancellationToken);
                await unitOfWork.SaveChangesAsync();
            }

            var saved = (await unitOfWork.AccountingRuleDbRepository.ListAsync(false, cancellationToken))
                       .MaxBy(r => r.Id) ?? rule;
            _logger.LogInformation("Accounting rule {RuleId} added: priority {Priority} to {Account}", saved.Id,
                                   saved.Priority, saved.TargetAccount);
            return response with { Rules = [saved] };
        }
        finally
        {
            _ruleGate.Release();
        }
    }

    private async Task<AccountingClassifyClientResponse> ListRulesAsync(AccountingClassifyClientResponse response,
                                                                        List<string> warnings,
                                                                        CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var rules = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingRuleDbRepository
                               .ListAsync(false, cancellationToken);
        var engine = new ClassificationEngine(Chart, rules);
        warnings.AddRange(engine.InvalidRuleIds.Select(id => $"Rule {id} can never match (invalid pattern or "
                                                           + "target); remove it."));
        return response with { Rules = rules };
    }

    private async Task<AccountingClassifyClientResponse> ChangeRuleAsync(AccountingClassifyClientRequest request,
                                                                         AccountingClassifyClientResponse response,
                                                                         CancellationToken cancellationToken)
    {
        var id = request.RuleId ?? throw Invalid("A rule id is required.");
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var rules = unitOfWork.AccountingRuleDbRepository;
        var changed = request.Action switch
        {
            AccountingClassifyAction.RuleRemove => await rules.RemoveAsync(id, cancellationToken),
            AccountingClassifyAction.RuleEnable => await rules.SetEnabledAsync(id, true, cancellationToken),
            _ => await rules.SetEnabledAsync(id, false, cancellationToken)
        };
        if (changed)
        {
            using (await _sink.EnterAsync(cancellationToken))
            {
                await RequestReprojectionAsync(unitOfWork, null, cancellationToken);
                await unitOfWork.SaveChangesAsync();
            }

            _logger.LogInformation("Accounting rule {RuleId}: {Action}", id, request.Action);
        }

        return response with { Changed = changed };
    }

    private async Task<AccountingClassifyClientResponse> TestAsync(AccountingClassifyClientRequest request,
                                                                   AccountingClassifyClientResponse response,
                                                                   List<string> warnings,
                                                                   CancellationToken cancellationToken)
    {
        var key = request.EventKey ?? throw Invalid("An event key is required.");

        // A candidate needs no target to be tested
        var candidate = request.Rule is { } given && string.IsNullOrEmpty(given.TargetAccount)
                            ? given with { TargetAccount = CandidatePlaceholderAccount }
                            : request.Rule;
        if (candidate is not null && !AccountingRuleValidator.TryValidate(candidate, out var error))
            throw Invalid(error!);

        await PrepareBooksAsync(cancellationToken);
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var (entry, accountingEvent) = await FindEntryAsync(unitOfWork, key, cancellationToken);
        var engine = await CreateEngineAsync(unitOfWork, cancellationToken);
        warnings.AddRange(engine.InvalidRuleIds.Select(id => $"Rule {id} can never match (invalid pattern or "
                                                           + "target); remove it."));
        var accountingOverride = await unitOfWork.AccountingOverrideDbRepository.GetAsync(key, cancellationToken);
        var classification = engine.Classify(entry, accountingEvent, accountingOverride);
        var result = new AccountingClassifyTestResult(entry.LedgerSeq, entry.EventKey, entry.Kind, entry.OccurredAt,
                                                      classification,
                                                      engine.MapPostings(entry, accountingEvent, classification));
        if (candidate is { } rule)
        {
            var matches = engine.RuleMatches(rule, entry, accountingEvent, out var timedOut);
            result = result with { CandidateMatches = matches, CandidateTimedOut = timedOut };
        }

        return response with { Test = result };
    }

    private async Task<AccountingClassifyClientResponse> SetAsync(AccountingClassifyClientRequest request,
                                                                  AccountingClassifyClientResponse response,
                                                                  List<string> warnings,
                                                                  CancellationToken cancellationToken)
    {
        var key = request.EventKey ?? throw Invalid("An event key is required.");
        var account = request.Account ?? throw Invalid("An account is required.");
        if (!FinancialAccountNameRules.TryValidateTarget(account, out var error))
            throw Invalid(error!);
        if (request.Note is { Length: > AccountingSchemaLimits.NoteMaxLength })
            throw Invalid($"A note is at most {AccountingSchemaLimits.NoteMaxLength} characters.");

        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var accountingEvent = await unitOfWork.AccountingEventDbRepository.GetByKeyAsync(key, cancellationToken)
                           ?? throw Invalid($"No accounting event has the key '{key}'.");
        if (await unitOfWork.AccountingBooksDbRepository.GetEntryByKeyAsync(key, cancellationToken) is { } entry
         && !entry.Postings.Any(p => FinancialChart.IsClassifiable(p.Account)))
            throw Invalid($"The entry of '{key}' has no income, expense or transfer line to classify.");

        try
        {
            if (await unitOfWork.AccountingPeriodDbRepository.GetClosedContainingAsync(
                    accountingEvent.OccurredAt, cancellationToken) is { } period)
                warnings.Add($"The event lies in the closed period {period.PeriodId}: the financial book posts the "
                           + "change as an adjustment in the open period.");
        }
        catch (NotSupportedException)
        {
            // A unit of work without the periods (test doubles): no closed period to warn about
        }

        var stored = new AccountingOverride(key, account, request.Note, _timeProvider.GetUtcNow());
        await unitOfWork.AccountingOverrideDbRepository.SetAsync(stored, cancellationToken);
        using (await _sink.EnterAsync(cancellationToken))
        {
            await RequestReprojectionAsync(unitOfWork, key, cancellationToken);
            await unitOfWork.SaveChangesAsync();
        }

        _logger.LogInformation("Accounting override of {EventKey}: {Account}", key, account);
        return response with { Override = stored };
    }

    private async Task<AccountingClassifyClientResponse> UnsetAsync(AccountingClassifyClientRequest request,
                                                                    AccountingClassifyClientResponse response,
                                                                    CancellationToken cancellationToken)
    {
        var key = request.EventKey ?? throw Invalid("An event key is required.");
        using var scope = _scopeFactory.CreateScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        var changed = await unitOfWork.AccountingOverrideDbRepository.RemoveAsync(key, cancellationToken);
        if (changed)
        {
            using (await _sink.EnterAsync(cancellationToken))
            {
                await RequestReprojectionAsync(unitOfWork, key, cancellationToken);
                await unitOfWork.SaveChangesAsync();
            }

            _logger.LogInformation("Accounting override of {EventKey} removed", key);
        }

        return response with { Changed = changed };
    }

    private async Task<AccountingClassifyClientResponse> ListOverridesAsync(AccountingClassifyClientRequest request,
                                                                            AccountingClassifyClientResponse response,
                                                                            CancellationToken cancellationToken)
    {
        CheckLimit(request.Limit);
        if (request.Skip < 0)
            throw Invalid("skip must not be negative.");

        using var scope = _scopeFactory.CreateScope();
        var overrides = await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().AccountingOverrideDbRepository
                                   .ListAsync(request.Skip, request.Limit, cancellationToken);
        return response with { Overrides = overrides };
    }

    private async Task<AccountingClassifyClientResponse> ListUnclassifiedAsync(
        AccountingClassifyClientRequest request, AccountingClassifyClientResponse response, List<string> warnings,
        CancellationToken cancellationToken)
    {
        CheckLimit(request.Limit);
        if (request.AfterLedgerSeq < 0)
            throw Invalid("after must not be negative.");

        await PrepareBooksAsync(cancellationToken);
        var page = await ListUnclassifiedAsync(request.AfterLedgerSeq, request.Limit, cancellationToken);
        if (page.HasMore && page.Items.Count < request.Limit)
            warnings.Add($"Looked at {page.Scanned} entries; continue after {page.NextAfter} for the rest.");
        return response with { Unclassified = page };
    }

    /// <summary>
    /// Lowers the financial book's cursor (staged on <paramref name="unitOfWork"/>; the caller holds the period lock's
    /// write lock and saves) so the financial projector projects the open-period entry of <paramref name="eventKey"/>
    /// again, or with no key every open-period entry (A3-T4). Nothing for an event the financial book has not projected
    /// or holds in a closed period, or with a unit of work that keeps no financial book (test doubles).
    /// </summary>
    private static async Task RequestReprojectionAsync(IUnitOfWork unitOfWork, string? eventKey,
                                                       CancellationToken cancellationToken)
    {
        try
        {
            var books = unitOfWork.AccountingBooksDbRepository;
            long from;
            if (eventKey is not null)
            {
                var entries = await books.GetEntriesByKeyAsync(AccountingBook.Financial, eventKey, cancellationToken);
                if (entries.FirstOrDefault(e => e.Adjustment == 0 && e.ClosedPeriodId is null) is not { } open)
                    return;

                from = open.LedgerSeq;
            }
            else
            {
                // The open period starts after the last close's replay point (A3-T5's closing state)
                var last = await unitOfWork.AccountingPeriodDbRepository.GetLastClosedAsync(cancellationToken);
                from = 1;
                if (last is not null)
                {
                    try
                    {
                        from = AccountingClosingState.Decode(last.ClosingState).ReplayAfterLedgerSeq + 1;
                    }
                    catch (FormatException)
                    {
                        // An unreadable closing state (verify reports it): from the start, the closed facts stay
                    }
                }
            }

            var cursor = await books.GetCursorAsync(AccountingBook.Financial, cancellationToken);
            if (from - 1 < cursor)
                await books.SetCursorAsync(AccountingBook.Financial, Math.Max(0, from - 1), cancellationToken);
        }
        catch (NotSupportedException)
        {
            // A unit of work without the financial book: nothing to project again
        }
    }

    private async Task PrepareBooksAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _access.PrepareAsync(cancellationToken);
        }
        catch (AccountingBooksDisabledException)
        {
            throw Invalid("The accounting books are disabled (Accounting:Enabled=false): a test and the unclassified "
                        + "listing read the operational entries.");
        }
    }

    private static async Task<(AccountingEntry Entry, AccountingEventModel Event)> FindEntryAsync(
        IUnitOfWork unitOfWork, string key, CancellationToken cancellationToken)
    {
        var entry = await unitOfWork.AccountingBooksDbRepository.GetEntryByKeyAsync(key, cancellationToken)
                 ?? throw Invalid($"No projected accounting entry has the key '{key}' (unknown, or not sealed yet).");

        // The entry's own event (its ledger sequence), never a duplicate row of the key
        var events = await unitOfWork.AccountingEventDbRepository.GetSealedRangeAsync(entry.LedgerSeq, 1,
                                                                                      cancellationToken);
        var accountingEvent = events.FirstOrDefault(e => e.LedgerSeq == entry.LedgerSeq)
                           ?? throw Invalid($"The event of '{key}' is missing from the feed.");
        return (entry, accountingEvent);
    }

    private static void CheckLimit(int limit)
    {
        if (limit is < 1 or > MaxLimit)
            throw Invalid($"limit must be between 1 and {MaxLimit}.");
    }

    private static ClientException Invalid(string message) => new(ErrorCodes.InvalidOperation, message);
}