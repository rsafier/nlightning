using MessagePack;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Ipc.Handlers;

using Application.Channels.RoutingPolicies;
using Application.Channels.Services;
using Application.Gossip.Interfaces;
using Daemon.Extensions;
using Daemon.Ipc.Interfaces;
using Domain.Bitcoin.Transactions.Outputs;
using Domain.Channels.Enums;
using Domain.Channels.Interfaces;
using Domain.Channels.Models;
using Domain.Channels.RoutingPolicies;
using Domain.Channels.ValueObjects;
using Domain.Client.Constants;
using Domain.Client.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Enums;
using Domain.Money;
using Domain.Node.Options;
using Domain.Payments.Interfaces;
using Domain.Payments.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Enums;
using Infrastructure.Bitcoin.Wallet.Interfaces;
using Infrastructure.Persistence.Contexts;
using Transport.Ipc;
using Transport.Ipc.MessagePack;
using Transport.Ipc.Requests;
using Transport.Ipc.Responses;

/// <summary>
/// Wave sp1 lane SP1-G: <c>setchannelpolicy</c> (35) and <c>getchannelpolicy</c> (36) over IPC, through the
/// production policy service, and the composition with the node graph.
/// </summary>
public class ChannelPolicyIpcHandlerTests : IDisposable
{
    private static readonly MessagePackSerializerOptions s_options = NLightningMessagePackOptions.Options;
    private static readonly CompactPubKey s_peer = CreatePubKey(2);
    private static readonly ShortChannelId s_scid = new(120, 3, 1);
    private static readonly ShortChannelId s_alias = new(16_000_000, 1, 0);

    private readonly List<ChannelModel> _channels = [];
    private readonly Mock<IChannelMemoryRepository> _memory = new();
    private readonly Mock<IChannelUpdateService> _updates = new();
    private readonly ServiceProvider _provider;

    public ChannelPolicyIpcHandlerTests()
    {
        MessagePackSerializer.DefaultOptions = s_options;
        _memory.Setup(x => x.FindChannels(It.IsAny<Func<ChannelModel, bool>>()))
               .Returns((Func<ChannelModel, bool> predicate) => _channels.Where(predicate).ToList());
        _memory.Setup(x => x.TryGetChannel(It.IsAny<ChannelId>(), out It.Ref<ChannelModel?>.IsAny))
               .Returns(new TryGetChannelCallback((ChannelId channelId, out ChannelModel? channel) =>
                {
                    channel = _channels.FirstOrDefault(c => c.ChannelId == channelId);
                    return channel is not null;
                }));
        _channels.Add(CreateChannel(1, s_scid));

        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(_memory.Object);
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(_updates.Object);
        services.AddScoped(_ =>
        {
            // A unit of work without lane SP1-C's repository: the policies are kept in memory
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(u => u.ChannelPolicyDbRepository).Throws(new NotSupportedException("no table"));
            return unitOfWork.Object;
        });
        services.AddChannelPolicyIpcServices();
        _provider = services.BuildServiceProvider();
    }

    private delegate bool TryGetChannelCallback(ChannelId channelId, out ChannelModel? channel);

    private ChannelModel Channel => _channels[0];

    [Fact]
    public async Task Given_ValuesByChannelId_When_Set_Then_TheyApplyAndAnUpdateIsSent()
    {
        // Arrange
        var request = new SetChannelPolicyIpcRequest
        {
            ChannelId = Channel.ChannelId,
            FeeBaseMsat = 2_500,
            CltvExpiryDelta = 72,
            HtlcMaximumMsat = 100_000_000
        };

        // Act
        var response = await SendAsync<ChannelPolicyIpcResponse>(ClientCommand.SetChannelPolicy, request);

        // Assert
        Assert.Equal(Channel.ChannelId, response.ChannelId);
        Assert.Equal(Number(s_scid), response.ShortChannelId);
        Assert.Equal(2_500u, response.FeeBaseMsat);
        Assert.Equal(1u, response.FeeProportionalMillionths);
        Assert.Equal((ushort)72, response.CltvExpiryDelta);
        Assert.Equal(100_000_000ul, response.HtlcMaximumMsat);
        Assert.True(response.IsFeeBaseMsatOverridden);
        Assert.False(response.IsFeeProportionalMillionthsOverridden);
        Assert.True(response.IsCltvExpiryDeltaOverridden);
        Assert.True(response.IsHtlcMaximumMsatOverridden);
        Assert.NotNull(response.OverrideUpdatedAt);
        Assert.True(response.IsMemoryOnly); // this fixture's unit of work has no ChannelPolicies table
        _updates.Verify(u => u.SendChannelUpdateAsync(Channel.ChannelId, It.IsAny<CancellationToken>()), Times.Once);

        // ... and getchannelpolicy reads it back, by short channel id
        var read = await SendAsync<ChannelPolicyIpcResponse>(ClientCommand.GetChannelPolicy,
                                                             new GetChannelPolicyIpcRequest
                                                             {
                                                                 ShortChannelId = Number(s_scid)
                                                             });
        Assert.Equal(2_500u, read.FeeBaseMsat);
        Assert.Equal(100_000_000ul, read.HtlcMaximumMsat);
    }

    [Fact]
    public async Task Given_ASetPolicy_When_ListingChannels_Then_TheEffectivePolicyIsShown()
    {
        // Arrange
        await SendAsync<ChannelPolicyIpcResponse>(ClientCommand.SetChannelPolicy,
                                                  new SetChannelPolicyIpcRequest
                                                  {
                                                      ChannelId = Channel.ChannelId,
                                                      FeeProportionalMillionths = 321,
                                                      CltvExpiryDelta = 99,
                                                      HtlcMinimumMsat = 2_000,
                                                      HtlcMaximumMsat = 70_000_000
                                                  });
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.Setup(u => u.ChannelDbRepository.GetAllAsync()).ReturnsAsync(Array.Empty<ChannelModel>());
        var handler = new Daemon.Handlers.ListChannelsClientHandler(
            _memory.Object, new Mock<Domain.Node.Interfaces.IPeerManager>().Object,
            new Mock<Domain.Channels.Reestablish.IReestablishTracker>().Object, unitOfWork.Object,
            Options.Create(new NodeOptions()), _provider.GetRequiredService<IChannelPolicyProvider>());

        // Act
        var response = await handler.HandleAsync(new Domain.Client.Requests.ListChannelsClientRequest(),
                                                  TestContext.Current.CancellationToken);

        // Assert
        var channel = Assert.Single(response.Channels);
        Assert.Equal(new RoutingOptions().FeeBaseMsat, channel.FeeBaseMsat);
        Assert.Equal(321u, channel.FeePpm);
        Assert.Equal((ushort)99, channel.CltvExpiryDelta);
        Assert.Equal(2_000ul, channel.HtlcMinimumMsat);
        Assert.Equal(70_000_000ul, channel.HtlcMaximumMsat);
        Assert.True(channel.HasPolicyOverride);
    }

    [Fact]
    public async Task Given_AUnitOfWorkWithThePolicyTable_When_Set_Then_TheResponseSaysItIsPersisted()
    {
        // Arrange
        var repository = new Mock<IChannelPolicyDbRepository>();
        repository.Setup(r => r.GetAllAsync()).ReturnsAsync(Array.Empty<ChannelPolicyOverride>());
        var services = new ServiceCollection();
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddSingleton(_memory.Object);
        services.AddSingleton<IChannelLockProvider, ChannelLockProvider>();
        services.AddSingleton(Options.Create(new NodeOptions()));
        services.AddSingleton(_updates.Object);
        services.AddScoped(_ =>
        {
            var unitOfWork = new Mock<IUnitOfWork>();
            unitOfWork.Setup(u => u.ChannelPolicyDbRepository).Returns(repository.Object);
            return unitOfWork.Object;
        });
        services.AddChannelPolicyIpcServices();
        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var handler = scope.ServiceProvider.GetRequiredService<
            Daemon.Interfaces.IClientCommandHandler<Domain.Client.Requests.SetChannelPolicyClientRequest,
                Domain.Client.Responses.ChannelPolicyClientResponse>>();

        // Act
        var response = await handler.HandleAsync(
                           new Domain.Client.Requests.SetChannelPolicyClientRequest(
                               new Domain.Client.Requests.ChannelReference(Channel.ChannelId))
                           {
                               FeeBaseMsat = 2_500
                           }, TestContext.Current.CancellationToken);

        // Assert
        Assert.True(response.IsPersisted);
        repository.Verify(r => r.UpsertAsync(It.Is<ChannelPolicyOverride>(o => o.FeeBaseMsat == 2_500)), Times.Once);
    }

    [Fact]
    public async Task Given_AnAlias_When_Set_Then_ItNamesTheChannel()
    {
        // Arrange
        Channel.LocalAliases = [s_alias];

        // Act
        var response = await SendAsync<ChannelPolicyIpcResponse>(ClientCommand.SetChannelPolicy,
                                                                 new SetChannelPolicyIpcRequest
                                                                 {
                                                                     ShortChannelId = Number(s_alias),
                                                                     FeeProportionalMillionths = 42
                                                                 });

        // Assert
        Assert.Equal(Channel.ChannelId, response.ChannelId);
        Assert.Equal(42u, response.FeeProportionalMillionths);
    }

    [Fact]
    public async Task Given_AnOverride_When_Reset_Then_TheNodesValuesAreBack()
    {
        // Arrange
        await SendAsync<ChannelPolicyIpcResponse>(ClientCommand.SetChannelPolicy,
                                                  new SetChannelPolicyIpcRequest
                                                  {
                                                      ChannelId = Channel.ChannelId,
                                                      FeeBaseMsat = 9
                                                  });

        // Act
        var response = await SendAsync<ChannelPolicyIpcResponse>(ClientCommand.SetChannelPolicy,
                                                                 new SetChannelPolicyIpcRequest
                                                                 {
                                                                     ChannelId = Channel.ChannelId,
                                                                     Reset = true
                                                                 });

        // Assert
        Assert.True(response.WasReset);
        Assert.Equal(new RoutingOptions().FeeBaseMsat, response.FeeBaseMsat);
        Assert.False(response.IsFeeBaseMsatOverridden);
        Assert.Null(response.OverrideUpdatedAt);
        _updates.Verify(u => u.SendChannelUpdateAsync(Channel.ChannelId, It.IsAny<CancellationToken>()),
                        Times.Exactly(2));
    }

    [Theory]
    // BOLT 7: htlc_maximum_msat MUST NOT exceed the capacity (1,000,000 sat)
    [InlineData(null, 1_000_000_001UL, null, "capacity")]
    [InlineData((ushort)20, null, null, "cltv_expiry_delta")]
    [InlineData(null, 10_000UL, 20_000UL, "no HTLC would fit")]
    public async Task Given_AnInvalidValue_When_Set_Then_InvalidOperationAndNothingSent(ushort? delta, ulong? maximum,
        ulong? minimum, string expected)
    {
        // Act
        var error = await SendForErrorAsync(ClientCommand.SetChannelPolicy,
                                            new SetChannelPolicyIpcRequest
                                            {
                                                ChannelId = Channel.ChannelId,
                                                CltvExpiryDelta = delta,
                                                HtlcMaximumMsat = maximum,
                                                HtlcMinimumMsat = minimum
                                            });

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, error.Code);
        Assert.Contains(expected, error.Message);
        _updates.Verify(u => u.SendChannelUpdateAsync(It.IsAny<ChannelId>(), It.IsAny<CancellationToken>()),
                        Times.Never);
    }

    [Fact]
    public async Task Given_AnUnknownChannel_When_SetOrGet_Then_InvalidChannel()
    {
        // Act
        var bySet = await SendForErrorAsync(ClientCommand.SetChannelPolicy,
                                            new SetChannelPolicyIpcRequest
                                            {
                                                ShortChannelId = Number(new ShortChannelId(9, 9, 9)),
                                                FeeBaseMsat = 1
                                            });
        var byGet = await SendForErrorAsync(ClientCommand.GetChannelPolicy,
                                            new GetChannelPolicyIpcRequest
                                            {
                                                ChannelId = new ChannelId(Enumerable.Repeat((byte)9, 32).ToArray())
                                            });

        // Assert
        Assert.Equal(ErrorCodes.InvalidChannel, bySet.Code);
        Assert.Equal(ErrorCodes.InvalidChannel, byGet.Code);
    }

    [Fact]
    public async Task Given_ResetWithValuesOrNothingToSet_When_Set_Then_InvalidOperation()
    {
        // Act
        var both = await SendForErrorAsync(ClientCommand.SetChannelPolicy,
                                           new SetChannelPolicyIpcRequest
                                           {
                                               ChannelId = Channel.ChannelId,
                                               Reset = true,
                                               FeeBaseMsat = 1
                                           });
        var none = await SendForErrorAsync(ClientCommand.SetChannelPolicy,
                                           new SetChannelPolicyIpcRequest { ChannelId = Channel.ChannelId });

        // Assert
        Assert.Equal(ErrorCodes.InvalidOperation, both.Code);
        Assert.Equal(ErrorCodes.InvalidOperation, none.Code);
    }

    [Fact]
    public async Task Given_NoChannelOrBoth_When_Get_Then_AnErrorEnvelope()
    {
        // Act
        var neither = await SendForErrorAsync(ClientCommand.GetChannelPolicy, new GetChannelPolicyIpcRequest());
        var both = await SendForErrorAsync(ClientCommand.GetChannelPolicy,
                                           new GetChannelPolicyIpcRequest
                                           {
                                               ChannelId = Channel.ChannelId,
                                               ShortChannelId = Number(s_scid)
                                           });

        // Assert
        Assert.Contains("exactly one", neither.Message);
        Assert.Contains("exactly one", both.Message);
    }

    [Fact]
    public async Task Given_TheNodeComposition_When_ChannelPolicyServicesAreAdded_Then_EverythingResolvesAndForwardingUsesIt()
    {
        // Arrange: a migrated SQLite file, so the policy is stored in the ChannelPolicies table (migration
        // AddSpliceFundings)
        var databasePath = Path.Combine(Path.GetTempPath(), $"nltg-policy-{Guid.NewGuid():N}.db");
        var services = new ServiceCollection();
        services.AddNltgNodeServices(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Node:Network"] = "regtest",
            ["Database:Provider"] = "Sqlite",
            ["Database:ConnectionString"] = $"Data Source={databasePath};Pooling=False"
        }).Build(), new Mock<ISecureKeyManager>().Object);
        services.AddSingleton(new Mock<IBitcoinChainService>().Object);
        services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
        services.AddChannelPolicyIpcServices();
        services.AddChannelPolicyIpcServices();

        // Act
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using (var scope = provider.CreateScope())
            await scope.ServiceProvider.GetRequiredService<NLightningDbContext>().Database
                       .MigrateAsync(TestContext.Current.CancellationToken);
        var store = provider.GetRequiredService<ChannelPolicyStore>();

        // Assert: one handler per command, no dependency cycle, one store behind the provider
        var commands = provider.GetServices<IIpcCommandHandler>().Select(h => h.Command).ToList();
        Assert.Single(commands, c => c == ClientCommand.SetChannelPolicy);
        Assert.Single(commands, c => c == ClientCommand.GetChannelPolicy);
        Assert.NotNull(provider.GetRequiredService<IChannelPolicyService>());
        Assert.NotNull(provider.GetRequiredService<IChannelUpdateService>());
        Assert.Same(store, provider.GetRequiredService<IChannelPolicyProvider>());

        // The forwarding policy reads the channel's values, stored in the table
        var channelId = new ChannelId(Enumerable.Repeat((byte)5, 32).ToArray());
        await store.SaveAsync(new ChannelPolicyOverride(channelId, HtlcMaximumMsat: 10_000),
                              TestContext.Current.CancellationToken);
        var decision = provider.GetRequiredService<IForwardingPolicy>().Evaluate(
            new ForwardingRequest(LightningMoney.MilliSatoshis(30_000), 1_200, LightningMoney.MilliSatoshis(20_000), 1_100,
                                  1_000, new OutgoingChannelInfo(channelId, true, LightningMoney.MilliSatoshis(1),
                                                                 LightningMoney.Satoshis(1_000_000))));
        Assert.Equal(FailureCode.TemporaryChannelFailure, decision.FailureCode);
        Assert.True(store.IsPersistent);
        File.Delete(databasePath);
    }

    public void Dispose()
    {
        _provider.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<T> SendAsync<T>(ClientCommand command, object request)
    {
        var response = await HandleAsync(command, request);
        Assert.True(response.Kind == IpcEnvelopeKind.Response,
                    response.Kind == IpcEnvelopeKind.Error
                        ? MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options).Message
                        : "not a response");
        return MessagePackSerializer.Deserialize<T>(response.Payload, s_options, TestContext.Current.CancellationToken);
    }

    private async Task<IpcError> SendForErrorAsync(ClientCommand command, object request)
    {
        var response = await HandleAsync(command, request);
        Assert.Equal(IpcEnvelopeKind.Error, response.Kind);
        return MessagePackSerializer.Deserialize<IpcError>(response.Payload, s_options,
                                                           TestContext.Current.CancellationToken);
    }

    private Task<IpcEnvelope> HandleAsync(ClientCommand command, object request)
    {
        var handler = Assert.Single(_provider.GetServices<IIpcCommandHandler>(), h => h.Command == command);
        var envelope = new IpcEnvelope
        {
            Version = 1,
            Command = command,
            CorrelationId = Guid.NewGuid(),
            Kind = IpcEnvelopeKind.Request,
            Payload = MessagePackSerializer.Serialize(request.GetType(), request, s_options,
                                                      TestContext.Current.CancellationToken)
        };
        return handler.HandleAsync(envelope, TestContext.Current.CancellationToken);
    }

    private static ChannelModel CreateChannel(byte id, ShortChannelId scid)
    {
        var party = new ChannelParty(LightningMoney.Satoshis(354), LightningMoney.Zero, LightningMoney.MilliSatoshis(1),
                                     30, LightningMoney.Zero, 144, null);
        var channelParams = new ChannelParams(party, party, LightningMoney.Satoshis(2_500), 3, false,
                                              FeatureSupport.No);
        var keySet = new ChannelKeySetModel(0, s_peer, s_peer, s_peer, s_peer, s_peer, s_peer);
        return new ChannelModel(channelParams, new ChannelId(Enumerable.Repeat(id, 32).ToArray()), null,
                                new FundingOutputInfo(LightningMoney.Satoshis(1_000_000), s_peer, CreatePubKey(3)),
                                true, null, null, LightningMoney.Zero, keySet, 0, 0, LightningMoney.Zero, keySet, 0,
                                s_peer, 0, ChannelState.Open, ChannelVersion.V1)
        {
            ShortChannelId = scid
        };
    }

    private static ulong Number(ShortChannelId scid) =>
        ((ulong)scid.BlockHeight << 40) | ((ulong)scid.TransactionIndex << 16) | scid.OutputIndex;

    private static CompactPubKey CreatePubKey(byte fill)
    {
        var bytes = Enumerable.Repeat(fill, 33).ToArray();
        bytes[0] = 0x02;
        return new CompactPubKey(bytes);
    }
}