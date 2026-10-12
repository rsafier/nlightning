using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace NLightning.Infrastructure.Bitcoin.Tests.Crypto.SilentPayments;

using Domain.Bitcoin.SilentPayments;
using Domain.Bitcoin.SilentPayments.Models;
using Domain.Crypto.ValueObjects;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Crypto.SilentPayments;
using static Bip352VectorKit;

/// <summary>Every sending and receiving case in the pinned BIP 352 v1.1.1 fixtures, including intermediates.</summary>
public class Bip352VectorTests
{
    private static readonly BigInteger s_order = new(Convert.FromHexString(
        "FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141"), true, true);
    private readonly SilentPaymentCrypto _crypto = new();

    public static TheoryData<int, int, string> SendingCases => Cases("sending");
    public static TheoryData<int, int, string> ReceivingCases => Cases("receiving");

    [Fact]
    public void Given_OfficialFixture_When_Hashed_Then_MatchesPinnedUpstreamBytes()
    {
        // Arrange
        var path = Path.Combine(AppContext.BaseDirectory, "Crypto", "SilentPayments", "Vectors",
                                "send_and_receive_test_vectors.json");

        // Act
        var digest = SHA256.HashData(File.ReadAllBytes(path));

        // Assert
        Assert.Equal("f5f9ed4afd76a1b76f3c70b1cbe67532f89abbe559f8e02d7fc3d8ecb93af4a1", Hex(digest));
        Assert.Equal(28, Vectors.GetArrayLength());
        Assert.Equal(28, SendingCases.Count);
        Assert.Equal(29, ReceivingCases.Count);
    }

    [Theory]
    [MemberData(nameof(SendingCases))]
    public void Given_SendingVector_When_InputsClassifiedAndOutputsDerived_Then_ExactKeysSecretsAndOutputSetMatch(
        int index, int subcase, string description)
    {
        // Arrange
        Assert.False(string.IsNullOrWhiteSpace(description));
        var vector = Vectors[index].GetProperty("sending")[subcase];
        var given = vector.GetProperty("given");
        var expected = vector.GetProperty("expected");
        var inputs = new List<SilentPaymentSenderInput>();
        var publicKeys = new List<CompactPubKey>();
        foreach (var input in given.GetProperty("vin").EnumerateArray())
        {
            var script = Hex(input.GetProperty("prevout").GetProperty("scriptPubKey").GetProperty("hex"));
            var eligible = _crypto.TryGetInputPublicKey(script, Hex(input.GetProperty("scriptSig")),
                                                       Witness(input), out var publicKey);
            if (eligible)
                publicKeys.Add(publicKey);
            inputs.Add(new SilentPaymentSenderInput(Outpoint(input),
                eligible ? Hex(input.GetProperty("private_key")) : null, script.Length == 34 && script[0] == 0x51));
        }

        var recipients = new List<SilentPaymentRecipient>();
        foreach (var recipient in given.GetProperty("recipients").EnumerateArray())
        {
            var address = SilentPaymentAddressCodec.Decode(recipient.GetProperty("address").GetString()!, BitcoinNetwork.Mainnet);
            Assert.Equal(Hex(recipient.GetProperty("scan_pub_key")), (byte[])address.ScanKey);
            Assert.Equal(Hex(recipient.GetProperty("spend_pub_key")), (byte[])address.SpendKey);
            var count = recipient.TryGetProperty("count", out var repetitions) ? repetitions.GetInt32() : 1;
            for (var i = 0; i < count; i++)
                recipients.Add(new SilentPaymentRecipient(new CompactPubKey(Hex(recipient.GetProperty("scan_pub_key"))),
                    new CompactPubKey(Hex(recipient.GetProperty("spend_pub_key")))));
        }

        // Act / Assert
        Assert.Equal(expected.GetProperty("input_pub_keys").EnumerateArray().Select(x => x.GetString()),
                     publicKeys.Select(x => Hex((byte[])x)));
        if (!expected.TryGetProperty("input_private_key_sum", out var expectedScalar))
        {
            Assert.All(expected.GetProperty("outputs").EnumerateArray(), set => Assert.Empty(set.EnumerateArray()));
            Assert.Throws<ArgumentException>(() => _crypto.DeriveOutputs(inputs, recipients));
            return;
        }

        var aggregate = new byte[32];
        Bip352.AggregateSenderSecret(inputs, aggregate);
        Assert.Equal(Hex(expectedScalar), aggregate);
        Assert.True(_crypto.TrySumPublicKeys(publicKeys, out var sum));
        Assert.Equal(Bip352.IndividualPublicKey(aggregate), (byte[])sum);
        var smallest = inputs.Select(x => x.Outpoint36).OrderBy(Hex, StringComparer.Ordinal).First();
        var inputHash = _crypto.ComputeInputHash(smallest, sum);
        if (recipients.GroupBy(x => x.ScanKey).Any(group => group.Count() > Bip352.MaxRecipients))
        {
            Assert.All(expected.GetProperty("outputs").EnumerateArray(), set => Assert.Empty(set.EnumerateArray()));
            Assert.Throws<ArgumentException>(() => _crypto.DeriveOutputs(inputs, recipients));
            return;
        }

        var secrets = expected.GetProperty("shared_secrets").EnumerateArray().ToArray();
        for (var i = 0; i < secrets.Length; i++)
        {
            var tweakedScanKey = Bip352.TweakInputPublicKey(recipients[i].ScanKey, inputHash);
            Assert.Equal(Hex(secrets[i]), Bip352.ComputeSharedSecret(tweakedScanKey, aggregate));
        }

        var derived = _crypto.DeriveOutputs(inputs, recipients);
        var actualSet = derived.Select(output => Hex(output.OutputKey32)).ToHashSet(StringComparer.Ordinal);
        Assert.Contains(expected.GetProperty("outputs").EnumerateArray(),
            set => actualSet.SetEquals(set.EnumerateArray().Select(x => x.GetString()!)));
        Assert.Equal(recipients.Count, derived.Count);
    }

    [Theory]
    [MemberData(nameof(ReceivingCases))]
    public void Given_ReceivingVector_When_ScannedAndSigned_Then_ExactTweakSecretReceiptsAndSignaturesMatch(
        int index, int subcase, string description)
    {
        // Arrange
        Assert.False(string.IsNullOrWhiteSpace(description));
        var vector = Vectors[index].GetProperty("receiving")[subcase];
        var given = vector.GetProperty("given");
        var expected = vector.GetProperty("expected");
        var publicKeys = new List<CompactPubKey>();
        var outpoints = new List<byte[]>();
        foreach (var input in given.GetProperty("vin").EnumerateArray())
        {
            outpoints.Add(Outpoint(input));
            if (_crypto.TryGetInputPublicKey(Hex(input.GetProperty("prevout").GetProperty("scriptPubKey").GetProperty("hex")),
                    Hex(input.GetProperty("scriptSig")), Witness(input), out var key))
                publicKeys.Add(key);
        }

        // Act / Assert
        if (!_crypto.TrySumPublicKeys(publicKeys, out var sum))
        {
            Assert.Empty(expected.GetProperty("outputs").EnumerateArray());
            return;
        }

        Assert.Equal(expected.GetProperty("input_pub_key_sum").GetString(), Hex((byte[])sum));
        var inputHash = _crypto.ComputeInputHash(outpoints.OrderBy(Hex, StringComparer.Ordinal).First(), sum);
        var tweaked = _crypto.TweakInputPublicKey(sum, inputHash);
        Assert.Equal(Hex(expected.GetProperty("tweak")), (byte[])tweaked);
        var material = given.GetProperty("key_material");
        var scanSecret = Hex(material.GetProperty("scan_priv_key"));
        var spendSecret = Hex(material.GetProperty("spend_priv_key"));
        var shared = Bip352.ComputeSharedSecret(tweaked, scanSecret);
        Assert.Equal(Hex(expected.GetProperty("shared_secret")), shared);
        var spendKey = new CompactPubKey(Bip352.IndividualPublicKey(spendSecret));
        var labelScalars = given.GetProperty("labels").EnumerateArray().ToDictionary(x => x.GetUInt32(),
            x => Bip352.ComputeLabelTweak(scanSecret, x.GetUInt32()));
        var labelPoints = labelScalars.ToDictionary(x => x.Key,
            x => new CompactPubKey(Bip352.IndividualPublicKey(x.Value)));
        var candidates = given.GetProperty("outputs").EnumerateArray().Select((x, i) =>
            new SilentPaymentScanCandidate((uint)i, Hex(x))).ToArray();
        var matches = _crypto.Scan(shared, spendKey, candidates, labelPoints);
        if (expected.TryGetProperty("n_outputs", out var expectedCount))
        {
            Assert.Equal(expectedCount.GetInt32(), matches.Count);
            Assert.Equal(Bip352.MaxRecipients, matches.Count);
            Assert.DoesNotContain(matches, match => match.OutputIndex == Bip352.MaxRecipients);
            return;
        }

        var expectedOutputs = expected.GetProperty("outputs").EnumerateArray().ToDictionary(
            output => output.GetProperty("pub_key").GetString()!, StringComparer.Ordinal);
        Assert.Equal(expectedOutputs.Count, matches.Count);
        Assert.Equal(matches.Count, matches.Select(match => match.OutputIndex).Distinct().Count());
        var message = SHA256.HashData(Encoding.UTF8.GetBytes("message"));
        var auxiliary = SHA256.HashData(Encoding.UTF8.GetBytes("random auxiliary data"));
        foreach (var match in matches)
        {
            var expectedOutput = expectedOutputs[Hex(match.OutputKey32)];
            Assert.Equal(candidates[match.OutputIndex].OutputKey32, match.OutputKey32);
            var labelScalar = match.Label.HasValue ? labelScalars[match.Label.Value] : [];
            var combinedTweak = AddScalars(match.Tweak32, labelScalar);
            Assert.Equal(Hex(expectedOutput.GetProperty("priv_key_tweak")), combinedTweak);
            var signature = Bip352.SignSpend(spendSecret, match.Tweak32, labelScalar, message, auxiliary);
            Assert.Equal(Hex(expectedOutput.GetProperty("signature")), signature);
            var privateKey = Bip352.DeriveSpendPrivateKey(spendSecret, match.Tweak32, labelScalar);
            Assert.Equal(match.OutputKey32, Bip352.IndividualPublicKey(privateKey)[1..]);
        }
    }

    [Theory]
    [MemberData(nameof(ReceivingCases))]
    public void Given_ReceivingKeysAndLabels_When_AddressesEncoded_Then_AllOfficialAddressesMatch(
        int index, int subcase, string description)
    {
        // Arrange
        Assert.False(string.IsNullOrWhiteSpace(description));
        var vector = Vectors[index].GetProperty("receiving")[subcase];
        var given = vector.GetProperty("given");
        var material = given.GetProperty("key_material");
        var scanSecret = Hex(material.GetProperty("scan_priv_key"));
        var scanKey = new CompactPubKey(Bip352.IndividualPublicKey(scanSecret));
        var spendKey = new CompactPubKey(Bip352.IndividualPublicKey(Hex(material.GetProperty("spend_priv_key"))));

        // Act
        var addresses = new List<string> { SilentPaymentAddressCodec.Encode(scanKey, spendKey, BitcoinNetwork.Mainnet) };
        foreach (var label in given.GetProperty("labels").EnumerateArray())
        {
            var labeledKey = new CompactPubKey(Bip352.AddPublicTweak(spendKey,
                Bip352.ComputeLabelTweak(scanSecret, label.GetUInt32())));
            addresses.Add(SilentPaymentAddressCodec.Encode(scanKey, labeledKey, BitcoinNetwork.Mainnet));
        }

        // Assert
        Assert.Equal(vector.GetProperty("expected").GetProperty("addresses").EnumerateArray().Select(x => x.GetString()),
                     addresses);
    }

    [Fact]
    public void Given_UnspendableTaprootOutputBesideAReceipt_When_Scanned_Then_ReceiptRetainsItsTransactionIndex()
    {
        // Arrange
        var vector = Vectors[0].GetProperty("receiving")[0];
        var expected = vector.GetProperty("expected");
        var material = vector.GetProperty("given").GetProperty("key_material");
        var spendKey = new CompactPubKey(Bip352.IndividualPublicKey(Hex(material.GetProperty("spend_priv_key"))));
        var output = Hex(expected.GetProperty("outputs")[0].GetProperty("pub_key"));
        SilentPaymentScanCandidate[] candidates =
        [
            new(0, Enumerable.Repeat((byte)0xff, 32).ToArray()),
            new(1, output)
        ];

        // Act
        var matches = _crypto.Scan(Hex(expected.GetProperty("shared_secret")), spendKey, candidates);

        // Assert
        var match = Assert.Single(matches);
        Assert.Equal(1u, match.OutputIndex);
        Assert.Equal(output, match.OutputKey32);
        Assert.Equal(Hex(expected.GetProperty("outputs")[0].GetProperty("priv_key_tweak")), match.Tweak32);
    }

    [Theory]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFEBAAEDCE6AF48A03BBFD25E8CD0364141")]
    [InlineData("FFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF")]
    public void Given_InvalidSecretScalar_When_UsedForSilentPayments_Then_FailsBeforeDerivingKeys(string scalar)
    {
        // Arrange
        var secret = Convert.FromHexString(scalar);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => Bip352.IndividualPublicKey(secret));
        Assert.Throws<ArgumentException>(() => Bip352.ComputeLabelTweak(secret, 0));
    }

    private static byte[] AddScalars(byte[] first, byte[] second)
    {
        var sum = (new BigInteger(first, true, true) + new BigInteger(second, true, true)) % s_order;
        var bytes = sum.ToByteArray(true, true);
        var result = new byte[32];
        bytes.CopyTo(result, result.Length - bytes.Length);
        return result;
    }
}