using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using NBitcoin;
using NLightning.Application.Tests.Channels.Taproot;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Bitcoin.Wallet.Models;
using NLightning.Domain.Channels.ValueObjects;
using NLightning.Domain.Money;
using NLightning.Domain.Onchain.Enums;
using NLightning.Domain.Onchain.Models;
using NLightning.Domain.Signing.Recovery;
using NLightning.Infrastructure.RemoteSigning;
using SignedTransaction = NLightning.Domain.Bitcoin.ValueObjects.SignedTransaction;
using TxId = NLightning.Domain.Bitcoin.ValueObjects.TxId;
using WireRequest = NLightning.Signing.Contracts.SigningRequest;

namespace NLightning.RemoteSigning.Tests;

public sealed class PsbtPublicationWorkflowRecoveryTests
{
    [Theory]
    [InlineData(AddressType.P2Wpkh, false)]
    [InlineData(AddressType.P2Wpkh, true)]
    [InlineData(AddressType.P2Tr, false)]
    [InlineData(AddressType.P2Tr, true)]
    public async Task Given_CommittedPsbtReceipt_When_NodeAndSignerRestart_Then_OriginalEnvelopeAndSignedBytesAreConsumedWithPublication(AddressType type,
                                                                                                                                    bool receiptSaved)
    {
        // Arrange
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        await using var harness = await TaprootOpenHarness.CreateAsync();
        using var firstConnection = new RemoteSignerConnection(daemon.Options());
        var fixture = new Publication(firstConnection, daemon, type);
        byte[]? receipt = null;
        Guid workflowId;
        using (var coordinator = new RemoteSigningWorkflowCoordinator(firstConnection,
                   harness.Alice.Services.GetRequiredService<IServiceScopeFactory>()))
        using (var workflow = await coordinator.BeginAsync(fixture.Descriptor))
        {
            workflowId = workflow.WorkflowId;
            workflow.Activate();
            byte[] Dispatch(byte[] saved)
            {
                Assert.Equal(fixture.Envelope, saved);
                receipt = firstConnection.ExecutePayload(WireRequest.Parser.ParseFrom(saved));
                if (!receiptSaved) throw new IOException("Signer committed before its reply reached the node.");
                return receipt;
            }
            if (receiptSaved)
            {
                var response = coordinator.Execute(SignerOperations.SignWalletTransaction2, fixture.Envelope,
                    fixture.Fingerprint, _ => new RemoteSigningRequestStatus(RemoteSigningRequestOutcome.NotFound), Dispatch);
                Assert.Equal(receipt, response);
            }
            else
                Assert.Throws<IOException>(() => coordinator.Execute(SignerOperations.SignWalletTransaction2,
                    fixture.Envelope, fixture.Fingerprint,
                    _ => new RemoteSigningRequestStatus(RemoteSigningRequestOutcome.NotFound), Dispatch));
        }
        Assert.NotNull(receipt);
        await daemon.RestartAsync();

        // Act
        using var recoveredConnection = new RemoteSignerConnection(daemon.Options());
        using var recovered = new RemoteSigningWorkflowCoordinator(recoveredConnection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        using var recovery = await recovered.BeginAsync(fixture.Descriptor);
        recovery.Activate();
        var signed = Assert.IsType<SignedTransaction>(recovered.ReplayPsbtPublication(recovery));
        Assert.Equal(fixture.Signed(receipt!).RawTxBytes, signed.RawTxBytes);
        await harness.Alice.InScopeAsync(async uow =>
        {
            uow.BroadcastTransactionDbRepository.Add(new BroadcastTransactionModel(signed, BroadcastPurpose.WalletSend,
                null, fixture.Intent.Height, fixture.Intent.FeeRatePerKw, fee: LightningMoney.Satoshis(fixture.Intent.FeeSat))
            { Label = fixture.Intent.Label });
            await recovery.StageConsumeAsync(uow);
            await uow.SaveChangesAsync();
            return 0;
        });

        // Assert
        await harness.Alice.InScopeAsync(async uow =>
        {
            var request = Assert.Single(await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflowId));
            Assert.Equal(fixture.Envelope, request.Envelope);
            Assert.Equal(fixture.Fingerprint, request.ArgumentFingerprint);
            Assert.Equal(receipt, request.Response);
            Assert.Equal(SigningRequestState.Consumed, request.State);
            Assert.Equal(SigningWorkflowState.Consumed, (await uow.SigningWorkflowDbRepository.GetAsync(workflowId))!.State);
            var publication = await uow.BroadcastTransactionDbRepository.GetByTransactionIdAsync(signed.TxId);
            Assert.Equal(signed.RawTxBytes, publication!.RawTransaction);
            Assert.Equal(fixture.Intent.Label, publication.Label);
            return 0;
        });
        var tx = Transaction.Load(signed.RawTxBytes, Network.RegTest);
        Assert.True(tx.CreateValidator([fixture.Previous]).ValidateInput(0).Error is null or ScriptError.OK);
    }

    [Theory]
    [InlineData(RemoteSigningRequestOutcome.Unknown)]
    [InlineData(RemoteSigningRequestOutcome.Invalidated)]
    [InlineData(RemoteSigningRequestOutcome.Unsupported)]
    public async Task Given_UncertainPsbtRequest_When_Reconciling_Then_OriginalEnvelopeIsBlockedWithoutDispatch(RemoteSigningRequestOutcome outcome)
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        await using var harness = await TaprootOpenHarness.CreateAsync();
        using var connection = new RemoteSignerConnection(daemon.Options());
        var fixture = new Publication(connection, daemon, AddressType.P2Wpkh);
        using var coordinator = new RemoteSigningWorkflowCoordinator(connection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        using (var workflow = await coordinator.BeginAsync(fixture.Descriptor))
        {
            workflow.Activate();
            var calls = 0;
            Assert.Throws<RemoteSigningWorkflowException>(() => coordinator.Execute(SignerOperations.SignWalletTransaction2,
                fixture.Envelope, fixture.Fingerprint, _ => new RemoteSigningRequestStatus(outcome), _ =>
                { calls++; throw new Exception("An uncertain request must not dispatch."); }));
            Assert.Equal(0, calls);
            await harness.Alice.InScopeAsync(async uow =>
            {
                var request = Assert.Single(await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflow.WorkflowId));
                Assert.Equal(fixture.Envelope, request.Envelope);
                Assert.Equal(SigningRequestState.Blocked, request.State);
                Assert.Equal(fixture.Descriptor.PublicationIntent,
                    (await uow.SigningWorkflowDbRepository.GetAsync(workflow.WorkflowId))!.PublicationIntent);
                return 0;
            });
        }
        await Assert.ThrowsAsync<RemoteSigningWorkflowException>(() => coordinator.BeginAsync(fixture.Descriptor));
    }

    [Theory]
    [InlineData("unsigned")]
    [InlineData("reservation")]
    [InlineData("amount")]
    [InlineData("script")]
    [InlineData("external")]
    public async Task Given_FrozenPsbtIntent_When_RequestChangesFrozenPrerequisites_Then_NoRequestIsDispatched(string attack)
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        await using var harness = await TaprootOpenHarness.CreateAsync();
        using var connection = new RemoteSignerConnection(daemon.Options());
        var fixture = new Publication(connection, daemon, AddressType.P2Wpkh);
        using var coordinator = new RemoteSigningWorkflowCoordinator(connection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        using var workflow = await coordinator.BeginAsync(fixture.Descriptor);
        workflow.Activate();
        var args = SignerWire.Decode(WireRequest.Parser.ParseFrom(fixture.Envelope).Payload.ToByteArray());
        var unsigned = SignerWire.Read<SignedTransaction>(args[0]);
        var snapshot = SignerWire.Read<WalletSnapshot>(args[2]);
        if (attack == "unsigned")
        {
            var tx = Transaction.Load(unsigned.RawTxBytes, Network.RegTest);
            tx.Outputs[0].Value -= Money.Satoshis(1);
            unsigned = new SignedTransaction(new TxId(tx.GetHash().ToBytes()), tx.ToBytes());
        }
        if (attack == "reservation") snapshot = snapshot with
        { Reservations = [snapshot.Reservations[0] with { ReservationId = Guid.NewGuid() }] };
        if (attack is "amount" or "script") snapshot = snapshot with
        {
            Utxos = [new UtxoModel(snapshot.Utxos[0].TxId, 0, LightningMoney.Satoshis(attack == "amount" ? 99_999 : 100_000),
                100, attack == "script" ? new WalletAddressModel(AddressType.P2Wpkh, 0, false,
                    new Script(fixture.Destination).GetDestinationAddress(Network.RegTest)!.ToString())
                    : snapshot.Utxos[0].WalletAddress!)]
        };
        var external = attack == "external"
            ? new[] { new SpentOutput(snapshot.Utxos[0].TxId, 0, LightningMoney.Satoshis(100_000), fixture.Previous.ScriptPubKey.ToBytes()) }
            : [];
        var changed = connection.PrepareForContext(SignerOperations.SignWalletTransaction2, unsigned, external, snapshot).ToByteArray();
        var calls = 0;
        Assert.Throws<RemoteSigningWorkflowException>(() =>
        {
            _ = coordinator.Execute(SignerOperations.SignWalletTransaction2, changed, Fingerprint(changed),
                _ => new RemoteSigningRequestStatus(RemoteSigningRequestOutcome.NotFound), _ =>
                { calls++; throw new Exception("Changed prerequisites must not dispatch."); });
        });
        Assert.Equal(0, calls);
        await harness.Alice.InScopeAsync(async uow =>
        {
            Assert.Empty(await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflow.WorkflowId));
            return 0;
        });
    }

    [Fact]
    public async Task Given_IntentSavedBeforeRequest_When_Resuming_Then_NoFreshSigningEnvelopeIsCreated()
    {
        await using var daemon = new SignerDaemonFixture(injected: true);
        await daemon.InitializeAsync();
        await using var harness = await TaprootOpenHarness.CreateAsync();
        using var connection = new RemoteSignerConnection(daemon.Options());
        var fixture = new Publication(connection, daemon, AddressType.P2Wpkh);
        using var coordinator = new RemoteSigningWorkflowCoordinator(connection,
            harness.Alice.Services.GetRequiredService<IServiceScopeFactory>());
        using var workflow = await coordinator.BeginAsync(fixture.Descriptor);
        workflow.Activate();
        Assert.Null(coordinator.ReplayPsbtPublication(workflow));
        await harness.Alice.InScopeAsync(async uow =>
        {
            Assert.Empty(await uow.SigningWorkflowDbRepository.GetRequestsAsync(workflow.WorkflowId));
            return 0;
        });
    }

    private static byte[] Fingerprint(byte[] envelope)
    {
        var request = WireRequest.Parser.ParseFrom(envelope);
        var material = new byte[sizeof(uint) + request.Payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(material, request.Operation);
        request.Payload.Span.CopyTo(material.AsSpan(sizeof(uint)));
        return SHA256.HashData(material);
    }

    private sealed class Publication
    {
        public NativeWalletPsbtPublicationIntent Intent { get; }
        public SigningWorkflowDescriptor Descriptor { get; }
        public byte[] Envelope { get; }
        public byte[] Fingerprint { get; }
        public TxOut Previous { get; }
        public byte[] Destination { get; } = Script.FromHex("0014" + new string('2', 40)).ToBytes();

        public Publication(RemoteSignerConnection connection, SignerDaemonFixture daemon, AddressType type)
        {
            var key = new PubKey((byte[])daemon.LocalKeys.GetWalletPublicKey(0, false, type));
            var script = type == AddressType.P2Tr ? key.GetTaprootFullPubKey().ScriptPubKey : key.WitHash.ScriptPubKey;
            Previous = new TxOut(Money.Satoshis(100_000), script);
            var tx = Transaction.Create(Network.RegTest);
            tx.Inputs.Add(new TxIn(new OutPoint(uint256.Parse(new string('a', 64)), 0)));
            tx.Outputs.Add(new TxOut(Money.Satoshis(99_000), new Script(Destination)));
            var psbt = PSBT.FromTransaction(tx, Network.RegTest);
            psbt.Inputs[0].WitnessUtxo = Previous;
            var reservation = Guid.NewGuid();
            Intent = new NativeWalletPsbtPublicationIntent(psbt.ToBytes(), tx.ToBytes(), [reservation], 1000, 2500, 200,
                "original-psbt-publication");
            var encoded = JsonSerializer.SerializeToUtf8Bytes(Intent);
            Descriptor = new SigningWorkflowDescriptor(new ChannelId(SHA256.HashData("wallet-psbt-test"u8)),
                SigningWorkflowKind.WalletPsbtPublication, 0, 0, SHA256.HashData(encoded))
            { PublicationIntent = encoded };
            var txid = new TxId(tx.Inputs[0].PrevOut.Hash.ToBytes());
            var address = new WalletAddressModel(type, 0, false, script.GetDestinationAddress(Network.RegTest)!.ToString());
            var snapshot = new WalletSnapshot([new UtxoModel(txid, 0, LightningMoney.Satoshis(100_000), 100, address)],
                [new FeeReservation(txid, 0, reservation)]);
            Envelope = connection.PrepareForContext(SignerOperations.SignWalletTransaction2,
                new SignedTransaction(new TxId(tx.GetHash().ToBytes()), tx.ToBytes()), Array.Empty<SpentOutput>(), snapshot).ToByteArray();
            Fingerprint = PsbtPublicationWorkflowRecoveryTests.Fingerprint(Envelope);
        }

        public SignedTransaction Signed(byte[] receipt) => SignerWire.Read<SignedTransaction>(SignerWire.Decode(receipt)[1]);
    }
}