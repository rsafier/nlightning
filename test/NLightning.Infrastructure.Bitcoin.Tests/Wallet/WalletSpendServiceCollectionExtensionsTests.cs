using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Channels.ValueObjects;
using Domain.Node.Options;
using Domain.Signing.Recovery;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Bitcoin.Wallet.Interfaces;

public class WalletSpendServiceCollectionExtensionsTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_OptionalNativeCoordinator_When_ComposedWalletCleansReservations_Then_RecoveryRunsBeforeCleanup(
        bool native)
    {
        var calls = new List<string>();
        var coordinator = new Mock<IRemoteSigningWorkflowCoordinator>(MockBehavior.Strict);
        coordinator.As<INativeWalletSigningRecovery>();
        var workflowKey = new ChannelId(SHA256.HashData("NLightning/native-wallet-withdrawal/v1"u8));
        coordinator.Setup(c => c.GetPendingAsync(workflowKey))
                   .Callback(() => calls.Add("recover"))
                   .ReturnsAsync(Array.Empty<SigningWorkflow>());
        var selector = new Mock<IFeeInputSelector>(MockBehavior.Strict);
        selector.Setup(s => s.GetAllAsync(It.IsAny<CancellationToken>()))
                .Callback(() => calls.Add("cleanup"))
                .ReturnsAsync(Array.Empty<FeeInputReservation>());

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(selector.Object);
        services.AddSingleton(Mock.Of<IAnchorReserveService>());
        services.AddSingleton(Mock.Of<IUtxoMemoryRepository>());
        services.AddSingleton(Mock.Of<ILightningSigner>());
        services.AddSingleton(Mock.Of<IBlockchainMonitor>());
        services.AddSingleton(Mock.Of<IFeeService>());
        services.AddSingleton(Mock.Of<IWalletPsbtService>());
        services.AddSingleton<IOptions<NodeOptions>>(Microsoft.Extensions.Options.Options.Create(new NodeOptions { BitcoinNetwork = "regtest" }));
        if (native)
            services.AddSingleton(coordinator.Object);
        services.AddWalletSpendServices();
        using var provider = services.BuildServiceProvider();

        var spend = provider.GetRequiredService<IWalletSpendService>();
        var released = await spend.ReleaseOrphanedReservationsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, released);
        Assert.Equal(native ? ["recover", "cleanup"] : new[] { "cleanup" }, calls);
        coordinator.Verify(c => c.GetPendingAsync(workflowKey), native ? Times.Once() : Times.Never());
        selector.Verify(s => s.GetAllAsync(It.IsAny<CancellationToken>()), Times.Once());
    }
}