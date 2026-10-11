using Microsoft.Extensions.Logging.Abstractions;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Signing.Recovery;
using NLightning.Infrastructure.Bitcoin.Wallet;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

public partial class WalletSpendServiceTests
{
    [Fact]
    public async Task NativeWithdrawalConsumesReceiptWithBroadcastBeforePublication()
    {
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var coordinator = new Mock<IRemoteSigningWorkflowCoordinator>();
        coordinator.As<INativeWalletSigningRecovery>();
        coordinator.Setup(c => c.GetPendingAsync(It.IsAny<NLightning.Domain.Channels.ValueObjects.ChannelId>()))
            .ReturnsAsync(Array.Empty<SigningWorkflow>());
        var workflow = new Mock<ISigningWorkflowScope>();
        SigningWorkflowDescriptor? descriptor = null;
        coordinator.Setup(c => c.BeginAsync(It.IsAny<SigningWorkflowDescriptor>()))
            .Callback<SigningWorkflowDescriptor>(value => descriptor = value).ReturnsAsync(workflow.Object);
        var workflows = new Mock<ISigningWorkflowDbRepository>();
        _unitOfWork.Setup(u => u.SigningWorkflowDbRepository).Returns(workflows.Object);
        workflows.Setup(w => w.GetAsync(It.IsAny<Guid>())).ReturnsAsync(() => new SigningWorkflow(Guid.NewGuid(),
            descriptor!.ChannelId, descriptor.Kind, 0, 0, descriptor.SnapshotFingerprint, new byte[33], "regtest", 1,
            SigningWorkflowState.Pending, 0, 0)
        { PublicationIntent = descriptor.PublicationIntent });
        var events = new List<string>();
        _broadcasts.Setup(b => b.Add(It.IsAny<NLightning.Domain.Onchain.Models.BroadcastTransactionModel>()))
            .Callback(() => events.Add("broadcast"));
        workflow.Setup(w => w.StageConsumeAsync(_unitOfWork.Object)).Callback(() => events.Add("consume"))
            .Returns(Task.CompletedTask);
        _unitOfWork.Setup(u => u.SaveChangesAsync()).Callback(() => events.Add("save")).Returns(Task.CompletedTask);
        _monitor.Setup(m => m.PublishAsync(It.IsAny<NLightning.Domain.Onchain.Models.BroadcastTransactionModel>()))
            .Callback(() => events.Add("publish")).ReturnsAsync(true);
        var service = new WalletSpendService(_selector, _anchorReserve.Object, _utxos, _signer, _monitor.Object,
            _feeService.Object, _scopeFactory, Microsoft.Extensions.Options.Options.Create(_nodeOptions), NullLogger<WalletSpendService>.Instance,
            signingWorkflows: coordinator.Object);
        await service.WithdrawAsync(Request(40_000), TestContext.Current.CancellationToken);
        Assert.Equal(new[] { "broadcast", "consume", "save", "publish" }, events.TakeLast(4));
        workflow.Verify(w => w.Activate(), Times.Once());
        workflow.Verify(w => w.Dispose(), Times.Once());
        _monitor.Verify(m => m.SaveAndPublishAsync(It.IsAny<NLightning.Domain.Onchain.Models.BroadcastTransactionModel>()), Times.Never());
    }

    [Fact]
    public async Task LostWithdrawalReplyRetainsInputsAndRecoveryFailurePreventsOrphanRelease()
    {
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var coordinator = new Mock<IRemoteSigningWorkflowCoordinator>();
        coordinator.As<INativeWalletSigningRecovery>();
        coordinator.Setup(c => c.GetPendingAsync(It.IsAny<NLightning.Domain.Channels.ValueObjects.ChannelId>()))
            .ReturnsAsync(Array.Empty<SigningWorkflow>());
        var workflow = new Mock<ISigningWorkflowScope>();
        coordinator.Setup(c => c.BeginAsync(It.IsAny<SigningWorkflowDescriptor>())).ReturnsAsync(workflow.Object);
        var signer = new Mock<ILightningSigner>();
        signer.Setup(s => s.SignWalletTransaction(It.IsAny<SignedTransaction>(), It.IsAny<Guid>(),
            It.IsAny<IReadOnlyList<NLightning.Domain.Bitcoin.Wallet.Models.SpentOutput>>()))
            .Throws(new IOException("Signing reply was lost."));
        var service = new WalletSpendService(_selector, _anchorReserve.Object, _utxos, signer.Object, _monitor.Object,
            _feeService.Object, _scopeFactory, Microsoft.Extensions.Options.Options.Create(_nodeOptions), NullLogger<WalletSpendService>.Instance,
            signingWorkflows: coordinator.Object);
        await Assert.ThrowsAsync<IOException>(() => service.WithdrawAsync(Request(40_000), TestContext.Current.CancellationToken));
        Assert.Single(_stored);
        coordinator.Setup(c => c.GetPendingAsync(It.IsAny<NLightning.Domain.Channels.ValueObjects.ChannelId>()))
            .ThrowsAsync(new InvalidOperationException("Signing recovery is unavailable."));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReleaseOrphanedReservationsAsync(TestContext.Current.CancellationToken));
        Assert.Single(_stored);
        _reservations.Verify(r => r.DeleteAsync(It.IsAny<Guid>()), Times.Never());
    }
}