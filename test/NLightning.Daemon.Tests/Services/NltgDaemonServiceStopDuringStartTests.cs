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
/// NL-1393: a stop requested while the daemon is still starting (an orchestrator or an operator) is clean. The start
/// sequence is held at each phase in turn; the stop ends it, stops what had started, and tolerates the services the
/// start never reached, which refuse a stop as the real ones do ("not running").
/// </summary>
public sealed class NltgDaemonServiceStopDuringStartTests
{
    public static TheoryData<string> Phases => new()
    {
        "FeeService", "LoadWallet", "PeerManager", "ChainMonitor", "Ipc"
    };

    [Theory]
    [MemberData(nameof(Phases))]
    public async Task Given_AStartHeldAtAPhase_When_TheNodeIsStopped_Then_TheStopIsClean(string phase)
    {
        // Arrange: every service refuses a stop it was not started for; the start blocks at the phase until cancelled
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new HashSet<string>();

        Task Enter(string name, CancellationToken ct)
        {
            if (name != phase)
            {
                lock (started)
                    started.Add(name);
                return Task.CompletedTask;
            }

            reached.TrySetResult();
            return Task.Delay(Timeout.Infinite, ct);
        }

        Task Leave(string name)
        {
            lock (started)
            {
                return started.Contains(name)
                           ? Task.CompletedTask
                           : Task.FromException(new InvalidOperationException("Service is not running"));
            }
        }

        var feeService = new Mock<IFeeService>();
        feeService.Setup(f => f.StartAsync(It.IsAny<CancellationToken>()))
                  .Returns((CancellationToken ct) => Enter("FeeService", ct));
        feeService.Setup(f => f.StopAsync()).Returns(() => Leave("FeeService"));
        var monitor = new Mock<IBlockchainMonitor>();
        monitor.Setup(m => m.LoadWalletAsync(It.IsAny<CancellationToken>()))
               .Returns((CancellationToken ct) => Enter("LoadWallet", ct));
        monitor.Setup(m => m.StartAsync(It.IsAny<uint>(), It.IsAny<CancellationToken>()))
               .Returns((uint _, CancellationToken ct) => Enter("ChainMonitor", ct));
        monitor.Setup(m => m.StopAsync()).Returns(() => Leave("ChainMonitor"));
        var peerManager = new Mock<IPeerManager>();
        peerManager.Setup(p => p.StartAsync(It.IsAny<CancellationToken>()))
                   .Returns((CancellationToken ct) => Enter("PeerManager", ct));
        peerManager.Setup(p => p.StopAsync()).Returns(() => Leave("PeerManager"));
        var ipc = new Mock<INamedPipeIpcService>();
        ipc.Setup(i => i.StartAsync(It.IsAny<CancellationToken>()))
           .Returns((CancellationToken ct) => Enter("Ipc", ct));
        ipc.Setup(i => i.StopAsync()).Returns(() => Leave("Ipc"));
        var feeUpdates = new Mock<IFeeUpdateScheduler>();
        feeUpdates.Setup(f => f.StartAsync(It.IsAny<CancellationToken>()))
                  .Returns((CancellationToken ct) => Enter("FeeUpdates", ct));
        feeUpdates.Setup(f => f.StopAsync()).Returns(() => Leave("FeeUpdates"));

        var service = new NltgDaemonService(monitor.Object, Mock.Of<IChannelFailureService>(),
                                            new ConfigurationBuilder().Build(), feeService.Object, feeUpdates.Object,
                                            Mock.Of<IHtlcExpiryMonitor>(), NullLogger<NltgDaemonService>.Instance,
                                            ipc.Object,
                                            new OnionReplayBlockPruner(monitor.Object, Mock.Of<IOnionReplayStore>(),
                                                                       NullLogger<OnionReplayBlockPruner>.Instance),
                                            Options.Create(new NodeOptions()), Mock.Of<IPaymentOutcomeHandler>(),
                                            peerManager.Object, Mock.Of<ISecureKeyManager>(),
                                            Mock.Of<IMempoolReactor>());
        await service.StartAsync(TestContext.Current.CancellationToken);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Act
        var stop = Record.ExceptionAsync(() => service.StopAsync(TestContext.Current.CancellationToken)).AsTask();
        var exception = await stop.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // Assert: the stop succeeded, what started was stopped, and nothing after the phase was started
        Assert.Null(exception);
        lock (started)
        {
            if (started.Contains("PeerManager"))
                peerManager.Verify(p => p.StopAsync(), Times.Once);
            if (phase != "Ipc")
                Assert.DoesNotContain("Ipc", started);
        }

        monitor.Verify(m => m.StopAsync(), Times.Once);
        feeService.Verify(f => f.StopAsync(), Times.Once);
    }
}