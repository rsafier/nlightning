namespace NLightning.Infrastructure.Bitcoin.Tests.Crypto.Musig2;

using Infrastructure.Bitcoin.Crypto.Musig2;
using static Musig2VectorKit;

/// <summary>
/// BIP 327 <c>key_sort_vectors.json</c> and <c>key_agg_vectors.json</c>, every case, through the BIP 327 module and,
/// where the Domain types can hold the inputs, through <see cref="Musig2Service"/>.
/// </summary>
public class Bip327KeyVectorTests
{
    private const string KeySortFile = "key_sort_vectors.json";
    private const string KeyAggFile = "key_agg_vectors.json";

    private readonly Musig2Service _service = new();

    public static TheoryData<int> KeyAggValidCases => CaseIndexes(KeyAggFile, "valid_test_cases");
    public static TheoryData<int> KeyAggErrorCases => CaseIndexes(KeyAggFile, "error_test_cases");

    [Fact]
    public void Given_KeySortVector_When_Sorted_Then_EqualsSortedPubkeys()
    {
        // Arrange
        var vector = Load(KeySortFile);
        var pubKeys = HexArray(vector.GetProperty("pubkeys"));
        var expected = HexArray(vector.GetProperty("sorted_pubkeys"));

        // Act
        var sorted = Bip327.KeySort(pubKeys);
        var sortedByService = _service.SortPubKeys(ToPubKeys(pubKeys));

        // Assert
        Assert.Equal(expected, sorted);
        Assert.Equal(expected, sortedByService.Select(k => (byte[])k));
    }

    [Theory]
    [MemberData(nameof(KeyAggValidCases))]
    public void Given_KeyAggValidCase_When_Aggregated_Then_XOnlyKeyEqualsExpected(int index)
    {
        // Arrange
        var vector = Load(KeyAggFile);
        var testCase = Case(KeyAggFile, "valid_test_cases", index);
        var pubKeys = Pick(HexArray(vector.GetProperty("pubkeys")), Ints(testCase.GetProperty("key_indices")));
        var expected = Hex(testCase.GetProperty("expected"));

        // Act
        var xOnly = Bip327.GetXOnlyPubKey(Bip327.KeyAgg(pubKeys));
        var aggregate = _service.AggregatePubKeys(ToPubKeys(pubKeys));

        // Assert
        Assert.Equal(expected, xOnly);
        Assert.Equal(expected, ((byte[])aggregate.InternalKey)[1..]);
        Assert.Equal(aggregate.InternalKey, aggregate.OutputKey);
    }

    [Theory]
    [MemberData(nameof(KeyAggErrorCases))]
    public void Given_KeyAggErrorCase_When_AggregatedAndTweaked_Then_FailsAsTheVectorSays(int index)
    {
        // Arrange
        var vector = Load(KeyAggFile);
        var testCase = Case(KeyAggFile, "error_test_cases", index);
        var pubKeys = Pick(HexArray(vector.GetProperty("pubkeys")), Ints(testCase.GetProperty("key_indices")));
        var tweaks = Tweaks(HexArray(vector.GetProperty("tweaks")), testCase);

        // Act / Assert
        AssertError(testCase.GetProperty("error"), () => Bip327.KeyAggAndTweak(pubKeys, tweaks));

        // The Domain key type refuses a prefix other than 02/03 itself; the service sees every other case
        if (pubKeys.All(k => k[0] is 0x02 or 0x03))
            AssertError(testCase.GetProperty("error"),
                        () => _service.AggregatePubKeys(ToPubKeys(pubKeys), ToMusigTweaks(tweaks)));
        else
            Assert.ThrowsAny<ArgumentException>(() => ToPubKeys(pubKeys));
    }
}