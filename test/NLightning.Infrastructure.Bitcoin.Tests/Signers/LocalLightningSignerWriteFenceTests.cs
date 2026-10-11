using System.Reflection;
using Microsoft.Extensions.Logging;
using NBitcoin;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.Infrastructure.Bitcoin.Tests.Signers;

using Domain.Bitcoin.Interfaces;
using Domain.Crypto.ValueObjects;
using Domain.Node.Fencing;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Signers;

/// <summary>
/// NL-1341: <see cref="LocalLightningSigner"/> asks the node write fence before every signature. Every public
/// <c>Sign*</c>/<c>Aggregate*</c> method checks it first, so a refusal makes no signature (whatever the arguments); a
/// fence that allows it changes no signature.
/// </summary>
public class LocalLightningSignerWriteFenceTests
{
    private static readonly byte[] s_nodePrivateKey =
        Convert.FromHexString("1111111111111111111111111111111111111111111111111111111111111111");

    private readonly Mock<ISecureKeyManager> _secureKeyManager = new();

    public LocalLightningSignerWriteFenceTests()
    {
        using var key = new Key(s_nodePrivateKey);
        CompactPubKey nodeId = key.PubKey.ToBytes();
        _secureKeyManager.Setup(x => x.GetNodeKeyPair())
                         .Returns(() => new CryptoKeyPair(s_nodePrivateKey.ToArray(), nodeId));
    }

    public static TheoryData<string> SigningMethods()
    {
        var data = new TheoryData<string>();
        foreach (var method in GetSigningMethods())
            data.Add(Describe(method));
        return data;
    }

    [Fact]
    public void Given_TheSigner_When_ItsSigningMethodsAreListed_Then_EveryKnownKindIsThere()
    {
        // Arrange / Act
        var names = GetSigningMethods().Select(m => m.Name).Append(nameof(LocalLightningSigner.SignLightningMessage))
                                       .ToHashSet();

        // Assert: commitments, HTLCs, funding, wallet, sweeps, gossip, splices, closes, BOLT 12 and messages
        Assert.Superset(new HashSet<string>
        {
            "SignNodeMessage", "SignNodeMessageBip340", "SignLightningMessage", "SignWalletMessage",
            "SignChannelAnnouncement", "SignChannelAnnouncement2", "SignRemoteHtlcTransactions",
            "SignLocalHtlcTransaction", "SignLocalCommitmentForBroadcast", "SignSweepInput", "SignWalletTransaction",
            "SignFundingTransaction", "SignChannelTransaction", "SignAnchorInput", "SignTaprootAnchorInput",
            "SignRemoteCommitmentPartial", "SignClosingAsCloser", "SignClosingAsClosee", "AggregateClosingSignature",
            "SignSpliceSharedInput", "SignSpliceSharedInputPartial", "AggregateSpliceSharedInputSignature", "SignBolt12"
        }, names);
    }

    [Theory]
    [MemberData(nameof(SigningMethods))]
    public void Given_ARefusingFence_When_ASigningMethodIsCalled_Then_TheFenceRefusalIsThrownFirst(string method)
    {
        // Arrange: default arguments; the fence is asked before any of them is looked at
        var fence = new FakeNodeWriteFence { Refuse = true };
        var signer = CreateSigner(fence);
        var target = GetSigningMethods().Single(m => Describe(m) == method);
        var arguments = target.GetParameters().Select(p => p.ParameterType.IsValueType
                                                               ? Activator.CreateInstance(p.ParameterType)
                                                               : null).ToArray();

        // Act
        var exception = Assert.Throws<TargetInvocationException>(() => target.Invoke(signer, arguments));

        // Assert
        Assert.IsType<NodeFencedException>(exception.InnerException);
        Assert.Contains(NodeEffect.Sign, fence.Effects);
    }

    [Fact]
    public void Given_ARefusingFence_When_ALightningMessageIsSigned_Then_NoSignatureIsMade()
    {
        // Arrange
        var fence = new FakeNodeWriteFence { Refuse = true };
        var signer = CreateSigner(fence);

        // Act / Assert
        Assert.Throws<NodeFencedException>(() => signer.SignLightningMessage("hello"u8, false));
        _secureKeyManager.Verify(x => x.GetNodeKeyPair(), Times.Never);
    }

    [Fact]
    public void Given_AFenceThatAllowsIt_When_ANodeMessageIsSigned_Then_TheSignatureIsTheUnfencedOne()
    {
        // Arrange
        var fence = new FakeNodeWriteFence();
        var hash = new Hash(Enumerable.Repeat((byte)0x42, 32).ToArray());

        // Act
        var fenced = CreateSigner(fence).SignNodeMessage(hash);
        var unfenced = CreateSigner(null).SignNodeMessage(hash);

        // Assert
        Assert.Equal((byte[])unfenced, (byte[])fenced);
        Assert.Equal([NodeEffect.Sign], fence.Effects);
    }

    private LocalLightningSigner CreateSigner(INodeWriteFence? fence) =>
        new(new Mock<IFundingOutputBuilder>().Object, new Mock<IKeyDerivationService>().Object,
            new Mock<ILogger<LocalLightningSigner>>().Object, new NodeOptions(), _secureKeyManager.Object,
            new Mock<IUtxoMemoryRepository>().Object, writeFence: fence);

    // Methods with a by-ref-like parameter (a span) cannot be invoked by reflection; they have their own test
    private static IEnumerable<MethodInfo> GetSigningMethods() =>
        typeof(LocalLightningSigner).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                                    .Where(m => m.Name.StartsWith("Sign", StringComparison.Ordinal)
                                             || m.Name.StartsWith("Aggregate", StringComparison.Ordinal))
                                    .Where(m => m.GetParameters().All(p => !p.ParameterType.IsByRefLike
                                                                        && !p.ParameterType.IsByRef));

    private static string Describe(MethodInfo method) =>
        $"{method.Name}({string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name))})";
}