using NBitcoin;

namespace NLightning.LndGrpc.Tests.Wave3;

using Domain.Accounting.Enums;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.ValueObjects;
using LndGrpc.Macaroons;
using Testing.Lnd.Lnrpc;
using Testing.Lnd.Walletrpc;
using WalletAddressType = Testing.Lnd.Walletrpc.AddressType;

public sealed partial class LndGrpcWave3HostTests
{
    [Fact]
    public async Task Given_SilentPaymentCoin_When_ListAddresses_Then_ActualTaprootAddressAndOutputKeyAreVisible()
    {
        // Arrange
        SetUpAccounts();
        var key = new Key().PubKey.ToBytes()[1..];
        var output = new SilentPaymentOutputModel(new TxId(RandomUtils.GetUInt256().ToBytes()), 2,
            key, new byte[32], 0, 42_000, 100, new Hash(new byte[32]));
        _unspent.Add(new UtxoModel(output));
        var script = new Script(new byte[] { 0x51, 0x20 }.Concat(key).ToArray());
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);
        // Act
        var response = await connection.WalletKitClient.ListAddressesAsync(new ListAddressesRequest(), cancellationToken: Ct);
        // Assert
        var account = Assert.Single(response.AccountWithAddresses.Where(a => a.AddressType == WalletAddressType.TaprootPubkey));
        var address = Assert.Single(account.Addresses);
        Assert.Equal(script.GetDestinationAddress(Network.RegTest)!.ToString(), address.Address);
        Assert.Equal(42_000, address.Balance);
        Assert.True(address.IsInternal);
        Assert.Empty(address.DerivationPath);
        Assert.Equal(new byte[] { 2 }.Concat(key).ToArray(), address.PublicKey.ToByteArray());
    }

    [Fact]
    public async Task Given_PrunedSilentPaymentReceipt_When_GetTransactions_Then_OwnershipAndActualScriptSurvive()
    {
        // Arrange
        var key = new Key().PubKey.ToBytes()[1..];
        var script = new Script(new byte[] { 0x51, 0x20 }.Concat(key).ToArray());
        var address = script.GetDestinationAddress(Network.RegTest)!.ToString();
        var txid = new TxId(RandomUtils.GetUInt256().ToBytes());
        AddEvent(AccountingEventKind.WalletReceived, txid, 2, 100, 42_000_000,
            ("address", address), ("silentPayment", "true"));
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);
        // Act
        var response = await connection.LightningClient.GetTransactionsAsync(new GetTransactionsRequest(), cancellationToken: Ct);
        // Assert
        var tx = Assert.Single(response.Transactions);
        Assert.Equal(42_000, tx.Amount);
        var output = Assert.Single(tx.OutputDetails);
        Assert.Equal(address, output.Address);
        Assert.True(output.IsOurAddress);
        Assert.Equal(OutputScriptType.ScriptTypeWitnessV1Taproot, output.OutputType);
        Assert.Equal(Convert.ToHexStringLower(script.ToBytes()), output.PkScript);
        Assert.Equal(2, output.OutputIndex);
    }
}