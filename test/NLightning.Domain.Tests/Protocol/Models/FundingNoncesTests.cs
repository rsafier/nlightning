namespace NLightning.Domain.Tests.Protocol.Models;

using Domain.Bitcoin.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.Models;

public class FundingNoncesTests
{
    private static TxId Txid(byte first) => new([first, .. Enumerable.Repeat((byte)0x11, 31)]);
    private static MusigPublicNonce Nonce(byte fill) => new(Enumerable.Repeat(fill, 66).ToArray());

    [Fact]
    public void Given_UnsortedEntries_When_Constructed_Then_SortedByTxIdBytes()
    {
        // Arrange
        var high = (Txid(0xf0), Nonce(0x02));
        var low = (Txid(0x01), Nonce(0x03));

        // Act
        var nonces = new FundingNonces([high, low]);

        // Assert
        Assert.Equal(2, nonces.Count);
        Assert.Equal(Txid(0x01), nonces.Entries[0].FundingTxId);
        Assert.Equal(Txid(0xf0), nonces.Entries[1].FundingTxId);
        var bytes = nonces.ToBytes();
        Assert.Equal(2 * FundingNonces.EntryLength, bytes.Length);
        Assert.Equal((byte[])Txid(0x01), bytes[..32]);
        Assert.Equal((byte[])Nonce(0x03), bytes[32..98]);
        Assert.Equal((byte[])Txid(0xf0), bytes[98..130]);
    }

    [Fact]
    public void Given_Map_When_TryGetNonce_Then_FindsByTxId()
    {
        // Arrange
        var nonces = new FundingNonces([(Txid(0x01), Nonce(0x03)), (Txid(0x02), Nonce(0x04))]);

        // Act / Assert
        Assert.True(nonces.TryGetNonce(Txid(0x02), out var nonce));
        Assert.Equal(Nonce(0x04), nonce);
        Assert.False(nonces.Contains(Txid(0x03)));
        Assert.True(FundingNonces.Single(Txid(0x01), Nonce(0x03)).Contains(Txid(0x01)));
    }

    [Fact]
    public void Given_SameTxIdTwice_When_Constructed_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => new FundingNonces([(Txid(0x01), Nonce(0x03)),
                                                                  (Txid(0x01), Nonce(0x04))]));
    }

    [Fact]
    public void Given_SeventeenEntries_When_Constructed_Then_Throws()
    {
        // Arrange (LND's limit is 16)
        var entries = Enumerable.Range(0, FundingNonces.MaxEntries + 1).Select(i => (Txid((byte)i), Nonce(0x03)));

        // Act / Assert
        Assert.Throws<ArgumentException>(() => new FundingNonces(entries));
        Assert.Equal(16, new FundingNonces(entries.Take(16)).Count);
    }

    [Fact]
    public void Given_SameEntriesInAnotherOrder_When_Equals_Then_True()
    {
        // Act / Assert
        var a = new FundingNonces([(Txid(0x01), Nonce(0x03)), (Txid(0x02), Nonce(0x04))]);
        var b = new FundingNonces([(Txid(0x02), Nonce(0x04)), (Txid(0x01), Nonce(0x03))]);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
    }
}