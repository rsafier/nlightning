using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.Interfaces;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Node.Options;
using Domain.Persistence.Interfaces;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Wallet;
using Infrastructure.Bitcoin.Wallet.Interfaces;

public sealed class WalletAccountServiceTests
{
    private readonly Dictionary<string, WalletAccountModel> _accounts = new(StringComparer.Ordinal);
    private readonly List<WalletAddressModel> _addresses = [];
    private readonly List<ImportedTapscript> _imports = [];
    private readonly Mock<IUnitOfWork> _uow = new();
    private readonly Mock<IBlockchainMonitor> _monitor = new();
    private readonly WalletAccountService _service;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly ExtKey s_master = ExtKey.CreateFromSeed(Enumerable.Repeat((byte)42, 32).ToArray());

    public WalletAccountServiceTests()
    {
        var accounts = new Mock<IWalletAccountDbRepository>();
        accounts.Setup(r => r.ListAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => _accounts.Values.ToArray());
        accounts.Setup(r => r.GetAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string name, CancellationToken _) => _accounts.GetValueOrDefault(name));
        accounts.Setup(r => r.StageAsync(It.IsAny<WalletAccountModel>(), It.IsAny<CancellationToken>()))
            .Callback<WalletAccountModel, CancellationToken>((account, _) => _accounts[account.Name] = account)
            .Returns(Task.CompletedTask);
        var addresses = new Mock<IWalletAddressesDbRepository>();
        addresses.Setup(r => r.GetAllAddresses()).Returns(() => _addresses.ToArray());
        addresses.Setup(r => r.AddRange(It.IsAny<List<WalletAddressModel>>()))
            .Callback<List<WalletAddressModel>>(_addresses.AddRange);
        addresses.Setup(r => r.ReserveAsync(It.IsAny<WalletAddressModel>())).Returns(Task.CompletedTask);
        var imports = new Mock<IImportedTapscriptDbRepository>();
        imports.Setup(r => r.GetAsync(It.IsAny<byte[]>())).ReturnsAsync((byte[] script) =>
            _imports.FirstOrDefault(i => i.Script.SequenceEqual(script)));
        imports.Setup(r => r.ListAsync()).ReturnsAsync(() => _imports.ToArray());
        imports.Setup(r => r.Add(It.IsAny<ImportedTapscript>())).Callback<ImportedTapscript>(_imports.Add);
        _uow.Setup(u => u.WalletAccountDbRepository).Returns(accounts.Object);
        _uow.Setup(u => u.WalletAddressesDbRepository).Returns(addresses.Object);
        _uow.Setup(u => u.ImportedTapscriptDbRepository).Returns(imports.Object);
        _uow.Setup(u => u.SaveChangesAsync()).Returns(Task.CompletedTask);
        _monitor.Setup(m => m.LastProcessedBlockHeight).Returns(100u);
        var keys = new Mock<ISecureKeyManager>();
        keys.Setup(k => k.GetDepositAccount(It.IsAny<AddressType>(), It.IsAny<uint>()))
            .Returns((AddressType type, uint index) => AccountKey(type, index));
        keys.Setup(k => k.GetDepositAccount(It.IsAny<AddressType>())).Returns((AddressType type) => AccountKey(type, 0));
        _service = new WalletAccountService(_uow.Object, keys.Object, _monitor.Object,
            Microsoft.Extensions.Options.Options.Create(new NodeOptions { BitcoinNetwork = "regtest" }));
    }

    [Fact]
    public async Task NamedAccountHandsOutActualAccountChildrenWithIndependentStorageIndices()
    {
        var account = await _service.CreateAsync("hardware", AddressType.P2Tr, Ct);
        var first = await _service.NextAsync("hardware", AddressType.P2Tr, false, Ct);
        var second = await _service.NextAsync("hardware", AddressType.P2Tr, false, Ct);
        Assert.Equal(1u, account.AccountIndex);
        Assert.Equal(0u, first.DerivationIndex);
        Assert.Equal(1u, second.DerivationIndex);
        Assert.True(first.Index >= 0x80000000);
        Assert.NotEqual(first.Address, second.Address);
        Assert.Equal(_service.Address(account, false, 0), first.Address);
        Assert.Equal(2u, _accounts["hardware"].ExternalKeyCount);
        Assert.All(_addresses, a => Assert.Equal("hardware", a.AccountName));
    }

    [Fact]
    public async Task ImportDryRunHasNoDurableOrWatchSideEffects()
    {
        var key = AccountKey(AddressType.P2Wpkh, 7);
        var account = await _service.ImportAsync("watch", key.ExtendedPublicKey, key.MasterFingerprint,
            AddressType.P2Wpkh, true, Ct);
        Assert.True(account.WatchOnly);
        Assert.Equal(7u, account.AccountIndex);
        Assert.Empty(_accounts);
        Assert.Empty(_imports);
        Assert.Empty(_addresses);
        _uow.Verify(u => u.SaveChangesAsync(), Times.Never);
    }

    [Fact]
    public async Task ImportedHistoricalSpentReceiptsExtendGapWithoutSpendableCustody()
    {
        var key = AccountKey(AddressType.P2Wpkh, 7);
        var account = await _service.ImportAsync("watch", key.ExtendedPublicKey, key.MasterFingerprint,
            AddressType.P2Wpkh, false, Ct);
        Assert.Equal(0u, account.BirthdayHeight);
        Assert.Equal(40, _imports.Count);
        var historical = Network.RegTest.CreateTransaction();
        historical.Outputs.Add(Money.Satoshis(10_000), BitcoinAddress.Create(_service.Address(account, false, 19), Network.RegTest));
        Assert.True(await _service.ExtendWatchDiscoveryAsync([historical], Ct));
        Assert.Equal(20u, _accounts["watch"].ExternalKeyCount);
        Assert.Equal(60, _imports.Count);
        Assert.False(await _service.ExtendWatchDiscoveryAsync([historical], Ct));
        Assert.Empty(_addresses);
        _monitor.Verify(m => m.WatchBitcoinAddress(It.IsAny<WalletAddressModel>()), Times.Never);
    }

    [Fact]
    public async Task BlockDiscoveryIncludesReceivedThenSpentChildrenAndDoesNotSaveOutsideBlock()
    {
        await _service.CreateAsync("one", AddressType.P2Tr, Ct);
        await _service.CreateAsync("two", AddressType.P2Tr, Ct);
        var used = _addresses.Where(a => !a.IsChange && a.DerivationIndex == 19).ToArray();
        _uow.Invocations.Clear();
        using var gate = await WalletAccountService.EnterDiscoveryAsync(Ct);
        var added = await _service.StageOwnedDiscoveryAsync(_uow.Object, used, Ct);
        Assert.Equal(40, added.Count);
        Assert.Equal(20u, _accounts["one"].ExternalKeyCount);
        Assert.Equal(20u, _accounts["two"].ExternalKeyCount);
        Assert.Equal(_addresses.Count, _addresses.Select(a => (a.AddressType, a.IsChange, a.Index)).Distinct().Count());
        _uow.Verify(u => u.SaveChangesAsync(), Times.Never);
    }

    [Theory]
    [InlineData("default")]
    [InlineData("imported")]
    [InlineData("")]
    [InlineData("bad\nname")]
    public async Task ReservedOrInvalidAccountNamesRefuse(string name) =>
        await Assert.ThrowsAsync<ArgumentException>(() => _service.CreateAsync(name, AddressType.P2Tr, Ct));

    private static DepositAccountInfo AccountKey(AddressType type, uint index)
    {
        var purpose = type == AddressType.P2Tr ? 86 : 84;
        var path = $"m/{purpose}'/0'/{index}'";
        var key = s_master.Derive(new KeyPath(path));
        return new DepositAccountInfo(key.Neuter().ToString(Network.RegTest), path,
            s_master.Neuter().PubKey.GetHDFingerPrint().ToBytes());
    }
}