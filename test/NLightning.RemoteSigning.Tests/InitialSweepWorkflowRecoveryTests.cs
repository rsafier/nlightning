using System.Buffers.Binary;
using System.Security.Cryptography;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.RemoteSigning.Tests;

using Application.Tests.Onchain.Resolvers.Local;
using Domain.Onchain.Enums;
using Domain.Onchain.Models;
using Domain.Signing.Recovery;
using Infrastructure.Bitcoin.Builders.Interfaces;
using Infrastructure.RemoteSigning;
using WireRequest = Signing.Contracts.SigningRequest;

public sealed class InitialSweepWorkflowRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_SignerProcessRestart_When_InitialSweepReplays_Then_RegistrationAndSigningReceiptsKeepTheirIdentities(bool taproot)
    {
        // Arrange: the real signer process captures registration and signs an initial delayed-output request.
        await using var signer = new SignerDaemonFixture(injected: true);
        await signer.InitializeAsync();
        await using var database = await Application.Tests.Channels.Taproot.TaprootOpenHarness.CreateAsync();
        using var harness = new LocalCommitResolutionHarness(simpleTaproot: taproot, aliceKeyManager: signer.LocalKeys);
        await harness.ResolveAsync();
        var row = Assert.Single(harness.Rows.Values, r => r.Descriptor == OutputDescriptorKind.DelayedToLocal);
        var data = OutputDescriptorData.TryDecode(row)!;
        var unsigned = harness.GetService<ISweepTransactionBuilder>().BuildWithFee(
            [new SweepInput(row.TransactionId, row.OutputIndex, data.AmountSat, SweepSpendKind.DelayedOutput,
                data.WitnessScript, data.CsvDelay, PerCommitmentPoint: data.PerCommitmentPoint,
                TaprootControlBlock: data.TaprootControlBlock, SpentScriptPubKey: data.ScriptPubKey)], harness.Destination, 1_000);
        SigningWorkflowDescriptor descriptor;
        Guid workflowId;
        byte[][] originalEnvelopes = [];
        Domain.Crypto.ValueObjects.CompactSignature[] originalSignatures;
        byte[] originalSignedTransaction;
        using (var connection = new RemoteSignerConnection(signer.Options()))
        using (var capture = new RemoteSigningWorkflowCoordinator(connection,
            database.Alice.Services.GetRequiredService<IServiceScopeFactory>()))
        {
            var intent = capture.EncodeInitialSweepIntent(new InitialDelayedSweepIntent(unsigned, row, harness.Close,
                harness.Height, 1_000, harness.Channel.GetSigningInfo(), true));
            descriptor = new SigningWorkflowDescriptor(row.ChannelId, SigningWorkflowKind.OnchainInitialSweep,
                0, 0, SHA256.HashData(intent))
            { PublicationIntent = intent };
            using var workflow = await capture.BeginAsync(descriptor);
            workflowId = workflow.WorkflowId;
            workflow.Activate();
            originalSignatures = capture.SignSweepInputs(workflow, new RemoteLightningSigner(connection)).ToArray();
            var originalTransaction = harness.GetService<ISweepTransactionBuilder>()
                                             .AddWitnesses(unsigned, originalSignatures);
            originalSignedTransaction = originalTransaction.RawTxBytes;
            harness.AssertAllInputsVerify(Transaction.Load(originalSignedTransaction, Network.Main));
            await database.Alice.InScopeAsync(async uow =>
            {
                var requests = await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflowId);
                Assert.Equal(new[] { SignerOperations.RegisterChannel, SignerOperations.SignSweepInput },
                    requests.Select(request => request.Operation));
                originalEnvelopes = requests.Select(request => request.Envelope).ToArray();
                return 0;
            });
        }

        // Act: both client connection/capture and signer process restart against their original durable stores.
        await signer.RestartAsync();
        using var recoveredConnection = new RemoteSignerConnection(signer.Options());
        using var recovery = new RemoteSigningWorkflowCoordinator(recoveredConnection,
            database.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        using var recovered = await recovery.BeginAsync(descriptor);
        recovered.Activate();
        var signatures = recovery.SignSweepInputs(recovered, new RemoteLightningSigner(recoveredConnection));

        // Assert: original registration is restored and no replacement request or private signature is allocated.
        Assert.Equal(workflowId, recovered.WorkflowId);
        Assert.Equal(originalSignatures, signatures);
        var recoveredTransaction = harness.GetService<ISweepTransactionBuilder>().AddWitnesses(unsigned, signatures);
        Assert.Equal(originalSignedTransaction, recoveredTransaction.RawTxBytes);
        harness.AssertAllInputsVerify(Transaction.Load(recoveredTransaction.RawTxBytes, Network.Main));
        await database.Alice.InScopeAsync(async uow =>
        {
            await recovered.StageConsumeAsync(uow);
            await uow.SaveChangesAsync();
            var requests = await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflowId);
            Assert.Equal(originalEnvelopes.Length, requests.Count);
            for (var i = 0; i < requests.Count; i++)
            {
                Assert.Equal(originalEnvelopes[i], requests[i].Envelope);
                Assert.Equal(SigningRequestState.Consumed, requests[i].State);
            }
            return 0;
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_InitialSweep_When_IntentRoundTrips_Then_AllWitnessAndSigningPrerequisitesArePreserved(bool taproot)
    {
        // Arrange: real delayed-output scripts and contexts, encoded by the native recovery coordinator.
        await using var signer = new SignerDaemonFixture(injected: true);
        await signer.InitializeAsync();
        using var connection = new RemoteSignerConnection(signer.Options());
        await using var database = await Application.Tests.Channels.Taproot.TaprootOpenHarness.CreateAsync();
        using var coordinator = new RemoteSigningWorkflowCoordinator(connection,
            database.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        using var harness = new LocalCommitResolutionHarness(simpleTaproot: taproot);
        await harness.ResolveAsync();
        var row = Assert.Single(harness.Rows.Values, r => r.Descriptor == OutputDescriptorKind.DelayedToLocal);
        var data = OutputDescriptorData.TryDecode(row)!;
        var unsigned = harness.GetService<ISweepTransactionBuilder>().BuildWithFee(
            [new SweepInput(row.TransactionId, row.OutputIndex, data.AmountSat, SweepSpendKind.DelayedOutput,
                data.WitnessScript, data.CsvDelay, PerCommitmentPoint: data.PerCommitmentPoint,
                TaprootControlBlock: data.TaprootControlBlock, SpentScriptPubKey: data.ScriptPubKey)], harness.Destination, 1_000);
        var original = new InitialDelayedSweepIntent(unsigned, row, harness.Close, harness.Height, 1_000, harness.Channel.GetSigningInfo(), true);

        // Act
        var encoded = coordinator.EncodeInitialSweepIntent(original);
        var restored = coordinator.DecodeInitialSweepIntent(encoded);

        // Assert: the full unsigned bytes, witness data, close and output snapshot remain byte exact.
        Assert.Equal(SignerWire.Encode([original]), SignerWire.Encode([restored]));
        Assert.Equal(original.Transaction.Transaction.RawTxBytes, restored.Transaction.Transaction.RawTxBytes);
        Assert.Equal(original.Transaction.DestinationScript, restored.Transaction.DestinationScript);
        Assert.Equal(original.Transaction.FeeSat, restored.Transaction.FeeSat);
        Assert.Equal(SignerWire.Encode([original.Transaction.GetSigningContext(0)]),
            SignerWire.Encode([restored.Transaction.GetSigningContext(0)]));
    }

    [Theory]
    [InlineData(RemoteSigningRequestOutcome.Unknown)]
    [InlineData(RemoteSigningRequestOutcome.Invalidated)]
    [InlineData(RemoteSigningRequestOutcome.Unsupported)]
    public async Task Given_UncertainInitialRequest_When_Reconciled_Then_OriginalIntentAndEnvelopeRemainBlocked(
        RemoteSigningRequestOutcome outcome)
    {
        // Arrange: real output data, with a synthetic receipt checkpoint over the node's actual SQLite repository.
        await using var signer = new SignerDaemonFixture(injected: true);
        await signer.InitializeAsync();
        using var connection = new RemoteSignerConnection(signer.Options());
        await using var database = await Application.Tests.Channels.Taproot.TaprootOpenHarness.CreateAsync();
        using var coordinator = new RemoteSigningWorkflowCoordinator(connection,
            database.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        using var harness = new LocalCommitResolutionHarness();
        await harness.ResolveAsync();
        var row = Assert.Single(harness.Rows.Values, r => r.Descriptor == OutputDescriptorKind.DelayedToLocal);
        var data = OutputDescriptorData.TryDecode(row)!;
        var unsigned = harness.GetService<ISweepTransactionBuilder>().BuildWithFee(
            [new SweepInput(row.TransactionId, row.OutputIndex, data.AmountSat, SweepSpendKind.DelayedOutput,
                data.WitnessScript, data.CsvDelay, PerCommitmentPoint: data.PerCommitmentPoint)], harness.Destination, 1_000);
        var encoded = coordinator.EncodeInitialSweepIntent(new InitialDelayedSweepIntent(unsigned, row,
            harness.Close, harness.Height, 1_000, harness.Channel.GetSigningInfo(), true));
        var descriptor = new SigningWorkflowDescriptor(row.ChannelId, SigningWorkflowKind.OnchainInitialSweep,
            0, 0, SHA256.HashData(encoded))
        { PublicationIntent = encoded };
        Guid workflowId;
        var envelope = RemoteSignerConnection.Prepare(SignerOperations.SignSweepInput, row.ChannelId,
            unsigned.GetSigningContext(0)).ToByteArray();
        var parsed = WireRequest.Parser.ParseFrom(envelope);
        var material = new byte[sizeof(uint) + parsed.Payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(material, parsed.Operation);
        parsed.Payload.Span.CopyTo(material.AsSpan(sizeof(uint)));

        // Act / Assert: an uncertain checkpoint never dispatches a new private operation.
        using (var workflow = await coordinator.BeginAsync(descriptor))
        {
            workflowId = workflow.WorkflowId;
            workflow.Activate();
            Assert.Throws<RemoteSigningWorkflowException>(() => coordinator.Execute(parsed.Operation, envelope,
                SHA256.HashData(material), _ => new RemoteSigningRequestStatus(outcome),
                _ => throw new InvalidOperationException("An uncertain request must never dispatch.")));
        }
        await Assert.ThrowsAsync<RemoteSigningWorkflowException>(() => coordinator.BeginAsync(descriptor));
        await database.Alice.InScopeAsync(async uow =>
        {
            var saved = await uow.SigningWorkflowDbRepository.GetAsync(workflowId);
            Assert.Equal(SigningWorkflowState.Blocked, saved!.State);
            Assert.Equal(encoded, saved.PublicationIntent);
            var request = Assert.Single(await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflowId));
            Assert.Equal(envelope, request.Envelope);
            Assert.Equal(SigningRequestState.Blocked, request.State);
            return 0;
        });
    }
}