namespace NLightning.Application.Tests.OnionMessages;

using Application.OnionMessages;
using Application.Tests.Payments;
using Domain.Protocol.OnionMessages;

public class PendingReplyRegistryTests
{
    private static ReceivedOnionMessage Message(params ulong[] types) =>
        new(new OnionMessageContents(types.Select(t => new OnionMessageTlvRecord(t, new byte[] { 1 })).ToList()),
            null, null, new TestNodeKeyManager(1).NodeId);

    [Fact]
    public async Task Given_AWait_When_AReplyOfAnExpectedTypeArrives_Then_ItCompletes()
    {
        // Arrange
        var registry = new PendingReplyRegistry();
        using var pending = registry.TryRegister([66, 68])!;
        var reply = Message(68);

        // Act
        var taken = registry.TryComplete(pending.PathId, reply);

        // Assert
        Assert.True(taken);
        Assert.Same(reply, await pending.Reply);
        Assert.Equal(0, registry.Count);
        // Only once
        Assert.False(registry.TryComplete(pending.PathId, reply));
    }

    [Theory]
    [InlineData(new ulong[] { 64 })]
    [InlineData(new ulong[] { 66, 68 })]
    [InlineData(new ulong[0])]
    public void Given_AWait_When_TheMessageIsNotAnExpectedReply_Then_NotTaken(ulong[] types)
    {
        // Arrange
        var registry = new PendingReplyRegistry();
        using var pending = registry.TryRegister([66, 68])!;

        // Act
        var taken = registry.TryComplete(pending.PathId, Message(types));

        // Assert
        Assert.False(taken);
        Assert.False(pending.Reply.IsCompleted);
        Assert.Equal(1, registry.Count);
    }

    [Fact]
    public void Given_OurPathIds_When_Checked_Then_RecognizedEvenAfterTheWaitEnded()
    {
        // Arrange
        var registry = new PendingReplyRegistry();
        var pending = registry.TryRegister([66])!;
        var pathId = pending.PathId;
        pending.Dispose();
        var other = new PendingReplyRegistry().TryRegister([66])!.PathId;

        // Act / Assert
        Assert.True(registry.IsOurs(pathId));
        Assert.False(registry.IsOurs(other));
        Assert.False(registry.IsOurs(pathId.AsSpan(1)));
        Assert.False(registry.TryComplete(pathId, Message(66)));
        Assert.Equal(PendingReplyRegistry.PathIdLength, pathId.Length);
    }

    [Fact]
    public void Given_TheLimit_When_Registering_Then_Refused()
    {
        // Arrange
        var registry = new PendingReplyRegistry(maxPending: 1);
        using var first = registry.TryRegister([66]);

        // Act
        var second = registry.TryRegister([66]);

        // Assert
        Assert.NotNull(first);
        Assert.Null(second);
    }
}