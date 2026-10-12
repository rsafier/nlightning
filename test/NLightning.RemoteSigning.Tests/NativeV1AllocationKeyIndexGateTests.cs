using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NLightning.Application.Channels.Handlers;
using NLightning.Application.Channels.Services;
using NLightning.Application.Tests.Channels.Taproot;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Channels.Acceptance;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Channels.Interfaces;
using NLightning.Domain.Channels.KeyIndexes;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Serialization.Interfaces;
using NLightning.Infrastructure.RemoteSigning;

namespace NLightning.RemoteSigning.Tests;

/// <summary>NL-1392: the native remote signer's allocations and their replays go through the channel key index gate.
/// </summary>
public sealed partial class NativeV1AllocationWorkflowTests
{
    [Fact]
    public async Task Given_AnAllocationSavedBeforeARestart_When_ARestoreRuns_Then_ItsIndexIsInUseAndTheReplayIsGated()
    {
        // Arrange: an outbound open allocated key index 1 and stored no channel; the node restarts
        var ct = TestContext.Current.CancellationToken;
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        using var connection = new RemoteSignerConnection(daemon.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var request = Request(harness);
        uint original;
        using (var coordinator = Coordinator(connection, harness))
            original = (await Opening(connection, harness, coordinator)
                             .CreateOutboundAsync(request, harness.NegotiatedFeatures, harness.Bob.NodeId))
                      .LocalKeySet.KeyIndex;
        await harness.RestartAsync(harness.Alice);
        var gate = new ChannelKeyIndexGate();
        using var restarted = new RemoteSigningWorkflowCoordinator(
            connection, harness.Alice.Services.GetRequiredService<IServiceScopeFactory>(), keyIndexGate: gate);
        var resumed = GatedOpening(connection, harness, restarted, gate);

        // Act / Assert: the restore sees the saved index although this process handed nothing out
        Assert.Equal([original], await restarted.GetPendingKeyIndexesAsync(ct));
        Assert.False(gate.IsAllocatedByThisProcess(original));

        // A replay while a restore holds the gate is refused and keeps its intent and receipt
        var workflowId = Assert.Single(await resumed.GetUnconsumedAsync()).WorkflowId;
        using (await gate.BeginRestoreAsync(ct))
            await Assert.ThrowsAsync<ChannelKeyIndexHeldException>(() =>
                resumed.CreateOutboundAsync(request, harness.NegotiatedFeatures, harness.Bob.NodeId));
        Assert.Equal(workflowId, Assert.Single(await resumed.GetUnconsumedAsync()).WorkflowId);

        // After it, the replay returns the same index and the gate remembers it
        var channel = await resumed.CreateOutboundAsync(request, harness.NegotiatedFeatures, harness.Bob.NodeId);
        Assert.Equal(original, channel.LocalKeySet.KeyIndex);
        Assert.True(gate.IsAllocatedByThisProcess(original));
    }

    [Fact]
    public async Task Given_ARestoreThatStartsAfterTheCheck_When_ANewIntentAllocates_Then_TheIntentIsAbandoned()
    {
        // Arrange: the opening's own check passes (no gate there); the signer's gate refuses the allocation, as when a
        // restore begins between the check and the allocation
        var ct = TestContext.Current.CancellationToken;
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        using var connection = new RemoteSignerConnection(daemon.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var gate = new ChannelKeyIndexGate();
        using var coordinator = new RemoteSigningWorkflowCoordinator(
            connection, harness.Alice.Services.GetRequiredService<IServiceScopeFactory>(), keyIndexGate: gate);
        var opening = new NativeV1ChannelOpening(
            harness.Alice.Services.GetRequiredService<IChannelFactory>(),
            new RemoteLightningSigner(connection, keyIndexGate: gate),
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>(),
            harness.Alice.Services.GetRequiredService<IChannelIdFactory>(),
            harness.Alice.Services.GetRequiredService<IMessageSerializer>(),
            harness.Alice.Services.GetRequiredService<IFeeService>(), Options.Create(harness.Alice.Options),
            coordinator);
        var request = Request(harness);

        // Act
        using (await gate.BeginRestoreAsync(ct))
            await Assert.ThrowsAsync<ChannelKeyIndexHeldException>(() =>
                opening.CreateOutboundAsync(request, harness.NegotiatedFeatures, harness.Bob.NodeId));

        // Assert: no orphaned intent; an open with other inputs to the same peer works afterwards
        Assert.Empty(await opening.GetUnconsumedAsync());
        Assert.Empty(await coordinator.GetPendingKeyIndexesAsync(ct));
        request.FundingAmount = LightningMoney.Satoshis(900_000);
        var channel = await opening.CreateOutboundAsync(request, harness.NegotiatedFeatures, harness.Bob.NodeId);
        Assert.Equal(1u, channel.LocalKeySet.KeyIndex);
    }

    [Fact]
    public async Task Given_AnInboundOpenWaitingOnTheChannelAcceptor_When_ARestoreStarts_Then_RefusedWithoutAnIntent()
    {
        // Arrange: the handler's early check passes; the acceptor (LND's ChannelAcceptor) answers only once a restore
        // holds the gate
        var ct = TestContext.Current.CancellationToken;
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        using var connection = new RemoteSignerConnection(daemon.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var message = await InboundMessage(harness);
        var gate = new ChannelKeyIndexGate();
        using var coordinator = new RemoteSigningWorkflowCoordinator(
            connection, harness.Alice.Services.GetRequiredService<IServiceScopeFactory>(), keyIndexGate: gate);
        var opening = GatedOpening(connection, harness, coordinator, gate);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var answer = new TaskCompletionSource<ChannelOpenDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        var acceptor = new Mock<IChannelOpenDecisionGate>();
        acceptor.SetupGet(x => x.HasDeciders).Returns(true);
        acceptor.Setup(x => x.DecideAsync(It.IsAny<ChannelOpenRequest>(), It.IsAny<CancellationToken>()))
                .Returns(() =>
                 {
                     waiting.TrySetResult();
                     return answer.Task;
                 });
        var handler = new OpenChannel1MessageHandler(
            harness.Alice.Services.GetRequiredService<IChannelFactory>(), harness.Alice.Memory,
            NullLogger<OpenChannel1MessageHandler>.Instance, harness.Alice.Services.GetRequiredService<IMessageFactory>(),
            nodeOptions: Options.Create(harness.Alice.Options),
            lightningSigner: new RemoteLightningSigner(connection, keyIndexGate: gate), openDecisionGate: acceptor.Object,
            nativeOpening: opening, keyIndexGate: gate);

        // Act
        var handling = handler.HandleAsync(message, ChannelState.None, harness.NegotiatedFeatures, harness.Bob.NodeId);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        using var restore = await gate.BeginRestoreAsync(ct);
        answer.SetResult(new ChannelOpenDecision { Accept = true });
        var refusal = await Assert.ThrowsAsync<ChannelErrorException>(() => handling);

        // Assert: an error for the temporary channel that keeps the connection, and no intent left behind
        Assert.True(refusal.KeepConnection);
        Assert.Equal(message.Payload.ChannelId, refusal.ChannelId);
        Assert.Equal(ChannelKeyIndexHold.PeerMessage, refusal.PeerMessage);
        Assert.Empty(await opening.GetUnconsumedAsync());
    }

    private static NativeV1ChannelOpening GatedOpening(RemoteSignerConnection connection, TaprootOpenHarness harness,
                                                       RemoteSigningWorkflowCoordinator coordinator,
                                                       IChannelKeyIndexGate gate) => new(
        harness.Alice.Services.GetRequiredService<IChannelFactory>(),
        new RemoteLightningSigner(connection, keyIndexGate: gate),
        harness.Alice.Services.GetRequiredService<IServiceScopeFactory>(),
        harness.Alice.Services.GetRequiredService<IChannelIdFactory>(),
        harness.Alice.Services.GetRequiredService<IMessageSerializer>(),
        harness.Alice.Services.GetRequiredService<IFeeService>(), Options.Create(harness.Alice.Options), coordinator,
        gate);
}