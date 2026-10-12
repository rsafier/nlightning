using System.Buffers.Binary;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NLightning.Application.Channels.Handlers.Interfaces;
using NLightning.Application.Channels.Services;
using NLightning.Application.Tests.Channels.Taproot;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Channels.Enums;
using NLightning.Domain.Channels.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Protocol.Interfaces;
using NLightning.Domain.Protocol.Messages;
using NLightning.Domain.Protocol.Payloads;
using NLightning.Domain.Serialization.Interfaces;
using NLightning.Domain.Signing.Recovery;
using NLightning.Infrastructure.RemoteSigning;

namespace NLightning.RemoteSigning.Tests;

public sealed class NativeFundedInboundOpeningRecoveryTests
{
    [Theory]
    [InlineData(false, 2)]
    [InlineData(false, 3)]
    [InlineData(false, 8)]
    [InlineData(false, 9)]
    [InlineData(false, 10)]
    [InlineData(true, 2)]
    [InlineData(true, 3)]
    [InlineData(true, 8)]
    [InlineData(true, 9)]
    [InlineData(true, 10)]
    public async Task FundedInboundRestartsFromImmutableIntentAndRetainsExactNativeReply(bool taproot, int failedSave)
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        using var connection = new RemoteSignerConnection(daemon.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync(configureServices: (node, services) =>
        {
            if (node.Name != "Bob") return;
            var keys = new RemoteSecureKeyManager(connection);
            node.PublicKeyManager = keys;
            services.Replace(ServiceDescriptor.Singleton<ISecureKeyManager>(keys));
            services.Replace(ServiceDescriptor.Singleton<ILightningSigner>(sp => new RemoteLightningSigner(connection,
                sp.GetRequiredService<IChannelSigningInfoSource>(), sp.GetRequiredService<IUtxoMemoryRepository>())));
            services.AddSingleton(connection);
            services.AddSingleton<RemoteSigningWorkflowCoordinator>();
            services.AddSingleton<IRemoteSigningWorkflowCoordinator>(sp => sp.GetRequiredService<RemoteSigningWorkflowCoordinator>());
            services.AddSingleton<NativeV1ChannelOpening>();
            services.AddSingleton<NativeV1FundedInboundOpening>();
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
        var inputBytes = await EncodeAsync(harness, original);
        var ids = harness.Bob.Services.GetRequiredService<IChannelIdFactory>();
        var realId = ids.CreateV1(original.Payload.FundingTxId, original.Payload.FundingOutputIndex);
        harness.Bob.Crash.Arm(failedSave);
        await Assert.ThrowsAnyAsync<Exception>(() => harness.Bob.InScopeAsync(async unit =>
        {
            using var scope = harness.Bob.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<IChannelMessageHandler<FundingCreatedMessage>>()
                .HandleAsync(original, ChannelState.None, harness.NegotiatedFeatures, harness.Alice.NodeId);
        }));
        var before = await ReadAsync(harness, realId);
        Assert.Equal(ChannelState.V1Opening, before.Channel.State);
        Assert.Equal(SigningWorkflowState.Pending, before.Workflow.State);
        var requestBefore = before.Requests.ToArray();
        var journalBefore = await File.ReadAllBytesAsync(Path.Combine(daemon.DirectoryPath, "state"), TestContext.Current.CancellationToken);

        await harness.RestartAsync(harness.Bob);

        var after = await ReadAsync(harness, realId);
        Assert.Equal(ChannelState.V1FundingSigned, after.Channel.State);
        Assert.Equal(before.Channel.LocalKeySet.KeyIndex, after.Channel.LocalKeySet.KeyIndex);
        Assert.Equal(before.Channel.ChannelParams.FeeRateAmountPerKw, after.Channel.ChannelParams.FeeRateAmountPerKw);
        Assert.Equal(before.Workflow.WorkflowId, after.Workflow.WorkflowId);
        Assert.Equal(before.Workflow.PublicationIntent, after.Workflow.PublicationIntent);
        Assert.Equal(before.Workflow.SnapshotFingerprint, after.Workflow.SnapshotFingerprint);
        Assert.Equal(SigningWorkflowState.Consumed, after.Workflow.State);
        Assert.Equal(4, after.Requests.Count);
        Assert.All(after.Requests, request => Assert.Equal(SigningRequestState.Consumed, request.State));
        foreach (var request in requestBefore)
        {
            var retained = Assert.Single(after.Requests, candidate => candidate.RequestId == request.RequestId);
            Assert.Equal(request.Envelope, retained.Envelope);
            if (request.Response is not null) Assert.Equal(request.Response, retained.Response);
        }
        if (failedSave >= 9)
            Assert.Equal(journalBefore, await File.ReadAllBytesAsync(Path.Combine(daemon.DirectoryPath, "state"), TestContext.Current.CancellationToken));
        var retainedReply = await ReplyAsync(harness, original);
        var replyBytes = await EncodeAsync(harness, retainedReply);
        var journalAfter = await File.ReadAllBytesAsync(Path.Combine(daemon.DirectoryPath, "state"), TestContext.Current.CancellationToken);
        await daemon.RestartAsync();
        Assert.Equal(replyBytes, await EncodeAsync(harness, await ReplyAsync(harness, original)));
        Assert.Equal(journalAfter, await File.ReadAllBytesAsync(Path.Combine(daemon.DirectoryPath, "state"), TestContext.Current.CancellationToken));
        Assert.Equal(inputBytes, await EncodeAsync(harness, original));
        // The peer verifies the original retained commitment-zero signature, including taproot nonce material.
        using (var scope = harness.Alice.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IChannelMessageHandler<FundingSignedMessage>>()
                .HandleAsync(retainedReply, ChannelState.V1FundingCreated, harness.NegotiatedFeatures, harness.Bob.NodeId);
        Assert.Single(harness.Alice.Published);

        var changed = new FundingCreatedMessage(new FundingCreatedPayload(original.Payload.ChannelId,
            original.Payload.FundingTxId, original.Payload.FundingOutputIndex,
            new NLightning.Domain.Crypto.ValueObjects.CompactSignature(Enumerable.Repeat((byte)1, 64).ToArray())));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReplyAsync(harness, changed));
        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Bob.InScopeAsync(unit =>
            harness.Bob.Services.GetRequiredService<NativeV1FundedInboundOpening>()
                .TryHandleRetainedAsync(original, harness.Bob.NodeId, unit)));
        await daemon.StopAsync();
        RemoveReceipt(Path.Combine(daemon.DirectoryPath, "state"), after.Requests[^1].RequestId);
        await daemon.RestartAsync();
        var missingReceiptJournal = await File.ReadAllBytesAsync(Path.Combine(daemon.DirectoryPath, "state"), TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<RemoteSigningWorkflowException>(() => ReplyAsync(harness, original));
        Assert.Equal(missingReceiptJournal, await File.ReadAllBytesAsync(Path.Combine(daemon.DirectoryPath, "state"), TestContext.Current.CancellationToken));
        await harness.Bob.InScopeAsync(async unit =>
        {
            var channel = (await unit.ChannelDbRepository.GetByIdAsync(realId))!;
            channel.UpdateState(ChannelState.Closed);
            await unit.ChannelDbRepository.UpdateAsync(channel);
            await unit.SaveChangesAsync();
            return 0;
        });
        harness.Bob.Memory.TryRemoveChannel(realId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReplyAsync(harness, original));
        Assert.False(harness.Bob.Memory.TryGetChannel(realId, out _));
        Assert.Equal(missingReceiptJournal, await File.ReadAllBytesAsync(Path.Combine(daemon.DirectoryPath, "state"), TestContext.Current.CancellationToken));
    }

    private static async Task<FundingSignedMessage> ReplyAsync(TaprootOpenHarness harness, FundingCreatedMessage original)
    {
        using var scope = harness.Bob.Services.CreateScope();
        var replies = await scope.ServiceProvider.GetRequiredService<IChannelMessageHandler<FundingCreatedMessage>>()
            .HandleAsync(original, ChannelState.None, harness.NegotiatedFeatures, harness.Alice.NodeId);
        return Assert.IsType<FundingSignedMessage>(Assert.Single(replies));
    }

    private static Task<SavedOpening> ReadAsync(TaprootOpenHarness harness, ChannelId realId)
        => harness.Bob.InScopeAsync(async unit =>
        {
            var channel = (await unit.ChannelDbRepository.GetByIdAsync(realId))!;
            var workflow = (await unit.SigningWorkflowDbRepository.GetLatestForChannelAsync(realId, SigningWorkflowKind.Opening))!;
            return new SavedOpening(channel, workflow, await unit.SigningWorkflowDbRepository.GetRequestsAsync(workflow.WorkflowId));
        });

    private static async Task<byte[]> EncodeAsync(TaprootOpenHarness harness, NLightning.Domain.Protocol.Interfaces.IMessage message)
    {
        using var stream = new MemoryStream();
        await harness.Bob.Services.GetRequiredService<IMessageSerializer>().SerializeAsync(message, stream);
        return stream.ToArray();
    }

    private sealed record SavedOpening(ChannelModel Channel, SigningWorkflow Workflow, IReadOnlyList<SigningRequest> Requests);
    private sealed class InterceptedOpeningException : Exception;

    private static void RemoveReceipt(string path, Guid requestId)
    {
        var journal = File.ReadAllBytes(path);
        const int headerLength = 8 + 33 + 32;
        using var retained = new MemoryStream();
        retained.Write(journal.AsSpan(0, headerLength));
        var removed = 0;
        for (var position = headerLength; position < journal.Length;)
        {
            var size = BinaryPrimitives.ReadInt32LittleEndian(journal.AsSpan(position, sizeof(int)));
            Assert.True(size > 0 && position + sizeof(int) + size <= journal.Length);
            using var record = JsonDocument.Parse(journal.AsMemory(position + sizeof(int), size));
            if (record.RootElement.GetProperty("RequestId").GetString() == requestId.ToString("N")) removed++;
            else retained.Write(journal.AsSpan(position, sizeof(int) + size));
            position += sizeof(int) + size;
        }
        Assert.Equal(1, removed);
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        retained.Position = 0;
        retained.CopyTo(file);
        file.Flush(flushToDisk: true);
    }
}