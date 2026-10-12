namespace NLightning.Infrastructure.Bitcoin.Tests.Crypto.Musig2;

using Domain.Crypto.ValueObjects;
using Infrastructure.Bitcoin.Crypto.Musig2;
using static Musig2VectorKit;

/// <summary>
/// BIP 327 <c>nonce_gen_vectors.json</c> and <c>nonce_agg_vectors.json</c>, every case, through the BIP 327 module and
/// <see cref="Musig2Service"/>.
/// </summary>
public class Bip327NonceVectorTests
{
    private const string NonceGenFile = "nonce_gen_vectors.json";
    private const string NonceAggFile = "nonce_agg_vectors.json";

    private readonly Musig2Service _service = new();

    public static TheoryData<int> NonceGenCases => CaseIndexes(NonceGenFile, "test_cases");
    public static TheoryData<int> NonceAggValidCases => CaseIndexes(NonceAggFile, "valid_test_cases");
    public static TheoryData<int> NonceAggErrorCases => CaseIndexes(NonceAggFile, "error_test_cases");

    [Theory]
    [MemberData(nameof(NonceGenCases))]
    public void Given_NonceGenCase_When_GeneratedWithTheVectorRandomness_Then_NoncesEqualExpected(int index)
    {
        // Arrange
        var testCase = Case(NonceGenFile, "test_cases", index);
        var rand = Hex(testCase.GetProperty("rand_"));
        var secretKey = HexOrNull(testCase.GetProperty("sk"));
        var pubKey = Hex(testCase.GetProperty("pk"));
        var aggPubKey = HexOrNull(testCase.GetProperty("aggpk"));
        var msg = HexOrNull(testCase.GetProperty("msg"));
        var extraIn = HexOrNull(testCase.GetProperty("extra_in"));
        var expectedSecNonce = Hex(testCase.GetProperty("expected_secnonce"));
        var expectedPubNonce = Hex(testCase.GetProperty("expected_pubnonce"));

        // Act
        var (secNonce, pubNonce) = Bip327.NonceGen(rand, pubKey, secretKey, aggPubKey, msg, extraIn);
        var pair = _service.GenerateNonce(rand, pubKey, secretKey is null ? (PrivKey?)null : new PrivKey(secretKey),
                                          aggPubKey, msg, extraIn);

        // Assert
        Assert.Equal(expectedSecNonce, secNonce);
        Assert.Equal(expectedPubNonce, pubNonce);
        Assert.Equal(expectedPubNonce, (byte[])pair.PublicNonce);
        Assert.Equal(pubKey, (byte[])pair.SecretNonce.PublicKey);
        Assert.False(pair.SecretNonce.IsUsed);
    }

    [Theory]
    [MemberData(nameof(NonceAggValidCases))]
    public void Given_NonceAggValidCase_When_Aggregated_Then_EqualsExpected(int index)
    {
        // Arrange
        var vector = Load(NonceAggFile);
        var testCase = Case(NonceAggFile, "valid_test_cases", index);
        var pubNonces = Pick(HexArray(vector.GetProperty("pnonces")), Ints(testCase.GetProperty("pnonce_indices")));
        var expected = Hex(testCase.GetProperty("expected"));

        // Act
        var aggNonce = Bip327.NonceAgg(pubNonces);
        var aggNonceByService = _service.AggregateNonces(pubNonces.Select(n => (MusigPublicNonce)n).ToArray());

        // Assert
        Assert.Equal(expected, aggNonce);
        Assert.Equal(expected, (byte[])aggNonceByService);
    }

    [Theory]
    [MemberData(nameof(NonceAggErrorCases))]
    public void Given_NonceAggErrorCase_When_Aggregated_Then_FailsAsTheVectorSays(int index)
    {
        // Arrange
        var vector = Load(NonceAggFile);
        var testCase = Case(NonceAggFile, "error_test_cases", index);
        var pubNonces = Pick(HexArray(vector.GetProperty("pnonces")), Ints(testCase.GetProperty("pnonce_indices")));

        // Act / Assert
        AssertError(testCase.GetProperty("error"), () => Bip327.NonceAgg(pubNonces));
        AssertError(testCase.GetProperty("error"),
                    () => _service.AggregateNonces(pubNonces.Select(n => (MusigPublicNonce)n).ToArray()));
    }
}