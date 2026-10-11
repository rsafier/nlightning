namespace NLightning.Application.Tests.Channels.Backup;

using Application.Channels.Backup;
using Domain.Bitcoin.ValueObjects;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;
using Infrastructure.Bitcoin.Managers;

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
        keyManager.Verify(k => k.ReserveChannelKeyIndex(), Times.Once);
    }

    [Fact]
    public async Task Given_AKeyManagerThatDoesNotAdvance_When_Reserved_Then_ItThrowsInsteadOfLooping()
    {
        // Arrange: a key manager (e.g. a bare mock) that always hands out index 0
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.EnsureLastUsedChannelIndexAtLeast(It.IsAny<uint>()))
                  .Throws<NotSupportedException>();
        var reserver = new SecureKeyManagerKeyIndexReserver(keyManager.Object);

        // Act / Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => reserver.ReserveThroughAsync(2, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Given_AFileBackedKeyManagerBehind_When_Reserved_Then_TheIndexIsRaisedAndPersistedWithoutUsingOneUp()
    {
        // Arrange
        var directory = Directory.CreateTempSubdirectory("nltg-reserver-");
        try
        {
            var path = Path.Combine(directory.FullName, "nltg.key.json");
            using var keyManager = SecureKeyManager.CreateNew(BitcoinNetwork.Regtest, path, 0);
            var reserver = new SecureKeyManagerKeyIndexReserver(keyManager);

            // Act
            var last = await reserver.ReserveThroughAsync(9, TestContext.Current.CancellationToken);
            var again = await reserver.ReserveThroughAsync(3, TestContext.Current.CancellationToken);

            // Assert: the next channel gets 10 (the second call never lowers it nor uses an index up)
            Assert.Equal(9u, last);
            Assert.Equal(3u, again);
            keyManager.GetNextChannelKey(out var next);
            Assert.Equal(10u, next);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    private Mock<ISecureKeyManager> CreateKeyManager()
    {
        var keyManager = new Mock<ISecureKeyManager>();
        keyManager.Setup(k => k.EnsureLastUsedChannelIndexAtLeast(It.IsAny<uint>()))
                  .Throws<NotSupportedException>();
        keyManager.Setup(k => k.ReserveChannelKeyIndex()).Returns(() => ++_lastUsedIndex);
        keyManager.Setup(k => k.GetNextChannelKey(out It.Ref<uint>.IsAny))
                  .Callback(new NextChannelKey((out uint index) => index = ++_lastUsedIndex))
                  .Returns(default(ExtPrivKey));
        return keyManager;
    }
}