using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace NLightning.Daemon.Tests.Services;

using Application.Channels.Fees;
using Application.Channels.Safety.Interfaces;
using Application.Onchain.Mempool;
using Application.Payments.Send.Interfaces;
using Daemon.Services;
using Domain.Bitcoin.Interfaces;
using Domain.Client.Interfaces;
using Domain.Node.Interfaces;
using Domain.Node.Options;
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
    public async Task Given_TheDaemonStarts_When_ThePeersStart_Then_TheWalletIsAlreadyLoaded()
    {
        // Arrange
        var calls = new List<string>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.Setup(m => m.LoadWalletAsync(It.IsAny<CancellationToken>()))
               .Callback(() => calls.Add("LoadWallet"))
               .Returns(Task.CompletedTask);
        monitor.Setup(m => m.StartAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
               .Callback(() => calls.Add("ChainMonitorStart"))
               .Returns(Task.CompletedTask);
        var peerManager = new Mock<IPeerManager>();
        peerManager.Setup(p => p.StartAsync(It.IsAny<CancellationToken>()))
                   .Callback(() => calls.Add("PeerManagerStart"))
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
                                            Mock.Of<IMempoolReactor>());

        // Act
        await service.StartAsync(TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(["LoadWallet", "PeerManagerStart", "ChainMonitorStart"], calls);
    }
}