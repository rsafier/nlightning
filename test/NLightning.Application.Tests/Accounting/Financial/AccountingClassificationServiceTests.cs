using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Application.Tests.Accounting.Financial;

using Application.Accounting;
using Application.Accounting.Books;
using Application.Accounting.Financial;
using Domain.Accounting.Books;
using Domain.Accounting.Enums;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Classification;
using Domain.Accounting.Models;
using Domain.Accounting.Services;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Crypto.Hashes;
using Domain.Persistence.Interfaces;
using Infrastructure.Crypto.Hashes;
using Infrastructure.Persistence;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories;
using TestUtils;

/// <summary>
/// The <c>accounting classify</c> administration (NL-602 A3-T3) over the production unit of work, sealer and
/// operational books on a SQLite file: rules added, listed, disabled and removed, overrides set and unset, an event
/// tested, and the unclassified listing with its paging, resolved by a rule and by an override.
/// </summary>
public sealed class AccountingClassificationServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"nltg-classify-{Guid.NewGuid():N}.db");

    private ServiceProvider? _provider;
    private AccountingEventSealerService? _sealer;
    private AccountingBooksService? _books;

    private ServiceProvider Provider => _provider ?? throw new InvalidOperationException("Not initialized");

    public async ValueTask InitializeAsync()
    {
        _provider = BuildProvider(_databasePath);
        using (var scope = Provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database
                       .MigrateAsync(TestContext.Current.CancellationToken);

        var options = Options.Create(new AccountingOptions
        {
            SealInterval = TimeSpan.FromHours(1),
            Profile = AccountingProfile.Financial
        });
        var scopes = Provider.GetRequiredService<IServiceScopeFactory>();
        _sealer = new AccountingEventSealerService(scopes, NullLogger<AccountingEventSealerService>.Instance, options);
        _books = new AccountingBooksService(scopes, NullLogger<AccountingBooksService>.Instance, options, _sealer);
    }

    public async ValueTask DisposeAsync()
    {
        if (_books is not null)
            await _books.DisposeAsync();
        if (_sealer is not null)
            await _sealer.DisposeAsync();
        if (_provider is not null)
            await _provider.DisposeAsync();

        SqliteTestPools.Clear(_databasePath);
        foreach (var path in new[] { _databasePath, _databasePath + "-wal", _databasePath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // A leftover temp file is harmless
            }
        }
    }

    [Fact]
    public async Task Given_RulesAdded_When_ListedDisabledAndRemoved_Then_TheTableFollowsInMatchOrder()
    {
        // Arrange
        var service = CreateService();

        // Act
        var first = await RunAsync(service, AccountingClassifyAction.RuleAdd,
                                   rule: Rule(priority: 20, target: "income:consulting", label: "consult"));
        var second = await RunAsync(service, AccountingClassifyAction.RuleAdd,
                                    rule: Rule(priority: 10, target: "expenses:payroll", tagKey: "team",
                                               description: "salaries"));
        var firstId = first.Rules![0].Id;
        var secondId = second.Rules![0].Id;
        var disabled = await RunAsync(service, AccountingClassifyAction.RuleDisable, ruleId: firstId);
        var listed = await RunAsync(service, AccountingClassifyAction.RuleList);
        var removed = await RunAsync(service, AccountingClassifyAction.RuleRemove, ruleId: secondId);
        var removedAgain = await RunAsync(service, AccountingClassifyAction.RuleRemove, ruleId: secondId);
        var after = await RunAsync(service, AccountingClassifyAction.RuleList);

        // Assert
        Assert.True(firstId > 0);
        Assert.NotEqual(firstId, secondId);
        Assert.Equal("income:consulting", first.Rules[0].TargetAccount);
        Assert.Equal(s_at, first.Rules[0].CreatedAt);
        Assert.Equal("salaries", second.Rules[0].Description);
        Assert.True(disabled.Changed);
        Assert.Equal([secondId, firstId], listed.Rules!.Select(r => r.Id));
        Assert.False(listed.Rules![1].Enabled);
        Assert.True(removed.Changed);
        Assert.False(removedAgain.Changed);
        Assert.Equal([firstId], after.Rules!.Select(r => r.Id));
        Assert.Empty(after.Warnings);
    }

    [Theory]
    [InlineData("assets:lightning:channels", null)]
    [InlineData("income:sales", "(a)\\1")]
    [InlineData("sales", null)]
    public async Task Given_AnInvalidRule_When_Added_Then_ItIsRefusedAndNothingIsStored(string target, string? label)
    {
        // Arrange
        var service = CreateService();

        // Act
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => RunAsync(service, AccountingClassifyAction.RuleAdd,
                                           rule: Rule(target: target, label: label)));
        var listed = await RunAsync(service, AccountingClassifyAction.RuleList);

        // Assert
        Assert.Equal("invalid_operation", exception.ErrorCode);
        Assert.Empty(listed.Rules!);
    }

    [Fact]
    public async Task Given_PushesAndSales_When_TheUnclassifiedAreListed_Then_OnlyThePushesAreAndARuleOrAnOverrideResolvesThem()
    {
        // Arrange: two sales (income:sales by default), two pushes received (unclassified by default), a close
        // (nothing to classify)
        var service = CreateService();
        await AddAsync(Event("inv:1", AccountingEventKind.InvoiceSettled, 1_000, label: "consulting"),
                       Event("push:1", AccountingEventKind.PushReceived, 2_000),
                       Event("close:1", AccountingEventKind.ChannelClosedMutual, -500, fee: 100),
                       Event("push:2", AccountingEventKind.PushReceived, 3_000, label: "gift"),
                       Event("inv:2", AccountingEventKind.InvoiceSettled, 4_000));

        // Act
        var all = await RunAsync(service, AccountingClassifyAction.ListUnclassified);
        var firstPage = await RunAsync(service, AccountingClassifyAction.ListUnclassified, limit: 1);
        var secondPage = await RunAsync(service, AccountingClassifyAction.ListUnclassified, limit: 1,
                                        after: firstPage.Unclassified!.NextAfter);
        await RunAsync(service, AccountingClassifyAction.RuleAdd,
                       rule: Rule(target: "income:gifts", label: "^gift$",
                                  kinds: [AccountingEventKind.PushReceived]));
        await RunAsync(service, AccountingClassifyAction.Set, eventKey: "push:1", account: "income:liquidity-fees",
                       note: "paid for inbound");
        var resolved = await RunAsync(service, AccountingClassifyAction.ListUnclassified);

        // Assert
        Assert.Equal(["push:1", "push:2"], all.Unclassified!.Items.Select(i => i.EventKey));
        Assert.All(all.Unclassified.Items, i => Assert.Equal("income:unclassified", i.Account));
        Assert.Equal(-2_000, all.Unclassified.Items[0].AmountMsat);
        Assert.Equal("gift", all.Unclassified.Items[1].Label);
        Assert.False(all.Unclassified.HasMore);
        Assert.Equal(5, all.Unclassified.Scanned);
        Assert.Equal(["push:1"], firstPage.Unclassified.Items.Select(i => i.EventKey));
        Assert.True(firstPage.Unclassified.HasMore);
        Assert.Equal(2, firstPage.Unclassified.NextAfter);
        Assert.Equal(["push:2"], secondPage.Unclassified!.Items.Select(i => i.EventKey));
        Assert.Empty(resolved.Unclassified!.Items);
    }

    [Fact]
    public async Task Given_AnEvent_When_Tested_Then_TheAnswerSaysWhereItGoesWhyAndWhetherACandidateMatches()
    {
        // Arrange
        var service = CreateService();
        await AddAsync(Event("inv:1", AccountingEventKind.InvoiceSettled, 1_000, label: "consulting october"));
        var added = await RunAsync(service, AccountingClassifyAction.RuleAdd,
                                   rule: Rule(target: "income:consulting", label: "^consulting"));

        // Act
        var byRule = await RunAsync(service, AccountingClassifyAction.RuleTest, eventKey: "inv:1",
                                    rule: Rule(target: string.Empty, label: "october$"));
        await RunAsync(service, AccountingClassifyAction.Set, eventKey: "inv:1", account: "income:other-work");
        var byOverride = await RunAsync(service, AccountingClassifyAction.RuleTest, eventKey: "inv:1");
        var overrides = await RunAsync(service, AccountingClassifyAction.ListOverrides);
        var unset = await RunAsync(service, AccountingClassifyAction.Unset, eventKey: "inv:1");
        var afterUnset = await RunAsync(service, AccountingClassifyAction.RuleTest, eventKey: "inv:1");

        // Assert
        var test = byRule.Test!;
        Assert.Equal(1, test.LedgerSeq);
        Assert.Equal(AccountingClassificationSource.Rule, test.Classification.Source);
        Assert.Equal(added.Rules![0].Id, test.Classification.RuleId);
        Assert.Equal("income:consulting", test.Classification.Account);
        Assert.Equal(["assets:lightning:channels", "income:consulting"], test.Lines.Select(l => l.AccountName));
        Assert.Equal(0, test.Lines.Sum(l => l.AmountMsat));
        Assert.True(test.CandidateMatches);
        Assert.Equal(AccountingClassificationSource.Override, byOverride.Test!.Classification.Source);
        Assert.Equal("income:other-work", byOverride.Test.Classification.Account);
        Assert.Null(byOverride.Test.CandidateMatches);
        Assert.Equal(["inv:1"], overrides.Overrides!.Select(o => o.EventKey));
        Assert.True(unset.Changed);
        Assert.Equal(AccountingClassificationSource.Rule, afterUnset.Test!.Classification.Source);
    }

    [Fact]
    public async Task Given_AnOverrideOfAnUnknownOrUnclassifiableEvent_When_Set_Then_ItIsRefused()
    {
        // Arrange
        var service = CreateService();
        await AddAsync(Event("close:1", AccountingEventKind.ChannelClosedMutual, -500, fee: 100));
        await RunAsync(service, AccountingClassifyAction.ListUnclassified);

        // Act
        var unknown = await Assert.ThrowsAsync<ClientException>(
                          () => RunAsync(service, AccountingClassifyAction.Set, eventKey: "nope",
                                         account: "income:x"));
        var nothing = await Assert.ThrowsAsync<ClientException>(
                          () => RunAsync(service, AccountingClassifyAction.Set, eventKey: "close:1",
                                         account: "income:x"));
        var asset = await Assert.ThrowsAsync<ClientException>(
                        () => RunAsync(service, AccountingClassifyAction.Set, eventKey: "close:1",
                                       account: "assets:onchain:wallet"));

        // Assert
        Assert.Contains("No accounting event", unknown.Message);
        Assert.Contains("no income, expense or transfer line", nothing.Message);
        Assert.Contains("asset", asset.Message);
    }

    [Fact]
    public async Task Given_TheOperationalProfileAndTheBooksOff_When_Classifying_Then_RulesAreKeptWithAWarningAndTheListingIsRefused()
    {
        // Arrange
        var options = Options.Create(new AccountingOptions { Enabled = false });
        var scopes = Provider.GetRequiredService<IServiceScopeFactory>();
        await using var books = new AccountingBooksService(scopes, NullLogger<AccountingBooksService>.Instance,
                                                           options);
        var service = new AccountingClassificationService(scopes,
                                                          NullLogger<AccountingClassificationService>.Instance,
                                                          options, books, timeProvider: new FixedTimeProvider());

        // Act
        var added = await RunAsync(service, AccountingClassifyAction.RuleAdd, rule: Rule(target: "income:x"));
        var exception = await Assert.ThrowsAsync<ClientException>(
                            () => RunAsync(service, AccountingClassifyAction.ListUnclassified));

        // Assert
        Assert.Equal(AccountingProfile.Operational, added.Profile);
        Assert.Contains(added.Warnings, w => w.Contains("Operational", StringComparison.Ordinal));
        Assert.Contains("disabled", exception.Message);
    }

    [Fact]
    public async Task Given_TheServiceRegistered_When_Resolved_Then_TheAdminIsTheSameSingleton()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Provider.GetRequiredService<IServiceScopeFactory>());
        services.AddAccountingClassificationServices();
        services.AddAccountingClassificationServices();
        await using var provider = services.BuildServiceProvider();

        // Act
        var service = provider.GetRequiredService<AccountingClassificationService>();
        var admin = provider.GetRequiredService<IAccountingClassificationAdmin>();

        // Assert
        Assert.Same(service, admin);
        Assert.Single(services, d => d.ServiceType == typeof(IAccountingClassificationAdmin));
    }

    private AccountingClassificationService CreateService() =>
        new(Provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AccountingClassificationService>.Instance,
            Options.Create(new AccountingOptions { Profile = AccountingProfile.Financial }), _books, _sealer,
            new FixedTimeProvider());

    private static Task<Domain.Client.Responses.AccountingClassifyClientResponse> RunAsync(
        AccountingClassificationService service, AccountingClassifyAction action, AccountingRule? rule = null,
        long? ruleId = null, string? eventKey = null, string? account = null, string? note = null, int limit = 100,
        long after = 0) =>
        service.HandleAsync(new AccountingClassifyClientRequest
        {
            Action = action,
            Rule = rule,
            RuleId = ruleId,
            EventKey = eventKey,
            Account = account,
            Note = note,
            Limit = limit,
            AfterLedgerSeq = after
        }, TestContext.Current.CancellationToken);

    private async Task AddAsync(params AccountingEventModel[] events)
    {
        await using var scope = Provider.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        foreach (var accountingEvent in events)
            unitOfWork.AccountingEventDbRepository.Add(accountingEvent);
        await unitOfWork.SaveChangesAsync();
    }

    private static AccountingEventModel Event(string key, AccountingEventKind kind, long amountMsat, long fee = 0,
                                              string? label = null) =>
        new()
        {
            EventKey = key,
            Kind = kind,
            OccurredAt = s_at,
            AmountMsat = amountMsat,
            FeeMsat = fee,
            Details = AccountingDetailsCodec.Create((ClassificationEngine.LabelDetail, label))
        };

    private static AccountingRule Rule(string target, string? label = null, int priority = 0, string? tagKey = null,
                                       string? description = null, AccountingEventKind[]? kinds = null) =>
        new(0, priority, kinds, label, tagKey, null, null, null, null, target, true, DateTimeOffset.MinValue,
            description);

    private static ServiceProvider BuildProvider(string databasePath)
    {
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Database:Provider"] = "sqlite",
                               ["Database:ConnectionString"] = $"Data Source={databasePath}"
                           })
                           .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ISha256, Sha256>();
        services.AddPersistenceInfrastructureServices(configuration);
        services.AddRepositoriesInfrastructureServices();
        return services.BuildServiceProvider();
    }

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => s_at;
    }
}