using System.Text.Json;
using NBitcoin;

namespace NLightning.Integration.Tests.BOLT3;

using Domain.Bitcoin.ValueObjects;
using Domain.Money;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using Infrastructure.Bitcoin.InteractiveTx;

/// <summary>
/// BOLT 3 Appendix G (Dual Funded Transaction Test Vectors) through the interactive-tx builder (splicing plan IT2-T2):
/// inputs and outputs sorted by <c>serial_id</c>, locktime 120, feerate 253, the vector's unsigned transaction, txid
/// and signed transaction byte for byte. The vector is <c>BOLT3/Vectors/appendix-g.json</c> (see the README there for
/// the upstream commit).
/// </summary>
public class AppendixGVectorTests
{
    private static readonly AppendixG s_vector = AppendixG.Load();

    [Fact]
    public void Given_AppendixG_When_Building_Then_UnsignedTxAndTxIdEqualTheVector()
    {
        // Arrange
        var builder = new InteractiveTxBuilder();

        // Act
        var constructed = builder.Build(s_vector.Locktime, s_vector.Inputs, s_vector.Outputs);

        // Assert
        Assert.Equal(s_vector.UnsignedTx, Convert.ToHexString(constructed.UnsignedTx).ToLowerInvariant());
        Assert.Equal(s_vector.TxId, uint256.Parse(s_vector.TxIdHex).ToBytes());
        Assert.Equal(s_vector.TxId, (byte[])constructed.TxId);
        Assert.Equal(120u, constructed.Locktime);
        Assert.Equal(new ulong[] { 11, 20 }, constructed.Inputs.Select(i => i.SerialId));
        Assert.Equal(new ulong[] { 30, 33, 44 }, constructed.Outputs.Select(o => o.SerialId));
        Assert.Equal(2u, constructed.SharedOutputIndex);
    }

    [Fact]
    public void Given_AppendixGInputsInAnyOrder_When_Building_Then_TheTransactionIsTheSame()
    {
        // Arrange
        var builder = new InteractiveTxBuilder();
        var reversedInputs = s_vector.Inputs.Reverse().ToList();
        var shuffledOutputs = new[] { s_vector.Outputs[2], s_vector.Outputs[0], s_vector.Outputs[1] };

        // Act
        var constructed = builder.Build(s_vector.Locktime, reversedInputs, shuffledOutputs);

        // Assert
        Assert.Equal(s_vector.UnsignedTx, Convert.ToHexString(constructed.UnsignedTx).ToLowerInvariant());
    }

    [Fact]
    public void Given_AppendixGWitnesses_When_Finalizing_Then_SignedTxEqualsTheVector()
    {
        // Arrange
        var builder = new InteractiveTxBuilder();
        var constructed = builder.Build(s_vector.Locktime, s_vector.Inputs, s_vector.Outputs);

        // Act
        var signed = builder.Finalize(constructed, s_vector.WitnessesBySerialId);

        // Assert
        Assert.Equal(s_vector.SignedTx, Convert.ToHexString(signed.RawTxBytes).ToLowerInvariant());
        Assert.Equal(s_vector.TxId, (byte[])signed.TxId);
    }

    [Fact]
    public void Given_TheSignedAppendixGTx_When_Verifying_Then_EveryInputIsValidAndTheFeerateIs253()
    {
        // Arrange
        var builder = new InteractiveTxBuilder();
        var constructed = builder.Build(s_vector.Locktime, s_vector.Inputs, s_vector.Outputs);
        var signed = Transaction.Load(builder.Finalize(constructed, s_vector.WitnessesBySerialId).RawTxBytes,
                                      Network.RegTest);
        var spent = constructed.Inputs
                               .Select(i => new TxOut(Money.Satoshis(i.Amount.Satoshi),
                                                      new Script((byte[])i.ScriptPubKey)))
                               .ToArray();

        // Act
        var validator = signed.CreateValidator(spent);
        var results = Enumerable.Range(0, signed.Inputs.Count).Select(i => validator.ValidateInput(i).Error).ToList();
        var feeSat = spent.Sum(o => o.Value.Satoshi) - signed.Outputs.Sum(o => o.Value.Satoshi);
        var weight = signed.GetSerializedSize(TransactionOptions.None) * 3
                   + signed.GetSerializedSize(TransactionOptions.Witness);

        // Assert
        Assert.All(results, e => Assert.True(e is null or ScriptError.OK, $"input error {e}"));
        Assert.Equal(s_vector.OpenerFeeSat + s_vector.AccepterFeeSat, feeSat);
        Assert.True(feeSat * 1000 >= 253L * weight, $"fee {feeSat} sat over {weight} WU is below 253 sat/kw");
        Assert.True(constructed.EstimatedWeight >= weight - 2,
                    $"estimated {constructed.EstimatedWeight} WU, signed {weight} WU");
        Assert.All(signed.Inputs, i => Assert.Equal(0xFFFFFFFDu, i.Sequence.Value));
        Assert.Equal(120u, signed.LockTime.Value);
        Assert.Equal(2u, signed.Version);
    }

    [Fact]
    public void Given_TheAccepterPrivateKey_When_SigningItsInput_Then_TheWitnessEqualsTheVector()
    {
        // Arrange
        var builder = new InteractiveTxBuilder();
        var constructed = builder.Build(s_vector.Locktime, s_vector.Inputs, s_vector.Outputs);
        var tx = Transaction.Load(constructed.UnsignedTx, Network.RegTest);
        var accepterInput = constructed.Inputs.Single(i => i.SerialId == 11);
        var key = Key.Parse(s_vector.AccepterPrivKey, Network.RegTest);
        var coin = new Coin(tx.Inputs[0].PrevOut, new TxOut(Money.Satoshis(accepterInput.Amount.Satoshi),
                                                            new Script((byte[])accepterInput.ScriptPubKey)));

        // Act
        var hash = tx.GetSignatureHash(coin.GetScriptCode(), 0, SigHash.All, coin.TxOut, HashVersion.WitnessV0);
        var signature = key.Sign(hash, new SigningOptions(SigHash.All, false));
        var witness = InteractiveTxTransactionReader.WriteWitness(
            new WitScript(Op.GetPushOp(signature.ToBytes()), Op.GetPushOp(key.PubKey.ToBytes())));

        // Assert
        Assert.Equal(s_vector.WitnessesBySerialId[11], new Witness(witness));
    }

    [Fact]
    public void Given_TheAppendixGParentTx_When_Inspecting_Then_OnlyItsWitnessOutputsAreAccepted()
    {
        // Arrange
        var inspector = new PrevTxInspector();

        // Act
        var results = Enumerable.Range(0, 5).Select(v => inspector.Inspect(s_vector.ParentTx, (uint)v)).ToList();

        // Assert
        Assert.All(results, r => Assert.Equal(s_vector.ParentTxId, r.TxId is { } id ? (byte[])id : null));
        Assert.All(results, r => Assert.Equal(5, r.OutputCount));
        Assert.True(results[0].IsValid && results[0].IsWitnessProgram); // P2WSH
        Assert.True(results[2].IsValid && results[2].IsWitnessProgram); // P2WPKH
        Assert.All(new[] { results[1], results[3], results[4] }, r => Assert.False(r.IsValid || r.IsWitnessProgram));
        Assert.Equal(LightningMoney.Satoshis(250_000_000), results[0].Amount);
    }

    private sealed record AppendixG(
        uint Locktime,
        IReadOnlyList<InteractiveTxInput> Inputs,
        IReadOnlyList<InteractiveTxOutput> Outputs,
        IReadOnlyDictionary<ulong, Witness> WitnessesBySerialId,
        byte[] ParentTx,
        byte[] ParentTxId,
        string AccepterPrivKey,
        long OpenerFeeSat,
        long AccepterFeeSat,
        string UnsignedTx,
        string TxIdHex,
        byte[] TxId,
        string SignedTx)
    {
        public static AppendixG Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "BOLT3", "Vectors", "appendix-g.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;

            var parentTxId = uint256.Parse(root.GetProperty("parent_txid").GetString()!).ToBytes();
            var inputs = new List<InteractiveTxInput>();
            var witnesses = new Dictionary<ulong, Witness>();
            string? accepterKey = null;
            foreach (var input in root.GetProperty("inputs").EnumerateArray())
            {
                var serialId = input.GetProperty("serial_id").GetUInt64();
                inputs.Add(new InteractiveTxInput(serialId, Party(input), parentTxId,
                                                  input.GetProperty("prevtx_vout").GetUInt32(),
                                                  input.GetProperty("sequence").GetUInt32(),
                                                  LightningMoney.Satoshis(
                                                      input.GetProperty("amount_satoshis").GetInt64()),
                                                  Hex(input, "script_pubkey"),
                                                  Hex(root, "parent_tx"), false));
                witnesses[serialId] = new Witness(Hex(input, "witness_data"));
                if (input.TryGetProperty("privkey", out var privKey))
                    accepterKey = privKey.GetString();
            }

            var outputs = root.GetProperty("outputs").EnumerateArray()
                              .Select(o => new InteractiveTxOutput(o.GetProperty("serial_id").GetUInt64(), Party(o),
                                                                   LightningMoney.Satoshis(
                                                                       o.GetProperty("sats").GetInt64()),
                                                                   Hex(o, "script"),
                                                                   o.GetProperty("shared").GetBoolean()))
                              .ToList();

            var txIdHex = root.GetProperty("txid").GetString()!;
            return new AppendixG(root.GetProperty("locktime").GetUInt32(), inputs, outputs, witnesses,
                                 Hex(root, "parent_tx"), parentTxId, accepterKey!,
                                 root.GetProperty("expected_opener_fee_satoshis").GetInt64(),
                                 root.GetProperty("expected_accepter_fee_satoshis").GetInt64(),
                                 root.GetProperty("unsigned_tx").GetString()!, txIdHex,
                                 uint256.Parse(txIdHex).ToBytes(), root.GetProperty("signed_tx").GetString()!);
        }

        // The vector is seen from the opener's side
        private static InteractiveTxParty Party(JsonElement element) =>
            element.GetProperty("party").GetString() == "opener" ? InteractiveTxParty.Local : InteractiveTxParty.Remote;

        private static byte[] Hex(JsonElement element, string name) =>
            Convert.FromHexString(element.GetProperty(name).GetString()!);
    }
}