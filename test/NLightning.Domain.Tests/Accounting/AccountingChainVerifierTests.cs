namespace NLightning.Domain.Tests.Accounting;

using Domain.Accounting.Enums;
using Domain.Accounting.Interfaces;
using Domain.Accounting.Models;
using Domain.Accounting.Services;

/// <summary>
/// <c>accounting verify</c>'s chain walk (NL-602 A2): an intact chain over several pages, an edited row, a row whose
/// hash was rewritten to match its edit (breaks at the next row), a gap and a missing hash.
/// </summary>
public class AccountingChainVerifierTests
{
    private static readonly DateTimeOffset s_at = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Given_AnIntactChain_When_VerifiedInSmallPages_Then_TheTipIsTheLastEvent()
    {
        // Arrange
        var chain = Seal(5);

        // Act
        var result = await AccountingChainVerifier.VerifyAsync(Repository(chain), 2,
                                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.IsIntact);
        Assert.Equal(5, result.VerifiedCount);
        Assert.Equal(5, result.TipLedgerSeq);
        Assert.Equal(chain[^1].Hash, result.TipHash);
    }

    [Fact]
    public async Task Given_AnEmptyFeed_When_Verified_Then_IntactAtGenesis()
    {
        // Act
        var result = await AccountingChainVerifier.VerifyAsync(Repository([]), 10,
                                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.True(result.IsIntact);
        Assert.Equal(0, result.TipLedgerSeq);
        Assert.Equal(new byte[32], result.TipHash);
    }

    [Fact]
    public async Task Given_AnEditedRow_When_Verified_Then_ItIsTheBreak()
    {
        // Arrange
        var chain = Seal(4);
        chain[2] = With(chain[2], amountMsat: 1, hash: chain[2].Hash);

        // Act
        var result = await AccountingChainVerifier.VerifyAsync(Repository(chain), 10,
                                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, result.BreakLedgerSeq);
        Assert.Equal(2, result.TipLedgerSeq);
        Assert.Equal(chain[1].Hash, result.TipHash);
    }

    [Fact]
    public async Task Given_AnEditWhoseHashWasRewritten_When_Verified_Then_TheNextRowIsTheBreak()
    {
        // Arrange
        var chain = Seal(4);
        var edited = With(chain[1], amountMsat: 1, hash: null);
        chain[1] = With(edited, amountMsat: 1,
                        hash: AccountingEventHasher.ComputeHash(chain[0].Hash, 2, edited));

        // Act
        var result = await AccountingChainVerifier.VerifyAsync(Repository(chain), 10,
                                                               TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(3, result.BreakLedgerSeq);
    }

    [Fact]
    public async Task Given_AGapOrAMissingHash_When_Verified_Then_TheMissingSequenceIsTheBreak()
    {
        // Arrange
        var gap = Seal(4);
        gap.RemoveAt(1);
        var noHash = Seal(3);
        noHash[0] = With(noHash[0], noHash[0].AmountMsat, hash: null);

        // Act
        var gapResult = await AccountingChainVerifier.VerifyAsync(Repository(gap), 10,
                                                                  TestContext.Current.CancellationToken);
        var noHashResult = await AccountingChainVerifier.VerifyAsync(Repository(noHash), 10,
                                                                     TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(2, gapResult.BreakLedgerSeq);
        Assert.Contains("missing", gapResult.BreakReason);
        Assert.Equal(1, noHashResult.BreakLedgerSeq);
        Assert.Equal(0, noHashResult.TipLedgerSeq);
    }

    private static IAccountingEventDbRepository Repository(List<AccountingEventModel> chain)
    {
        var repository = new Mock<IAccountingEventDbRepository>();
        repository.Setup(r => r.GetSealedRangeAsync(It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
                  .ReturnsAsync((long from, int take, CancellationToken _) =>
                                    chain.Where(e => e.LedgerSeq >= from).Take(take).ToList());
        return repository.Object;
    }

    private static List<AccountingEventModel> Seal(int count)
    {
        var chain = new List<AccountingEventModel>();
        var previous = new byte[32];
        for (var seq = 1; seq <= count; seq++)
        {
            var unsealed = new AccountingEventModel
            {
                EventKey = $"k:{seq}",
                Kind = AccountingEventKind.InvoiceSettled,
                OccurredAt = s_at.AddSeconds(seq),
                AmountMsat = seq * 1_000,
                LedgerSeq = seq
            };
            var hash = AccountingEventHasher.ComputeHash(previous, seq, unsealed);
            chain.Add(With(unsealed, unsealed.AmountMsat, hash));
            previous = hash;
        }

        return chain;
    }

    private static AccountingEventModel With(AccountingEventModel source, long amountMsat, byte[]? hash) => new()
    {
        EventKey = source.EventKey,
        Kind = source.Kind,
        OccurredAt = source.OccurredAt,
        AmountMsat = amountMsat,
        LedgerSeq = source.LedgerSeq,
        Hash = hash
    };
}