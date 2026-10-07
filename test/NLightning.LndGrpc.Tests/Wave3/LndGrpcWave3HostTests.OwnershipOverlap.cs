using NBitcoin;

namespace NLightning.LndGrpc.Tests.Wave3;

using Domain.Accounting.Enums;
using AddressType = Domain.Bitcoin.Enums.AddressType;
using OutPoint = NBitcoin.OutPoint;
using Domain.Bitcoin.ValueObjects;
using Domain.Bitcoin.Wallet.Interfaces;
using Domain.Bitcoin.Wallet.Models;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using LndGrpc.Macaroons;
using Testing.Lnd.Lnrpc;
using ListUnspentRequest = Testing.Lnd.Walletrpc.ListUnspentRequest;

public sealed partial class LndGrpcWave3HostTests
{
    private readonly List<ImportedTapscript> _importedScripts = [];
    private ImportedWatchIndex? _importedIndex;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Given_CanonicalAndImportedOwnershipOverlap_When_GetTransactions_Then_OutpointUnionPreservesMixedAmounts(bool silentPayment)
    {
        // Arrange: one wallet output is watched again, plus a different watch-only output in the same deposit.
        var canonical = new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        var imported = new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        var external = new Key().PubKey.WitHash.ScriptPubKey;
        var deposit = Network.RegTest.CreateTransaction();
        deposit.Inputs.Add(new TxIn(new OutPoint(RandomUtils.GetUInt256(), 0)));
        deposit.Outputs.Add(Money.Satoshis(10_000), canonical);
        deposit.Outputs.Add(Money.Satoshis(20_000), imported);
        var spend = Network.RegTest.CreateTransaction();
        spend.Inputs.Add(new TxIn(new OutPoint(deposit.GetHash(), 0)));
        spend.Inputs.Add(new TxIn(new OutPoint(deposit.GetHash(), 1)));
        spend.Outputs.Add(Money.Satoshis(10_000), external);
        spend.Outputs.Add(Money.Satoshis(5_000), canonical);
        spend.Outputs.Add(Money.Satoshis(14_000), imported);
        SetImportedBlock(canonical, imported, deposit, spend);
        var depositId = new TxId(deposit.GetHash().ToBytes());
        var spendId = new TxId(spend.GetHash().ToBytes());
        AddEvent(AccountingEventKind.WalletReceived, depositId, 0, 150, 10_000_000,
            ("address", canonical.GetDestinationAddress(Network.RegTest)!.ToString()),
            ("silentPayment", silentPayment ? "true" : "false"));
        AddEvent(AccountingEventKind.WalletOutputSpent, depositId, 0, 150, -10_000_000, ("spentBy", spendId.ToString()));
        AddEvent(AccountingEventKind.WalletReceived, spendId, 1, 150, 5_000_000,
            ("address", canonical.GetDestinationAddress(Network.RegTest)!.ToString()));
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);
        // Act
        var response = await connection.LightningClient.GetTransactionsAsync(new GetTransactionsRequest(), cancellationToken: Ct);
        // Assert
        var receipt = Assert.Single(response.Transactions, t => t.TxHash == depositId.ToString());
        Assert.Equal(30_000, receipt.Amount);
        Assert.Equal(2, receipt.OutputDetails.Count(o => o.IsOurAddress));
        var payment = Assert.Single(response.Transactions, t => t.TxHash == spendId.ToString());
        Assert.Equal(-11_000, payment.Amount);
        Assert.Equal(1_000, payment.TotalFees);
        Assert.Equal(2, payment.PreviousOutpoints.Count);
        Assert.All(payment.PreviousOutpoints, input => Assert.True(input.IsOurOutput));
        Assert.Equal(2, payment.OutputDetails.Count(o => o.IsOurAddress));
    }

    [Fact]
    public async Task Given_ImportedWatchCoversSilentCoin_When_ListUnspent_Then_OneSpendableEntryAndOneOtherWatchEntry()
    {
        // Arrange
        var key = new Key().PubKey.ToBytes()[1..];
        var canonical = new Script(new byte[] { 0x51, 0x20 }.Concat(key).ToArray());
        var imported = new Key().PubKey.GetScriptPubKey(ScriptPubKeyType.TaprootBIP86);
        var deposit = Network.RegTest.CreateTransaction();
        deposit.Inputs.Add(new TxIn(new OutPoint(RandomUtils.GetUInt256(), 0)));
        deposit.Outputs.Add(Money.Satoshis(10_000), canonical);
        deposit.Outputs.Add(Money.Satoshis(20_000), imported);
        SetImportedBlock(canonical, imported, deposit);
        var id = new TxId(deposit.GetHash().ToBytes());
        _unspent.Add(new UtxoModel(new SilentPaymentOutputModel(id, 0, key, new byte[32], 7, 10_000, 150,
            new Hash(new byte[32]))));
        _psbt.Setup(x => x.ListUnspentAsync(1, (uint)int.MaxValue, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new WalletUnspentOutput(id, 0, LightningMoney.Satoshis(10_000), AddressType.P2Tr,
                canonical.GetDestinationAddress(Network.RegTest)!.ToString(), canonical.ToBytes(), 1)]);
        using var connection = Connect(LndMacaroonFiles.ReadOnlyFileName);
        // Act
        var response = await connection.WalletKitClient.ListUnspentAsync(new ListUnspentRequest { MinConfs = 1 }, cancellationToken: Ct);
        // Assert
        Assert.Equal(2, response.Utxos.Count);
        Assert.Equal(30_000, response.Utxos.Sum(u => u.AmountSat));
        Assert.Single(response.Utxos, u => u.Outpoint.OutputIndex == 0);
    }

    private void SetImportedBlock(Script canonical, Script imported, params NBitcoin.Transaction[] transactions)
    {
        _importedScripts.Add(new ImportedTapscript(canonical.ToBytes(), new byte[32], [], 150));
        _importedScripts.Add(new ImportedTapscript(imported.ToBytes(), new byte[32], [], 150));
        var block = Network.RegTest.Consensus.ConsensusFactory.CreateBlock();
        foreach (var transaction in transactions)
            block.Transactions.Add(transaction);
        block.Header.HashMerkleRoot = block.GetMerkleRoot().Hash;
        _chain.Setup(c => c.GetBlockAsync(150)).ReturnsAsync(block);
        _chain.Setup(c => c.GetBlockHashAsync(150)).ReturnsAsync(block.GetHash());
    }
}