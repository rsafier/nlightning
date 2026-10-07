using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Exceptions;

public partial class WalletPsbtServiceTests
{
    [Fact]
    public async Task CoinSelectCreditsForeignInputsAndPreservesTheirMetadata()
    {
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        using var foreignKey = new Key();
        var foreignScript = foreignKey.PubKey.GetAddress(ScriptPubKeyType.Segwit, Network.RegTest).ScriptPubKey;
        var tx = Network.RegTest.CreateTransaction();
        var foreign = new OutPoint(uint256.One, 0);
        tx.Inputs.Add(new TxIn(foreign));
        tx.Outputs.Add(Money.Satoshis(70_000), s_destination);
        var template = PSBT.FromTransaction(tx, Network.RegTest);
        template.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(30_000), foreignScript);
        template.Inputs[0].SighashType = SigHash.All;
        var result = await _service.FundPsbtAsync(Request(70_000) with
        { CoinSelectTemplate = true, TemplatePsbt = template.ToBytes() }, Ct);
        var funded = PSBT.Load(result.Psbt, Network.RegTest);
        var final = funded.GetGlobalTransaction();
        Assert.Equal(2, final.Inputs.Count);
        Assert.Equal(foreign, final.Inputs[0].PrevOut);
        Assert.Equal(30_000, funded.Inputs[0].WitnessUtxo!.Value.Satoshi);
        Assert.Equal(foreignScript, funded.Inputs[0].WitnessUtxo!.ScriptPubKey);
        Assert.Equal(SigHash.All, funded.Inputs[0].SighashType);
        Assert.Equal(70_000, final.Outputs[0].Value.Satoshi);
        Assert.Single(result.Leases);
        Assert.Equal(130_000 - final.Outputs.Sum(o => o.Value.Satoshi), result.Fee.Satoshi);
        var signed = PSBT.Load((await _service.SignPsbtAsync(result.Psbt, Ct)).SignedPsbt, Network.RegTest);
        Assert.Empty(signed.Inputs[0].PartialSigs);
        Assert.NotEmpty(signed.Inputs[1].PartialSigs);
    }

    [Fact]
    public async Task CoinSelectOnlyUsesExistingForeignValueWhenSufficient()
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(Money.Satoshis(40_000), s_destination);
        var template = PSBT.FromTransaction(tx, Network.RegTest);
        template.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(100_000), s_destination.ScriptPubKey);
        var result = await _service.FundPsbtAsync(Request(40_000) with
        { CoinSelectTemplate = true, TemplatePsbt = template.ToBytes() }, Ct);
        var funded = PSBT.Load(result.Psbt, Network.RegTest).GetGlobalTransaction();
        Assert.Single(funded.Inputs);
        Assert.Empty(result.Leases);
        Assert.Equal(2, funded.Outputs.Count);
        Assert.Equal(100_000 - funded.Outputs.Sum(o => o.Value.Satoshi), result.Fee.Satoshi);
    }

    [Fact]
    public async Task CoinSelectAugmentsExistingChangeWithoutReplacingItsScript()
    {
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(uint256.One, 0)));
        tx.Outputs.Add(Money.Satoshis(40_000), s_destination);
        tx.Outputs.Add(Money.Zero, s_destination);
        var template = PSBT.FromTransaction(tx, Network.RegTest);
        template.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(100_000), s_destination.ScriptPubKey);
        var result = await _service.FundPsbtAsync(Request(40_000) with
        { CoinSelectTemplate = true, TemplatePsbt = template.ToBytes(), ExistingChangeOutputIndex = 1 }, Ct);
        var funded = PSBT.Load(result.Psbt, Network.RegTest).GetGlobalTransaction();
        Assert.Empty(result.Leases);
        Assert.Equal(2, funded.Outputs.Count);
        Assert.Equal(s_destination.ScriptPubKey, funded.Outputs[1].ScriptPubKey);
        Assert.Equal(60_000 - result.Fee.Satoshi, funded.Outputs[1].Value.Satoshi);
    }

    [Fact]
    public async Task CoinSelectRefusesUnleasedExistingWalletInput()
    {
        var (coin, _, _) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var tx = Network.RegTest.CreateTransaction();
        tx.Inputs.Add(new TxIn(new OutPoint(new uint256((byte[])coin.TxId), coin.Index)));
        tx.Outputs.Add(Money.Satoshis(40_000), s_destination);
        var template = PSBT.FromTransaction(tx, Network.RegTest);
        template.Inputs[0].WitnessUtxo = new TxOut(Money.Satoshis(100_000),
            BitcoinAddress.Create(coin.WalletAddress!.Address, Network.RegTest).ScriptPubKey);
        await Assert.ThrowsAsync<WalletPsbtException>(() => _service.FundPsbtAsync(Request(40_000) with
        { CoinSelectTemplate = true, TemplatePsbt = template.ToBytes() }, Ct));
        Assert.Empty(_stored);
    }
}