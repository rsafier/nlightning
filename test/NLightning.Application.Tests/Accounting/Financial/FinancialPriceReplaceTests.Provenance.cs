using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace NLightning.Application.Tests.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Books.Export;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Export;
using Domain.Accounting.Prices;
using Infrastructure.Persistence.Contexts;

public sealed partial class FinancialPriceReplaceTests
{
    [Fact]
    public async Task Given_AnUnusedPrice_When_ReplacedTwice_Then_ListRetainsEveryOldValueAndOperatorNote()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_feb2);
        await kit.AddPricesAsync((s_jan10, 50_000m));
        await kit.Valuation.ReplaceAsync(new AccountingPriceReplacement(Usd, s_jan10, 45_000m,
            "exchange statement", "decimal slip"), ct);
        kit.Clock.Now = s_feb2.AddMinutes(1);
        await kit.Valuation.ReplaceAsync(new AccountingPriceReplacement(Usd, s_jan10, 48_000m,
            "corrected statement", "second review"), ct);
        var price = Assert.Single(await kit.ListPricesAsync());
        var history = await kit.Valuation.ListReplacementAuditsAsync([price.Id], ct);
        Assert.Equal(2, history.Count);
        Assert.Equal(50_000m, history[0].OldPrice);
        Assert.Equal(45_000m, history[0].NewPrice);
        Assert.Equal(AccountingPriceSource.Csv, history[0].OldSource);
        Assert.Equal(AccountingPriceSource.Manual, history[0].NewSource);
        Assert.Equal("exchange statement", history[0].OperatorSource);
        Assert.Equal("decimal slip", history[0].Note);
        Assert.Equal(s_feb2, history[0].ReplacedAt);
        Assert.Equal(45_000m, history[1].OldPrice);
        Assert.Equal(48_000m, history[1].NewPrice);
        Assert.Equal(AccountingPriceSource.Manual, history[1].OldSource);
        Assert.Equal(s_feb2, history[1].OldFetchedAt);
        Assert.Equal("second review", history[1].Note);
        Assert.True(history[1].Id > history[0].Id);
        await kit.Valuation.ReplaceAsync(new AccountingPriceReplacement(Usd, s_jan10, 48_000m), ct);
        Assert.Equal(2, (await kit.Valuation.ListReplacementAuditsAsync([price.Id], ct)).Count);
    }

    [Theory]
    [InlineData(AccountingExportFormat.Csv)]
    [InlineData(AccountingExportFormat.Hledger)]
    [InlineData(AccountingExportFormat.Beancount)]
    public async Task Given_ASignedClose_When_ItsPriceIsCorrectedAndTheOpenBookRebuilt_Then_ExportsAndClosedLotCostsRemainOriginal(
        AccountingExportFormat format)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await StoryAsync(50_000m);
        await kit.Periods.CloseAsync("2026-01", false, ct);
        var query = new AccountingFinancialExportQuery(format, 0, 1000, s_jan1, s_feb2.AddDays(-1));
        var before = await kit.CreateExports().ExportAsync(query, ct);
        var costs = (await kit.ListLotsAsync()).ToDictionary(l => l.Lot.Id, l => l.Lot.FiatCost);
        var period = await kit.ReadAsync(u => u.AccountingPeriodDbRepository.GetAsync("2026-01", ct));
        var captured = AccountingClosingState.Decode(period!.ClosingState);
        Assert.Equal(50_000m, Assert.Single(captured.Prices, p => p.Time == s_jan10).Price);
        await kit.Valuation.ReplaceAsync(new AccountingPriceReplacement(Usd, s_jan10, 45_000m, "operator", "correction"), ct);
        kit.Clock.Now = s_feb2.AddMinutes(1);
        await kit.Valuation.ReplaceAsync(new AccountingPriceReplacement(Usd, s_jan10, 48_000m), ct);
        await kit.ProjectAsync();
        Assert.Equal(before.Text, (await kit.CreateExports().ExportAsync(query, ct)).Text);
        foreach (var (lot, _) in await kit.ListLotsAsync())
            if (costs.TryGetValue(lot.Id, out var cost)) Assert.Equal(cost, lot.FiatCost);
        Assert.All(await kit.Periods.VerifyClosesAsync(ct), verification => Assert.True(verification.IsIntact, verification.Problem));
        var incremental = await kit.SnapshotFinancialAsync();
        await kit.Periods.RebuildFinancialAsync(ct);
        Assert.Equal(incremental, await kit.SnapshotFinancialAsync());
        Assert.Equal(before.Text, (await kit.CreateExports().ExportAsync(query, ct)).Text);
        Assert.Equal(period.Digest, (await kit.ReadAsync(u => u.AccountingPeriodDbRepository.GetAsync("2026-01", ct)))!.Digest);
    }

    [Fact]
    public async Task Given_AnOperatorCorrection_When_CommitFailsAfterSql_Then_PriceAndAuditRollBackTogetherAndRetryWritesOnce()
    {
        var ct = TestContext.Current.CancellationToken;
        var failure = new RejectPriceCommit();
        await using var kit = await FinancialProjectorTestKit.CreateAsync(s_feb2,
            configureServices: services => services.AddDbContext<NLightningDbContext>(options => options.AddInterceptors(failure)));
        await kit.AddPricesAsync((s_jan10, 50_000m));
        var original = Assert.Single(await kit.ListPricesAsync());
        var replacement = new AccountingPriceReplacement(Usd, s_jan10, 45_000m, "operator", "retry correction");
        failure.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Valuation.ReplaceAsync(replacement, ct));
        Assert.True(failure.Rejected > 0);
        Assert.Equal(original, Assert.Single(await kit.ListPricesAsync()));
        Assert.Empty(await kit.Valuation.ListReplacementAuditsAsync([original.Id], ct));
        failure.Armed = false;
        await kit.Valuation.ReplaceAsync(replacement, ct);
        Assert.Equal(45_000m, Assert.Single(await kit.ListPricesAsync()).Price);
        Assert.Single(await kit.Valuation.ListReplacementAuditsAsync([original.Id], ct));
    }

    [Fact]
    public async Task Given_ASignedLegacyClose_When_ItsPriceIsCorrected_Then_VerificationAndOriginalExportSurvive()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await StoryAsync(50_000m);
        await kit.Periods.CloseAsync("2026-01", false, ct);
        await MakeLegacyCloseAsync(kit);
        var query = new AccountingFinancialExportQuery(AccountingExportFormat.Csv, 0, 1000, s_jan1, s_feb2.AddDays(-1));
        var original = await kit.CreateExports().ExportAsync(query, ct);
        kit.Clock.Now = s_feb2.AddMinutes(1);
        await kit.Valuation.ReplaceAsync(new AccountingPriceReplacement(Usd, s_jan10, 45_000m), ct);
        Assert.Equal(original.Text, (await kit.CreateExports().ExportAsync(query, ct)).Text);
        Assert.All(await kit.Periods.VerifyClosesAsync(ct), verification => Assert.True(verification.IsIntact, verification.Problem));
    }

    [Fact]
    public async Task Given_ALegacyCloseWithAnUnauditedCorrection_When_Exported_Then_TheUnknownOriginalPriceIsExplicit()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var kit = await StoryAsync(50_000m);
        await kit.Periods.CloseAsync("2026-01", false, ct);
        await MakeLegacyCloseAsync(kit);
        var price = Assert.Single(await kit.ListPricesAsync(), p => p.Time == s_jan10);
        using (var scope = kit.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<Domain.Persistence.Interfaces.IUnitOfWork>();
            await uow.AccountingPriceDbRepository.ReplaceAsync(price.Id, 45_000m, AccountingPriceSource.Manual, s_feb2.AddMinutes(1), ct);
            await uow.SaveChangesAsync();
        }
        var exported = await kit.CreateExports().ExportAsync(new AccountingFinancialExportQuery(
            AccountingExportFormat.Csv, 0, 1000, s_jan1, s_feb2.AddDays(-1)), ct);
        Assert.Contains("original price unavailable", exported.Text);
        Assert.DoesNotContain(",45000,", exported.Text);
        Assert.All(await kit.Periods.VerifyClosesAsync(ct), verification => Assert.True(verification.IsIntact, verification.Problem));
    }

    // Seed a genuinely signed pre-NL-759 close: its canonical state contains no snapshot field, and the signature
    // covers that exact old encoding. This is fixture preparation, not a production rewrite of a locked period.
    private static async Task MakeLegacyCloseAsync(FinancialProjectorTestKit kit)
    {
        var ct = TestContext.Current.CancellationToken;
        using var scope = kit.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<Domain.Persistence.Interfaces.IUnitOfWork>();
        var period = (await uow.AccountingPeriodDbRepository.GetAsync("2026-01", ct))!;
        var state = AccountingClosingState.Decode(period.ClosingState) with { Prices = [] };
        var text = state.Encode();
        using var digest = new AccountingCloseDigest(new AccountingCloseHeader(period.PeriodId, period.Start, period.End,
            period.LastLedgerSeq, period.ChainHash!, null, (byte[])kit.Signer.GetNodePublicKey(), period.Forced, period.ClosedAt!.Value));
        foreach (var entry in await uow.AccountingBooksDbRepository.ListEntriesAsync(new AccountingEntryQuery(0, 1000)
        { Book = AccountingBook.Financial, AfterAdjustment = -1, ClosedPeriodId = period.PeriodId }, ct))
            digest.AddEntry(entry);
        var lots = uow.AccountingLotDbRepository;
        foreach (var relief in await lots.ListPeriodReliefsAsync(period.PeriodId, period.End, 0, 1000, ct))
            digest.AddRelief(relief);
        var since = await lots.SumReliefsSinceAsync(period.End, ct);
        foreach (var lot in await lots.ListLotsAcquiredBeforeAsync(period.End, 0, 1000, ct))
        {
            var remaining = lot.RemainingMsat + since.GetValueOrDefault(lot.Id);
            if (remaining > 0) digest.AddOpenLot(lot, remaining);
        }
        var hash = digest.Finish(text);
        await uow.AccountingPeriodDbRepository.UpdateAsync(period with
        {
            ClosingState = text,
            Digest = hash,
            Signature = (byte[])kit.Signer.SignNodeMessage(new Domain.Crypto.ValueObjects.Hash(hash))
        }, ct);
        await uow.SaveChangesAsync();
    }

    [Fact]
    public void Given_ALegacyClosingState_When_DecodedAndEncoded_Then_NoPriceFieldsChangeItsDigestInput()
    {
        const string legacy = "{\"version\":1,\"replayAfter\":7,\"balances\":[]}";
        var state = AccountingClosingState.Decode(legacy);
        Assert.Empty(state.Prices);
        Assert.Equal(legacy, state.Encode());
    }

    private sealed class RejectPriceCommit : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public int Rejected { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (Armed)
            {
                Rejected++;
                throw new InvalidOperationException("Reject the price correction after SQL writes.");
            }
            return ValueTask.FromResult(result);
        }
    }
}