using Microsoft.Extensions.Logging;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Signers;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.KeyIndexes;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Services;
using Infrastructure.Bitcoin.Signers;

/// <summary>NL-1388: a new channel's key index is claimed in the database's high-water mark before it is used.</summary>
public class LocalLightningSignerKeyIndexTests
{
    private static readonly ExtKey s_master = ExtKey.CreateFromSeed(Enumerable.Repeat((byte)0x11, 32).ToArray());

    [Fact]
    public void Given_AClaimedIndex_When_CreatingAChannel_Then_ItsKeyIsDerivedAtThatIndex()
    {
        // Arrange
        var keys = KeyManager(3);
        var store = new Mock<IChannelKeyIndexStore>();
        store.Setup(s => s.TryClaim(4)).Returns(true);
        var signer = CreateSigner(keys.Object, store.Object);

        // Act
        var index = signer.CreateNewChannel(out _, out _);

        // Assert
        Assert.Equal(4u, index);
        keys.Verify(k => k.GetChannelKeyAtIndex(4), Times.Once);
        keys.Verify(k => k.GetNextChannelKey(out It.Ref<uint>.IsAny), Times.Never);
    }

    [Fact]
    public void Given_AnIndexAnotherProcessClaimed_When_CreatingAChannel_Then_ItSkipsPastTheMark()
    {
        // Arrange: the key manager is at 3, the database's mark at 9
        var keys = KeyManager(3);
        var store = new Mock<IChannelKeyIndexStore>();
        store.Setup(s => s.TryClaim(It.Is<uint>(i => i <= 9))).Returns(false);
        store.Setup(s => s.TryClaim(10)).Returns(true);
        store.Setup(s => s.GetHighWater()).Returns(9u);
        var signer = CreateSigner(keys.Object, store.Object);

        // Act
        var index = signer.CreateNewChannel(out _, out _);

        // Assert
        Assert.Equal(10u, index);
        keys.Verify(k => k.EnsureLastUsedChannelIndexAtLeast(9), Times.Once);
        store.Verify(s => s.TryClaim(4), Times.Once);
        store.Verify(s => s.TryClaim(10), Times.Once);
    }

    [Fact]
    public void Given_AStoreThatFails_When_CreatingAChannel_Then_TheChannelIsRefused()
    {
        // Arrange
        var keys = KeyManager(0);
        var store = new Mock<IChannelKeyIndexStore>();
        store.Setup(s => s.TryClaim(It.IsAny<uint>())).Throws(new InvalidOperationException("database down"));
        var signer = CreateSigner(keys.Object, store.Object);

        // Act & Assert
        Assert.Throws<SignerException>(() => signer.CreateNewChannel(out _, out _));
        keys.Verify(k => k.GetChannelKeyAtIndex(It.IsAny<uint>()), Times.Never);
    }

    [Fact]
    public void Given_AStoreThatNeverClaims_When_CreatingAChannel_Then_ItGivesUp()
    {
        // Arrange
        var keys = KeyManager(0);
        var store = new Mock<IChannelKeyIndexStore>();
        store.Setup(s => s.TryClaim(It.IsAny<uint>())).Returns(false);
        var signer = CreateSigner(keys.Object, store.Object);

        // Act & Assert
        Assert.Throws<SignerException>(() => signer.CreateNewChannel(out _, out _));
    }

    [Fact]
    public void Given_AHeldKeyIndexGate_When_CreatingAChannel_Then_NothingIsReservedOrClaimed()
    {
        // Arrange: a restore holds the gate (NL-1392)
        var keys = KeyManager(3);
        var store = new Mock<IChannelKeyIndexStore>();
        var gate = new ChannelKeyIndexGate();
        using var restore = gate.BeginRestore();
        var signer = CreateSigner(keys.Object, store.Object, gate);

        // Act & Assert
        var exception = Assert.Throws<ChannelKeyIndexHeldException>(() => signer.CreateNewChannel(out _, out _));
        Assert.Equal(ChannelKeyIndexHold.RestoreInProgressReason, exception.Reason);
        keys.Verify(k => k.ReserveChannelKeyIndex(), Times.Never);
        keys.Verify(k => k.GetChannelKeyAtIndex(It.IsAny<uint>()), Times.Never);
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public void Given_AnOpenKeyIndexGate_When_CreatingAChannel_Then_TheGateRemembersTheIndex()
    {
        // Arrange
        var keys = KeyManager(3);
        var store = new Mock<IChannelKeyIndexStore>();
        store.Setup(s => s.TryClaim(4)).Returns(true);
        var gate = new ChannelKeyIndexGate();
        var signer = CreateSigner(keys.Object, store.Object, gate);

        // Act
        var index = signer.CreateNewChannel(out _, out _);

        // Assert
        Assert.Equal(4u, index);
        Assert.True(gate.IsAllocatedByThisProcess(4));
    }

    [Fact]
    public void Given_NoStoreAndAnOpenGate_When_CreatingAChannel_Then_TheKeyManagersNextKeyIsUsedAndRemembered()
    {
        // Arrange: without a store the key manager's next key, as before NL-1388
        var keys = KeyManager(3);
        keys.Setup(k => k.GetNextChannelKey(out It.Ref<uint>.IsAny))
            .Returns(new GetNextChannelKeyCallback((out uint index) =>
             {
                 index = 6;
                 return s_master.Derive(6).ToBytes();
             }));
        var gate = new ChannelKeyIndexGate();
        var signer = new LocalLightningSigner(new Mock<IFundingOutputBuilder>().Object, new KeyDerivationService(),
                                              new Mock<ILogger<LocalLightningSigner>>().Object, new NodeOptions(),
                                              keys.Object, new Mock<IUtxoMemoryRepository>().Object,
                                              keyIndexGate: gate);

        // Act
        var index = signer.CreateNewChannel(out _, out _);

        // Assert
        Assert.Equal(6u, index);
        Assert.True(gate.IsAllocatedByThisProcess(6));
        keys.Verify(k => k.GetChannelKeyAtIndex(It.IsAny<uint>()), Times.Never);
    }

    private delegate ExtPrivKey GetNextChannelKeyCallback(out uint index);

    /// <summary>A key manager whose reservations count up from <paramref name="lastUsed"/> and that can be raised.
    /// </summary>
    private static Mock<ISecureKeyManager> KeyManager(uint lastUsed)
    {
        var keys = new Mock<ISecureKeyManager>();
        keys.Setup(k => k.ReserveChannelKeyIndex()).Returns(() => ++lastUsed);
        keys.Setup(k => k.EnsureLastUsedChannelIndexAtLeast(It.IsAny<uint>()))
            .Returns((uint highest) =>
             {
                 if (highest <= lastUsed)
                     return false;
                 lastUsed = highest;
                 return true;
             });
        keys.Setup(k => k.GetChannelKeyAtIndex(It.IsAny<uint>()))
            .Returns((uint index) => s_master.Derive(index).ToBytes());
        return keys;
    }

    private static LocalLightningSigner CreateSigner(ISecureKeyManager keys, IChannelKeyIndexStore store,
                                                     IChannelKeyIndexGate? gate = null) =>
        new(new Mock<IFundingOutputBuilder>().Object, new KeyDerivationService(),
            new Mock<ILogger<LocalLightningSigner>>().Object, new NodeOptions(), keys,
            new Mock<IUtxoMemoryRepository>().Object, keyIndexStore: store, keyIndexGate: gate);
}