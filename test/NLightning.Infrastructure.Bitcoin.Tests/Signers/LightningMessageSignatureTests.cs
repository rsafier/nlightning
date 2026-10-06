using Microsoft.Extensions.Logging;

namespace NLightning.Infrastructure.Bitcoin.Tests.Signers;

using Domain.Bitcoin.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Signers;

/// <summary>
/// LND's message signatures (NL-1162): <see cref="LocalLightningSigner.SignLightningMessage"/> byte-exact against the
/// signatures LND's own code path makes (btcec v2.3.6 <c>ecdsa.SignCompact</c> over SHA256d / SHA256 of
/// <c>"Lightning Signed Message:" || msg</c>; <c>scripts/lnd-grpc/signmessage-vectors</c>), and
/// <see cref="LightningMessageSignature.Recover"/> against signatures real LND nodes made (CLN's <c>test_signmessage</c>
/// corpus, "contributions from LND users").
/// </summary>
public class LightningMessageSignatureTests
{
    /// <summary>key | message | single_hash | node id | signature (hex), from the Go generator.</summary>
    public static readonly TheoryData<string, string, bool, string, string> LndVectors = new()
    {
        { "0000000000000000000000000000000000000000000000000000000000000001", "", false, "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "20cf2dbee488a97471ef6338860e208bcb85cbdca19d5906b298fd1be065791a8e21718d0060bc7733e4b53ad1f427c103d09c4619a6d42e5774c73aa0b59a3c15" },
        { "0000000000000000000000000000000000000000000000000000000000000001", "", true, "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "1f7ea04ec3abe0c026808908b4858f0d964d4cf795b8bd2e72e44757754351c1f222e170144a0cdb15b01eb4907247477defc60248746a453a97b5ea62db4eec88" },
        { "0000000000000000000000000000000000000000000000000000000000000001", "hi", false, "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "1fdb6b10757ffec34c53240f903ccc46f019abb736642c30db4438c83b32ff16732f22e75f23e057b2d8fc493018dc1ea5b9ba021d64ba5827ed78750ee6a41759" },
        { "0000000000000000000000000000000000000000000000000000000000000001", "hi", true, "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "1fcadb90ddc6972f5e1457014ba4bfa1e3abbceaca261d1e6dfb3e202b66d3bd3b26b689657caaf96fe4a7b6273e12ee22f88fc71d053e9333fb7c3540ab7424ca" },
        { "0000000000000000000000000000000000000000000000000000000000000001", "is this compatible?", false, "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "1fed1f80374a8183261265d625a258f6373ae260f27dfe14b41f3a6f0c6fb44b5843adf84d30f276f1aab73f6d67008119d71c9267f05db99c07f8830a49e18960" },
        { "0000000000000000000000000000000000000000000000000000000000000001", "is this compatible?", true, "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "20d2843657c3716fc3f57529b9a77c3a5a197d8f83bdc74c89ceb15095ffbd2d21547c5158a487867f6c080cee383f82360544d4765b929aa746251866843b5a91" },
        { "0000000000000000000000000000000000000000000000000000000000000001", "Lightning Signed Message: nested", false, "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "1f85fc425b7842a910458109f70b5ffc249d9388b58dc93341aa455c40da3d78fe75379de8706c98f5533755cf3eed5e5518354cfd314fc8334df44cf8b7faba8c" },
        { "0000000000000000000000000000000000000000000000000000000000000001", "Lightning Signed Message: nested", true, "0279be667ef9dcbbac55a06295ce870b07029bfcdb2dce28d959f2815b16f81798", "1f6b46b1860efa7235d70826e45191f41c6cf64bdba59777a7e69183cffa1edad7229174c75ae08fc8353fe49871fb89b25e1bd19e8a169b7739b11be2fcfbc0ae" },
        { "1111111111111111111111111111111111111111111111111111111111111111", "", false, "034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa", "1f5b4172ff40972a8dd3e8c8c3df0991676a5329ff2db351263ce6ca28e855fefc66e12e6bee529a8da4552ea1fa40426b17e1b0eb60342a17134307a04d8e07aa" },
        { "1111111111111111111111111111111111111111111111111111111111111111", "", true, "034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa", "200b566aa8ad5bb727461d9de48d366851b32cd0a4f302cc3811d1d874afb0500e199c8c3ee68f783674e8297ed401bfc903cee9c70ccfb417396846bdc156c78d" },
        { "1111111111111111111111111111111111111111111111111111111111111111", "hi", false, "034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa", "2036f699aff84a71332da5c1e9005559b0434e893b4b0f450b5a01b5db4b8a612f2f6c25503e820f5cff60c6b862e8950d20c0e3a8db783d3df1b18f2592554631" },
        { "1111111111111111111111111111111111111111111111111111111111111111", "hi", true, "034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa", "20f4e65356a689a5cc1eeef4b3e83db160006e9d2a044cbaaf97aa9ec3f17bc0e87be19a22ad3965d9cc9f22326dcd9023d2ca00e19d617d37fcd24f31b1a84026" },
        { "1111111111111111111111111111111111111111111111111111111111111111", "is this compatible?", false, "034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa", "20fbc70ebd8cfb7ffe2f1add2976e5773deabc638cfad25edf23acfa0690ccfdca5ee248dbb0f7d23c6d247591a059145d8dcbed50edf9d31c2be327e73a168447" },
        { "1111111111111111111111111111111111111111111111111111111111111111", "is this compatible?", true, "034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa", "1fa7364dc828798d7f5692a46cebf78d2e5ce23813aa3df177acdbc6bd08e0119b1439f0cfb14d9acf19cbee0b6b085f8609595cdf7e1407b3dc85c7f8d3ba735f" },
        { "1111111111111111111111111111111111111111111111111111111111111111", "Lightning Signed Message: nested", false, "034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa", "1f188dc454274091bdb6eb07b62c4bb16189797e7dcb354872933d62ba5c1a883a2190e29fa33fdb387389d5f6a608abb61d2dace9421098749b964eefb7cb40a6" },
        { "1111111111111111111111111111111111111111111111111111111111111111", "Lightning Signed Message: nested", true, "034f355bdcb7cc0af728ef3cceb9615d90684bb5b2ca5f859ab0f0b704075871aa", "20ede784e6f6e78dde3ba1a9d9aaf7f500c6f87c03fbd4440a44bb5520c0f093252afdcf439769aa68b80dcbde90f8484a3c14a56fc015eea62adcc1a04be12bf1" },
        { "e126f68f7eafcc8b74f54d269fe206be715000f94dac067d1c04a8ca3b2db734", "", false, "03e7156ae33b0a208d0744199163177e909e80176e55d97a2f221ede0f934dd9ad", "20ea151edc632bb5ccbe95ce7f756b299cf0b18e873c5345fe0d144aab3d6f3b47079e5afb422097db489f41bfe9d5f2c5644aa5aff24bcfd68c553da7e8693bd6" },
        { "e126f68f7eafcc8b74f54d269fe206be715000f94dac067d1c04a8ca3b2db734", "", true, "03e7156ae33b0a208d0744199163177e909e80176e55d97a2f221ede0f934dd9ad", "1f649a0d8a4db1d47499a4310b5ac43b2223da55c56d277461acf9a6bd209a0f7962107996774aa40e378b2a2713bb390910891155e515364a782f7172e57417c2" },
        { "e126f68f7eafcc8b74f54d269fe206be715000f94dac067d1c04a8ca3b2db734", "hi", false, "03e7156ae33b0a208d0744199163177e909e80176e55d97a2f221ede0f934dd9ad", "206b2ed7945aa1f56a5e47469ff77079a66072363b6298adbbd02a25478cd3781f43bdf8231599a6916592b4fcdece25f053b265fce86d394083f2af1a64d800f0" },
        { "e126f68f7eafcc8b74f54d269fe206be715000f94dac067d1c04a8ca3b2db734", "hi", true, "03e7156ae33b0a208d0744199163177e909e80176e55d97a2f221ede0f934dd9ad", "20bd87cdd60f4bed6af0c3ae6e5516128d22194ec15757d8a28359112129324da515423aa8b05a30883febfbef31af0f8980f8b6292069e1f482ef5a4935579d78" },
        { "e126f68f7eafcc8b74f54d269fe206be715000f94dac067d1c04a8ca3b2db734", "is this compatible?", false, "03e7156ae33b0a208d0744199163177e909e80176e55d97a2f221ede0f934dd9ad", "20bffb69acf913d196315f09dc0e98cbae4d28d626b90eba51527f115d63df96c742283abcdea6ce4212019f1224fd116a422dde59b8b128cf91de6706b4d03727" },
        { "e126f68f7eafcc8b74f54d269fe206be715000f94dac067d1c04a8ca3b2db734", "is this compatible?", true, "03e7156ae33b0a208d0744199163177e909e80176e55d97a2f221ede0f934dd9ad", "20488536588975e74329153dbeb606293d7622228c3fd739102a0c3c7131af12f061be85079f26e8e357a5396db25a259eb0e8446166e71534b7ca70d739f3d2a3" },
        { "e126f68f7eafcc8b74f54d269fe206be715000f94dac067d1c04a8ca3b2db734", "Lightning Signed Message: nested", false, "03e7156ae33b0a208d0744199163177e909e80176e55d97a2f221ede0f934dd9ad", "1f0297812c62d87eaa036ca6bd1b9b0f7fccca387f3bc58080a1036c8682dccc1f7bfb3c4a76efc3f982123a5f881e4ce1b764cfc29670af342f6eefd29781f854" },
        { "e126f68f7eafcc8b74f54d269fe206be715000f94dac067d1c04a8ca3b2db734", "Lightning Signed Message: nested", true, "03e7156ae33b0a208d0744199163177e909e80176e55d97a2f221ede0f934dd9ad", "2028477c38daa97621887173eaa067b8b962db260a344843a648b3ff69128a1b506a10fce0647a526ee6a69ffa794e5e78928ab3d4f174cac9f61d030f306a9f2b" },
    };

    /// <summary>message | signature (z-base-32 decoded to hex) | node id: made by LND nodes (CLN's corpus, credited
    /// there to @bitconner, @duck1123 and @jochemin).</summary>
    public static readonly TheoryData<string, string, string> LndUserSignatures = new()
    {
        {
            "is this compatible?",
            "rbgfioj114mh48d8egqx8o9qxqw4fmhe8jbeeabdioxnjk8z3t1ma1hu1fiswpakgucwwzwo6ofycffbsqusqdimugbh41n1g698hr9t",
            "02b80cabdf82638aac86948e4c06e82064f547768dcef977677b9ea931ea75bab5"
        },
        {
            "hi",
            "rnrphcjswusbacjnmmmrynh9pqip7sy5cx695h6mfu64iac6qmcmsd8xnsyczwmpqp9shqkth3h4jmkgyqu5z47jfn1q7gpxtaqpx4xg",
            "02de60d194e1ca5947b59fe8e2efd6aadeabfb67f2e89e13ae1a799c1e08e4a43b"
        },
        {
            "hi",
            "ry8bbsopmduhxy3dr5d9ekfeabdpimfx95kagdem7914wtca79jwamtbw4rxh69hg7n6x9ty8cqk33knbxaqftgxsfsaeprxkn1k48p3",
            "022b8ece90ee891cbcdac0c1cc6af46b73c47212d8defbce80265ac81a6b794931"
        }
    };

    [Theory]
    [MemberData(nameof(LndVectors))]
    public void Given_LndVector_When_SignLightningMessage_Then_SignatureIsByteExact(
        string keyHex, string message, bool singleHash, string nodeIdHex, string signatureHex)
    {
        // Arrange
        var signer = CreateSigner(Convert.FromHexString(keyHex), nodeIdHex);

        // Act
        var signature = signer.SignLightningMessage(System.Text.Encoding.UTF8.GetBytes(message), singleHash);

        // Assert
        Assert.Equal(signatureHex, Convert.ToHexStringLower(signature));
    }

    [Theory]
    [MemberData(nameof(LndVectors))]
    public void Given_LndVector_When_Recovered_Then_TheNodeIdComesBack(
        string keyHex, string message, bool singleHash, string nodeIdHex, string signatureHex)
    {
        // Arrange
        _ = keyHex;
        var bytes = System.Text.Encoding.UTF8.GetBytes(message);

        // Act
        var recovered = LightningMessageSignature.Recover(bytes, Convert.FromHexString(signatureHex), singleHash);

        // Assert
        Assert.Equal(nodeIdHex, recovered?.ToString());
    }

    [Theory]
    [MemberData(nameof(LndUserSignatures))]
    public void Given_SignatureMadeByLnd_When_Recovered_Then_ItIsTheLndNodesKey(string message, string zbase32,
                                                                                 string nodeIdHex)
    {
        // Arrange
        var signature = ZBase32Decode(zbase32);

        // Act
        var recovered = LightningMessageSignature.Recover(System.Text.Encoding.UTF8.GetBytes(message), signature);
        var modified = LightningMessageSignature.Recover(System.Text.Encoding.UTF8.GetBytes(message + "modified"),
                                                         signature);

        // Assert
        Assert.Equal(nodeIdHex, recovered?.ToString());
        Assert.NotEqual(nodeIdHex, modified?.ToString());
    }

    [Fact]
    public void Given_GarbageSignatures_When_Recovered_Then_Null()
    {
        // Arrange
        var message = "hi"u8.ToArray();
        var badHeader = new byte[65];
        badHeader[0] = 26;
        var zeroes = new byte[65];
        zeroes[0] = 31;

        // Act / Assert
        Assert.Null(LightningMessageSignature.Recover(message, new byte[64]));
        Assert.Null(LightningMessageSignature.Recover(message, badHeader));
        Assert.Null(LightningMessageSignature.Recover(message, zeroes));
    }

    [Fact]
    public void Given_AMessage_When_Digested_Then_ThePrefixIsInside()
    {
        // Arrange
        var expected = System.Security.Cryptography.SHA256.HashData(
            System.Security.Cryptography.SHA256.HashData("Lightning Signed Message:hi"u8));

        // Act
        var digest = LightningMessageSignature.Digest("hi"u8, false);

        // Assert
        Assert.Equal(expected, digest);
    }

    private static LocalLightningSigner CreateSigner(byte[] privateKey, string nodeIdHex)
    {
        var keyManager = new Mock<ISecureKeyManager>();
        var nodeId = new CompactPubKey(Convert.FromHexString(nodeIdHex));
        keyManager.Setup(x => x.GetNodeKeyPair()).Returns(() => new CryptoKeyPair(privateKey.ToArray(), nodeId));
        return new LocalLightningSigner(new Mock<IFundingOutputBuilder>().Object,
                                        new Mock<IKeyDerivationService>().Object,
                                        new Mock<ILogger<LocalLightningSigner>>().Object, new NodeOptions(),
                                        keyManager.Object, new Mock<IUtxoMemoryRepository>().Object);
    }

    /// <summary>z-base-32 (the server's codec lives in NLightning.LndGrpc; this is a plain reference decoder).</summary>
    private static byte[] ZBase32Decode(string text)
    {
        const string alphabet = "ybndrfg8ejkmcpqxot1uwisza345h769";
        var bits = string.Concat(text.Select(c => Convert.ToString(alphabet.IndexOf(c), 2).PadLeft(5, '0')));
        return Enumerable.Range(0, bits.Length / 8).Select(i => Convert.ToByte(bits.Substring(i * 8, 8), 2)).ToArray();
    }
}