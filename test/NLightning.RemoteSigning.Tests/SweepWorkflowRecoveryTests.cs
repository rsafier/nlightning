using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Application.Tests.Channels.Taproot;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Onchain.Enums;
using NLightning.Domain.Onchain.Models;
using NLightning.Domain.Signing.Recovery;
using NLightning.Infrastructure.RemoteSigning;
using WireRequest = NLightning.Signing.Contracts.SigningRequest;

namespace NLightning.RemoteSigning.Tests;

public sealed class SweepWorkflowRecoveryTests
{
    [Theory]
    [InlineData(false, RemoteSigningRequestOutcome.NotFound, false, true)]
    [InlineData(false, RemoteSigningRequestOutcome.Completed, false, true)]
    [InlineData(true, RemoteSigningRequestOutcome.Completed, false, true)]
    [InlineData(true, RemoteSigningRequestOutcome.NotFound, false, false)]
    [InlineData(false, RemoteSigningRequestOutcome.Unknown, false, false)]
    [InlineData(true, RemoteSigningRequestOutcome.Completed, true, false)]
    public async Task Given_ObsoleteSweep_When_ReconciledWithoutDispatch_Then_OnlyKnownReceiptsRetire(
        bool receiptSaved, RemoteSigningRequestOutcome outcome, bool alteredReceipt, bool retires)
    {
        // Arrange: synthetic checkpoint outcomes over the node's real SQLite lifecycle repository.
        await using var signer = new SignerDaemonFixture(injected: true);
        await signer.InitializeAsync();
        using var connection = new RemoteSignerConnection(signer.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var context = new SweepSigningContext([1], 0, null, 1_000, SweepKeyKind.Payment);
        var intent = JsonSerializer.SerializeToUtf8Bytes(new
        { Contexts = JsonSerializer.SerializeToUtf8Bytes(new[] { context }, SignerWire.Options) });
        var descriptor = new SigningWorkflowDescriptor(new ChannelId(SHA256.HashData("sweep-retirement"u8)),
            SigningWorkflowKind.OnchainSweep, 0, 0, SHA256.HashData(intent))
        { PublicationIntent = intent };
        var envelope = RemoteSignerConnection.Prepare(SignerOperations.SignSweepInput, descriptor.ChannelId, context).ToByteArray();
        var parsed = WireRequest.Parser.ParseFrom(envelope);
        var material = new byte[sizeof(uint) + parsed.Payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(material, parsed.Operation);
        parsed.Payload.Span.CopyTo(material.AsSpan(sizeof(uint)));
        var receipt = "original-receipt"u8.ToArray();
        Guid workflowId;
        using (var capture = new RemoteSigningWorkflowCoordinator(connection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>()))
        using (var workflow = await capture.BeginAsync(descriptor))
        {
            workflowId = workflow.WorkflowId;
            workflow.Activate();
            if (receiptSaved)
                capture.Execute(parsed.Operation, envelope, SHA256.HashData(material),
                    _ => new RemoteSigningRequestStatus(RemoteSigningRequestOutcome.NotFound), _ => receipt);
            else
                Assert.Throws<IOException>(() => capture.Execute(parsed.Operation, envelope, SHA256.HashData(material),
                    _ => new RemoteSigningRequestStatus(RemoteSigningRequestOutcome.NotFound),
                    _ => throw new IOException("No response was delivered.")));
        }
        using var recovery = new RemoteSigningWorkflowCoordinator(connection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        var reconciled = 0;
        RemoteSigningRequestStatus Reconcile(byte[] saved)
        {
            Assert.Equal(envelope, saved);
            reconciled++;
            return new RemoteSigningRequestStatus(outcome,
                outcome == RemoteSigningRequestOutcome.Completed ? (alteredReceipt ? "changed"u8.ToArray() : receipt) : null);
        }

        // Act: retirement reconciles the original identity and stages one terminal lifecycle write.
        await harness.Alice.InScopeAsync(async uow =>
        {
            if (retires)
            {
                await recovery.StageRetireSweepAsync(descriptor, uow, Reconcile);
                Assert.Empty(await uow.SigningWorkflowDbRepository.GetPendingForChannelAsync(descriptor.ChannelId));
                await uow.SaveChangesAsync();
            }
            else
                await Assert.ThrowsAsync<RemoteSigningWorkflowException>(() =>
                    recovery.StageRetireSweepAsync(descriptor, uow, Reconcile));
            return 0;
        });

        // Assert: all original material survives; uncertain or lost checkpoints remain durably blocked.
        Assert.Equal(1, reconciled);
        await harness.Alice.InScopeAsync(async uow =>
        {
            var saved = await uow.SigningWorkflowDbRepository.GetAsync(workflowId);
            Assert.NotNull(saved);
            Assert.Equal(retires ? SigningWorkflowState.Abandoned : SigningWorkflowState.Blocked, saved.State);
            Assert.Equal(intent, saved.PublicationIntent);
            var request = Assert.Single(await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflowId));
            Assert.Equal(envelope, request.Envelope);
            Assert.Equal(receiptSaved || retires && outcome == RemoteSigningRequestOutcome.Completed ? receipt : null,
                request.Response);
            if (retires)
            {
                Assert.Empty(await uow.SigningWorkflowDbRepository.GetPendingForChannelAsync(descriptor.ChannelId));
                await Assert.ThrowsAsync<InvalidOperationException>(() => uow.SigningWorkflowDbRepository.UpdateWorkflowAsync(
                    saved with { State = SigningWorkflowState.Pending }));
            }
            return 0;
        });
        if (retires)
        {
            using var next = await recovery.BeginAsync(descriptor);
            Assert.NotEqual(workflowId, next.WorkflowId); // Terminal ownership releases the unique active-channel slot.
        }
        else
            await Assert.ThrowsAsync<RemoteSigningWorkflowException>(() => recovery.BeginAsync(descriptor));
    }

    [Fact]
    public async Task Given_SweepWorkflow_When_RegisteringChannel_Then_RegistrationEnvelopeIsCaptured()
    {
        // Arrange: registration carries ChannelSigningInfo, rather than a sweep input context.
        await using var signer = new SignerDaemonFixture(injected: true);
        await signer.InitializeAsync();
        using var connection = new RemoteSignerConnection(signer.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var (channelId, _) = await harness.OpenAsync(NLightning.Domain.Money.LightningMoney.Satoshis(1_000_000));
        var context = new SweepSigningContext([1], 0, null, 1_000, SweepKeyKind.Payment);
        using var coordinator = new RemoteSigningWorkflowCoordinator(connection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        var intent = JsonSerializer.SerializeToUtf8Bytes(new { Contexts = coordinator.EncodeSweepContexts([context]) });
        using var workflow = await coordinator.BeginAsync(new SigningWorkflowDescriptor(channelId,
            SigningWorkflowKind.OnchainSweep, 0, 0, SHA256.HashData(intent))
        { PublicationIntent = intent });
        workflow.Activate();

        // Act: the native signer accepts registration inside the sweep workflow.
        new RemoteLightningSigner(connection).RegisterChannel(channelId, harness.Alice.Channel(channelId).GetSigningInfo());

        // Assert: the exact registration request and its receipt were captured before further sweep signing.
        await harness.Alice.InScopeAsync(async uow =>
        {
            var request = Assert.Single(await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflow.WorkflowId));
            Assert.Equal(SignerOperations.RegisterChannel, request.Operation);
            Assert.Equal(SigningRequestState.Completed, request.State);
            Assert.NotNull(request.Response);
            return 0;
        });
    }

    [Theory]
    [InlineData(RemoteSigningRequestOutcome.Unknown)]
    [InlineData(RemoteSigningRequestOutcome.Invalidated)]
    [InlineData(RemoteSigningRequestOutcome.Unsupported)]
    public async Task UncertainSweepKeepsOriginalEnvelopeAndBlocksReplacement(RemoteSigningRequestOutcome outcome)
    {
        await using var signer = new SignerDaemonFixture(injected: true);
        await signer.InitializeAsync();
        using var connection = new RemoteSignerConnection(signer.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        using var coordinator = new RemoteSigningWorkflowCoordinator(connection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        var context = new SweepSigningContext([1], 0, null, 1_000, SweepKeyKind.Payment);
        var contexts = JsonSerializer.SerializeToUtf8Bytes(new[] { context }, SignerWire.Options);
        var intent = JsonSerializer.SerializeToUtf8Bytes(new { Contexts = contexts });
        var descriptor = new SigningWorkflowDescriptor(new ChannelId(SHA256.HashData("sweep-test"u8)),
            SigningWorkflowKind.OnchainSweep, 0, 0, SHA256.HashData(intent))
        { PublicationIntent = intent };
        using (var workflow = await coordinator.BeginAsync(descriptor))
        {
            workflow.Activate();
            var envelope = RemoteSignerConnection.Prepare(SignerOperations.SignSweepInput,
                descriptor.ChannelId, context).ToByteArray();
            var request = WireRequest.Parser.ParseFrom(envelope);
            var material = new byte[sizeof(uint) + request.Payload.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(material, request.Operation);
            request.Payload.Span.CopyTo(material.AsSpan(sizeof(uint)));
            var dispatched = 0;
            Assert.Throws<RemoteSigningWorkflowException>(() => coordinator.Execute(request.Operation,
                envelope, SHA256.HashData(material), _ => new RemoteSigningRequestStatus(outcome), _ =>
                { dispatched++; return "[]"u8.ToArray(); }));
            Assert.Equal(0, dispatched);
            await harness.Alice.InScopeAsync(async uow =>
            {
                var saved = Assert.Single(await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflow.WorkflowId));
                Assert.Equal(envelope, saved.Envelope);
                Assert.Equal(SigningRequestState.Blocked, saved.State);
                var savedWorkflow = await uow.SigningWorkflowDbRepository.GetAsync(workflow.WorkflowId);
                Assert.Equal(intent, savedWorkflow!.PublicationIntent);
                return 0;
            });
        }
        await Assert.ThrowsAsync<RemoteSigningWorkflowException>(() => coordinator.BeginAsync(descriptor));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartReplaysOriginalSweepRequestAndExactReceipt(bool receiptSaved)
    {
        await using var signer = new SignerDaemonFixture(injected: true);
        await signer.InitializeAsync();
        using var connection = new RemoteSignerConnection(signer.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var context = new SweepSigningContext([1], 0, null, 1_000, SweepKeyKind.Payment);
        var contexts = JsonSerializer.SerializeToUtf8Bytes(new[] { context }, SignerWire.Options);
        var intent = JsonSerializer.SerializeToUtf8Bytes(new { Contexts = contexts });
        var descriptor = new SigningWorkflowDescriptor(new ChannelId(SHA256.HashData("sweep-test"u8)),
            SigningWorkflowKind.OnchainSweep, 0, 0, SHA256.HashData(intent))
        { PublicationIntent = intent };
        var original = RemoteSignerConnection.Prepare(SignerOperations.SignSweepInput,
            descriptor.ChannelId, context).ToByteArray();
        var parsed = WireRequest.Parser.ParseFrom(original);
        var material = new byte[sizeof(uint) + parsed.Payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(material, parsed.Operation);
        parsed.Payload.Span.CopyTo(material.AsSpan(sizeof(uint)));
        var fingerprint = SHA256.HashData(material);
        var receipt = "exact-public-receipt"u8.ToArray();
        using (var first = new RemoteSigningWorkflowCoordinator(connection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>()))
        using (var workflow = await first.BeginAsync(descriptor))
        {
            workflow.Activate();
            if (receiptSaved)
                Assert.Equal(receipt, first.Execute(parsed.Operation, original, fingerprint,
                    _ => new RemoteSigningRequestStatus(RemoteSigningRequestOutcome.NotFound), _ => receipt));
            else
                Assert.Throws<IOException>(() => first.Execute(parsed.Operation, original, fingerprint,
                    _ => new RemoteSigningRequestStatus(RemoteSigningRequestOutcome.NotFound), _ =>
                    throw new IOException("Signer committed before reply was delivered.")));
        }
        using var recovered = new RemoteSigningWorkflowCoordinator(connection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        using var recovery = await recovered.BeginAsync(descriptor);
        recovery.Activate();
        var replacement = RemoteSignerConnection.Prepare(SignerOperations.SignSweepInput,
            descriptor.ChannelId, context).ToByteArray();
        Assert.NotEqual(original, replacement);
        var result = recovered.Execute(parsed.Operation, replacement, fingerprint, saved =>
        {
            Assert.Equal(original, saved);
            return new RemoteSigningRequestStatus(RemoteSigningRequestOutcome.Completed, receipt);
        }, _ => throw new InvalidOperationException("Reconciliation must not dispatch another request."));
        Assert.Equal(receipt, result);
        await harness.Alice.InScopeAsync(async uow =>
        {
            await recovery.StageConsumeAsync(uow);
            await uow.SaveChangesAsync();
            var request = Assert.Single(await uow.SigningWorkflowDbRepository.GetRequestsAsync(recovery.WorkflowId));
            Assert.Equal(original, request.Envelope);
            Assert.Equal(receipt, request.Response);
            Assert.Equal(SigningRequestState.Consumed, request.State);
            return 0;
        });
    }

    [Fact]
    public async Task SweepRejectsChangedPublicationIntentBeforeDispatch()
    {
        await using var signer = new SignerDaemonFixture(injected: true);
        await signer.InitializeAsync();
        using var connection = new RemoteSignerConnection(signer.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        using var coordinator = new RemoteSigningWorkflowCoordinator(connection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        var intent = "original"u8.ToArray();
        var descriptor = new SigningWorkflowDescriptor(new ChannelId(SHA256.HashData("sweep-test"u8)),
            SigningWorkflowKind.OnchainSweep, 0, 0, SHA256.HashData(intent))
        { PublicationIntent = intent };
        using (await coordinator.BeginAsync(descriptor)) { }
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.BeginAsync(descriptor with
        { PublicationIntent = "altered"u8.ToArray() }));
        var pending = Assert.Single(await coordinator.GetPendingAsync(descriptor.ChannelId));
        Assert.Equal(intent, pending.PublicationIntent);
    }
}