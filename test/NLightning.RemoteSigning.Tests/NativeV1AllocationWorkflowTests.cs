using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NLightning.Application.Channels.Handlers;
using NLightning.Application.Channels.Services;
using NLightning.Application.Tests.Channels.Taproot;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Channels.Acceptance;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Channels.Interfaces;
using NLightning.Domain.Channels.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Client.Requests;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Protocol.Messages;
using NLightning.Domain.Protocol.Tlv;
using NLightning.Domain.Serialization.Interfaces;
using NLightning.Domain.Signing.Recovery;
using NLightning.Infrastructure.RemoteSigning;
using WireRequest = NLightning.Signing.Contracts.SigningRequest;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeV1AllocationWorkflowTests
{
    [Fact]
    public async Task Given_InvalidOutboundFunding_When_CorrectedForSamePeer_Then_NoIntentOrKeyWasAllocatedForInvalidRequest()
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        using var connection = new RemoteSignerConnection(daemon.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        using var coordinator = Coordinator(connection, harness);
        var opening = Opening(connection, harness, coordinator);
        var request = Request(harness);
        request.FundingAmount = LightningMoney.Satoshis(1);
        await Assert.ThrowsAsync<ChannelErrorException>(() =>
            opening.CreateOutboundAsync(request, harness.NegotiatedFeatures, harness.Bob.NodeId));
        Assert.Empty(await opening.GetUnconsumedAsync());
        request.FundingAmount = LightningMoney.Satoshis(1_000_000);
        var channel = await opening.CreateOutboundAsync(request, harness.NegotiatedFeatures, harness.Bob.NodeId);
        Assert.Equal(1u, channel.LocalKeySet.KeyIndex);
        Assert.Single(await opening.GetUnconsumedAsync());
    }

    [Fact]
    public async Task Given_AcceptedInboundIntent_When_NodeRestartsWithChangedGateFeeAndDefaults_Then_OriginalAcceptWireIsReplayed()
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        using var connection = new RemoteSignerConnection(daemon.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var message = await InboundMessage(harness);
        var fee = new MutableFee { Quote = LightningMoney.Satoshis(2_500) };
        var acceptance = new ChannelOpenDecision
        {
            Accept = true,
            MinimumDepth = 6,
            ToSelfDelay = 201,
            MaxAcceptedHtlcs = 11,
            ChannelReserve = LightningMoney.Satoshis(15_000),
            HtlcMinimum = LightningMoney.Satoshis(3),
            MaxHtlcValueInFlight = LightningMoney.MilliSatoshis(400_000_000)
        };
        byte[] originalWire;
        Guid workflowId;
        byte[] allocationEnvelope;
        using (var coordinator = Coordinator(connection, harness))
        {
            var gate = new Mock<IChannelOpenDecisionGate>();
            gate.SetupGet(x => x.HasDeciders).Returns(true);
            gate.Setup(x => x.DecideAsync(It.IsAny<ChannelOpenRequest>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(acceptance);
            var opening = Opening(connection, harness, coordinator, fee);
            var handler = InboundHandler(connection, harness, opening, gate.Object);
            var response = Assert.IsType<AcceptChannel1Message>(Assert.Single(await handler.HandleAsync(message,
                ChannelState.None, harness.NegotiatedFeatures, harness.Bob.NodeId)));
            originalWire = await EncodeAny(harness, response);
            gate.Verify(x => x.DecideAsync(It.IsAny<ChannelOpenRequest>(), It.IsAny<CancellationToken>()), Times.Once);
            workflowId = Assert.Single(await opening.GetUnconsumedAsync()).WorkflowId;
            allocationEnvelope = await harness.Alice.InScopeAsync(async uow =>
                Assert.Single(await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflowId)).Envelope);
        }
        harness.Alice.Options.MinimumChannelSize = LightningMoney.Satoshis(2_000_000);
        harness.Alice.Options.MinimumDepth = 91;
        harness.Alice.Options.DustLimitAmount = LightningMoney.Satoshis(5_000);
        harness.Alice.Options.ToSelfDelay = 999;
        harness.Alice.Options.MaxAcceptedChannelReservePercent = 1;
        await harness.RestartAsync(harness.Alice);
        fee.Quote = LightningMoney.Satoshis(1_000_000);
        using var restarted = Coordinator(connection, harness);
        var resumed = Opening(connection, harness, restarted, fee);
        var changedGate = new Mock<IChannelOpenDecisionGate>();
        changedGate.SetupGet(x => x.HasDeciders).Returns(true);
        changedGate.Setup(x => x.DecideAsync(It.IsAny<ChannelOpenRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(ChannelOpenDecision.Rejected("policy changed"));
        var replay = Assert.IsType<AcceptChannel1Message>(Assert.Single(await
            InboundHandler(connection, harness, resumed, changedGate.Object).HandleAsync(message,
                ChannelState.None, harness.NegotiatedFeatures, harness.Bob.NodeId)));
        Assert.Equal(originalWire, await EncodeAny(harness, replay));
        changedGate.Verify(x => x.DecideAsync(It.IsAny<ChannelOpenRequest>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Equal(workflowId, Assert.Single(await resumed.GetUnconsumedAsync()).WorkflowId);
        Assert.Equal(1, fee.Reads);
        await harness.Alice.InScopeAsync(async uow =>
        {
            Assert.Equal(allocationEnvelope,
                Assert.Single(await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflowId)).Envelope);
            return 0;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_NativeOpening_When_ProductionV1HandlersComplete_Then_AllocationAndInitialReceiptsAreConsumedWithChannels(bool taproot)
    {
        await using var aliceDaemon = new SignerDaemonFixture(new string('0', 63) + "1");
        await using var bobDaemon = new SignerDaemonFixture(new string('0', 63) + "2");
        await aliceDaemon.InitializeAsync();
        await bobDaemon.InitializeAsync();
        using var aliceConnection = new RemoteSignerConnection(aliceDaemon.Options());
        using var bobConnection = new RemoteSignerConnection(bobDaemon.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync(configureServices: (node, services) =>
        {
            var connection = node.Name == "Alice" ? aliceConnection : bobConnection;
            node.PublicKeyManager = new RemoteSecureKeyManager(connection);
            services.AddSingleton<ISecureKeyManager>(node.PublicKeyManager);
            services.AddSingleton<ILightningSigner>(sp => new RemoteLightningSigner(connection,
                sp.GetRequiredService<IChannelSigningInfoSource>(),
                sp.GetRequiredService<NLightning.Domain.Bitcoin.Interfaces.IUtxoMemoryRepository>()));
            services.AddSingleton<IRemoteSigningWorkflowCoordinator>(sp =>
                new RemoteSigningWorkflowCoordinator(connection, sp.GetRequiredService<IServiceScopeFactory>()));
            services.AddSingleton<NativeV1ChannelOpening>();
        });
        var request = Request(harness);
        request.IsSimpleTaproot = taproot;
        var channel = await harness.Alice.Services.GetRequiredService<NativeV1ChannelOpening>()
            .CreateOutboundAsync(request, harness.NegotiatedFeatures, harness.Bob.NodeId);
        harness.Alice.Services.GetRequiredService<NLightning.Domain.Bitcoin.Interfaces.IUtxoMemoryRepository>()
            .LockUtxosToSpendOnChannel(request.FundingAmount, channel.ChannelId);
        var factory = harness.Alice.Services.GetRequiredService<IMessageFactory>();
        var open = taproot
            ? factory.CreateOpenChannel1Message(channel.ChannelId, request.FundingAmount,
            channel.LocalKeySet.FundingCompactPubKey, channel.RemoteBalance, channel.ChannelParams.Local,
            channel.ChannelParams.FeeRateAmountPerKw, channel.LocalKeySet.RevocationCompactBasepoint,
            channel.LocalKeySet.PaymentCompactBasepoint, channel.LocalKeySet.DelayedPaymentCompactBasepoint,
            channel.LocalKeySet.HtlcCompactBasepoint, channel.LocalKeySet.CurrentPerCommitmentCompactPoint,
            new ChannelFlags(), new ChannelTypeTlv(channel.ChannelParams.ToChannelType()), new UpfrontShutdownScriptTlv(new NLightning.Domain.Bitcoin.ValueObjects.BitcoinScript(Array.Empty<byte>())),
                harness.Alice.Services.GetRequiredService<ILightningSigner>()
                    .GetLocalVerificationNonce(channel.LocalKeySet.KeyIndex, null, 0))
            : factory.CreateOpenChannel1Message(channel.ChannelId, request.FundingAmount,
            channel.LocalKeySet.FundingCompactPubKey, channel.RemoteBalance, channel.ChannelParams.Local,
            channel.ChannelParams.FeeRateAmountPerKw, channel.LocalKeySet.RevocationCompactBasepoint,
            channel.LocalKeySet.PaymentCompactBasepoint, channel.LocalKeySet.DelayedPaymentCompactBasepoint,
            channel.LocalKeySet.HtlcCompactBasepoint, channel.LocalKeySet.CurrentPerCommitmentCompactPoint,
            new ChannelFlags(), new ChannelTypeTlv(channel.ChannelParams.ToChannelType()), new UpfrontShutdownScriptTlv(new NLightning.Domain.Bitcoin.ValueObjects.BitcoinScript(Array.Empty<byte>())));
        await harness.Alice.ChannelManager.StartOpeningChannelAsync(harness.Bob.NodeId, channel, open);
        await harness.PumpAsync();
        Assert.Single(harness.Alice.Published);
        foreach (var node in harness.Nodes)
        {
            await node.InScopeAsync(async uow =>
            {
                Assert.Empty(await uow.SigningWorkflowDbRepository.GetUnconsumedAsync(SigningWorkflowKind.ChannelKeyAllocation));
                Assert.Empty(await uow.SigningWorkflowDbRepository.GetUnconsumedAsync(SigningWorkflowKind.Opening));
                var saved = Assert.Single(await uow.ChannelDbRepository.GetByPeerIdAsync(harness.Other(node).NodeId));
                Assert.NotNull(saved);
                Assert.Equal(taproot, saved.ChannelParams.OptionSimpleTaproot);
                return 0;
            });
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_PreChannelAllocation_When_NodeAndSignerRestart_Then_OpeningUsesOriginalIndexRequestAndFee(bool injected)
    {
        await using var daemon = new SignerDaemonFixture(injected);
        await daemon.InitializeAsync();
        using var connection = new RemoteSignerConnection(daemon.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var request = Request(harness);
        ChannelModel original;
        Guid workflowId;
        byte[] envelope;
        using (var coordinator = Coordinator(connection, harness))
        {
            var opening = Opening(connection, harness, coordinator);
            original = await opening.CreateOutboundAsync(request, harness.NegotiatedFeatures, harness.Bob.NodeId);
            var workflow = Assert.Single(await opening.GetUnconsumedAsync());
            workflowId = workflow.WorkflowId;
            envelope = await harness.Alice.InScopeAsync(async uow =>
                Assert.Single(await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflow.WorkflowId)).Envelope);
            var restored = opening.ReadOutboundRequest(workflow);
            Assert.Equal(request.FundingAmount, restored.FundingAmount);
            Assert.Equal(request.PushAmount, restored.PushAmount);
            Assert.Equal(request.Tags, restored.Tags);
        }
        await harness.RestartAsync(harness.Alice);
        await daemon.RestartAsync();
        using var resumedCoordinator = Coordinator(connection, harness);
        var resumed = Opening(connection, harness, resumedCoordinator);
        var channel = await resumed.CreateOutboundAsync(request, harness.NegotiatedFeatures, harness.Bob.NodeId);
        Assert.Equal(original.ChannelId, channel.ChannelId);
        Assert.Equal(original.LocalKeySet.KeyIndex, channel.LocalKeySet.KeyIndex);
        Assert.Equal(original.LocalKeySet.FundingCompactPubKey, channel.LocalKeySet.FundingCompactPubKey);
        Assert.Equal(original.ChannelParams.FeeRateAmountPerKw, channel.ChannelParams.FeeRateAmountPerKw);
        Assert.Equal(workflowId, Assert.Single(await resumed.GetUnconsumedAsync()).WorkflowId);
        await harness.Alice.InScopeAsync(async uow =>
        {
            var saved = Assert.Single(await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflowId));
            Assert.Equal(envelope, saved.Envelope);
            Assert.Equal(SigningRequestState.Completed, saved.State);
            return 0;
        });
        Assert.Equal(NLightning.Signing.Contracts.RequestOutcome.Completed,
            connection.Reconcile(WireRequest.Parser.ParseFrom(envelope)).Outcome);
    }

    [Fact]
    public async Task Given_UnknownAllocation_When_NodeRestarts_Then_OriginalOpeningStaysBlockedWithoutReplacementRequest()
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        using var connection = new RemoteSignerConnection(daemon.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var request = Request(harness);
        Guid workflowId;
        byte[] envelope;
        using (var coordinator = new RemoteSigningWorkflowCoordinator(connection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>(), attachCapture: false))
        {
            var capture = new UnknownCapture(coordinator);
            connection.AttachWorkflowCapture(capture);
            try
            {
                var opening = Opening(connection, harness, coordinator);
                await Assert.ThrowsAsync<RemoteSigningWorkflowException>(() =>
                    opening.CreateOutboundAsync(request, harness.NegotiatedFeatures, harness.Bob.NodeId));
                var workflow = Assert.Single(await opening.GetUnconsumedAsync());
                workflowId = workflow.WorkflowId;
                Assert.Equal(SigningWorkflowState.Blocked, workflow.State);
                envelope = await harness.Alice.InScopeAsync(async uow =>
                    Assert.Single(await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflowId)).Envelope);
                Assert.Equal(0, capture.Dispatched);
            }
            finally { connection.DetachWorkflowCapture(capture); }
        }
        await harness.RestartAsync(harness.Alice);
        using var restarted = Coordinator(connection, harness);
        var resumed = Opening(connection, harness, restarted);
        await Assert.ThrowsAsync<RemoteSigningWorkflowException>(() =>
            resumed.CreateOutboundAsync(request, harness.NegotiatedFeatures, harness.Bob.NodeId));
        Assert.Equal(workflowId, Assert.Single(await resumed.GetUnconsumedAsync()).WorkflowId);
        await harness.Alice.InScopeAsync(async uow =>
        {
            Assert.Equal(envelope, Assert.Single(await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflowId)).Envelope);
            return 0;
        });
    }

    [Fact]
    public async Task Given_InboundOpening_When_NodeRestarts_Then_ExactWireIntentAndKeysAreRecoveredAndConsumedWithChannel()
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        using var connection = new RemoteSignerConnection(daemon.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var message = await InboundMessage(harness);
        ChannelModel original;
        Guid workflowId;
        using (var coordinator = Coordinator(connection, harness))
        {
            var opening = Opening(connection, harness, coordinator);
            original = await opening.CreateInboundAsync(message, harness.NegotiatedFeatures, harness.Bob.NodeId, null);
            workflowId = Assert.Single(await opening.GetUnconsumedAsync()).WorkflowId;
        }
        await harness.RestartAsync(harness.Alice);
        using var restarted = Coordinator(connection, harness);
        var resumed = Opening(connection, harness, restarted);
        var workflow = Assert.Single(await resumed.GetUnconsumedAsync());
        var savedMessage = await resumed.ReadInboundMessageAsync(workflow);
        Assert.Equal(await Encode(harness, message), await Encode(harness, savedMessage));
        var channel = await resumed.CreateInboundAsync(savedMessage, harness.NegotiatedFeatures, harness.Bob.NodeId, null);
        Assert.Equal(original.LocalKeySet.KeyIndex, channel.LocalKeySet.KeyIndex);
        var temporaryId = channel.ChannelId;
        channel.FundingOutput!.TransactionId = new TxId(Enumerable.Repeat((byte)7, 32).ToArray());
        channel.FundingOutput.Index = 0;
        channel.UpdateChannelId(harness.Alice.Services.GetRequiredService<IChannelIdFactory>()
            .CreateV1(channel.FundingOutput.TransactionId.Value, 0));
        await harness.Alice.InScopeAsync(async uow =>
        {
            await uow.ChannelDbRepository.AddAsync(channel);
            await resumed.StageConsumeAsync(channel, temporaryId, uow);
            Assert.False(await uow.ChannelDbRepository.ExistsAsync(channel.ChannelId));
            await uow.SaveChangesAsync();
            return 0;
        });
        await harness.Alice.InScopeAsync(async uow =>
        {
            Assert.Equal(channel.LocalKeySet.KeyIndex, (await uow.ChannelDbRepository.GetByIdAsync(channel.ChannelId))!.LocalKeySet.KeyIndex);
            Assert.Equal(SigningWorkflowState.Consumed, (await uow.SigningWorkflowDbRepository.GetAsync(workflowId))!.State);
            Assert.Equal(SigningRequestState.Consumed, Assert.Single(await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflowId)).State);
            return 0;
        });
    }

    [Fact]
    public async Task Given_CommitmentZeroHasStarted_When_PreChannelOpeningRetries_Then_NewFundingNegotiationIsBlocked()
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        using var connection = new RemoteSignerConnection(daemon.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var message = await InboundMessage(harness);
        using var coordinator = Coordinator(connection, harness);
        var opening = Opening(connection, harness, coordinator);
        var channel = await opening.CreateInboundAsync(message, harness.NegotiatedFeatures, harness.Bob.NodeId, null);
        var temporaryId = channel.ChannelId;
        channel.FundingOutput!.TransactionId = new TxId(Enumerable.Repeat((byte)9, 32).ToArray());
        channel.FundingOutput.Index = 0;
        channel.UpdateChannelId(harness.Alice.Services.GetRequiredService<IChannelIdFactory>()
            .CreateV1(channel.FundingOutput.TransactionId.Value, 0));
        using (var initial = await opening.BeginInitialCommitAsync(channel, temporaryId))
        {
            initial!.Activate();
            new RemoteLightningSigner(connection).RegisterChannel(channel.ChannelId, channel.GetSigningInfo());
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            opening.CreateInboundAsync(message, harness.NegotiatedFeatures, harness.Bob.NodeId, null));
        Assert.Single(await opening.GetUnconsumedAsync());
    }

    private static NativeV1ChannelOpening Opening(RemoteSignerConnection connection, TaprootOpenHarness harness,
        RemoteSigningWorkflowCoordinator coordinator, IFeeService? fees = null) => new(
        harness.Alice.Services.GetRequiredService<IChannelFactory>(), new RemoteLightningSigner(connection),
        harness.Alice.Services.GetRequiredService<IServiceScopeFactory>(),
        harness.Alice.Services.GetRequiredService<IChannelIdFactory>(),
        harness.Alice.Services.GetRequiredService<IMessageSerializer>(),
        fees ?? harness.Alice.Services.GetRequiredService<IFeeService>(), Options.Create(harness.Alice.Options), coordinator);
    private static OpenChannel1MessageHandler InboundHandler(RemoteSignerConnection connection,
        TaprootOpenHarness harness, NativeV1ChannelOpening opening, IChannelOpenDecisionGate gate) => new(
        harness.Alice.Services.GetRequiredService<IChannelFactory>(), harness.Alice.Memory,
        NullLogger<OpenChannel1MessageHandler>.Instance, harness.Alice.Services.GetRequiredService<IMessageFactory>(),
        nodeOptions: Options.Create(harness.Alice.Options), lightningSigner: new RemoteLightningSigner(connection),
        openDecisionGate: gate, nativeOpening: opening);
    private static RemoteSigningWorkflowCoordinator Coordinator(RemoteSignerConnection connection, TaprootOpenHarness harness) =>
        new(connection, harness.Alice.Services.GetRequiredService<IServiceScopeFactory>());
    private static OpenChannelClientRequest Request(TaprootOpenHarness harness) =>
        new(harness.Bob.NodeId.ToString(), LightningMoney.Satoshis(1_000_000))
        { ForceV1 = true, PushAmount = LightningMoney.Satoshis(10_000), Label = "saved opening", Tags = ["purpose=test"] };

    private static async Task<OpenChannel1Message> InboundMessage(TaprootOpenHarness harness)
    {
        var request = Request(harness);
        var channel = await harness.Bob.Services.GetRequiredService<IChannelFactory>()
            .CreateChannelV1AsInitiatorAsync(request, harness.NegotiatedFeatures, harness.Alice.NodeId);
        return harness.Bob.Services.GetRequiredService<IMessageFactory>().CreateOpenChannel1Message(
            channel.ChannelId, request.FundingAmount, channel.LocalKeySet.FundingCompactPubKey, channel.RemoteBalance,
            channel.ChannelParams.Local, channel.ChannelParams.FeeRateAmountPerKw,
            channel.LocalKeySet.RevocationCompactBasepoint, channel.LocalKeySet.PaymentCompactBasepoint,
            channel.LocalKeySet.DelayedPaymentCompactBasepoint, channel.LocalKeySet.HtlcCompactBasepoint,
            channel.LocalKeySet.CurrentPerCommitmentCompactPoint, new ChannelFlags(),
            new ChannelTypeTlv(channel.ChannelParams.ToChannelType()), new UpfrontShutdownScriptTlv(new NLightning.Domain.Bitcoin.ValueObjects.BitcoinScript(Array.Empty<byte>())));
    }
    private static async Task<byte[]> Encode(TaprootOpenHarness harness, OpenChannel1Message message)
    {
        using var stream = new MemoryStream();
        await harness.Alice.Services.GetRequiredService<IMessageSerializer>().SerializeAsync(message, stream);
        return stream.ToArray();
    }
    private static async Task<byte[]> EncodeAny(TaprootOpenHarness harness, IMessage message)
    {
        using var stream = new MemoryStream();
        await harness.Alice.Services.GetRequiredService<IMessageSerializer>().SerializeAsync(message, stream);
        return stream.ToArray();
    }

    private sealed class MutableFee : IFeeService
    {
        public LightningMoney Quote { get; set; } = LightningMoney.Satoshis(2_500);
        public int Reads { get; private set; }
        public Task<LightningMoney> GetFeeRatePerKwAsync(CancellationToken cancellationToken = default)
        { Reads++; return Task.FromResult(Quote); }
        public LightningMoney GetCachedFeeRatePerKw() => Quote;
        public Task RefreshFeeRateAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
    }

    // Supplementary unknown-receipt policy proof; no real process-kill claim for this injected outcome.
    private sealed class UnknownCapture(RemoteSigningWorkflowCoordinator coordinator) : IRemoteSigningRequestCapture
    {
        public int Dispatched { get; private set; }
        public byte[] Execute(uint operation, byte[] envelope, byte[] fingerprint,
            Func<byte[], RemoteSigningRequestStatus> reconcile, Func<byte[], byte[]> execute) =>
            coordinator.Execute(operation, envelope, fingerprint,
                _ => new RemoteSigningRequestStatus(RemoteSigningRequestOutcome.Unknown), saved =>
                { Dispatched++; return execute(saved); });
    }
}