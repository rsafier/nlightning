namespace NLightning.Domain.Tests.Channels.KeyIndexes;

using Domain.Channels.KeyIndexes;

/// <summary>NL-1392: the one gate in front of every new channel key index.</summary>
public class ChannelKeyIndexGateTests
{
    [Fact]
    public void Given_AnOpenGate_When_AnIndexIsAllocated_Then_ItIsHandedOutAndRemembered()
    {
        // Arrange
        var gate = new ChannelKeyIndexGate();

        // Act
        var index = gate.Allocate(() => 5);

        // Assert
        Assert.Equal(5u, index);
        Assert.True(gate.IsAllocatedByThisProcess(5));
        Assert.False(gate.IsAllocatedByThisProcess(4));
        Assert.Null(gate.HoldReason);
    }

    [Fact]
    public async Task Given_ARestoreInProgress_When_AnIndexIsAsked_Then_ItIsRefusedWithoutAllocating()
    {
        // Arrange
        var gate = new ChannelKeyIndexGate();
        var called = false;

        // Act
        using (await gate.BeginRestoreAsync(TestContext.Current.CancellationToken))
        {
            var exception = Assert.Throws<ChannelKeyIndexHeldException>(() => gate.Allocate(() =>
            {
                called = true;
                return 1;
            }));

            // Assert
            Assert.Equal(ChannelKeyIndexHold.RestoreInProgressReason, exception.Reason);
            Assert.IsAssignableFrom<InvalidOperationException>(exception);
            Assert.Contains("NL-1392", exception.Message);
            Assert.True(gate.IsRestoreInProgress);
        }

        Assert.False(called);
        Assert.False(gate.IsRestoreInProgress);
        Assert.Equal(2u, gate.Allocate(() => 2));
    }

    [Fact]
    public async Task Given_NestedRestores_When_OneEnds_Then_TheGateStaysHeldUntilBothEnd()
    {
        // Arrange
        var ct = TestContext.Current.CancellationToken;
        var gate = new ChannelKeyIndexGate();
        var first = await gate.BeginRestoreAsync(ct);
        var second = await gate.BeginRestoreAsync(ct);

        // Act
        first.Dispose();
        first.Dispose();

        // Assert: disposing twice ends one restore only
        Assert.True(gate.IsRestoreInProgress);
        second.Dispose();
        Assert.False(gate.IsRestoreInProgress);
    }

    [Fact]
    public async Task Given_AnAllocationInProgress_When_ARestoreBegins_Then_ItAwaitsTheAllocationWithoutBlockingReads()
    {
        // Arrange: an allocation blocked inside the gate (a slow signer)
        var ct = TestContext.Current.CancellationToken;
        var gate = new ChannelKeyIndexGate();
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var allocation = Task.Run(() => gate.Allocate(() =>
        {
            inside.Set();
            release.Wait(ct);
            return 7;
        }), ct);
        inside.Wait(ct);

        // Act
        var restore = gate.BeginRestoreAsync(ct);
        var startedEarly = await Task.WhenAny(restore, Task.Delay(TimeSpan.FromMilliseconds(200), ct)) == restore;

        // Assert: while the allocation runs the hold is already visible without waiting for it, and a new allocation
        // is refused at once
        Assert.False(startedEarly);
        Assert.Equal(ChannelKeyIndexHold.RestoreInProgressReason, gate.HoldReason);
        Assert.True(gate.IsRestoreInProgress);
        Assert.Throws<ChannelKeyIndexHeldException>(() => gate.Allocate(() => 8));
        release.Set();
        using var hold = await restore;
        Assert.Equal(7u, await allocation);
        Assert.True(gate.IsAllocatedByThisProcess(7));
        Assert.False(gate.IsAllocatedByThisProcess(8));
    }

    [Fact]
    public async Task Given_ACancelledRestoreStart_When_Cancelled_Then_TheGateIsNotLeftHeld()
    {
        // Arrange: an allocation holds the gate's semaphore
        var gate = new ChannelKeyIndexGate();
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var allocation = Task.Run(() => gate.Allocate(() =>
        {
            inside.Set();
            release.Wait(TestContext.Current.CancellationToken);
            return 3;
        }), TestContext.Current.CancellationToken);
        inside.Wait(TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();

        // Act
        var restore = gate.BeginRestoreAsync(cancel.Token);
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => restore);
        release.Set();
        await allocation;

        // Assert
        Assert.False(gate.IsRestoreInProgress);
        Assert.Equal(4u, gate.Allocate(() => 4));
    }

    [Fact]
    public async Task Given_TheStartHold_When_ARestoreCompletes_Then_OpensAreAllowedAgain()
    {
        // Arrange
        var gate = new ChannelKeyIndexGate();
        gate.HoldUntilRestore();
        Assert.Equal(ChannelKeyIndexHold.AwaitingRestoreReason, gate.HoldReason);
        Assert.Throws<ChannelKeyIndexHeldException>(() => gate.Allocate(() => 1));

        // Act
        using (await gate.BeginRestoreAsync(TestContext.Current.CancellationToken))
            gate.CompleteRestore();

        // Assert
        Assert.False(gate.IsAwaitingRestore);
        Assert.Null(gate.HoldReason);
    }

    [Fact]
    public async Task Given_TheStartHold_When_TheOperatorReleasesIt_Then_OnlyTheStartHoldIsLifted()
    {
        // Arrange
        var gate = new ChannelKeyIndexGate();
        gate.HoldUntilRestore();
        var restore = await gate.BeginRestoreAsync(TestContext.Current.CancellationToken);

        // Act
        var released = gate.ReleaseRestoreHold();

        // Assert: the running restore still holds the gate; a second release finds nothing to lift
        Assert.True(released);
        Assert.False(gate.ReleaseRestoreHold());
        Assert.Equal(ChannelKeyIndexHold.RestoreInProgressReason, gate.HoldReason);
        restore.Dispose();
        Assert.Null(gate.HoldReason);
    }
}