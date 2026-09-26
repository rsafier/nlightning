using MessagePack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Handlers;

using Application.Channels.Safety.Interfaces;
using Application.Onchain;
using Application.Onchain.Interfaces;
using Daemon.Extensions;
using Daemon.Handlers;
using Daemon.Interfaces;
using Daemon.Ipc.Interfaces;
using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Client.Exceptions;
using Domain.Client.Requests;
using Domain.Client.Responses;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Interfaces;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// The BOLT 5 IPC commands: ForceCloseChannel (ClientCommand 14, plan O2-T5) and PendingSweeps (15, O3-T6): the
/// handlers, the wire DTOs and the node composition.
/// </summary>
public class OnchainClientHandlerTests
{
    private static readonly ChannelId s_channelId = new(Enumerable.Repeat((byte)0x21, 32).ToArray());
    private static readonly TxId s_txId = new(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());

    [Fact]
    public async Task Given_ForceClose_When_Handled_Then_TheFailureServiceBroadcastsAndTheOutcomeIsMapped()
    {
        // Arrange
        var failureService = new Mock<IChannelFailureService>();
        ChannelFailureRequest? seen = null;
        failureService.Setup(f => f.FailChannelAsync(s_channelId, It.IsAny<ChannelFailureRequest>(),
                                                     It.IsAny<CancellationToken>()))
                      .Callback((ChannelId _, ChannelFailureRequest r, CancellationToken _) => seen = r)
                      .ReturnsAsync(new ChannelFailureOutcome(ChannelFailureStatus.Broadcast, s_txId));
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.TryGetChannelState(s_channelId, out It.Ref<ChannelState>.IsAny))
              .Returns(new TryGetStateCallback((ChannelId _, out ChannelState state) =>
               {
                   state = ChannelState.Failed;
                   return true;
               }));
        var handler = new ForceCloseChannelClientHandler(failureService.Object, memory.Object);

        // Act
        var response = await handler.HandleAsync(new ForceCloseChannelClientRequest(s_channelId),
                                                 TestContext.Current.CancellationToken);

        // Assert
        Assert.NotNull(seen);
        Assert.True(seen.Broadcast);
        Assert.Equal("B5-FAIL-05", seen.RequirementId);
        Assert.Equal(ForceCloseChannelClientHandler.PeerMessage, seen.PeerMessage);
        Assert.Equal(ChannelState.Failed, response.State);
        Assert.Equal("Broadcast", response.Status);
        Assert.Equal(s_txId, response.CommitmentTxId);
    }

    [Fact]
    public async Task Given_UnknownOrNotBroadcastableChannel_When_ForceClosed_Then_ClientErrors()
    {
        // Arrange
        var failureService = new Mock<IChannelFailureService>();
        failureService.Setup(f => f.FailChannelAsync(s_channelId, It.IsAny<ChannelFailureRequest>(),
                                                     It.IsAny<CancellationToken>()))
                      .ThrowsAsync(new KeyNotFoundException("unknown"));
        var other = new ChannelId(Enumerable.Repeat((byte)0x22, 32).ToArray());
        failureService.Setup(f => f.FailChannelAsync(other, It.IsAny<ChannelFailureRequest>(),
                                                     It.IsAny<CancellationToken>()))
                      .ReturnsAsync(new ChannelFailureOutcome(ChannelFailureStatus.NotApplicable, null));
        var handler = new ForceCloseChannelClientHandler(failureService.Object,
                                                         new Mock<IChannelMemoryRepository>().Object);

        // Act
        var unknown = await Assert.ThrowsAsync<ClientException>(
                          () => handler.HandleAsync(new ForceCloseChannelClientRequest(s_channelId),
                                                    TestContext.Current.CancellationToken));
        var closed = await Assert.ThrowsAsync<ClientException>(
                         () => handler.HandleAsync(new ForceCloseChannelClientRequest(other),
                                                   TestContext.Current.CancellationToken));

        // Assert
        Assert.Equal(ErrorCodes.InvalidChannel, unknown.ErrorCode);
        Assert.Equal(ErrorCodes.InvalidOperation, closed.ErrorCode);
    }

    [Fact]
    public async Task Given_RecordedCloses_When_PendingSweepsListed_Then_ClosedOnesOnlyWhenAskedAndOutputsMapped()
    {
        // Arrange: one channel resolving with one output (amount in its descriptor data), one already closed
        var closedChannel = new ChannelId(Enumerable.Repeat((byte)0x23, 32).ToArray());
        var closes = new List<ChannelCloseModel>
        {
            new(s_channelId, ChannelCloseKind.LocalCommitment, s_txId, 7, 900, new Hash(new byte[32]),
                DateTimeOffset.UtcNow),
            new(closedChannel, ChannelCloseKind.RemoteCommitment, s_txId, 3, 800, new Hash(new byte[32]),
                DateTimeOffset.UtcNow)
        };
        var output = new OutputResolutionModel
        {
            TransactionId = s_txId,
            OutputIndex = 1,
            ChannelId = s_channelId,
            Descriptor = OutputDescriptorKind.DelayedToLocal,
            DescriptorData = new OutputDescriptorData(12_345, [0x00, 0x20], [0x51], 144, false, null, null).Encode(),
            State = OutputResolutionState.Waiting,
            WaitUntilHeight = 1_043
        };
        var resolution = new Mock<IOnchainResolutionDbRepository>();
        resolution.Setup(r => r.GetClosesAsync()).ReturnsAsync(closes);
        resolution.Setup(r => r.GetOutputsByChannelIdAsync(s_channelId)).ReturnsAsync([output]);
        resolution.Setup(r => r.GetOutputsByChannelIdAsync(closedChannel)).ReturnsAsync([]);
        var channelDb = new Mock<IChannelDbRepository>();
        channelDb.Setup(c => c.GetByIdAsync(closedChannel)).ReturnsAsync((ChannelModel?)null);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.OnchainResolutionDbRepository).Returns(resolution.Object);
        unitOfWork.SetupGet(u => u.ChannelDbRepository).Returns(channelDb.Object);
        var memory = new Mock<IChannelMemoryRepository>();
        memory.Setup(m => m.TryGetChannelState(s_channelId, out It.Ref<ChannelState>.IsAny))
              .Returns(new TryGetStateCallback((ChannelId _, out ChannelState state) =>
               {
                   state = ChannelState.OnchainResolving;
                   return true;
               }));
        var handler = new PendingSweepsClientHandler(memory.Object, unitOfWork.Object);

        // Act
        var pending = await handler.HandleAsync(new PendingSweepsClientRequest(),
                                                TestContext.Current.CancellationToken);
        var all = await handler.HandleAsync(new PendingSweepsClientRequest { IncludeClosed = true },
                                            TestContext.Current.CancellationToken);
        var one = await handler.HandleAsync(new PendingSweepsClientRequest
        {
            ChannelId = closedChannel,
            IncludeClosed = true
        },
                                            TestContext.Current.CancellationToken);

        // Assert
        var channel = Assert.Single(pending.Channels);
        Assert.Equal(ChannelState.OnchainResolving, channel.State);
        Assert.Equal(ChannelCloseKind.LocalCommitment, channel.CloseKind);
        Assert.Equal(7UL, channel.CommitmentNumber);
        var info = Assert.Single(channel.Outputs);
        Assert.Equal(12_345UL, info.AmountSat);
        Assert.Equal(OutputResolutionState.Waiting, info.State);
        Assert.Equal(1_043U, info.WaitUntilHeight);
        Assert.Equal(2, all.Channels.Count);
        Assert.Equal(ChannelState.Closed, Assert.Single(one.Channels).State);
    }

    [Fact]
    public void Given_IpcDtos_When_RoundTripped_Then_EveryFieldIsPreserved()
    {
        // Arrange
        var options = NLightningMessagePackOptions.Options;
        var ct = TestContext.Current.CancellationToken;
        var forceRequest = new ForceCloseChannelIpcRequest { ChannelId = s_channelId };
        var forceResponse = ForceCloseChannelIpcResponse.FromClientResponse(
            new ForceCloseChannelClientResponse(s_channelId, ChannelState.Failed, "Broadcast", s_txId));
        var sweepsRequest = new PendingSweepsIpcRequest { ChannelId = s_channelId, IncludeClosed = true };
        var sweepsResponse = PendingSweepsIpcResponse.FromClientResponse(new PendingSweepsClientResponse(
        [
            new PendingSweepChannelInfo(s_channelId, ChannelState.OnchainResolving,
                                        ChannelCloseKind.RemoteCommitment, s_txId, 4, 700,
            [
                new PendingSweepOutputInfo(s_txId, 2, OutputDescriptorKind.RemoteOfferedHtlc,
                                           OutputResolutionState.Broadcast, 5_000, HtlcDirection.Incoming, 9,
                                           s_txId, 710, 740, null)
            ])
        ]));

        // Act
        var forceRequest2 = MessagePackSerializer.Deserialize<ForceCloseChannelIpcRequest>(
            MessagePackSerializer.Serialize(forceRequest, options, ct), options, ct);
        var forceResponse2 = MessagePackSerializer.Deserialize<ForceCloseChannelIpcResponse>(
            MessagePackSerializer.Serialize(forceResponse, options, ct), options, ct);
        var sweepsRequest2 = MessagePackSerializer.Deserialize<PendingSweepsIpcRequest>(
            MessagePackSerializer.Serialize(sweepsRequest, options, ct), options, ct);
        var sweepsResponse2 = MessagePackSerializer.Deserialize<PendingSweepsIpcResponse>(
            MessagePackSerializer.Serialize(sweepsResponse, options, ct), options, ct);

        // Assert
        var displayTxId = Convert.ToHexString(Enumerable.Range(1, 32).Reverse().Select(i => (byte)i).ToArray())
                                 .ToLowerInvariant();
        Assert.Equal(s_channelId, forceRequest2.ToClientRequest().ChannelId);
        Assert.Equal(ChannelState.Failed, forceResponse2.State);
        Assert.Equal("Broadcast", forceResponse2.Status);
        Assert.Equal(displayTxId, forceResponse2.CommitmentTxId);
        var clientSweeps = sweepsRequest2.ToClientRequest();
        Assert.Equal(s_channelId, clientSweeps.ChannelId);
        Assert.True(clientSweeps.IncludeClosed);
        var channel = Assert.Single(sweepsResponse2.Channels);
        Assert.Equal(ChannelCloseKind.RemoteCommitment, channel.CloseKind);
        Assert.Equal(displayTxId, channel.CommitmentTxId);
        Assert.Equal(4UL, channel.CommitmentNumber);
        Assert.Equal(700U, channel.SpentAtHeight);
        var output = Assert.Single(channel.Outputs);
        Assert.Equal(2U, output.OutputIndex);
        Assert.Equal(OutputDescriptorKind.RemoteOfferedHtlc, output.Descriptor);
        Assert.Equal(OutputResolutionState.Broadcast, output.State);
        Assert.Equal(5_000UL, output.AmountSat);
        Assert.Equal(HtlcDirection.Incoming, output.HtlcDirection);
        Assert.Equal(9UL, output.HtlcId);
        Assert.Equal(displayTxId, output.ResolvingTxId);
        Assert.Equal(710U, output.WaitUntilHeight);
        Assert.Equal(740U, output.DeadlineHeight);
        Assert.Null(output.ResolvedHeight);
    }

    [Fact]
    public void Given_NodeServices_When_Composed_Then_OnchainCommandsAndServicesResolve()
    {
        // Arrange
        var configuration = new ConfigurationBuilder()
                           .AddInMemoryCollection(new Dictionary<string, string?>
                           {
                               ["Node:Network"] = "regtest",
                               ["Database:Provider"] = "Sqlite",
                               ["Database:ConnectionString"] = "Data Source=:memory:",
                               ["Node:Onchain:IrrevocableDepth"] = "20"
                           })
                           .Build();
        var services = new ServiceCollection();
        services.AddNltgNodeServices(configuration, new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        // Act
        var commands = provider.GetServices<IIpcCommandHandler>().Select(h => h.Command).ToList();

        // Assert
        Assert.Contains(ClientCommand.ForceCloseChannel, commands);
        Assert.Contains(ClientCommand.PendingSweeps, commands);
        Assert.Equal(commands.Count, commands.Distinct().Count());
        Assert.IsType<ForceCloseChannelClientHandler>(
            scope.ServiceProvider
                 .GetRequiredService<IClientCommandHandler<ForceCloseChannelClientRequest,
                      ForceCloseChannelClientResponse>>());
        Assert.IsType<PendingSweepsClientHandler>(
            scope.ServiceProvider
                 .GetRequiredService<IClientCommandHandler<PendingSweepsClientRequest, PendingSweepsClientResponse>>());
        Assert.IsType<OnchainChannelWatcher>(provider.GetRequiredService<IOnchainChannelWatcher>());
        Assert.IsType<OnchainResolutionExecutor>(provider.GetRequiredService<IOnchainResolutionExecutor>());
        Assert.Equal(20U, provider.GetRequiredService<IOptions<OnchainOptions>>().Value.IrrevocableDepth);
        Assert.Equal(15, (int)ClientCommand.PendingSweeps);
        Assert.Equal(14, (int)ClientCommand.ForceCloseChannel);
    }

    private delegate bool TryGetStateCallback(ChannelId channelId, out ChannelState state);
}