using NBitcoin;

namespace NLightning.LndGrpc.Tests.Wave3;

using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Money;
using Domain.Protocol.Interfaces;
using LndGrpc.Macaroons;
using Testing.Lnd.Walletrpc;
using AddressType = Domain.Bitcoin.Enums.AddressType;
using WalletAddressType = Testing.Lnd.Walletrpc.AddressType;

/// <summary>walletrpc <c>ListAccounts</c> and <c>ListAddresses</c> over the deposit wallet (NL-1247).</summary>
public sealed partial class LndGrpcWave3HostTests
{
    private static readonly ExtKey s_master = ExtKey.CreateFromSeed(Convert.FromHexString("000102030405060708090a0b0c0d0e0f"));

    private void SetUpAccounts()
    {
        foreach (var (type, path) in new[] { (AddressType.P2Wpkh, "84'/0'/0'"), (AddressType.P2Tr, "86'/0'/0'") })
        {
            var account = new DepositAccountInfo(s_master.Derive(new KeyPath(path)).Neuter().ToString(Network.RegTest),
                                                 "m/" + path, s_master.GetPublicKey().GetHDFingerPrint().ToBytes());
            _keys.Setup(k => k.GetDepositAccount(type)).Returns(account);
        }
    }

    private WalletAddressModel AddAddress(AddressType type, uint index, bool isChange, bool reserved)
    {
        var key = s_master.Derive(new KeyPath(type == AddressType.P2Wpkh ? "84'/0'/0'" : "86'/0'/0'"))
                          .Derive(isChange ? 1u : 0u).Derive(index).GetPublicKey();
        var address = type == AddressType.P2Wpkh
                          ? key.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString()
                          : key.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.RegTest).ToString();
        var model = new WalletAddressModel(type, index, isChange, address) { IsReserved = reserved };
        _walletAddresses.Add(model);
        return model;
    }

    [Fact]
    public async Task Given_TheDepositWallet_When_ListAccounts_Then_ItsP2WpkhAndTaprootAccountsAreListed()
    {
        // Arrange: two P2WPKH addresses handed out, one funded change, a look-ahead address nobody was given
        SetUpAccounts();
        AddAddress(AddressType.P2Wpkh, 0, false, true);
        AddAddress(AddressType.P2Wpkh, 1, false, true);
        AddAddress(AddressType.P2Wpkh, 7, false, false);
        var change = AddAddress(AddressType.P2Wpkh, 2, true, false);
        _unspent.Add(new UtxoModel(new TxId(new byte[32]), 0, LightningMoney.Satoshis(5_000), 100, change));
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var all = await connection.WalletKitClient.ListAccountsAsync(new ListAccountsRequest(), cancellationToken: Ct);
        var taproot = await connection.WalletKitClient.ListAccountsAsync(
                          new ListAccountsRequest { AddressType = WalletAddressType.TaprootPubkey },
                          cancellationToken: Ct);
        var other = await connection.WalletKitClient.ListAccountsAsync(new ListAccountsRequest { Name = "other" },
                                                                       cancellationToken: Ct);

        // Assert: bos chain-deposit looks for the taproot account
        Assert.Equal([WalletAddressType.WitnessPubkeyHash, WalletAddressType.TaprootPubkey],
                     all.Accounts.Select(a => a.AddressType));
        var p2Wpkh = all.Accounts[0];
        Assert.Equal(("default", "m/84'/0'/0'", 2u, 3u, false),
                     (p2Wpkh.Name, p2Wpkh.DerivationPath, p2Wpkh.ExternalKeyCount, p2Wpkh.InternalKeyCount,
                      p2Wpkh.WatchOnly));
        Assert.Equal(s_master.Derive(new KeyPath("84'/0'/0'")).Neuter().ToString(Network.RegTest),
                     p2Wpkh.ExtendedPublicKey);
        Assert.Equal(s_master.GetPublicKey().GetHDFingerPrint().ToBytes(), p2Wpkh.MasterKeyFingerprint.ToByteArray());
        Assert.Equal("m/86'/0'/0'", Assert.Single(taproot.Accounts).DerivationPath);
        Assert.Empty(other.Accounts);
    }

    [Fact]
    public async Task Given_HandedOutAndFundedAddresses_When_ListAddresses_Then_EachHasItsBalancePathAndKey()
    {
        // Arrange
        SetUpAccounts();
        var deposit = AddAddress(AddressType.P2Tr, 3, false, true);
        AddAddress(AddressType.P2Tr, 9, false, false);
        _unspent.Add(new UtxoModel(new TxId(Enumerable.Repeat((byte)1, 32).ToArray()), 1,
                                   LightningMoney.Satoshis(42_000), 100, deposit));
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);

        // Act
        var response = await connection.WalletKitClient.ListAddressesAsync(new ListAddressesRequest(),
                                                                           cancellationToken: Ct);

        // Assert
        Assert.Equal(2, response.AccountWithAddresses.Count);
        Assert.Empty(response.AccountWithAddresses[0].Addresses);
        var taproot = response.AccountWithAddresses[1];
        Assert.Equal(WalletAddressType.TaprootPubkey, taproot.AddressType);
        var address = Assert.Single(taproot.Addresses);
        Assert.Equal((deposit.Address, false, 42_000L, "m/86'/0'/0'/0/3"),
                     (address.Address, address.IsInternal, address.Balance, address.DerivationPath));
        Assert.Equal(s_master.Derive(new KeyPath("86'/0'/0'/0/3")).GetPublicKey().ToBytes(),
                     address.PublicKey.ToByteArray());
    }
}