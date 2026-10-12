using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using NLightning.Application.Tests.Channels.Taproot;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Bitcoin.Wallet.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Signing.Recovery;
using NLightning.Infrastructure.RemoteSigning;
using WireRequest = NLightning.Signing.Contracts.SigningRequest;

namespace NLightning.RemoteSigning.Tests;

public sealed class WalletWorkflowRecoveryTests
{
    [Theory]
    [InlineData(RemoteSigningRequestOutcome.Unknown)]
    [InlineData(RemoteSigningRequestOutcome.Invalidated)]
    [InlineData(RemoteSigningRequestOutcome.Unsupported)]
    public async Task UncertainWithdrawalKeepsOriginalEnvelopeAndBlocksReplacement(RemoteSigningRequestOutcome outcome)
    {
        await using var signer = new SignerDaemonFixture(injected: true);
        await signer.InitializeAsync();
        using var connection = new RemoteSignerConnection(signer.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        using var coordinator = new RemoteSigningWorkflowCoordinator(connection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        var reservation = Guid.NewGuid();
        var unsigned = new SignedTransaction(new TxId(new byte[32]), [1]);
        var intent = JsonSerializer.SerializeToUtf8Bytes(new { ReservationId = reservation, UnsignedTransaction = unsigned.RawTxBytes });
        var descriptor = new SigningWorkflowDescriptor(new ChannelId(SHA256.HashData("wallet-test"u8)),
            SigningWorkflowKind.WalletWithdrawal, 0, 0, SHA256.HashData(intent))
        { PublicationIntent = intent };
        using (var workflow = await coordinator.BeginAsync(descriptor))
        {
            workflow.Activate();
            var envelope = RemoteSignerConnection.Prepare(SignerOperations.SignWalletTransaction3,
                unsigned, reservation, Array.Empty<SpentOutput>(), new WalletSnapshot([], [])).ToByteArray();
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
    public async Task RestartReplaysOriginalWithdrawalRequestAndExactReceipt(bool receiptSaved)
    {
        await using var signer = new SignerDaemonFixture(injected: true);
        await signer.InitializeAsync();
        using var connection = new RemoteSignerConnection(signer.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        var reservation = Guid.NewGuid();
        var unsigned = new SignedTransaction(new TxId(new byte[32]), [1]);
        var intent = JsonSerializer.SerializeToUtf8Bytes(new { ReservationId = reservation, UnsignedTransaction = unsigned.RawTxBytes });
        var descriptor = new SigningWorkflowDescriptor(new ChannelId(SHA256.HashData("wallet-test"u8)),
            SigningWorkflowKind.WalletWithdrawal, 0, 0, SHA256.HashData(intent))
        { PublicationIntent = intent };
        var original = RemoteSignerConnection.Prepare(SignerOperations.SignWalletTransaction3,
            unsigned, reservation, Array.Empty<SpentOutput>(), new WalletSnapshot([], [])).ToByteArray();
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
        var replacement = RemoteSignerConnection.Prepare(SignerOperations.SignWalletTransaction3,
            unsigned, reservation, Array.Empty<SpentOutput>(), new WalletSnapshot([], [])).ToByteArray();
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
    public async Task WithdrawalRejectsChangedPublicationIntentBeforeDispatch()
    {
        await using var signer = new SignerDaemonFixture(injected: true);
        await signer.InitializeAsync();
        using var connection = new RemoteSignerConnection(signer.Options());
        await using var harness = await TaprootOpenHarness.CreateAsync();
        using var coordinator = new RemoteSigningWorkflowCoordinator(connection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        var intent = "original"u8.ToArray();
        var descriptor = new SigningWorkflowDescriptor(new ChannelId(SHA256.HashData("wallet-test"u8)),
            SigningWorkflowKind.WalletWithdrawal, 0, 0, SHA256.HashData(intent))
        { PublicationIntent = intent };
        using (await coordinator.BeginAsync(descriptor)) { }
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.BeginAsync(descriptor with
        { PublicationIntent = "altered"u8.ToArray() }));
        var pending = Assert.Single(await coordinator.GetPendingAsync(descriptor.ChannelId));
        Assert.Equal(intent, pending.PublicationIntent);
    }
}