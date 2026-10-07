using System.Security.Cryptography;
using NBitcoin;
using NLightning.Domain.Bitcoin.Enums;
using NLightning.Domain.Bitcoin.Interfaces;
using NLightning.Domain.Bitcoin.SilentPayments;
using NLightning.Domain.Bitcoin.ValueObjects;
using NLightning.Domain.Bitcoin.Wallet.Models;
using NLightning.Domain.Exceptions;
using NLightning.Domain.Money;
using NLightning.Infrastructure.Bitcoin.Signers;
using NLightning.Infrastructure.RemoteSigning;
using NLightning.Infrastructure.Repositories.Memory;
using TxId = NLightning.Domain.Bitcoin.ValueObjects.TxId;

namespace NLightning.RemoteSigning.Tests;

public sealed class RemoteNewSurfaceTests(SignerDaemonFixture daemon) : IClassFixture<SignerDaemonFixture>
{
    [Fact]
    public void ProxyExplicitlyImplementsEveryCurrentSignerMethod()
    {
        var surface = typeof(RemoteLightningSigner).GetInterfaceMap(typeof(ILightningSigner));
        Assert.All(surface.TargetMethods, method => Assert.Equal(typeof(RemoteLightningSigner), method.DeclaringType));
    }

    [Theory]
    [InlineData(AddressType.P2Wpkh)]
    [InlineData(AddressType.P2Tr)]
    public void PublicDepositAccountAndWalletMessageMatchLocalSigner(AddressType type)
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var keys = new RemoteSecureKeyManager(connection);
        var localAccount = daemon.LocalKeys.GetDepositAccount(type);
        var remoteAccount = keys.GetDepositAccount(type);
        Assert.NotNull(localAccount);
        Assert.NotNull(remoteAccount);
        Assert.Equal(localAccount.ExtendedPublicKey, remoteAccount.ExtendedPublicKey);
        Assert.Equal(localAccount.DerivationPath, remoteAccount.DerivationPath);
        Assert.Equal(localAccount.MasterFingerprint, remoteAccount.MasterFingerprint);
        var publicKey = new PubKey((byte[])keys.GetWalletPublicKey(17, true, type));
        var address = type == AddressType.P2Wpkh
            ? publicKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString()
            : publicKey.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.RegTest).ToString();
        var walletAddress = new WalletAddressModel(type, 17, true, address);
        var message = "remote-wallet-message"u8.ToArray();
        var signer = new RemoteLightningSigner(connection);
        Assert.Equal(daemon.LocalSigner.SignWalletMessage(walletAddress, message),
                     signer.SignWalletMessage(walletAddress, message));
        Assert.Throws<NotSupportedException>(() => keys.GetKeyRingKeyAtIndex(10, 0));
        Assert.Throws<NotSupportedException>(() => keys.GetSilentPaymentSpendKey(new byte[32], null));
    }

    [Theory]
    [InlineData(AddressType.P2Wpkh, false)]
    [InlineData(AddressType.P2Wpkh, true)]
    [InlineData(AddressType.P2Tr, false)]
    [InlineData(AddressType.P2Tr, true)]
    public void NamedAccountMetadataMessagesAndReservedSpendsUseTheAccountChild(AddressType type, bool change)
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var keys = new RemoteSecureKeyManager(connection);
        const uint accountIndex = 7;
        const uint childIndex = 3;
        var account = keys.GetDepositAccount(type, accountIndex);
        var localAccount = daemon.LocalKeys.GetDepositAccount(type, accountIndex);
        Assert.NotNull(account);
        Assert.NotNull(localAccount);
        Assert.Equal(localAccount.ExtendedPublicKey, account.ExtendedPublicKey);
        Assert.Equal(localAccount.DerivationPath, account.DerivationPath);
        Assert.Equal(localAccount.MasterFingerprint, account.MasterFingerprint);
        Assert.Equal(keys.GetDepositAccount(type)!.ExtendedPublicKey,
                     keys.GetDepositAccount(type, 0)!.ExtendedPublicKey);
        var publicKey = ExtPubKey.Parse(account.ExtendedPublicKey, Network.RegTest)
            .Derive(change ? 1u : 0u).Derive(childIndex).PubKey;
        var bitcoinAddress = publicKey.GetAddress(type == AddressType.P2Tr
            ? ScriptPubKeyType.TaprootBIP86 : ScriptPubKeyType.Segwit, Network.RegTest);
        // The wallet row index is deliberately different from the account's child derivation index.
        var address = new WalletAddressModel(type, 40_003, change, bitcoinAddress.ToString())
        { AccountIndex = accountIndex, DerivationIndex = childIndex, AccountName = "savings" };
        var wallet = new UtxoMemoryRepository();
        var model = new UtxoModel(new TxId(RandomNumberGenerator.GetBytes(32)), 0,
            LightningMoney.Satoshis(50_000), 100, address);
        var restored = SignerWire.Read<UtxoModel>(SignerWire.Decode(SignerWire.Encode([model]))[0]);
        Assert.NotNull(restored.WalletAddress);
        Assert.Equal(accountIndex, restored.WalletAddress.AccountIndex);
        Assert.Equal(childIndex, restored.WalletAddress.DerivationIndex);
        Assert.Equal("savings", restored.WalletAddress.AccountName);
        wallet.Add(model);
        var signer = new RemoteLightningSigner(connection, wallet: wallet);
        var message = "named-account-message"u8.ToArray();
        Assert.Equal(daemon.LocalSigner.SignWalletMessage(address, message), signer.SignWalletMessage(address, message));
        var wrongAccount = new WalletAddressModel(type, address.Index, change, address.Address)
        { AccountIndex = accountIndex + 1, DerivationIndex = childIndex, AccountName = "wrong-account" };
        Assert.Throws<SignerException>(() => signer.SignWalletMessage(wrongAccount, message));
        Assert.Throws<NotSupportedException>(() => keys.GetDepositKeyAtIndex(type, accountIndex, childIndex, change));
        Assert.Throws<NotSupportedException>(() => keys.GetDepositKeyAtIndex(type, 0, childIndex, change));
        var reservation = Guid.NewGuid();
        Assert.True(wallet.TryReserveForFee([(model.TxId, model.Index)], reservation));
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])model.TxId), model.Index)));
        tx.Outputs.Add(Money.Satoshis(49_000), new Key().PubKey.WitHash.ScriptPubKey);
        var unsigned = new SignedTransaction(tx.GetHash().ToBytes(), tx.ToBytes());
        Assert.True(signer.SignWalletTransaction(unsigned, reservation, []));
        var signed = Transaction.Load(unsigned.RawTxBytes, Network.RegTest);
        var validator = signed.CreateValidator([new TxOut(Money.Satoshis(50_000), bitcoinAddress.ScriptPubKey)]);
        Assert.True(validator.ValidateInput(0).Error is null or ScriptError.OK);
    }

    [Fact]
    public void SilentPaymentSendUsesFullFrozenReservationAndMatchesLocalSigner()
    {
        using var connection = new RemoteSignerConnection(daemon.Options());
        var wallet = new UtxoMemoryRepository();
        var keys = new RemoteSecureKeyManager(connection);
        var signer = new RemoteLightningSigner(connection, wallet: wallet);
        var outpoints = new List<(TxId TxId, uint Index)>();
        foreach (var type in new[] { AddressType.P2Wpkh, AddressType.P2Tr })
        {
            var key = new PubKey((byte[])keys.GetWalletPublicKey(18, false, type));
            var address = type == AddressType.P2Wpkh
                ? key.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ToString()
                : key.GetAddress(ScriptPubKeyType.TaprootBIP86, Network.RegTest).ToString();
            var utxo = new UtxoModel(new TxId(RandomNumberGenerator.GetBytes(32)), 0,
                LightningMoney.Satoshis(50_000), 100, new WalletAddressModel(type, 18, false, address));
            wallet.Add(utxo);
            outpoints.Add((utxo.TxId, utxo.Index));
        }
        var reservation = Guid.NewGuid();
        Assert.True(wallet.TryReserveForFee(outpoints, reservation));
        var local = new LocalLightningSigner(new NLightning.Infrastructure.Bitcoin.Builders.FundingOutputBuilder(),
            new NLightning.Infrastructure.Bitcoin.Services.KeyDerivationService(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<LocalLightningSigner>.Instance,
            new NLightning.Domain.Node.Options.NodeOptions { BitcoinNetwork = "regtest" }, daemon.LocalKeys, wallet);
        using var scan = new Key();
        using var spend = new Key();
        var recipient = new SilentPaymentAddress(0, scan.PubKey.ToBytes(), spend.PubKey.ToBytes(), "sprt");
        var expected = local.ComputeSilentPaymentOutputs(reservation, [recipient], outpoints);
        Assert.Equal(expected, signer.ComputeSilentPaymentOutputs(reservation, [recipient], outpoints));
        Assert.Throws<SignerException>(() => signer.ComputeSilentPaymentOutputs(reservation, [recipient], [outpoints[0]]));
        wallet.ReleaseFeeReservation(reservation);
        Assert.Throws<SignerException>(() => signer.ComputeSilentPaymentOutputs(reservation, [recipient], outpoints));
    }

    [Fact]
    public void WalletWireRetainsSilentPaymentReceipt()
    {
        var output = new SilentPaymentOutputModel(new TxId(RandomNumberGenerator.GetBytes(32)), 1,
            RandomNumberGenerator.GetBytes(32), RandomNumberGenerator.GetBytes(32), 7, 50_000, 100,
            new NLightning.Domain.Crypto.ValueObjects.Hash(RandomNumberGenerator.GetBytes(32)));
        var original = new UtxoModel(output);
        var restored = SignerWire.Read<UtxoModel>(SignerWire.Decode(SignerWire.Encode([original]))[0]);
        Assert.NotNull(restored.SilentPayment);
        Assert.Equal(output.TransactionId, restored.TxId);
        Assert.Equal(output.OutputKey, restored.SilentPayment.OutputKey);
        Assert.Equal(output.Tweak, restored.SilentPayment.Tweak);
        Assert.Equal(output.Label, restored.SilentPayment.Label);
        Assert.Equal(original.Amount, restored.Amount);
    }
}