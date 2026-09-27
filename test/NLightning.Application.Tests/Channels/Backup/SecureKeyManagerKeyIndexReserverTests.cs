namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Domain.Bitcoin.ValueObjects;
using Domain.Protocol.Interfaces;

public class SecureKeyManagerKeyIndexReserverTests
{
    private delegate void NextChannelKey(out uint index);

    private uint _lastUsedIndex;

    [Fact]
    public async Task Given_AStaleKeyFileIndex_When_Reserved_Then_TheIndexIsAdvancedToTheHighestRestored()
    {
        // Arrange: the key file says 2, the restored channels use up to 9
        _lastUsedIndex = 2;
        var keyManager = CreateKeyManager();
        var reserver = new SecureKeyManagerKeyIndexReserver(keyManager.Object);

        // Act
        var last = await reserver.ReserveThroughAsync(9, TestContext.Current.CancellationToken);

        // Assert: the next channel gets 10, never a restored channel's index
        Assert.Equal(9u, last);
        Assert.Equal(9u, _lastUsedIndex);
        keyManager.Object.GetNextChannelKey(out var next);
        Assert.Equal(10u, next);
    }

    [Fact]
    public async Task Given_AKeyFileIndexAlreadyPastTheRestoredChannels_When_Reserved_Then_OnlyOneIndexIsUsedUp()
    {
        // Arrange
        _lastUsedIndex = 20;
        var keyManager = CreateKeyManager();
        var reserver = new SecureKeyManagerKeyIndexReserver(keyManager.Object);

        // Act
        var last = await reserver.ReserveThroughAsync(9, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(21u, last);
        keyManager.Verify(k => k.GetNextChannelKey(out It.Ref<uint>.IsAny), Times.Once);
    }

    private Mock<ISecureKeyManager> CreateKeyManager()
    {
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.GetNextChannelKey(out It.Ref<uint>.IsAny))
                  .Callback(new NextChannelKey((out uint index) => index = ++_lastUsedIndex))
                  .Returns(default(ExtPrivKey));
        return keyManager;
    }
}