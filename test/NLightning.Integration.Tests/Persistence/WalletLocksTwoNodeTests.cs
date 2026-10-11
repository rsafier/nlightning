using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Bitcoin.Wallet.Interfaces;

/// <summary>
/// Two nodes in one process, each with its own container, SQLite database and chain monitor (NL-1387): while one
/// node's wallet allocations hang holding both of its wallet locks, the other node's chain monitor commits a block
/// that extends a named account (it takes that node's account gate) and the other node hands out an address.
/// </summary>
public class WalletLocksTwoNodeTests
{
    // A failure bound only: nothing here waits unless the nodes' wallet locks are shared
    private static readonly TimeSpan s_bound = TimeSpan.FromSeconds(30);

    private static readonly ExtKey s_master = ExtKey.CreateFromSeed(Enumerable.Repeat((byte)0x2a, 32).ToArray());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Given_ANodeHoldingItsWalletLocks_When_AnotherNodeProcessesABlockAndAllocates_Then_ItNeverWaits()
    {
        // Arrange: both nodes up; node B has a named account
        await using var nodeA = CreateNode();
        await using var nodeB = CreateNode();
        await nodeA.StartAsync(95);
        await nodeB.StartAsync(95);
        using (var scope = nodeB.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<WalletAccountService>()
                       .CreateAsync("savings", AddressType.P2Wpkh, Ct);
        var named = NamedAddress(nodeB, "savings", 0);

        // Node A hangs inside both of its wallet locks (an allocation whose statement never returns)
        var locksA = nodeA.Services.GetRequiredService<WalletLocks>();
        using var addressLockA = await locksA.EnterAddressGenerationAsync(Ct);
        using var accountLockA = await locksA.EnterAccountsAsync(Ct);

        // Act: a deposit to B's named account, then an address of B's default wallet
        var deposit = Network.RegTest.CreateTransaction();
        deposit.Inputs.Add(new OutPoint(new uint256(Enumerable.Repeat((byte)0x31, 32).ToArray()), 0));
        deposit.Outputs.Add(Money.Satoshis(50_000), BitcoinAddress.Create(named, Network.RegTest));
        await nodeB.MineAndDeliverAsync(deposit).WaitAsync(s_bound, Ct);
        string address;
        using (var scope = nodeB.Services.CreateScope())
            address = (await scope.ServiceProvider.GetRequiredService<IBitcoinWalletService>()
                                  .GetUnusedAddressAsync(AddressType.P2Wpkh, false, Ct).WaitAsync(s_bound, Ct)).Address;

        // Assert: B's monitor committed the block with the account's discovery (derivation 0 used, so one address is
        // issued and the window moved by one) and B handed out its first default address
        Assert.Equal(nodeB.Chain.TipHeight, nodeB.Monitor.LastProcessedBlockHeight);
        using (var scope = nodeB.Services.CreateScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var account = await uow.WalletAccountDbRepository.GetAsync("savings", Ct);
            Assert.Equal(1u, account!.ExternalKeyCount);
            Assert.Contains(uow.WalletAddressesDbRepository.GetAllAddresses(),
                            a => a.AccountName == "savings" && !a.IsChange && a.DerivationIndex == 20);
        }

        Assert.StartsWith("bcrt1q", address);
    }

    private static string NamedAddress(ChainMonitorHarness node, string account, uint derivationIndex)
    {
        using var scope = node.Services.CreateScope();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        return uow.WalletAddressesDbRepository.GetAllAddresses()
                  .Single(a => a.AccountName == account && !a.IsChange && a.DerivationIndex == derivationIndex)
                  .Address;
    }

    /// <summary>A node: the real chain monitor over its own SQLite database, with the wallet services of a node.</summary>
    private static ChainMonitorHarness CreateNode()
    {
        ChainMonitorHarness? node = null;
        var keys = new Mock<ISecureKeyManager>();
        keys.SetupGet(k => k.SupportedWalletAddressTypes).Returns(AddressType.P2Wpkh | AddressType.P2Tr);
        keys.Setup(k => k.GetWalletPublicKey(It.IsAny<uint>(), It.IsAny<bool>(), It.IsAny<AddressType>()))
            .Returns((uint index, bool isChange, AddressType _) =>
                         (CompactPubKey)s_master.Derive(isChange ? 1u : 0u).Derive(index).Neuter().PubKey.ToBytes());
        keys.Setup(k => k.GetDepositAccount(It.IsAny<AddressType>(), It.IsAny<uint>()))
            .Returns((AddressType type, uint index) =>
            {
                var path = $"m/{(type == AddressType.P2Tr ? 86 : 84)}'/1'/{index}'";
                return new DepositAccountInfo(s_master.Derive(new KeyPath(path)).Neuter().ToString(Network.RegTest),
                                              path, s_master.Neuter().PubKey.GetHDFingerPrint().ToBytes());
            });
        node = new ChainMonitorHarness(configureServices: services =>
        {
            services.AddLogging();
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(
                                      new NodeOptions { BitcoinNetwork = "regtest" }));
            services.AddSingleton(keys.Object);
            services.AddSingleton<IBlockchainMonitor>(_ => node!.Monitor);
            services.AddSingleton<WalletLocks>();
            services.AddScoped<IBitcoinWalletService, BitcoinWalletService>();
            services.AddScoped<WalletAccountService>();
        });
        return node;
    }
}