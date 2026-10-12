using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Exceptions;
using Domain.Node.Options;
using Domain.Onchain.Models;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Domain.Signing.Recovery;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Signers;
using Infrastructure.Bitcoin.Wallet;

public partial class WalletPsbtServiceTests
{
    [Fact]
    public async Task NativeSendOutputsSavesBroadcastAndConsumesBeforePublication()
    {
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var coordinator = new PublicationCoordinator();
        var events = new List<string>();
        _broadcasts.Setup(r => r.Add(It.IsAny<BroadcastTransactionModel>()))
            .Callback<BroadcastTransactionModel>(row => { _published.Add(row); events.Add("row"); });
        _unitOfWork.Setup(u => u.SaveChangesAsync()).Callback(() => events.Add("save")).Returns(Task.CompletedTask);
        coordinator.OnConsume = () => events.Add("consume");
        _monitor.Setup(m => m.PublishAsync(It.IsAny<BroadcastTransactionModel>()))
            .Callback<BroadcastTransactionModel>(_ =>
            {
                Assert.Equal(new[] { "row", "consume", "save" }, events.TakeLast(3));
                events.Add("publish");
            }).ReturnsAsync(true);
        using var native = NativePublicationService(coordinator);
        var raw = await native.SendOutputsCapturedAsync(Request(40_000).Outputs, 1000, 1, "native outputs", Ct);
        var row = Assert.Single(_published);
        Assert.Equal("native outputs", row.Label);
        Assert.Equal(raw, row.RawTransaction);
        Assert.True(coordinator.Activated);
        Assert.Single(coordinator.Intents);
        var intent = coordinator.Intents[0];
        Assert.Equal(Assert.Single(_stored).Id, Assert.Single(intent.ReservationIds));
        Assert.Equal(2, Transaction.Load(raw, Network.RegTest).Outputs.Count);
    }

    [Fact]
    public async Task NativePendingIntentKeepsExpiredLeaseAndRejectsReleaseAndRenewal()
    {
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var funded = await _service.FundPsbtAsync(Request(40_000), Ct);
        var held = Assert.Single(_stored);
        var coordinator = new PublicationCoordinator();
        coordinator.Seed(funded, held.Id, SigningWorkflowState.Blocked);
        using var native = NativePublicationService(coordinator);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.NotEmpty(await native.ListLeasesAsync(Ct));
        Assert.Equal(held.Id, Assert.Single(_stored).Id);
        var lease = funded.Leases[0];
        Assert.Equal(WalletPsbtError.FailedPrecondition,
            (await Assert.ThrowsAsync<WalletPsbtException>(() => native.ReleaseAsync(lease.LockId, lease.TxId, lease.Index, Ct))).Error);
        Assert.Equal(WalletPsbtError.FailedPrecondition,
            (await Assert.ThrowsAsync<WalletPsbtException>(() => native.LeaseAsync(lease.LockId, lease.TxId, lease.Index, TimeSpan.FromHours(1), Ct))).Error);
        await Assert.ThrowsAsync<InvalidOperationException>(() => native.RecoverPublicationsAsync(Ct));
        Assert.Equal(held.Id, Assert.Single(_stored).Id);
        Assert.Empty(_published);
    }

    [Fact]
    public async Task NativePublicationSaveFailureNeverCallsCore()
    {
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var funded = await _service.FundPsbtAsync(Request(40_000), Ct);
        var final = await _service.FinalizePsbtAsync(funded.Psbt, Ct);
        var coordinator = new PublicationCoordinator();
        using var native = NativePublicationService(coordinator);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).ThrowsAsync(new InvalidOperationException("save failed"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => native.PublishAsync(final.RawFinalTx, "native", Ct));
        _chain.Verify(c => c.SendTransactionAsync(It.IsAny<Transaction>()), Times.Never);
        Assert.NotEmpty(_stored);
    }

    [Fact]
    public async Task NativePublicationRefusalRetainsExactBroadcastAndLease()
    {
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var funded = await _service.FundPsbtAsync(Request(40_000), Ct);
        var final = await _service.FinalizePsbtAsync(funded.Psbt, Ct);
        var coordinator = new PublicationCoordinator();
        using var native = NativePublicationService(coordinator);
        _broadcasts.Setup(r => r.Add(It.IsAny<BroadcastTransactionModel>())).Callback<BroadcastTransactionModel>(_published.Add);
        _chain.Setup(c => c.SendTransactionAsync(It.IsAny<Transaction>()))
            .ThrowsAsync(new NBitcoin.RPC.RPCException(NBitcoin.RPC.RPCErrorCode.RPC_VERIFY_REJECTED,
                "txn-mempool-conflict", null!));
        var error = await Assert.ThrowsAsync<WalletPsbtException>(() => native.PublishAsync(final.RawFinalTx, "native", Ct));
        Assert.Equal(WalletPsbtError.PublishRefused, error.Error);
        Assert.Equal(final.RawFinalTx, Assert.Single(_published).RawTransaction);
        _time.Advance(TimeSpan.FromHours(1));
        Assert.NotEmpty(await native.ListLeasesAsync(Ct));
        Assert.Single(_stored);
    }

    [Fact]
    public async Task NativeDuplicatePublicationRetainsOriginalBroadcastMetadata()
    {
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        using var native = NativePublicationService(new PublicationCoordinator());
        var funded = await native.FundPsbtAsync(Request(40_000), Ct);
        var final = await native.FinalizePsbtAsync(funded.Psbt, Ct);
        var tx = Transaction.Load(final.RawFinalTx, Network.RegTest);
        var original = new BroadcastTransactionModel(
            new SignedTransaction(new Domain.Bitcoin.ValueObjects.TxId(tx.GetHash().ToBytes()), final.RawFinalTx),
            Domain.Onchain.Enums.BroadcastPurpose.WalletSend, null, 42, 777,
            fee: Domain.Money.LightningMoney.Satoshis(999))
        { Label = "original", Tags = "origin=first" };
        _broadcasts.Setup(r => r.GetByTransactionIdAsync(original.TransactionId)).ReturnsAsync(original);
        Assert.True(await native.PublishAsync(final.RawFinalTx, "later label", Ct));
        _broadcasts.Verify(r => r.Add(It.IsAny<BroadcastTransactionModel>()), Times.Never);
        Assert.Equal("original", original.Label);
        Assert.Equal("origin=first", original.Tags);
        Assert.Equal(42U, original.FirstBroadcastHeight);
        Assert.Equal(777U, original.FeeratePerKw);
        Assert.Equal(999, original.Fee!.Satoshi);
        _chain.Verify(c => c.SendTransactionAsync(It.Is<Transaction>(sent => sent.ToBytes().SequenceEqual(final.RawFinalTx))), Times.Once);
    }

    [Fact]
    public async Task NativeIntentOnlyRecoveryUsesOriginalPsbtAndReservation()
    {
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var funded = await _service.FundPsbtAsync(Request(40_000), Ct);
        var reservation = Assert.Single(_stored);
        var coordinator = new PublicationCoordinator();
        coordinator.Seed(funded, reservation.Id, SigningWorkflowState.Pending);
        _broadcasts.Setup(r => r.Add(It.IsAny<BroadcastTransactionModel>())).Callback<BroadcastTransactionModel>(_published.Add);
        _monitor.Setup(m => m.PublishAsync(It.IsAny<BroadcastTransactionModel>())).ReturnsAsync(true);
        using var native = NativePublicationService(coordinator);
        await native.RecoverPublicationsAsync(Ct);
        var row = Assert.Single(_published);
        Assert.Equal(PSBT.Load(funded.Psbt, Network.RegTest).GetGlobalTransaction().GetHash().ToBytes(), (byte[])row.TransactionId);
        Assert.Equal(reservation.Id, Assert.Single(_stored).Id);
        Assert.Equal("recovered outputs", row.Label);
        Assert.Equal(1, coordinator.Replays);
    }

    [Theory]
    [InlineData(SigningWorkflowState.Pending, false)]
    [InlineData(SigningWorkflowState.Pending, true)]
    [InlineData(SigningWorkflowState.Blocked, false)]
    [InlineData(SigningWorkflowState.Blocked, true)]
    public async Task UnconsumedPublicationRejectsStandaloneSigningAndPublicationWithoutKeyOrCoreUse(
        SigningWorkflowState state, bool alterDestination)
    {
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var funded = await _service.FundPsbtAsync(Request(40_000), Ct);
        var signed = await _service.FinalizePsbtAsync(funded.Psbt, Ct);
        var held = Assert.Single(_stored);
        var coordinator = new PublicationCoordinator();
        coordinator.Seed(funded, held.Id, state);
        using var native = NativePublicationService(coordinator);
        var packet = PSBT.Load(funded.Psbt, Network.RegTest);
        var tx = packet.GetGlobalTransaction();
        var raw = signed.RawFinalTx;
        if (alterDestination)
        {
            tx.Outputs[0].Value -= Money.Satoshis(1);
            var changed = PSBT.FromTransaction(tx, Network.RegTest);
            for (var index = 0; index < changed.Inputs.Count; index++)
                changed.Inputs[index].WitnessUtxo = packet.Inputs[index].WitnessUtxo;
            packet = changed;
            var final = Transaction.Load(raw, Network.RegTest);
            final.Outputs[0].Value -= Money.Satoshis(1);
            raw = final.ToBytes();
        }
        _keyManager.Invocations.Clear();
        await Assert.ThrowsAsync<WalletPsbtException>(() => native.SignPsbtAsync(packet.ToBytes(), Ct));
        await Assert.ThrowsAsync<WalletPsbtException>(() => native.FinalizePsbtAsync(packet.ToBytes(), Ct));
        await Assert.ThrowsAsync<WalletPsbtException>(() => native.PublishAsync(raw, "conflicting", Ct));
        _keyManager.Verify(k => k.GetDepositP2WpkhKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()), Times.Never);
        _keyManager.Verify(k => k.GetDepositP2TrKeyAtIndex(It.IsAny<uint>(), It.IsAny<bool>()), Times.Never);
        _chain.Verify(c => c.SendTransactionAsync(It.IsAny<Transaction>()), Times.Never);
        _monitor.Verify(m => m.PublishAsync(It.IsAny<BroadcastTransactionModel>()), Times.Never);
        _broadcasts.Verify(r => r.Add(It.IsAny<BroadcastTransactionModel>()), Times.Never);
        Assert.Equal(held.Id, Assert.Single(_stored).Id);
        Assert.Equal(0, coordinator.Replays);
    }

    private WalletPsbtService NativePublicationService(PublicationCoordinator coordinator)
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _unitOfWork.Object);
        services.AddScoped(_ => _walletService.Object);
        var options = Microsoft.Extensions.Options.Options.Create(new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest });
        var signer = new LocalLightningSigner(Mock.Of<IFundingOutputBuilder>(), Mock.Of<IKeyDerivationService>(),
            NullLogger<LocalLightningSigner>.Instance, options.Value, new SilentPaymentTestKeys(_keyManager.Object), _utxos);
        return new WalletPsbtService(_selector, _anchorReserve.Object, _utxos, signer, _monitor.Object,
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(), options, _logger,
            _chain.Object, _time, _keyManager.Object, signingWorkflows: coordinator);
    }

    private sealed class PublicationCoordinator : IRemoteSigningWorkflowCoordinator, INativeWalletPsbtSigningRecovery
    {
        private readonly List<SigningWorkflow> _pending = [];
        public List<NativeWalletPsbtPublicationIntent> Intents { get; } = [];
        public Action? OnConsume { get; set; }
        public bool Activated { get; private set; }
        public int Replays { get; private set; }
        public Task<IReadOnlyList<SigningWorkflow>> GetPendingAsync(ChannelId id) => Task.FromResult<IReadOnlyList<SigningWorkflow>>(_pending.ToArray());
        public Task StageAsync(SigningWorkflowDescriptor descriptor, IUnitOfWork uow) => throw new NotSupportedException();
        public Task<ISigningWorkflowScope> BeginAsync(SigningWorkflowDescriptor descriptor)
        {
            if (_pending.Count == 0)
            {
                Intents.Add(JsonSerializer.Deserialize<NativeWalletPsbtPublicationIntent>(descriptor.PublicationIntent!)!);
                _pending.Add(new SigningWorkflow(Guid.NewGuid(), descriptor.ChannelId, descriptor.Kind, 0, 0,
                    descriptor.SnapshotFingerprint, [], "regtest", 1, SigningWorkflowState.Pending, 0, 0)
                { PublicationIntent = descriptor.PublicationIntent });
            }
            return Task.FromResult<ISigningWorkflowScope>(new PublicationScope(this, _pending[0].WorkflowId));
        }
        public SignedTransaction? ReplayPsbtPublication(ISigningWorkflowScope workflow) { Replays++; return null; }
        public void Seed(PsbtFundResult funded, Guid reservationId, SigningWorkflowState state)
        {
            var tx = PSBT.Load(funded.Psbt, Network.RegTest).GetGlobalTransaction();
            var intent = new NativeWalletPsbtPublicationIntent(funded.Psbt, tx.ToBytes(), [reservationId], funded.Fee.Satoshi,
                1000, Height, "recovered outputs");
            var encoded = JsonSerializer.SerializeToUtf8Bytes(intent);
            _pending.Add(new SigningWorkflow(Guid.NewGuid(), new ChannelId(new byte[32]), SigningWorkflowKind.WalletPsbtPublication,
                0, 0, System.Security.Cryptography.SHA256.HashData(encoded), [], "regtest", 1, state, 0, 0)
            { PublicationIntent = encoded });
        }
        private sealed class PublicationScope(PublicationCoordinator owner, Guid id) : ISigningWorkflowScope
        {
            public Guid WorkflowId => id;
            public void Activate() => owner.Activated = true;
            public Task StageConsumeAsync(IUnitOfWork uow) { owner.OnConsume?.Invoke(); return Task.CompletedTask; }
            public void Dispose() { }
        }
    }
}