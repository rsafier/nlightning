using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Services;

using Application.Channels.Fees;
using Application.Channels.Safety.Interfaces;
using Application.InteractiveTx;
using Application.Onchain.Mempool;
using Application.Payments.Send.Interfaces;
using Daemon.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Client.Interfaces;
using Domain.Money;
using Domain.Node.Interfaces;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.InteractiveTx.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.Onion.Interfaces;
using Infrastructure.Bitcoin.Onion;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// NL-600: the daemon loads the wallet (UTXO set and fee input reservations) before the peer manager lets a peer in, so
/// a splice a peer resumes right after a restart finds its reserved wallet inputs when it signs them; the chain monitor
/// starts afterwards.
/// </summary>
public sealed class NltgDaemonServiceStartupOrderTests
{
    [Fact]
    public async Task Given_TheDaemonStarts_When_ThePeersStart_Then_TheWalletIsLoadedAndAbortedReservationsAreGone()
    {
        // Arrange
        var calls = new List<string>();
        var reservation = new FeeInputReservation(Guid.NewGuid(), "itx:Splice:aborted", [], LightningMoney.Zero,
                                                  LightningMoney.Zero, null);
        var reservations = new List<FeeInputReservation>();
        var reservationsAtPeerStart = -1;
        var selector = new Mock<IFeeInputSelector>();
        selector.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => reservations.ToList());
        selector.Setup(s => s.ReleaseAsync(reservation.Id, It.IsAny<CancellationToken>())).Returns(() =>
        {
            calls.Add("ReleaseAbortedReservation");
            reservations.Remove(reservation);
            return Task.CompletedTask;
        });
        // The abort already committed before the previous process stopped; unresolved sessions do not include it.
        var sessions = new Mock<IInteractiveTxSessionDbRepository>();
        sessions.Setup(s => s.GetUnresolvedAsync()).ReturnsAsync([]);
        var unitOfWork = new Mock<IUnitOfWork>();
        unitOfWork.SetupGet(u => u.InteractiveTxSessionDbRepository).Returns(sessions.Object);
        using var provider = new ServiceCollection().AddScoped(_ => unitOfWork.Object).BuildServiceProvider();
        var contributor = new WalletInteractiveTxContributor(selector.Object, Mock.Of<ILightningSigner>(),
            Mock.Of<IUtxoMemoryRepository>(), Mock.Of<IWalletPrevTxSource>(), Mock.Of<IPrevTxInspector>(),
            Mock.Of<IInteractiveTxTransactionParser>(), provider.GetRequiredService<IServiceScopeFactory>());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.Setup(m => m.LoadWalletAsync(It.IsAny<CancellationToken>()))
               .Callback(() =>
               {
                   calls.Add("LoadWallet");
                   reservations.Add(reservation);
               })
               .Returns(Task.CompletedTask);
        monitor.Setup(m => m.StartAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
               .Callback(() => calls.Add("ChainMonitorStart"))
               .Returns(Task.CompletedTask);
        var peerManager = new Mock<IPeerManager>();
        peerManager.Setup(p => p.StartAsync(It.IsAny<CancellationToken>()))
                   .Callback(() =>
                   {
                       calls.Add("PeerManagerStart");
                       reservationsAtPeerStart = reservations.Count;
                   })
                   .Returns(Task.CompletedTask);
        var ipc = new Mock<INamedPipeIpcService>();
        ipc.Setup(i => i.StartAsync(It.IsAny<CancellationToken>()))
           .Callback(() => started.TrySetResult())
           .Returns(Task.CompletedTask);

        var service = new NltgDaemonService(monitor.Object, Mock.Of<IChannelFailureService>(),
                                            new ConfigurationBuilder().Build(), Mock.Of<IFeeService>(),
                                            Mock.Of<IFeeUpdateScheduler>(), Mock.Of<IHtlcExpiryMonitor>(),
                                            NullLogger<NltgDaemonService>.Instance, ipc.Object,
                                            new OnionReplayBlockPruner(monitor.Object, Mock.Of<IOnionReplayStore>(),
                                                                       NullLogger<OnionReplayBlockPruner>.Instance),
                                            Options.Create(new NodeOptions()), Mock.Of<IPaymentOutcomeHandler>(),
                                            peerManager.Object, Mock.Of<ISecureKeyManager>(),
                                            Mock.Of<IMempoolReactor>(), interactiveTxContributor: contributor);

        // Act
        await service.StartAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(0, reservationsAtPeerStart);
        Assert.Empty(reservations);
        Assert.Equal(["LoadWallet", "ReleaseAbortedReservation", "PeerManagerStart", "ChainMonitorStart"], calls);
    }
}