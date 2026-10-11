using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Signers;

using Domain.Bitcoin.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Exceptions;
using Domain.Money;
using Domain.Node.Options;
using Domain.Protocol.Constants;
using Domain.Protocol.Interfaces;
using Infrastructure.Bitcoin.Builders;
using Infrastructure.Bitcoin.Managers;
using Infrastructure.Bitcoin.Signers;
using Wallet;

public sealed class LocalLightningSignerAccountTests
{
    private const string MnemonicWords = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";

    [Theory]
    [InlineData(AddressType.P2Wpkh, false)]
    [InlineData(AddressType.P2Wpkh, true)]
    [InlineData(AddressType.P2Tr, false)]
    [InlineData(AddressType.P2Tr, true)]
    public void Given_ANamedAccountWithAGlobalAddressIndex_When_Signing_Then_TheRecordedAccountChildSpendsTheActualOutput(
        AddressType type, bool isChange)
    {
        // Arrange: a named account's global database index is not its nonhardened child index.
        using var manager = SecureKeyManager.FromMnemonic(MnemonicWords, "", NetworkConstants.Regtest,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var master = new Mnemonic(MnemonicWords).DeriveExtKey();
        var purpose = type == AddressType.P2Wpkh ? 84 : 86;
        var derived = master.Derive(new KeyPath($"{purpose}'/0'/7'/{(isChange ? 1 : 0)}/3"));
        var script = type == AddressType.P2Wpkh ? derived.PrivateKey.PubKey.WitHash.ScriptPubKey
                                              : derived.PrivateKey.PubKey.GetTaprootFullPubKey().ScriptPubKey;
        var address = new WalletAddressModel(type, 0x80000000, isChange, script.GetDestinationAddress(Network.RegTest)!.ToString())
        {
            AccountIndex = 7,
            DerivationIndex = 3,
            AccountName = "treasury"
        };
        var outpoint = new OutPoint(RandomUtils.GetUInt256(), 0);
        var coin = new UtxoModel(new TxId(outpoint.Hash.ToBytes()), 0, LightningMoney.Satoshis(75_000), 100, address);
        var wallet = new FakeWalletUtxoRepository();
        wallet.Add(coin);
        var reservation = Guid.NewGuid();
        Assert.True(wallet.TryReserveForFee([(coin.TxId, coin.Index)], reservation));
        var signer = new LocalLightningSigner(Mock.Of<IFundingOutputBuilder>(), Mock.Of<IKeyDerivationService>(),
            NullLogger<LocalLightningSigner>.Instance, new NodeOptions { BitcoinNetwork = NetworkConstants.Regtest }, manager, wallet);
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Inputs.Add(new TxIn(outpoint));
        transaction.Outputs.Add(Money.Satoshis(74_000), new Key().PubKey.WitHash.ScriptPubKey);
        var signed = new SignedTransaction(transaction.GetHash().ToBytes(), transaction.ToBytes());

        // Act
        Assert.True(signer.SignWalletTransaction(signed, reservation, []));

        // Assert: the independent script interpreter verifies against the on-chain script and exact input amount.
        var result = Transaction.Load(signed.RawTxBytes, Network.RegTest);
        var builder = Network.RegTest.CreateTransactionBuilder();
        builder.AddCoins(new Coin(outpoint, new TxOut(Money.Satoshis(75_000), script)));
        Assert.True(builder.Verify(result, out var errors), string.Join("; ", errors.Select(error => error.ToString())));
        var wrong = new UtxoModel(coin.TxId, 0, coin.Amount, 100, new WalletAddressModel(type, address.Index,
            isChange, address.Address)
        { AccountIndex = 8, DerivationIndex = 3, AccountName = "wrong" });
        wallet.Spend(coin);
        wallet.Add(wrong);
        Assert.Throws<SignerException>(() => signer.SignWalletTransaction(signed, reservation, []));
    }

    [Theory]
    [InlineData(AddressType.P2Wpkh)]
    [InlineData(AddressType.P2Tr)]
    public void Given_AccountZeroAndANamedAccount_When_DerivingPublicAndPrivateChildren_Then_DefaultKeysStayStableAndNamedKeysAreIsolated(
        AddressType type)
    {
        // Arrange
        using var manager = SecureKeyManager.FromMnemonic(MnemonicWords, "", NetworkConstants.Regtest,
            Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        byte[] original = type == AddressType.P2Wpkh ? manager.GetDepositP2WpkhKeyAtIndex(3, true)
                                                   : manager.GetDepositP2TrKeyAtIndex(3, true);
        byte[] zero = manager.GetDepositKeyAtIndex(type, 0, 3, true);
        byte[] named = manager.GetDepositKeyAtIndex(type, 7, 3, true);
        try
        {
            // Act / Assert
            Assert.Equal(original, zero);
            Assert.NotEqual(original, named);
            var descriptor = manager.GetDepositAccount(type, 7)!;
            var publicChild = ExtPubKey.Parse(descriptor.ExtendedPublicKey, Network.RegTest).Derive(1).Derive(3);
            Assert.Equal(publicChild.PubKey, ExtKey.CreateFromBytes(named).PrivateKey.PubKey);
            CryptographicOperations.ZeroMemory(named);
            byte[] again = manager.GetDepositKeyAtIndex(type, 7, 3, true);
            try
            {
                Assert.Equal(publicChild.PubKey, ExtKey.CreateFromBytes(again).PrivateKey.PubKey);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(again);
            }
            Assert.Throws<ArgumentOutOfRangeException>(() => manager.GetDepositAccount(type, 0x80000000));
            Assert.Throws<ArgumentOutOfRangeException>(() => manager.GetDepositKeyAtIndex(type, 7, 0x80000000, false));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(original);
            CryptographicOperations.ZeroMemory(zero);
            CryptographicOperations.ZeroMemory(named);
        }
    }
}