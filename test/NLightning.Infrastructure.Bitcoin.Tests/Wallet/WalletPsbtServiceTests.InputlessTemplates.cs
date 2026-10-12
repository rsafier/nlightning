using NBitcoin;

namespace NLightning.Infrastructure.Bitcoin.Tests.Wallet;

using Domain.Bitcoin.Enums;
using Domain.Exceptions;

public partial class WalletPsbtServiceTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    public async Task InputlessCoinSelectPreservesTransactionAndPsbtMetadata(int version)
    {
        var (coin, _, _) = AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var transaction = InputlessTransaction();
        byte[] bytes;
        if (version == 0) bytes = InputlessV0(transaction);
        else
        {
            var packet = PSBT.FromTransaction(transaction, Network.RegTest, PSBTVersion.PSBTv2);
            packet.Unknown.Add([0x50], [7, 8]);
            packet.Outputs[0].Unknown.Add([0x50], [9]);
            bytes = packet.ToBytes();
        }
        var result = await _service.FundPsbtAsync(Request(20_000) with
        { CoinSelectTemplate = true, TemplatePsbt = bytes, ExistingChangeOutputIndex = 1 }, Ct);
        var funded = PSBT.Load(result.Psbt, Network.RegTest);
        var actual = funded.GetGlobalTransaction();
        Assert.Equal(PSBTVersion.PSBTv0, funded.Version);
        Assert.Equal(1u, actual.Version);
        Assert.Equal(new LockTime(42), actual.LockTime);
        Assert.Equal(new OutPoint(new uint256((byte[])coin.TxId), coin.Index), Assert.Single(actual.Inputs).PrevOut);
        Assert.Single(result.Leases);
        Assert.Equal(2, actual.Outputs.Count);
        Assert.Equal(20_000, actual.Outputs[0].Value.Satoshi);
        Assert.Equal(transaction.Outputs[1].ScriptPubKey, actual.Outputs[1].ScriptPubKey);
        Assert.Equal(80_000 - result.Fee.Satoshi, actual.Outputs[1].Value.Satoshi);
        Assert.Equal(new byte[] { 7, 8 }, funded.Unknown[new byte[] { 0x50 }]);
        Assert.Equal(new byte[] { 9 }, funded.Outputs[0].Unknown[new byte[] { 0x50 }]);
        var finalized = await _service.FinalizePsbtAsync(result.Psbt, Ct);
        var signed = Transaction.Load(finalized.RawFinalTx, Network.RegTest);
        Assert.True(signed.CreateValidator([funded.Inputs[0].WitnessUtxo!]).ValidateInput(0).Error is null or ScriptError.OK);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("trailing")]
    [InlineData("noncanonical")]
    public async Task MalformedInputlessV0RefusesBeforeLeasing(string mutation)
    {
        AddWalletUtxo(AddressType.P2Wpkh, 0, 100_000);
        var bytes = InputlessV0(InputlessTransaction(), duplicate: mutation == "duplicate");
        if (mutation == "trailing") bytes = [.. bytes, 0];
        if (mutation == "noncanonical") bytes = [.. bytes.AsSpan(0, 5), 0xfd, 1, 0, .. bytes.AsSpan(6)];
        var error = await Assert.ThrowsAsync<WalletPsbtException>(() => _service.FundPsbtAsync(Request(20_000) with
        { CoinSelectTemplate = true, TemplatePsbt = bytes, ExistingChangeOutputIndex = 1 }, Ct));
        Assert.Equal(WalletPsbtError.InvalidArgument, error.Error);
        Assert.Empty(_stored);
    }

    private static Transaction InputlessTransaction()
    {
        var transaction = Network.RegTest.CreateTransaction();
        transaction.Version = 1;
        transaction.LockTime = new LockTime(42);
        transaction.Outputs.Add(Money.Satoshis(20_000), s_destination);
        transaction.Outputs.Add(Money.Zero, s_destination);
        return transaction;
    }

    // BIP174 unsigned transaction encoding excludes witness serialization, including the zero-input marker ambiguity.
    private static byte[] InputlessV0(Transaction transaction, bool duplicate = false)
    {
        using var raw = new MemoryStream();
        transaction.ReadWrite(new BitcoinStream(raw, true) { TransactionOptions = TransactionOptions.None });
        var unsigned = raw.ToArray();
        if (unsigned.Length >= 253) throw new InvalidOperationException("Fixture expects a small unsigned transaction.");
        using var encoded = new MemoryStream();
        using var writer = new BinaryWriter(encoded);
        writer.Write(new byte[] { 0x70, 0x73, 0x62, 0x74, 0xff, 1, 0 });
        writer.Write((byte)unsigned.Length);
        writer.Write(unsigned);
        writer.Write(new byte[] { 1, 0x50, 2, 7, 8 });
        if (duplicate) writer.Write(new byte[] { 1, 0x50, 2, 7, 8 });
        writer.Write((byte)0);
        writer.Write(new byte[] { 1, 0x50, 1, 9, 0 });
        writer.Write((byte)0);
        return encoded.ToArray();
    }
}