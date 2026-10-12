using Microsoft.Extensions.DependencyInjection;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Bitcoin.Wallet;
using Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.ValueObjects;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Domain.Protocol.ValueObjects;

/// <summary>
/// The wallet's allocation locks are per node (NL-1387): two nodes built by <c>AddBitcoinInfrastructure</c> in one
/// process never wait on each other, a waiting caller can give up, and one node's scopes still allocate one batch at
/// a time (NL-283).
/// </summary>
public class WalletLocksTests
{
    // A failure bound only: every awaited step finishes at once unless a lock couples the nodes
    private static readonly TimeSpan s_bound = TimeSpan.FromSeconds(30);

    private static readonly ExtKey s_masterKey =
        ExtKey.CreateFromSeed(Convert.FromHexString("0f0e0d0c0b0a09080706050403020100000102030405060708090a0b0c0d0e0f"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Given_TwoNodeContainers_When_ResolvingTheWalletLocks_Then_EachNodeHasItsOwnAndItsScopesShareIt()
    {
        // Arrange
        using var nodeA = new Node();
        using var nodeB = new Node();

        // Act
        using var scope1 = nodeA.Services.CreateScope();
        using var scope2 = nodeA.Services.CreateScope();
        var a1 = scope1.ServiceProvider.GetRequiredService<WalletLocks>();
        var a2 = scope2.ServiceProvider.GetRequiredService<WalletLocks>();
        var b = nodeB.Services.GetRequiredService<WalletLocks>();

        // Assert
        Assert.Same(a1, a2);
        Assert.NotSame(a1, b);
    }

    [Fact]
    public async Task Given_ANodeHangingInsideItsAddressAllocation_When_AnotherNodeAllocates_Then_ItNeverWaits()
    {
        // Arrange: node A's allocation hangs inside the lock (a statement that never returns), and A also holds its
        // account gate, as a hung account allocation would
        using var nodeA = new Node();
        using var nodeB = new Node();
        nodeA.Addresses.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var scopeA = nodeA.Services.CreateScope();
        var hung = scopeA.ServiceProvider.GetRequiredService<IBitcoinWalletService>()
                         .GetUnusedAddressAsync(AddressType.P2Wpkh, false, Ct);
        await nodeA.Addresses.Entered.Task.WaitAsync(s_bound, Ct);
        using var accountsA = await scopeA.ServiceProvider.GetRequiredService<WalletAccountService>()
                                          .EnterDiscoveryAsync(Ct);

        // Act: node B hands out an address (shutdown, sweep and penalty destinations) and its chain monitor takes its
        // account gate for a block commit
        using var scopeB = nodeB.Services.CreateScope();
        var address = await scopeB.ServiceProvider.GetRequiredService<IBitcoinWalletService>()
                                  .GetUnusedAddressAsync(AddressType.P2Wpkh, false, Ct).WaitAsync(s_bound, Ct);
        using var accountsB = await scopeB.ServiceProvider.GetRequiredService<WalletAccountService>()
                                          .EnterDiscoveryAsync(Ct).AsTask().WaitAsync(s_bound, Ct);

        // Assert: B is served while A still hangs; A finishes once its statement returns
        Assert.Equal(0u, address.Index);
        Assert.True(address.IsReserved);
        Assert.False(hung.IsCompleted);
        nodeA.Addresses.Hold.SetResult();
        Assert.Equal(0u, (await hung.WaitAsync(s_bound, Ct)).Index);
    }

    [Fact]
    public async Task Given_ANodeHangingInsideAnAllocation_When_AnotherCallerOfThatNodeCancels_Then_ItsWaitEnds()
    {
        // Arrange: one of the node's allocations hangs inside the address lock, and one holds the account gate
        using var node = new Node();
        node.Addresses.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var hungScope = node.Services.CreateScope();
        var hung = hungScope.ServiceProvider.GetRequiredService<IBitcoinWalletService>()
                            .GetUnusedAddressAsync(AddressType.P2Wpkh, false, Ct);
        await node.Addresses.Entered.Task.WaitAsync(s_bound, Ct);
        var accounts = hungScope.ServiceProvider.GetRequiredService<WalletAccountService>();
        var accountsHeld = await accounts.EnterDiscoveryAsync(Ct);
        using var waiterScope = node.Services.CreateScope();
        var wallet = waiterScope.ServiceProvider.GetRequiredService<IBitcoinWalletService>();
        var waiterAccounts = waiterScope.ServiceProvider.GetRequiredService<WalletAccountService>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        // Act
        var waitingAddress = wallet.GetUnusedAddressAsync(AddressType.P2Tr, true, cts.Token);
        var waitingReserve = wallet.ReserveUnusedAddressAsync(AddressType.P2Wpkh, false, cts.Token);
        var waitingAccount = waiterAccounts.NextAsync("savings", AddressType.P2Tr, false, cts.Token);
        var waitingGate = waiterAccounts.EnterDiscoveryAsync(cts.Token).AsTask();
        Assert.False(waitingAddress.IsCompleted);
        Assert.False(waitingGate.IsCompleted);
        await cts.CancelAsync();

        // Assert: every wait ends with the token, none got a lock, and the locks still work after the holders leave
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingAddress.WaitAsync(s_bound, Ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingReserve.WaitAsync(s_bound, Ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingAccount.WaitAsync(s_bound, Ct));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingGate.WaitAsync(s_bound, Ct));
        Assert.False(hung.IsCompleted);
        node.Addresses.Hold.SetResult();
        accountsHeld.Dispose();
        await hung.WaitAsync(s_bound, Ct);
        var next = await wallet.GetUnusedAddressAsync(AddressType.P2Wpkh, false, Ct).WaitAsync(s_bound, Ct);
        using var gate = await waiterAccounts.EnterDiscoveryAsync(Ct).AsTask().WaitAsync(s_bound, Ct);
        Assert.Equal(BatchLength, next.Index);
    }

    [Fact]
    public async Task Given_ScopesOfOneNode_When_AllocatingConcurrently_Then_NoTwoBatchesShareAnIndex()
    {
        // Arrange: every lookup finds no unused address and the index query yields, so scopes that ran the lookup
        // together would compute the same first index (NL-283)
        using var node = new Node();
        var scopes = Enumerable.Range(0, 4).Select(_ => node.Services.CreateScope()).ToList();

        // Act
        var addresses = await Task.WhenAll(scopes.Select(s => s.ServiceProvider
                                                               .GetRequiredService<IBitcoinWalletService>()
                                                               .GetUnusedAddressAsync(AddressType.P2Wpkh, false, Ct)))
                                  .WaitAsync(s_bound, Ct);

        // Assert: four batches one after the other, no duplicate key
        Assert.Equal(new uint[] { 0, BatchLength, 2 * BatchLength, 3 * BatchLength },
                     addresses.Select(a => a.Index).Order());
        Assert.Equal(Enumerable.Range(0, 4 * (int)BatchLength).Select(i => (uint)i),
                     node.Addresses.GetAllAddresses().Select(a => a.Index).Order());
        scopes.ForEach(s => s.Dispose());
    }

    // One batch: the hand-out addresses and the discovery window past them (NL-463)
    private const uint BatchLength = 10 + BitcoinWalletService.GapLimit;

    /// <summary>One node: its own container from <c>AddBitcoinInfrastructure</c> over its own address table.</summary>
    private sealed class Node : IDisposable
    {
        public FakeAddressRepository Addresses { get; } = new();
        public ServiceProvider Services { get; }

        public Node()
        {
            var keys = new Mock<ISecureKeyManager>();
            keys.SetupGet(k => k.SupportedWalletAddressTypes).Returns(AddressType.P2Wpkh | AddressType.P2Tr);
            keys.Setup(k => k.GetWalletPublicKey(It.IsAny<uint>(), It.IsAny<bool>(), It.IsAny<AddressType>()))
                .Returns((uint index, bool isChange, AddressType _) =>
                             (CompactPubKey)s_masterKey.Derive(isChange ? 1u : 0u).Derive(index).Neuter().PubKey
                                                       .ToBytes());
            var accounts = new Mock<IWalletAccountDbRepository>();
            accounts.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync((WalletAccountModel?)null);
            var uow = new Mock<IUnitOfWork>();
            uow.SetupGet(u => u.WalletAddressesDbRepository).Returns(Addresses);
            uow.SetupGet(u => u.WalletAccountDbRepository).Returns(accounts.Object);
            uow.Setup(u => u.SaveChangesAsync()).Returns(Task.CompletedTask);

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddBitcoinInfrastructure();
            services.AddSingleton(Microsoft.Extensions.Options.Options.Create(new NodeOptions { BitcoinNetwork = BitcoinNetwork.Regtest }));
            services.AddSingleton(new Mock<IBlockchainMonitor>().Object);
            services.AddSingleton(keys.Object);
            services.AddScoped(_ => uow.Object);
            Services = services.BuildServiceProvider();
        }

        public void Dispose() => Services.Dispose();
    }

    /// <summary>
    /// The node's address table. Every lookup finds no unused address (each call generates a batch), the index query
    /// yields, a duplicate (Index, IsChange, AddressType) fails as the table's key does, and <see cref="Hold"/> makes
    /// the lookup hang until it completes.
    /// </summary>
    private sealed class FakeAddressRepository : IWalletAddressesDbRepository
    {
        private readonly List<WalletAddressModel> _stored = [];

        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Hold { get; set; }

        public async Task<WalletAddressModel?> GetUnusedAddressAsync(AddressType type, bool isChange)
        {
            Entered.TrySetResult();
            if (Hold is { } hold)
                await hold.Task;
            return null;
        }

        public Task ReserveAsync(WalletAddressModel address) => Task.CompletedTask;

        public async Task<uint> GetLastUsedAddressIndex(AddressType addressType, bool isChange)
        {
            uint highest;
            lock (_stored)
                highest = _stored.Where(a => a.AddressType == addressType && a.IsChange == isChange)
                                 .Select(a => a.Index).DefaultIfEmpty(0u).Max();
            await Task.Yield();
            return highest;
        }

        public void AddRange(List<WalletAddressModel> addresses)
        {
            lock (_stored)
            {
                if (addresses.Any(n => _stored.Any(a => a.Index == n.Index && a.IsChange == n.IsChange
                                                     && a.AddressType == n.AddressType)))
                    throw new InvalidOperationException("Duplicate (Index, IsChange, AddressType)");
                _stored.AddRange(addresses);
            }
        }

        public void UpdateAsync(WalletAddressModel address)
        {
        }

        public IEnumerable<WalletAddressModel> GetAllAddresses()
        {
            lock (_stored)
                return _stored.ToList();
        }
    }
}