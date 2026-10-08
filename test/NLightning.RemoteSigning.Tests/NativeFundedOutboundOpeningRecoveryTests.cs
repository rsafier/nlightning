using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NLightning.Application.Channels.Services;
using NLightning.Application.Channels.Handlers.Interfaces;
using NLightning.Application.Tests.Channels.Taproot;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.Wallet.Models;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Channels.Interfaces;
using NLightning.Domain.Channels.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Protocol.Messages;
using NLightning.Domain.Serialization.Interfaces;
using NLightning.Domain.Signing.Recovery;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Infrastructure.Persistence.Contexts;
using NLightning.Infrastructure.Bitcoin.Wallet.Interfaces;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Tests.Utils.Mocks;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeFundedOutboundOpeningRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RetainedFundingCreatedSurvivesStartupAndSignerRestartAndRejectsChangedWallet(bool taproot)
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        using var connection = new RemoteSignerConnection(daemon.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync(configureServices: (node, services) =>
        {
            if (node.Name != "Alice") return;
            var keys = new RemoteSecureKeyManager(connection);
            node.PublicKeyManager = keys;
            services.Replace(ServiceDescriptor.Singleton<ISecureKeyManager>(keys));
            services.Replace(ServiceDescriptor.Singleton<ILightningSigner>(sp => new RemoteLightningSigner(connection,
                sp.GetRequiredService<IChannelSigningInfoSource>(), sp.GetRequiredService<IUtxoMemoryRepository>())));
            services.AddSingleton(connection);
            services.AddSingleton<RemoteSigningWorkflowCoordinator>();
            services.AddSingleton<IRemoteSigningWorkflowCoordinator>(sp => sp.GetRequiredService<RemoteSigningWorkflowCoordinator>());
            services.AddSingleton<NativeV1ChannelOpening>();
            services.AddSingleton<NativeV1FundedOutboundOpening>();
        }, simpleTaproot: taproot);
        FundingCreatedMessage? original = null;
        harness.Tamper = (from, message) =>
        {
            if (from == "Alice" && message is FundingCreatedMessage created)
            {
                original = created;
                throw new InterceptedOpeningException();
            }
            return message;
        };
        await Assert.ThrowsAsync<InterceptedOpeningException>(() => harness.OpenAsync(
            LightningMoney.Satoshis(1_000_000), LightningMoney.Satoshis(10_000)));
        Assert.NotNull(original);
        var realId = harness.Alice.Services.GetRequiredService<IChannelIdFactory>()
            .CreateV1(original.Payload.FundingTxId, original.Payload.FundingOutputIndex);
        var before = await ReadAsync(harness, realId);
        Assert.Equal(ChannelState.V1FundingCreated, before.Channel.State);
        Assert.Equal(SigningWorkflowState.Consumed, before.Workflow.State);
        Assert.Single(before.Reservations);
        var expected = await EncodeAsync(harness, original);
        var journal = await File.ReadAllBytesAsync(Path.Combine(daemon.DirectoryPath, "state"), TestContext.Current.CancellationToken);
        harness.LinkDown = true;
        harness.Tamper = null;
        await harness.RestartAsync(harness.Alice);
        var after = await ReadAsync(harness, realId);
        Assert.Equal(ChannelState.V1FundingCreated, after.Channel.State);
        Assert.Equal(before.Workflow.WorkflowId, after.Workflow.WorkflowId);
        Assert.Equal(before.Workflow.PublicationIntent, after.Workflow.PublicationIntent);
        Assert.Equal(before.Reservations[0].Id, Assert.Single(after.Reservations).Id);
        Assert.Equal(expected, await EncodeAsync(harness, await ReplayAsync(harness, realId)));
        Assert.Empty(harness.Alice.Published);
        await daemon.RestartAsync();
        Assert.Equal(expected, await EncodeAsync(harness, await ReplayAsync(harness, realId)));
        Assert.Equal(journal, await File.ReadAllBytesAsync(Path.Combine(daemon.DirectoryPath, "state"), TestContext.Current.CancellationToken));
        foreach (var request in before.Requests)
        {
            var retained = Assert.Single(after.Requests, x => x.RequestId == request.RequestId);
            Assert.Equal(request.Envelope, retained.Envelope);
            Assert.Equal(request.Response, retained.Response);
        }
        var recovery = harness.Alice.Services.GetRequiredService<NativeV1FundedOutboundOpening>();
        var unsigned = await harness.Alice.InScopeAsync(unit => recovery.RestoreUnsignedFundingAsync(after.Channel, unit));
        await harness.Alice.InScopeAsync(async unit =>
        {
            await recovery.ValidateFundingAsync(after.Channel, unsigned, after.Reservations[0].Fee, unit);
            return 0;
        });
        foreach (var invalid in new[]
                 {
                     after.Workflow with { PublicationIntent = null },
                     after.Workflow with { State = SigningWorkflowState.Blocked }
                 })
        {
            await CorruptWorkflowAsync(harness, invalid);
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Alice.InScopeAsync(async unit =>
            {
                await recovery.ValidateFundingAsync(after.Channel, unsigned, after.Reservations[0].Fee, unit);
                return 0;
            }));
            await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Alice.InScopeAsync(unit =>
                recovery.RestoreUnsignedFundingAsync(after.Channel, unit)));
            Assert.Equal(journal, await File.ReadAllBytesAsync(Path.Combine(daemon.DirectoryPath, "state"), TestContext.Current.CancellationToken));
            await CorruptWorkflowAsync(harness, after.Workflow);
        }
        var wallet = harness.Alice.Services.GetRequiredService<IUtxoMemoryRepository>();
        var reserved = Assert.Single(after.Reservations).Inputs[0];
        Assert.True(wallet.TryGetUtxo(reserved.TxId, reserved.Index, out var originalUtxo));
        var changed = new UtxoModel(originalUtxo.TxId, originalUtxo.Index,
            originalUtxo.Amount + LightningMoney.Satoshis(1), originalUtxo.BlockHeight, originalUtxo.WalletAddress!)
        { LockedToChannelId = originalUtxo.LockedToChannelId };
        wallet.Spend(originalUtxo);
        wallet.Add(changed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReplayAsync(harness, realId));
        Assert.Empty(harness.Alice.Published);
        Assert.Equal(journal, await File.ReadAllBytesAsync(Path.Combine(daemon.DirectoryPath, "state"), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupCompletesCapturedOutboundOpeningAfterPreparedRequestSaveFailure(bool taproot)
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        using var connection = new RemoteSignerConnection(daemon.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync(configureServices: (node, services) =>
        {
            if (node.Name != "Alice") return;
            var keys = new RemoteSecureKeyManager(connection);
            node.PublicKeyManager = keys;
            services.Replace(ServiceDescriptor.Singleton<ISecureKeyManager>(keys));
            services.Replace(ServiceDescriptor.Singleton<ILightningSigner>(sp => new RemoteLightningSigner(connection,
                sp.GetRequiredService<IChannelSigningInfoSource>(), sp.GetRequiredService<IUtxoMemoryRepository>())));
            services.AddSingleton(connection);
            services.AddSingleton<RemoteSigningWorkflowCoordinator>();
            services.AddSingleton<IRemoteSigningWorkflowCoordinator>(sp => sp.GetRequiredService<RemoteSigningWorkflowCoordinator>());
            services.AddSingleton<NativeV1ChannelOpening>();
            services.AddSingleton<NativeV1FundedOutboundOpening>();
        }, simpleTaproot: taproot);
        AcceptChannel1Message? original = null;
        harness.Tamper = (from, message) =>
        {
            if (from == "Bob" && message is AcceptChannel1Message accept)
            {
                original = accept;
                throw new InterceptedOpeningException();
            }
            return message;
        };
        await Assert.ThrowsAsync<InterceptedOpeningException>(() => harness.OpenAsync(
            LightningMoney.Satoshis(1_000_000), LightningMoney.Satoshis(10_000)));
        Assert.NotNull(original);
        // Generate the address batch before counting saves: the handler reserves its change address (1),
        // commits the immutable opening (2), then captures its first RegisterChannel request (3).
        await harness.Alice.Services.GetRequiredService<IBitcoinWalletService>().GetUnusedAddressAsync(AddressType.P2Wpkh, true);
        harness.Alice.Crash.Arm(3);
        using (var scope = harness.Alice.Services.CreateScope())
            await Assert.ThrowsAsync<SimulatedCrashException>(() => scope.ServiceProvider
                .GetRequiredService<IChannelMessageHandler<AcceptChannel1Message>>()
                .HandleAsync(original, ChannelState.None, harness.NegotiatedFeatures, harness.Bob.NodeId));
        var pending = await harness.Alice.InScopeAsync(async unit =>
            Assert.Single(await unit.SigningWorkflowDbRepository.GetUnconsumedAsync(SigningWorkflowKind.Opening)));
        var before = await ReadAsync(harness, pending.ChannelId);
        Assert.Equal(ChannelState.V1Opening, before.Channel.State);
        Assert.Equal(SigningWorkflowState.Pending, before.Workflow.State);
        Assert.NotNull(before.Workflow.PublicationIntent);
        Assert.Empty(before.Requests); // The immutable opening committed, but its first prepared-request save failed.
        Assert.Single(before.Reservations);
        harness.LinkDown = true;
        harness.Tamper = null;
        await harness.RestartAsync(harness.Alice);
        var after = await ReadAsync(harness, pending.ChannelId);
        Assert.Equal(ChannelState.V1FundingCreated, after.Channel.State);
        Assert.Equal(SigningWorkflowState.Consumed, after.Workflow.State);
        Assert.Equal(before.Workflow.PublicationIntent, after.Workflow.PublicationIntent);
        Assert.Equal(before.Workflow.WorkflowId, after.Workflow.WorkflowId);
        Assert.Equal(before.Reservations[0].Id, Assert.Single(after.Reservations).Id);
        Assert.Empty(harness.Alice.Published);
        var replay = await ReplayAsync(harness, pending.ChannelId);
        Assert.Equal(original.Payload.ChannelId, replay.Payload.ChannelId);
        foreach (var request in before.Requests)
        {
            var retained = Assert.Single(after.Requests, x => x.RequestId == request.RequestId);
            Assert.Equal(request.Envelope, retained.Envelope);
            if (request.Response is not null) Assert.Equal(request.Response, retained.Response);
        }
    }

    // Bypass the repository's prerequisite/state guards only to simulate storage corruption in this isolated fixture.
    private static async Task CorruptWorkflowAsync(TaprootOpenHarness harness, SigningWorkflow workflow)
    {
        using var scope = harness.Alice.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<NLightningDbContext>();
        var entity = await database.SigningWorkflows.SingleAsync(x => x.WorkflowId == workflow.WorkflowId,
            TestContext.Current.CancellationToken);
        entity.PublicationIntent = workflow.PublicationIntent?.ToArray();
        entity.State = (int)workflow.State;
        await database.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static Task<SavedOpening> ReadAsync(TaprootOpenHarness harness, ChannelId id)
        => harness.Alice.InScopeAsync(async unit => new SavedOpening(
            (await unit.ChannelDbRepository.GetByIdAsync(id))!,
            (await unit.SigningWorkflowDbRepository.GetLatestForChannelAsync(id, SigningWorkflowKind.Opening))!,
            await unit.SigningWorkflowDbRepository.GetRequestsAsync(
                (await unit.SigningWorkflowDbRepository.GetLatestForChannelAsync(id, SigningWorkflowKind.Opening))!.WorkflowId),
            await unit.FeeInputReservationDbRepository.GetAllAsync()));
    private static Task<FundingCreatedMessage> ReplayAsync(TaprootOpenHarness harness, ChannelId id)
        => harness.Alice.InScopeAsync(async unit => await harness.Alice.Services.GetRequiredService<NativeV1FundedOutboundOpening>()
            .TryGetFundingCreatedAsync((await unit.ChannelDbRepository.GetByIdAsync(id))!, unit)
            ?? throw new InvalidOperationException("Opening was not retained."));
    private static async Task<byte[]> EncodeAsync(TaprootOpenHarness harness, IMessage message)
    {
        using var stream = new MemoryStream();
        await harness.Alice.Services.GetRequiredService<IMessageSerializer>().SerializeAsync(message, stream);
        return stream.ToArray();
    }
    private sealed record SavedOpening(ChannelModel Channel, SigningWorkflow Workflow,
        IReadOnlyList<SigningRequest> Requests, IReadOnlyList<FeeInputReservation> Reservations);
    private sealed class InterceptedOpeningException : Exception;
}