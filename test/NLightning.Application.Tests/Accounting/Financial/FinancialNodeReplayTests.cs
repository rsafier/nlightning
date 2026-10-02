using System.Globalization;
using System.Text;

namespace NLightning.Application.Tests.Accounting.Financial;

using Domain.Accounting.Books;
using Domain.Accounting.Financial;
using Domain.Accounting.Financial.Lots;
using Domain.Bitcoin.Interfaces;
using Domain.Crypto.ValueObjects;

/// <summary>
/// Replays a node's financial book from a copy of its database (NL-749): set <c>NLTG_REPLAY_DB</c> to a SQLite file
/// (it is copied first, the copy migrated and rebuilt; the copy is deleted after) and run explicitly; prints each asset
/// account's balance next to the open lots and debts of its bucket and the closes' verification (with
/// <c>NLTG_REPLAY_NODE_ID</c>, the node's id in hex, they verify as on the node), writes the report to
/// <c>NLTG_REPLAY_OUT.{method}.txt</c> when set (<c>NLTG_REPLAY_TRACE=1</c> adds every entry and lot), and checks the
/// invariant per bucket.
/// </summary>
public class FinancialNodeReplayTests
{
    [Theory(Explicit = true)]
    [InlineData(AccountingCostBasisMethod.Fifo)]
    [InlineData(AccountingCostBasisMethod.Lifo)]
    [InlineData(AccountingCostBasisMethod.Hifo)]
    public async Task Given_ANodeDatabase_When_TheFinancialBookIsRebuilt_Then_ItsLotsByBucketArePrinted(
        AccountingCostBasisMethod method)
    {
        var source = Environment.GetEnvironmentVariable("NLTG_REPLAY_DB");
        Assert.SkipWhen(string.IsNullOrEmpty(source), "NLTG_REPLAY_DB is not set");
        var copy = Path.Combine(Path.GetTempPath(), $"nltg-replay-{Guid.NewGuid():N}.db");
        File.Copy(source!, copy);

        // The node's own id (NLTG_REPLAY_NODE_ID, hex) makes the stored closes' digests and signatures comparable
        ILightningSigner? signer = null;
        if (Environment.GetEnvironmentVariable("NLTG_REPLAY_NODE_ID") is { Length: 66 } nodeIdHex)
        {
            CompactPubKey nodeId = Convert.FromHexString(nodeIdHex);
            var verifier = FinancialProjectorTestKit.CreateSigner();
            var mock = new Mock<ILightningSigner>();
            mock.Setup(s => s.GetNodePublicKey()).Returns(nodeId);
            mock.Setup(s => s.VerifyNodeMessage(It.IsAny<Hash>(), It.IsAny<CompactSignature>(),
                                                It.IsAny<CompactPubKey>()))
                .Returns((Hash hash, CompactSignature signature, CompactPubKey key) =>
                             verifier.VerifyNodeMessage(hash, signature, key));
            signer = mock.Object;
        }

        await using var kit = await FinancialProjectorTestKit.CreateAsync(DateTimeOffset.UtcNow, method,
                                                                          databasePath: copy, signer: signer);
        await kit.ProjectAsync();
        await kit.Periods.RebuildFinancialAsync(TestContext.Current.CancellationToken);

        var report = new StringBuilder();
        foreach (var close in await kit.Periods.VerifyClosesAsync(TestContext.Current.CancellationToken))
            report.AppendLine(CultureInfo.InvariantCulture,
                              $"close {close.PeriodId}: digest {close.DigestMatches} chain {close.ChainHashMatches} "
                            + $"state {close.ClosingStateMatches} contiguous {close.Contiguous} signature "
                            + $"{close.SignatureValid}");
        var balances = await kit.ReadAsync(u => u.AccountingBooksDbRepository.GetAccountBalancesAsync(
                                               AccountingBook.Financial, TestContext.Current.CancellationToken));
        var lots = await kit.ListLotsAsync();
        var open = lots.Select(l => l.Lot).Where(l => l.RemainingMsat > 0).ToList();
        var invariantHolds = true;
        foreach (var balance in balances.Where(b => FinancialLotRules.IsAsset(b.Account)))
        {
            if (!balance.AccountName!.StartsWith("assets:", StringComparison.Ordinal))
                continue;

            var bucket = (AccountingLotBucket)(int)balance.Account;
            var held = open.Where(l => !l.IsDebt && l.Bucket == bucket).ToList();
            var owedTo = open.Where(l => l.IsDebt && l.Lender == bucket).ToList();
            var owedBy = open.Where(l => l.IsDebt && l.Bucket == bucket).ToList();
            report.AppendLine(CultureInfo.InvariantCulture,
                              $"{method} {balance.AccountName,-26} balance {balance.BalanceMsat,14} msat "
                            + $"{balance.FiatAmount,14} | lots {held.Sum(l => l.RemainingMsat),14} msat "
                            + $"{held.Sum(l => CostOf(l)),14} ({held.Count}) | owed to it "
                            + $"{owedTo.Sum(l => l.RemainingMsat)} ({owedTo.Sum(l => CostOf(l))}) owed by it "
                            + $"{owedBy.Sum(l => l.RemainingMsat)} ({owedBy.Sum(l => CostOf(l))})");
            invariantHolds &= balance.BalanceMsat == held.Sum(l => l.RemainingMsat) + owedTo.Sum(l => l.RemainingMsat)
                                                   - owedBy.Sum(l => l.RemainingMsat);
        }

        foreach (var group in open.GroupBy(l => (l.IsDebt, l.Bucket, l.Lender)))
            report.AppendLine(CultureInfo.InvariantCulture,
                              $"  {(group.Key.IsDebt ? "debt" : "lots")} {group.Key.Bucket?.ToString() ?? "-"}"
                            + $"{(group.Key.Lender is { } lender ? " -> " + lender : "")}: "
                            + $"{group.Sum(l => l.RemainingMsat)} msat, {group.Count()} rows");

        if (Environment.GetEnvironmentVariable("NLTG_REPLAY_TRACE") is { Length: > 0 })
        {
            foreach (var entry in await kit.ListEntriesAsync(AccountingBook.Financial))
                report.AppendLine(FinancialProjectorTestKit.Describe(entry));
            foreach (var (lot, reliefs) in lots)
            {
                report.AppendLine(CultureInfo.InvariantCulture,
                                  $"L{lot.Id} {lot.Bucket} {lot.Origin} src {lot.SourceLedgerSeq} parent {lot.ParentLotId} "
                                + $"lender {lot.Lender} {lot.OriginalMsat} left {lot.RemainingMsat} cost {lot.FiatCost}");
                foreach (var relief in reliefs)
                    report.AppendLine(CultureInfo.InvariantCulture,
                                      $"   R {relief.LedgerSeq}/{relief.Adjustment} {relief.Kind} {relief.Msat}");
            }
        }

        TestContext.Current.SendDiagnosticMessage(report.ToString());
        if (Environment.GetEnvironmentVariable("NLTG_REPLAY_OUT") is { Length: > 0 } output)
            await File.WriteAllTextAsync($"{output}.{method}.txt", report.ToString(),
                                         TestContext.Current.CancellationToken);

        // Each asset account: its bucket's lots, plus what it is owed, less what it owes (NL-749)
        Assert.True(invariantHolds, report.ToString());
    }

    // The cost of what is left of a lot
    private static decimal CostOf(AccountingLot lot) =>
        FinancialLotPool.CostOf(lot, lot.RemainingMsat, FinancialProjectorTestKit.Usd) ?? 0m;
}