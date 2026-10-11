using NBitcoin;
using NLightning.Integration.Tests.BOLT3;
using NLightning.Integration.Tests.BOLT3.Mocks;
using NLightning.Integration.Tests.BOLT3.Vectors;
using NLightning.Tests.Utils.Vectors;

namespace NLightning.Infrastructure.Bitcoin.Tests.Builders;

using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Onchain.Enums;
using Domain.Onchain.Factories;
using Domain.Onchain.Fees;
using Domain.Onchain.Models;
using Infrastructure.Bitcoin.Outputs;
using Signers;

/// <summary>
/// BOLT 5 plan O3-T1 (our <c>to_local</c> and second-level outputs after the CSV), O4-T1 (our <c>to_remote</c> on the
/// peer's commitment) and O4-T2 (direct HTLC claims on the peer's commitment): every spend is signed by
/// <c>LocalLightningSigner.SignSweepInput</c> with the Appendix C basepoint secrets and executed by NBitcoin's script
/// interpreter against the Appendix C output it spends.
/// </summary>
public class SweepTransactionBuilderTests
{
    private const uint FeeratePerKw = 253;

    private static readonly CompactPubKey s_localPoint = Bolt3TestCommitmentKeyDerivationService.LocalPerCommitmentPoint;

    public static TheoryData<string> AppendixCNames => Bolt3SpecVectors.AppendixCNames;
    public static TheoryData<string> AppendixFNames => Bolt3SpecVectors.AppendixFNames;

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_AppendixCToLocal_When_SweptAtCsv_Then_ScriptPassesWithLocalDelayedKey(string name)
    {
        // Arrange: we are node A and our commitment 42 is on chain
        var (commitTx, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.GetAppendixC(name), CommitmentCase.Local);
        var toLocal = map.Outputs.SingleOrDefault(o => o.Kind == OutputDescriptorKind.DelayedToLocal);
        if (toLocal is null)
        {
            // Two vectors trim the funder's to_local: every output is mapped and none is ours to sweep
            Assert.Empty(map.UnmappedVouts);
            Assert.DoesNotContain(map.Outputs, o => o.IsOurs);
            return;
        }

        var input = SweepInputFactory.ToLocal(toLocal, map.OnChainTxId!.Value, map.PerCommitmentPoint);
        var builder = SweepTestKit.CreateBuilder();

        // Act
        var unsigned = builder.Build([input], SweepTestKit.Destination, FeeratePerKw);
        var signed = builder.Sign(unsigned, new AppendixCSweepSigner(asNodeB: false), ChannelId.Zero);

        // Assert: nSequence = to_self_delay (B5-LCL-01), witness <local_delayedsig> <> <script>
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        Assert.Equal(2U, tx.Version);
        Assert.Equal((uint)Bolt3AppendixCVectors.LocalDelay, tx.Inputs[0].Sequence.Value);
        Assert.Equal(3, tx.Inputs[0].WitScript.PushCount);
        Assert.Empty(tx.Inputs[0].WitScript[1]);
        Assert.True(SweepTestKit.Verifies(signed, 0, commitTx.Outputs[(int)toLocal.Vout], out var error),
                    error.ToString());

        // The estimate uses 73-byte signatures: never below the real weight (a DER signature is 71-73 bytes)
        var weight = SweepTestKit.Weight(signed);
        Assert.InRange(unsigned.EstimatedWeight - weight, 0, 4);
        Assert.Equal(SweepWeights.FeeSat(FeeratePerKw, unsigned.EstimatedWeight), unsigned.FeeSat);
        Assert.Equal(toLocal.AmountSat - unsigned.FeeSat, (ulong)tx.Outputs[0].Value.Satoshi);
    }

    [Fact]
    public void Given_ToLocal_When_SequenceBelowCsv_Then_ScriptFails()
    {
        // Arrange: one block early (nSequence = to_self_delay - 1)
        var (commitTx, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.AppendixC[0], CommitmentCase.Local);
        var toLocal = map.Outputs.Single(o => o.Kind == OutputDescriptorKind.DelayedToLocal);
        var input = SweepInputFactory.ToLocal(toLocal, map.OnChainTxId!.Value, map.PerCommitmentPoint) with
        {
            CsvDelay = (ushort)(Bolt3AppendixCVectors.LocalDelay - 1)
        };
        var builder = SweepTestKit.CreateBuilder();

        // Act
        var signed = builder.Sign(builder.Build([input], SweepTestKit.Destination, FeeratePerKw),
                                  new AppendixCSweepSigner(asNodeB: false), ChannelId.Zero);

        // Assert
        Assert.False(SweepTestKit.Verifies(signed, 0, commitTx.Outputs[(int)toLocal.Vout], out var error));
        Assert.Equal(ScriptError.UnsatisfiedLockTime, error);
    }

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_AppendixCHtlcTransactions_When_SecondLevelOutputSweptAtCsv_Then_ScriptPasses(string name)
    {
        // Arrange: our HTLC-timeout/success transactions confirmed; their output is to_local-shaped (B5-LCL-LO-03)
        var vector = Bolt3SpecVectors.GetAppendixC(name);
        var builder = SweepTestKit.CreateBuilder();
        var signer = new AppendixCSweepSigner(asNodeB: false);
        var witnessScript = new ToLocalOutput(LightningMoney.Zero, Bolt3AppendixCVectors.NodeADelayedPubkey,
                                              Bolt3AppendixCVectors.NodeARevocationPubkey,
                                              Bolt3AppendixCVectors.LocalDelay).RedeemScript;

        foreach (var (htlcTx, _) in SweepTestKit.HtlcTransactions(vector))
        {
            var output = htlcTx.Outputs[0];
            Assert.Equal(witnessScript.WitHash.ScriptPubKey, output.ScriptPubKey);
            var input = SweepInputFactory.SecondLevelOutput(SweepTestKit.TxIdOf(htlcTx), (ulong)output.Value.Satoshi,
                                                            witnessScript.ToBytes(), Bolt3AppendixCVectors.LocalDelay,
                                                            s_localPoint);

            // Act
            var signed = builder.Sign(builder.Build([input], SweepTestKit.Destination, FeeratePerKw), signer,
                                      ChannelId.Zero);

            // Assert
            Assert.True(SweepTestKit.Verifies(signed, 0, output, out var error), error.ToString());
        }
    }

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_PeerCommitment_When_ToRemoteSwept_Then_P2WpkhSpendPasses(string name)
    {
        // Arrange: we are node B; node A's commitment is on chain (D5, B5-RMT-02)
        var (commitTx, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.GetAppendixC(name), CommitmentCase.Remote);
        var toRemote = map.Outputs.Single(o => o.Kind == OutputDescriptorKind.PaymentToRemote);
        var input = SweepInputFactory.ToRemote(toRemote, map.OnChainTxId!.Value,
                                               Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes());
        var builder = SweepTestKit.CreateBuilder();

        // Act
        var signed = builder.Sign(builder.Build([input], SweepTestKit.Destination, FeeratePerKw),
                                  new AppendixCSweepSigner(asNodeB: true), ChannelId.Zero);

        // Assert: <sig> <payment_basepoint>, no relative lock
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        Assert.Equal(SweepFeePolicy.RbfSequence, tx.Inputs[0].Sequence.Value);
        Assert.Equal(2, tx.Inputs[0].WitScript.PushCount);
        Assert.True(SweepTestKit.Verifies(signed, 0, commitTx.Outputs[(int)toRemote.Vout], out var error),
                    error.ToString());
    }

    [Theory]
    [MemberData(nameof(AppendixFNames))]
    public void Given_AnchorPeerCommitment_When_ToRemoteSwept_Then_CsvOneSpendPasses(string name)
    {
        // Arrange: option_anchors to_remote is <remotepubkey> OP_CHECKSIGVERIFY 1 OP_CSV (O7 shape, O4-T1)
        var (commitTx, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.GetAppendixF(name), CommitmentCase.Remote,
                                                        hasAnchors: true);
        var toRemote = map.Outputs.SingleOrDefault(o => o.Kind == OutputDescriptorKind.PaymentToRemote);
        if (toRemote is null)
        {
            // "single anchor": node B's to_remote is below the dust limit, so only node A's outputs exist
            Assert.Empty(map.UnmappedVouts);
            return;
        }

        var input = SweepInputFactory.ToRemote(toRemote, map.OnChainTxId!.Value,
                                               Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes());
        var builder = SweepTestKit.CreateBuilder();

        // Act
        var signed = builder.Sign(builder.Build([input], SweepTestKit.Destination, FeeratePerKw),
                                  new AppendixCSweepSigner(asNodeB: true), ChannelId.Zero);

        // Assert
        var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
        Assert.Equal(1U, tx.Inputs[0].Sequence.Value);
        Assert.True(SweepTestKit.Verifies(signed, 0, commitTx.Outputs[(int)toRemote.Vout], out var error),
                    error.ToString());
    }

    [Theory]
    [MemberData(nameof(AppendixFNames))]
    public void Given_AnchorPeerCommitment_When_HtlcsClaimedDirectly_Then_CsvOneSpendsPassAndCsvZeroFails(string name)
    {
        // Arrange: option_anchors HTLC scripts end with `1 OP_CHECKSEQUENCEVERIFY OP_DROP` (O4-T2 on LND's default
        // channel type): node B's timeout claims of the HTLCs it offered and preimage claims of those node A offered
        var (commitTx, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.GetAppendixF(name), CommitmentCase.Remote,
                                                        hasAnchors: true);
        var txId = map.OnChainTxId!.Value;
        var claims = map.Outputs
                        .Where(o => o.Kind is OutputDescriptorKind.RemoteReceivedHtlc
                                        or OutputDescriptorKind.RemoteOfferedHtlc)
                        .Select(o => (Output: o, Input: o.Kind == OutputDescriptorKind.RemoteReceivedHtlc
                                                            ? SweepInputFactory.HtlcTimeoutClaim(o, txId,
                                                                map.PerCommitmentPoint)
                                                            : SweepInputFactory.HtlcPreimageClaim(
                                                                o, txId, map.PerCommitmentPoint,
                                                                Bolt3VectorHarness.Preimages[(int)o.Htlc!.Value.Id])))
                        .ToList();
        var builder = SweepTestKit.CreateBuilder();
        var signer = new AppendixCSweepSigner(asNodeB: true);

        foreach (var (output, input) in claims)
        {
            var spent = commitTx.Outputs[(int)output.Vout];
            var withoutCsv = input with { CsvDelay = 0 }; // nSequence 0xFFFFFFFD: the CSV disable flag is set

            // Act
            var signed = builder.Sign(builder.Build([input], SweepTestKit.Destination, FeeratePerKw), signer,
                                      ChannelId.Zero);
            var tooEarly = builder.Sign(builder.Build([withoutCsv], SweepTestKit.Destination, FeeratePerKw), signer,
                                        ChannelId.Zero);

            // Assert
            Assert.Equal((ushort)1, input.CsvDelay);
            Assert.Equal(1U, Transaction.Load(signed.RawTxBytes, Network.Main).Inputs[0].Sequence.Value);
            Assert.True(SweepTestKit.Verifies(signed, 0, spent, out var error), $"{output.Kind}: {error}");
            Assert.False(SweepTestKit.Verifies(tooEarly, 0, spent, out error));
            Assert.Equal(ScriptError.UnsatisfiedLockTime, error);
        }

        // Every untrimmed HTLC output of the commitment was claimed
        Assert.Equal(map.Outputs.Count(o => o.Htlc is not null), claims.Count);
    }

    [Fact]
    public void Given_AppendixF_When_Mapped_Then_BothAnchorHtlcClaimKindsAreCovered()
    {
        // Arrange / Act: the theory above runs over these; make sure it exercises both script branches
        var kinds = Bolt3SpecVectors.AppendixF
                                    .Select(v => SweepTestKit.MapAppendixC(v, CommitmentCase.Remote,
                                                                           hasAnchors: true))
                                    .SelectMany(m => m.Map.Outputs)
                                    .Select(o => o.Kind)
                                    .ToList();

        // Assert
        Assert.Contains(OutputDescriptorKind.RemoteReceivedHtlc, kinds);
        Assert.Contains(OutputDescriptorKind.RemoteOfferedHtlc, kinds);
    }

    [Theory]
    [MemberData(nameof(AppendixCNames))]
    public void Given_PeerCommitment_When_AllHtlcsClaimedGroupedByKind_Then_EveryInputPasses(string name)
    {
        // Arrange: node B claims every HTLC of node A's commitment directly: the preimage claims of those node A
        // offered (B5-RMT-RO-01) with its to_remote in one tx at nLockTime 0, the timeout claims of those it offered
        // (B5-RMT-LO-02) in one tx per cltv_expiry, locked at that expiry
        var (commitTx, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.GetAppendixC(name), CommitmentCase.Remote);
        var txId = map.OnChainTxId!.Value;
        var immediate = new List<(SweepInput Input, TxOut Spent)>();
        var timeouts = new List<(SweepInput Input, TxOut Spent)>();
        foreach (var output in map.Outputs.Where(o => o.IsOurs))
        {
            var spent = commitTx.Outputs[(int)output.Vout];
            switch (output.Kind)
            {
                case OutputDescriptorKind.PaymentToRemote:
                    immediate.Add((SweepInputFactory.ToRemote(output, txId,
                                                              Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes()),
                                   spent));
                    break;
                case OutputDescriptorKind.RemoteOfferedHtlc:
                    immediate.Add((SweepInputFactory.HtlcPreimageClaim(
                                       output, txId, map.PerCommitmentPoint,
                                       Bolt3VectorHarness.Preimages[(int)output.Htlc!.Value.Id]), spent));
                    break;
                case OutputDescriptorKind.RemoteReceivedHtlc:
                    timeouts.Add((SweepInputFactory.HtlcTimeoutClaim(output, txId, map.PerCommitmentPoint), spent));
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected {output.Kind}");
            }
        }

        var batches = timeouts.GroupBy(t => t.Input.CltvExpiry).Select(g => g.ToList()).ToList();
        if (immediate.Count > 0)
            batches.Add(immediate);

        var builder = SweepTestKit.CreateBuilder();
        var signer = new AppendixCSweepSigner(asNodeB: true);

        foreach (var batch in batches)
        {
            var inputs = batch.Select(b => b.Input).ToList();

            // Act
            var unsigned = builder.Build(inputs, SweepTestKit.Destination, FeeratePerKw);
            var signed = builder.Sign(unsigned, signer, ChannelId.Zero);

            // Assert: a timeout batch locks at its expiry, the rest at 0 (below every preimage claim's deadline)
            var tx = Transaction.Load(signed.RawTxBytes, Network.Main);
            var expectedLockTime = inputs[0].SpendKind == SweepSpendKind.HtlcTimeoutClaim ? inputs[0].CltvExpiry : 0U;
            Assert.Equal(expectedLockTime, tx.LockTime.Value);
            SweepTestKit.AssertAllInputsVerify(signed, batch.Select(b => b.Spent).ToList());
            Assert.InRange(unsigned.EstimatedWeight - SweepTestKit.Weight(signed), 0, 4 * inputs.Count);
        }
    }

    [Fact]
    public void Given_TimeoutClaimAndPreimageClaim_When_BatchedTogether_Then_Refused()
    {
        // Arrange: the timeout claim's nLockTime would hold the preimage claim back past its deadline
        var (_, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.AppendixC[1], CommitmentCase.Remote);
        var txId = map.OnChainTxId!.Value;
        var timeout = SweepInputFactory.HtlcTimeoutClaim(
            map.Outputs.First(o => o.Kind == OutputDescriptorKind.RemoteReceivedHtlc), txId, map.PerCommitmentPoint);
        var offered = map.Outputs.First(o => o.Kind == OutputDescriptorKind.RemoteOfferedHtlc);
        var preimage = SweepInputFactory.HtlcPreimageClaim(offered, txId, map.PerCommitmentPoint,
                                                           Bolt3VectorHarness.Preimages[(int)offered.Htlc!.Value.Id]);
        var builder = SweepTestKit.CreateBuilder();

        // Act / Assert
        Assert.Throws<ArgumentException>(() => builder.Build([timeout, preimage], SweepTestKit.Destination,
                                                             FeeratePerKw));
        Assert.Throws<ArgumentException>(() => builder.BuildWithFee([preimage, timeout], SweepTestKit.Destination,
                                                                    1_000));
    }

    [Fact]
    public void Given_TimeoutClaimAndToRemote_When_BatchedTogether_Then_Refused()
    {
        // Arrange
        var (_, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.AppendixC[1], CommitmentCase.Remote);
        var txId = map.OnChainTxId!.Value;
        var timeout = SweepInputFactory.HtlcTimeoutClaim(
            map.Outputs.First(o => o.Kind == OutputDescriptorKind.RemoteReceivedHtlc), txId, map.PerCommitmentPoint);
        var toRemote = SweepInputFactory.ToRemote(
            map.Outputs.First(o => o.Kind == OutputDescriptorKind.PaymentToRemote), txId,
            Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes());

        // Act / Assert
        Assert.Throws<ArgumentException>(() => SweepTestKit.CreateBuilder()
                                                           .Build([toRemote, timeout], SweepTestKit.Destination,
                                                                  FeeratePerKw));
    }

    [Fact]
    public void Given_TimeoutClaimsWithDifferentExpiries_When_BatchedTogether_Then_Refused()
    {
        // Arrange: the earlier claim would wait for the later expiry
        var (_, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.AppendixC[1], CommitmentCase.Remote);
        var first = SweepInputFactory.HtlcTimeoutClaim(
            map.Outputs.First(o => o.Kind == OutputDescriptorKind.RemoteReceivedHtlc), map.OnChainTxId!.Value,
            map.PerCommitmentPoint);
        var later = first with { Vout = first.Vout + 100, CltvExpiry = first.CltvExpiry + 1 };

        // Act / Assert
        Assert.Throws<ArgumentException>(() => SweepTestKit.CreateBuilder()
                                                           .Build([first, later], SweepTestKit.Destination,
                                                                  FeeratePerKw));
    }

    [Fact]
    public void Given_TimeoutClaimsWithSameExpiry_When_Batched_Then_LockTimeIsThatExpiry()
    {
        // Arrange
        var (_, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.AppendixC[1], CommitmentCase.Remote);
        var first = SweepInputFactory.HtlcTimeoutClaim(
            map.Outputs.First(o => o.Kind == OutputDescriptorKind.RemoteReceivedHtlc), map.OnChainTxId!.Value,
            map.PerCommitmentPoint);
        var twin = first with { Vout = first.Vout + 100 };

        // Act
        var unsigned = SweepTestKit.CreateBuilder().Build([first, twin], SweepTestKit.Destination, FeeratePerKw);

        // Assert
        var tx = Transaction.Load(unsigned.Transaction.RawTxBytes, Network.Main);
        Assert.Equal(first.CltvExpiry, tx.LockTime.Value);
    }

    [Fact]
    public void Given_PreimageClaim_When_LockTimeAtOrAboveItsCltv_Then_Refused()
    {
        // Arrange: from cltv_expiry on, the peer can take the HTLC back through its HTLC-timeout path
        var (_, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.AppendixC[1], CommitmentCase.Remote);
        var offered = map.Outputs.First(o => o.Kind == OutputDescriptorKind.RemoteOfferedHtlc);
        var preimage = SweepInputFactory.HtlcPreimageClaim(offered, map.OnChainTxId!.Value, map.PerCommitmentPoint,
                                                           Bolt3VectorHarness.Preimages[(int)offered.Htlc!.Value.Id]);
        var builder = SweepTestKit.CreateBuilder();

        // Act
        var below = builder.Build([preimage], SweepTestKit.Destination, FeeratePerKw, preimage.CltvExpiry - 1);

        // Assert
        Assert.True(preimage.CltvExpiry > 0);
        Assert.Equal(preimage.CltvExpiry - 1,
                     Transaction.Load(below.Transaction.RawTxBytes, Network.Main).LockTime.Value);
        Assert.Throws<ArgumentException>(() => builder.Build([preimage], SweepTestKit.Destination, FeeratePerKw,
                                                             preimage.CltvExpiry));
        Assert.Throws<ArgumentException>(() => builder.BuildWithFee([preimage], SweepTestKit.Destination, 1_000,
                                                                    preimage.CltvExpiry + 1));
    }

    [Fact]
    public void Given_TimeoutClaim_When_LockTimeBelowCltv_Then_ScriptFails()
    {
        // Arrange: HTLC 0 (cltv_expiry 500), which node B offered: claimed one block too early
        var (commitTx, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.AppendixC[1], CommitmentCase.Remote);
        var htlcOutput = map.Outputs.First(o => o.Kind == OutputDescriptorKind.RemoteReceivedHtlc);
        var input = SweepInputFactory.HtlcTimeoutClaim(htlcOutput, map.OnChainTxId!.Value, map.PerCommitmentPoint);
        var early = input with { CltvExpiry = input.CltvExpiry - 1 };
        var builder = SweepTestKit.CreateBuilder();
        var signer = new AppendixCSweepSigner(asNodeB: true);

        // Act
        var onTime = builder.Sign(builder.Build([input], SweepTestKit.Destination, FeeratePerKw), signer,
                                  ChannelId.Zero);
        var tooEarly = builder.Sign(builder.Build([early], SweepTestKit.Destination, FeeratePerKw), signer,
                                    ChannelId.Zero);

        // Assert
        var spent = commitTx.Outputs[(int)htlcOutput.Vout];
        Assert.True(SweepTestKit.Verifies(onTime, 0, spent, out var error), error.ToString());
        Assert.False(SweepTestKit.Verifies(tooEarly, 0, spent, out error));
        Assert.Equal(ScriptError.UnsatisfiedLockTime, error);
    }

    [Fact]
    public void Given_PreimageClaim_When_PreimageIsWrong_Then_ScriptFails()
    {
        // Arrange: an HTLC node A offered, claimed with another HTLC's preimage
        var (commitTx, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.AppendixC[1], CommitmentCase.Remote);
        var htlcOutput = map.Outputs.First(o => o.Kind == OutputDescriptorKind.RemoteOfferedHtlc);
        var wrong = Bolt3VectorHarness.Preimages[((int)htlcOutput.Htlc!.Value.Id + 1) % 7];
        var input = SweepInputFactory.HtlcPreimageClaim(htlcOutput, map.OnChainTxId!.Value, map.PerCommitmentPoint,
                                                        wrong);
        var builder = SweepTestKit.CreateBuilder();

        // Act
        var signed = builder.Sign(builder.Build([input], SweepTestKit.Destination, FeeratePerKw),
                                  new AppendixCSweepSigner(asNodeB: true), ChannelId.Zero);

        // Assert
        Assert.False(SweepTestKit.Verifies(signed, 0, commitTx.Outputs[(int)htlcOutput.Vout], out _));
    }

    [Fact]
    public void Given_ClaimWithAnotherPoint_When_Signing_Then_SignerRefuses()
    {
        // Arrange: the HTLC key tweaked by the wrong point is not in the script
        var (_, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.AppendixC[1], CommitmentCase.Remote);
        var htlcOutput = map.Outputs.First(o => o.Kind == OutputDescriptorKind.RemoteReceivedHtlc);
        var input = SweepInputFactory.HtlcTimeoutClaim(htlcOutput, map.OnChainTxId!.Value,
                                                       Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes());
        var builder = SweepTestKit.CreateBuilder();
        var unsigned = builder.Build([input], SweepTestKit.Destination, FeeratePerKw);

        // Act / Assert
        Assert.Throws<Domain.Exceptions.SignerException>(
            () => builder.Sign(unsigned, new AppendixCSweepSigner(asNodeB: true), ChannelId.Zero));
    }

    [Fact]
    public void Given_AbsoluteFee_When_BuildingWithFee_Then_OutputIsTotalMinusFee()
    {
        // Arrange
        var (_, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.AppendixC[0], CommitmentCase.Remote);
        var toRemote = map.Outputs.Single(o => o.Kind == OutputDescriptorKind.PaymentToRemote);
        var input = SweepInputFactory.ToRemote(toRemote, map.OnChainTxId!.Value,
                                               Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes());

        // Act
        var unsigned = SweepTestKit.CreateBuilder().BuildWithFee([input], SweepTestKit.Destination, 1234);

        // Assert
        Assert.Equal(1234UL, unsigned.FeeSat);
        Assert.Equal(toRemote.AmountSat - 1234, unsigned.OutputSat);
    }

    [Fact]
    public void Given_ValueBelowFeeAndDust_When_Building_Then_Throws()
    {
        // Arrange: 600 sat do not pay ~110 vB at 2,000 sat/kw and a 294-sat P2WPKH output
        var input = new SweepInput(new byte[32], 0, 600, SweepSpendKind.PaymentToRemote, null,
                                   WitnessPubKey: Bolt3AppendixCVectors.NodeBPaymentBasepoint.ToBytes());

        // Act / Assert
        Assert.Throws<ArgumentException>(() => SweepTestKit.CreateBuilder()
                                                           .Build([input], SweepTestKit.Destination, 2_000));
    }

    private static readonly SweepInput[] s_incompleteInputs =
    [
        new(new byte[32], 0, 10_000, SweepSpendKind.DelayedOutput, [0x51], 0,
            PerCommitmentPoint: Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes()),
        new(new byte[32], 0, 10_000, SweepSpendKind.DelayedOutput, [0x51], 144),
        new(new byte[32], 0, 10_000, SweepSpendKind.PaymentToRemote, null),
        new(new byte[32], 0, 10_000, SweepSpendKind.PaymentToRemote, [0x51]),
        new(new byte[32], 0, 10_000, SweepSpendKind.HtlcTimeoutClaim, [0x51],
            PerCommitmentPoint: Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes()),
        new(new byte[32], 0, 10_000, SweepSpendKind.HtlcPreimageClaim, [0x51],
            PerCommitmentPoint: Bolt3AppendixCVectors.NodeAFundingPubkey.ToBytes(), Preimage: [1, 2]),
        new(new byte[32], 0, 10_000, SweepSpendKind.RevokedDelayedOutput, [0x51]),
        new(new byte[32], 0, 10_000, SweepSpendKind.RevokedHtlc, [0x51], PerCommitmentSecret: new Secret(new byte[32])),
        new(new byte[32], 0, 10_000, SweepSpendKind.RevokedHtlc, [0x51], PerCommitmentSecret: new Secret(new byte[32]),
            WitnessPubKey: Bolt3AppendixCVectors.NodeARevocationPubkey.ToBytes())
    ];

    public static TheoryData<int> IncompleteInputIndexes => new(Enumerable.Range(0, s_incompleteInputs.Length));

    [Theory]
    [MemberData(nameof(IncompleteInputIndexes))]
    public void Given_InputMissingWhatItsKindNeeds_When_Building_Then_Throws(int index)
    {
        // Arrange: no CSV, no point, no pubkey, no anchor CSV, no cltv, a short preimage, no secret, no revocation
        // pubkey, a revocation pubkey whose HASH160 is not in the script
        var input = s_incompleteInputs[index];

        // Act / Assert
        Assert.Throws<ArgumentException>(() => SweepTestKit.CreateBuilder()
                                                           .Build([input], SweepTestKit.Destination, FeeratePerKw));
    }

    [Fact]
    public void Given_NoInput_When_Building_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => SweepTestKit.CreateBuilder()
                                                           .Build([], SweepTestKit.Destination, FeeratePerKw));
    }

    [Fact]
    public void Given_WrongSignatureCount_When_AddingWitnesses_Then_Throws()
    {
        // Arrange
        var (_, map) = SweepTestKit.MapAppendixC(Bolt3SpecVectors.AppendixC[0], CommitmentCase.Remote);
        var toRemote = map.Outputs.Single(o => o.Kind == OutputDescriptorKind.PaymentToRemote);
        var builder = SweepTestKit.CreateBuilder();
        var unsigned = builder.Build([SweepInputFactory.ToRemote(toRemote, map.OnChainTxId!.Value,
                                                                  Bolt3AppendixCVectors.NodeBPaymentBasepoint
                                                                     .ToBytes())], SweepTestKit.Destination,
                                     FeeratePerKw);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => builder.AddWitnesses(unsigned, []));
    }
}