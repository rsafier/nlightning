using NBitcoin;
using NLightning.Integration.Tests.BOLT3;
using NLightning.Integration.Tests.BOLT3.Mocks;
using NLightning.Integration.Tests.BOLT3.Vectors;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Onchain;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Channels.Enums;
using Domain.Onchain.Enums;
using Infrastructure.Bitcoin.Onchain;

/// <summary>
/// BOLT 5 plan O2-T4: every output of every BOLT 3 Appendix C commitment maps to the right descriptor, as our local
/// commitment (node A), as the peer's commitment (node B) and as a revoked one; trimmed HTLCs land in the no-output set.
/// </summary>
public class CommitmentOutputMapperTests
{
    public static TheoryData<string> AppendixCNames => Bolt3SpecVectors.AppendixCNames;

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_AppendixCCommitment_When_MappedAsLocal_Then_EveryVoutHasItsDescriptor(string name)
    {
        // Arrange: node A holds the vector commitment (number 42)
        var vector = Bolt3SpecVectors.GetAppendixC(name);
        var harness = new Bolt3VectorHarness(vector, false);
        var onChain = ChainTxMapper.FromTransaction(Transaction.Parse(vector.CommitTxHex, Network.Main));
        var mapper = new CommitmentOutputMapper(harness.Factory, harness.CommitmentBuilder);

        // Act
        var map = mapper.Map(harness.Channel, harness.Spec, CommitmentCase.Local,
                             Bolt3AppendixCVectors.CommitmentNumber, null, onChain);

        // Assert: the rebuilt txid is the spec's, and each vout has exactly one descriptor
        Assert.True(map.TxIdMatched);
        Assert.Equal(onChain.TxId, map.ExpectedTxId);
        Assert.Empty(map.UnmappedVouts);
        Assert.Equal(Enumerable.Range(0, onChain.Outputs.Count).Select(v => (uint)v), map.Outputs.Select(o => o.Vout));
        Assert.Equal(Bolt3TestCommitmentKeyDerivationService.LocalPerCommitmentPoint, map.PerCommitmentPoint);
        foreach (var output in map.Outputs)
        {
            Assert.Equal(onChain.Outputs[(int)output.Vout].AmountSat, output.AmountSat);
            Assert.Equal(onChain.Outputs[(int)output.Vout].ScriptPubKey, output.ScriptPubKey);
        }

        // HTLC outputs: offered by us -> HTLC-timeout, received -> HTLC-success, with the vector's HTLC tx as second level
        foreach (var htlcTx in vector.HtlcTxs)
        {
            var descriptor = map.GetOutput((uint)htlcTx.OutputIndex)!;
            Assert.Equal(htlcTx.IsSuccess!.Value
                             ? OutputDescriptorKind.LocalReceivedHtlc
                             : OutputDescriptorKind.LocalOfferedHtlc, descriptor.Kind);
            Assert.Equal((ulong)htlcTx.HtlcId!.Value, descriptor.Htlc!.Value.Id);
            Assert.Equal(htlcTx.IsSuccess.Value ? HtlcDirection.Incoming : HtlcDirection.Outgoing,
                         descriptor.Htlc.Value.Direction);
            Assert.NotNull(descriptor.WitnessScript);
            Assert.Equal(new Script(descriptor.WitnessScript!).WitHash.ScriptPubKey.ToBytes(), descriptor.ScriptPubKey);

            var secondLevel = descriptor.SecondLevel!;
            Assert.Equal(htlcTx.IsSuccess.Value ? HtlcTransactionType.Success : HtlcTransactionType.Timeout,
                         secondLevel.Type);
            var built = harness.HtlcBuilder.Build(secondLevel);
            Assert.Equal(Transaction.Parse(htlcTx.TxHex, Network.Main).GetHash().ToBytes(),
                         (byte[])built.Transaction.TxId);
        }

        // to_local (when not trimmed: the funder's may be) is ours after the CSV delay, to_remote is the peer's
        var model = harness.CreateCommitmentModel();
        var nonHtlc = map.Outputs.Where(o => o.Htlc is null).ToList();
        var toLocal = nonHtlc.SingleOrDefault(o => o.Kind == OutputDescriptorKind.DelayedToLocal);
        Assert.Equal(model.ToLocalOutput is not null, toLocal is not null);
        if (toLocal is not null)
        {
            Assert.Equal((ulong)model.ToLocalOutput!.Amount.Satoshi, toLocal.AmountSat);
            Assert.Equal(Bolt3AppendixCVectors.LocalDelay, toLocal.CsvDelay);
            Assert.Equal(new Script(toLocal.WitnessScript!).WitHash.ScriptPubKey.ToBytes(), toLocal.ScriptPubKey);
        }

        var peerOutput = Assert.Single(nonHtlc, o => o.Kind == OutputDescriptorKind.PeerOutput);
        Assert.Equal((ulong)model.ToRemoteOutput!.Amount.Satoshi, peerOutput.AmountSat);
        Assert.Equal(vector.HtlcTxs.Count + nonHtlc.Count, map.Outputs.Count);

        // Trimmed HTLCs have no output
        Assert.Equal(vector.HtlcIds.Select(id => (ulong)id)
                           .Where(id => vector.HtlcTxs.All(h => (ulong)h.HtlcId!.Value != id))
                           .OrderBy(id => id),
                     map.HtlcsWithoutOutput.Select(h => h.Id).OrderBy(id => id));
    }

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_AppendixCCommitment_When_MappedAsRemote_Then_ToRemoteIsOursAndHtlcsAreClaims(string name)
    {
        // Arrange: we are node B; node A's commitment 42 is our remote commitment
        var vector = Bolt3SpecVectors.GetAppendixC(name);
        var harness = new Bolt3VectorHarness(vector, false, asNodeB: true);
        var onChain = ChainTxMapper.FromTransaction(Transaction.Parse(vector.CommitTxHex, Network.Main));
        var mapper = new CommitmentOutputMapper(harness.Factory, harness.CommitmentBuilder);

        // Act
        var map = mapper.Map(harness.Channel, harness.Spec, CommitmentCase.Remote,
                             Bolt3AppendixCVectors.CommitmentNumber,
                             Bolt3TestCommitmentKeyDerivationService.LocalPerCommitmentPoint, onChain);

        // Assert
        Assert.True(map.TxIdMatched);
        Assert.Empty(map.UnmappedVouts);
        Assert.Equal(onChain.Outputs.Count, map.Outputs.Count);
        foreach (var htlcTx in vector.HtlcTxs)
        {
            var descriptor = map.GetOutput((uint)htlcTx.OutputIndex)!;

            // Node A's HTLC-success outputs are HTLCs we (B) offered: a "received" output there, our timeout claim
            Assert.Equal(htlcTx.IsSuccess!.Value
                             ? OutputDescriptorKind.RemoteReceivedHtlc
                             : OutputDescriptorKind.RemoteOfferedHtlc, descriptor.Kind);
            Assert.Equal(htlcTx.IsSuccess.Value ? HtlcDirection.Outgoing : HtlcDirection.Incoming,
                         descriptor.Htlc!.Value.Direction);
            Assert.Null(descriptor.SecondLevel);
        }

        // Node A's to_local (when not trimmed) is the peer's; to_remote is ours
        var model = harness.CreateCommitmentModel();
        Assert.Equal(model.ToLocalOutput is null ? 0 : 1,
                     map.Outputs.Count(o => o.Kind == OutputDescriptorKind.PeerOutput));

        // static_remotekey: the vectors' to_remote pays node B's payment_basepoint itself (P2WPKH, no script; this is
        // the key the plan's §1.8 asked about)
        var toRemote = Assert.Single(map.Outputs, o => o.Kind == OutputDescriptorKind.PaymentToRemote);
        Assert.Null(toRemote.WitnessScript);
        Assert.Equal(Bolt3AppendixCVectors.NodeBPaymentBasepoint.WitHash.ScriptPubKey.ToBytes(), toRemote.ScriptPubKey);
        Assert.Equal((ulong)model.ToRemoteOutput!.Amount.Satoshi, toRemote.AmountSat);
    }

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_AppendixCCommitment_When_MappedAsRevoked_Then_EveryOutputIsPenalizedOrOurs(string name)
    {
        // Arrange: node A broadcast commitment 42 after revoking it
        var vector = Bolt3SpecVectors.GetAppendixC(name);
        var harness = new Bolt3VectorHarness(vector, false, asNodeB: true);
        var onChain = ChainTxMapper.FromTransaction(Transaction.Parse(vector.CommitTxHex, Network.Main));
        var mapper = new CommitmentOutputMapper(harness.Factory, harness.CommitmentBuilder);

        // Act
        var map = mapper.Map(harness.Channel, harness.Spec, CommitmentCase.Revoked,
                             Bolt3AppendixCVectors.CommitmentNumber,
                             Bolt3TestCommitmentKeyDerivationService.LocalPerCommitmentPoint, onChain);

        // Assert
        Assert.Empty(map.UnmappedVouts);
        Assert.All(map.Outputs, o => Assert.True(o.IsOurs));
        Assert.All(map.Outputs.Where(o => o.Htlc is not null),
                   o => Assert.Equal(OutputDescriptorKind.RevokedHtlc, o.Kind));
        Assert.Equal(vector.HtlcTxs.Count, map.Outputs.Count(o => o.Kind == OutputDescriptorKind.RevokedHtlc));
        var toLocal = map.Outputs.SingleOrDefault(o => o.Kind == OutputDescriptorKind.RevokedToLocal);
        if (toLocal is not null)
            Assert.Equal(Bolt3AppendixCVectors.LocalDelay, toLocal.CsvDelay);
    }

    public static TheoryData<string> AppendixFNames => Bolt3SpecVectors.AppendixFNames;

    [Theory]
    [MemberData(nameof(AppendixFNames))]
    public void Given_AppendixFAnchorCommitment_When_MappedAsLocal_Then_AnchorsAndCsvOneHtlcsMapped(string name)
    {
        // Arrange: option_anchors (O7 shapes; the mapper already carries them)
        var vector = Bolt3SpecVectors.GetAppendixF(name);
        var harness = new Bolt3VectorHarness(vector, true);
        var onChain = ChainTxMapper.FromTransaction(Transaction.Parse(vector.CommitTxHex, Network.Main));
        var mapper = new CommitmentOutputMapper(harness.Factory, harness.CommitmentBuilder);

        // Act
        var map = mapper.Map(harness.Channel, harness.Spec, CommitmentCase.Local,
                             Bolt3AppendixCVectors.CommitmentNumber, null, onChain);

        // Assert
        Assert.True(map.TxIdMatched);
        Assert.Empty(map.UnmappedVouts);
        Assert.Equal(onChain.Outputs.Count, map.Outputs.Count);
        Assert.All(map.Outputs, o => Assert.True(o.HasAnchors));
        Assert.All(map.Outputs.Where(o => o.Kind is OutputDescriptorKind.OurAnchor or OutputDescriptorKind.PeerAnchor),
                   o => Assert.Equal((330UL, (ushort)16), (o.AmountSat, o.CsvDelay)));
        Assert.All(map.Outputs.Where(o => o.Htlc is not null), o => Assert.Equal(1, o.CsvDelay));
        Assert.Equal(vector.HtlcTxs.Select(h => (uint)h.OutputIndex).OrderBy(v => v),
                     map.Outputs.Where(o => o.Htlc is not null).Select(o => o.Vout));
    }

    [Fact]
    public void Given_OnChainTxIdDiffers_When_Mapping_Then_OutputsMatchedByScript()
    {
        // Arrange: same outputs, other locktime (as a rebuild that differs, risk §8.1)
        var vector = Bolt3SpecVectors.GetAppendixC("commitment tx with seven outputs untrimmed (maximum feerate)");
        var harness = new Bolt3VectorHarness(vector, false);
        var tx = Transaction.Parse(vector.CommitTxHex, Network.Main);
        tx.LockTime = new LockTime(tx.LockTime.Value + 1);
        var onChain = ChainTxMapper.FromTransaction(tx);
        var mapper = new CommitmentOutputMapper(harness.Factory, harness.CommitmentBuilder);

        // Act
        var map = mapper.Map(harness.Channel, harness.Spec, CommitmentCase.Local,
                             Bolt3AppendixCVectors.CommitmentNumber, null, onChain);

        // Assert
        Assert.False(map.TxIdMatched);
        Assert.Empty(map.UnmappedVouts);
        Assert.Equal(7, map.Outputs.Count);
        foreach (var htlcTx in vector.HtlcTxs)
            Assert.Equal((ulong)htlcTx.HtlcId!.Value, map.GetOutput((uint)htlcTx.OutputIndex)!.Htlc!.Value.Id);

        // The second-level transactions spend the transaction that is on chain
        Assert.All(map.Outputs.Where(o => o.SecondLevel is not null),
                   o => Assert.Equal(onChain.TxId, o.SecondLevel!.CommitmentTxId));
    }

    [Fact]
    public void Given_OnChainTxWithUnknownAndMissingOutputs_When_Mapping_Then_ReportedSeparately()
    {
        // Arrange: drop HTLC output #2 (HTLC 2) and add a foreign output
        var vector = Bolt3SpecVectors.GetAppendixC("commitment tx with seven outputs untrimmed (maximum feerate)");
        var harness = new Bolt3VectorHarness(vector, false);
        var tx = Transaction.Parse(vector.CommitTxHex, Network.Main);
        var dropped = vector.HtlcTxs.Single(h => h.HtlcId == 2);
        tx.Outputs.RemoveAt(dropped.OutputIndex);
        tx.Outputs.Add(new TxOut(Money.Satoshis(1234), new Key().PubKey.WitHash.ScriptPubKey));
        var onChain = ChainTxMapper.FromTransaction(tx);
        var mapper = new CommitmentOutputMapper(harness.Factory, harness.CommitmentBuilder);

        // Act
        var map = mapper.Map(harness.Channel, harness.Spec, CommitmentCase.Local,
                             Bolt3AppendixCVectors.CommitmentNumber, null, onChain);

        // Assert
        Assert.Equal([(uint)(tx.Outputs.Count - 1)], map.UnmappedVouts);
        Assert.Equal(2UL, Assert.Single(map.HtlcsWithoutOutput).Id);
        Assert.Equal(tx.Outputs.Count - 1, map.Outputs.Count);
    }

    [Fact]
    public void Given_NoOnChainTx_When_Mapping_Then_RebuiltOutputsAreMapped()
    {
        // Arrange
        var vector = Bolt3SpecVectors.GetAppendixC("commitment tx with all five HTLCs untrimmed (minimum feerate)");
        var harness = new Bolt3VectorHarness(vector, false);
        var mapper = new CommitmentOutputMapper(harness.Factory, harness.CommitmentBuilder);

        // Act
        var map = mapper.Map(harness.Channel, harness.Spec, CommitmentCase.Local,
                             Bolt3AppendixCVectors.CommitmentNumber, null);

        // Assert
        Assert.True(map.TxIdMatched);
        Assert.Null(map.OnChainTxId);
        Assert.Equal(Transaction.Parse(vector.CommitTxHex, Network.Main).GetHash().ToBytes(), (byte[])map.ExpectedTxId);
        Assert.Equal(7, map.Outputs.Count);
        Assert.Empty(map.HtlcsWithoutOutput);
    }

    [Fact]
    public void Given_PeerCommitmentWithoutPoint_When_Mapping_Then_Throws()
    {
        // Arrange
        var vector = Bolt3SpecVectors.GetAppendixC("simple commitment tx with no HTLCs");
        var harness = new Bolt3VectorHarness(vector, false, asNodeB: true);
        var mapper = new CommitmentOutputMapper(harness.Factory, harness.CommitmentBuilder);

        // Act / Assert
        Assert.Throws<ArgumentNullException>(() => mapper.Map(harness.Channel, harness.Spec, CommitmentCase.Remote,
                                                              Bolt3AppendixCVectors.CommitmentNumber, null));
    }

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_PeerCommitmentWeCannotRebuild_When_FindingPaymentToRemote_Then_OurOutputFound(string name)
    {
        // Arrange: data loss; only our payment_basepoint is known (static_remotekey)
        var vector = Bolt3SpecVectors.GetAppendixC(name);
        var harness = new Bolt3VectorHarness(vector, false, asNodeB: true);
        var onChain = ChainTxMapper.FromTransaction(Transaction.Parse(vector.CommitTxHex, Network.Main));
        var mapper = new CommitmentOutputMapper(harness.Factory, harness.CommitmentBuilder);
        var expected = mapper.Map(harness.Channel, harness.Spec, CommitmentCase.Remote,
                                  Bolt3AppendixCVectors.CommitmentNumber,
                                  Bolt3TestCommitmentKeyDerivationService.LocalPerCommitmentPoint, onChain)
                             .Outputs.Where(o => o.Kind == OutputDescriptorKind.PaymentToRemote)
                             .Select(o => (o.Vout, o.AmountSat));

        // Act
        var found = mapper.FindPaymentToRemote(onChain, Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes(), false);

        // Assert
        Assert.Equal(expected, found.Select(o => (o.Vout, o.AmountSat)));
        Assert.All(found, o => Assert.Equal(OutputDescriptorKind.PaymentToRemote, o.Kind));
    }

    [Fact]
    public void Given_RawBytes_When_Parsing_Then_ChainTxMatchesTransaction()
    {
        // Arrange
        var vector = Bolt3SpecVectors.GetAppendixC("commitment tx with all five HTLCs untrimmed (minimum feerate)");
        var tx = Transaction.Parse(vector.CommitTxHex, Network.Main);

        // Act
        var ok = ChainTxMapper.TryParse(tx.ToBytes(), out var chainTx);

        // Assert: txid in internal order, locktime/sequence raw, witness items in order
        Assert.True(ok);
        Assert.Equal(tx.GetHash().ToBytes(), (byte[])chainTx!.TxId);
        Assert.Equal(0x2052193eU, chainTx.LockTime);
        Assert.Equal(0x802bb038U, Assert.Single(chainTx.Inputs).Sequence);
        Assert.Equal(tx.Inputs[0].PrevOut.Hash.ToBytes(), (byte[])chainTx.Inputs[0].PreviousTxId);
        Assert.Equal(4, chainTx.Inputs[0].Witness.Count);
        Assert.Empty(chainTx.Inputs[0].Witness[0]);
        Assert.Equal(tx.Outputs.Count, chainTx.Outputs.Count);
        Assert.Equal(0, chainTx.IndexOfInputSpending(chainTx.Inputs[0].PreviousTxId, 0));
        Assert.Equal(-1, chainTx.IndexOfInputSpending(chainTx.Inputs[0].PreviousTxId, 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("00")]
    [InlineData("deadbeef")]
    public void Given_Garbage_When_Parsing_Then_FalseNeverThrows(string hex)
    {
        // Act
        var ok = ChainTxMapper.TryParse(Convert.FromHexString(hex), out var chainTx);

        // Assert
        Assert.False(ok);
        Assert.Null(chainTx);
    }
}