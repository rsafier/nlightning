namespace NLightning.Domain.Tests.Onchain;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Crypto.ValueObjects;
using Domain.Onchain.Enums;
using Domain.Onchain.Factories;
using Domain.Onchain.Models;

/// <summary>
/// BOLT 5 plan §3.3 "Resolution" column: <see cref="SweepInputFactory"/> builds each spend only from the descriptor kind
/// it resolves, and <see cref="UnsignedSweepTransaction.GetSigningContext"/> hands the signer the input's key material.
/// </summary>
public class SweepInputFactoryTests
{
    private static readonly TxId s_txId = OnchainTestData.TxIdOf(3);
    private static readonly CompactPubKey s_point = Convert.FromHexString(
        "025f7117a78150fe2ef97db7cfc83bd57b2e2c0d0dd25eaf467a4a1c2a45ce1486");
    private static readonly Secret s_secret = new(Enumerable.Repeat((byte)1, 32).ToArray());
    private static readonly byte[] s_preimage = Enumerable.Repeat((byte)2, 32).ToArray();

    [Fact]
    public void Given_Descriptors_When_Creating_Then_SpendKindCsvCltvAndKeysCarried()
    {
        // Act
        var toLocal = SweepInputFactory.ToLocal(Output(OutputDescriptorKind.DelayedToLocal, 144), s_txId, s_point);
        var toRemote = SweepInputFactory.ToRemote(Output(OutputDescriptorKind.PaymentToRemote, 0, withScript: false),
                                                  s_txId, s_point);
        var timeout = SweepInputFactory.HtlcTimeoutClaim(Output(OutputDescriptorKind.RemoteReceivedHtlc, 0,
                                                                HtlcDirection.Outgoing), s_txId, s_point);
        var preimage = SweepInputFactory.HtlcPreimageClaim(Output(OutputDescriptorKind.RemoteOfferedHtlc, 0,
                                                                  HtlcDirection.Incoming), s_txId, s_point,
                                                           s_preimage);
        var penalty = SweepInputFactory.Penalty(Output(OutputDescriptorKind.RevokedHtlc, 0, HtlcDirection.Outgoing),
                                                s_txId, s_secret, s_point);
        var secondLevel = SweepInputFactory.SecondLevelOutput(s_txId, 900, [0x51], 144, s_point);
        var secondLevelPenalty = SweepInputFactory.SecondLevelPenalty(s_txId, 900, [0x51], s_secret);

        // Assert
        Assert.Equal((SweepSpendKind.DelayedOutput, (ushort)144, s_point),
                     (toLocal.SpendKind, toLocal.CsvDelay, toLocal.PerCommitmentPoint!.Value));
        Assert.Equal((SweepSpendKind.PaymentToRemote, s_point), (toRemote.SpendKind, toRemote.WitnessPubKey!.Value));
        Assert.Null(toRemote.WitnessScript);
        Assert.Equal((SweepSpendKind.HtlcTimeoutClaim, 600U), (timeout.SpendKind, timeout.CltvExpiry));
        Assert.Equal(s_preimage, preimage.Preimage);
        Assert.Equal((SweepSpendKind.RevokedHtlc, s_secret, s_point),
                     (penalty.SpendKind, penalty.PerCommitmentSecret!.Value, penalty.WitnessPubKey!.Value));
        Assert.Equal((0U, SweepSpendKind.DelayedOutput, (ushort)144),
                     (secondLevel.Vout, secondLevel.SpendKind, secondLevel.CsvDelay));
        Assert.Equal((0U, SweepSpendKind.RevokedDelayedOutput), (secondLevelPenalty.Vout, secondLevelPenalty.SpendKind));
        Assert.Equal(7U, toLocal.Vout);
        Assert.Equal(s_txId, toLocal.TxId);
    }

    [Fact]
    public void Given_WrongDescriptorKind_When_Creating_Then_Throws()
    {
        // Arrange
        var peerOutput = Output(OutputDescriptorKind.PeerOutput, 0);

        // Act / Assert
        Assert.Throws<ArgumentException>(() => SweepInputFactory.ToLocal(peerOutput, s_txId, s_point));
        Assert.Throws<ArgumentException>(() => SweepInputFactory.ToRemote(peerOutput, s_txId, s_point));
        Assert.Throws<ArgumentException>(() => SweepInputFactory.HtlcTimeoutClaim(peerOutput, s_txId, s_point));
        Assert.Throws<ArgumentException>(() => SweepInputFactory.HtlcPreimageClaim(peerOutput, s_txId, s_point,
                                                                                   s_preimage));
        Assert.Throws<ArgumentException>(() => SweepInputFactory.Penalty(peerOutput, s_txId, s_secret, s_point));
        Assert.Throws<ArgumentException>(() => SweepInputFactory.Penalty(
                                             Output(OutputDescriptorKind.DelayedToLocal, 144), s_txId, s_secret,
                                             s_point));
    }

    [Fact]
    public void Given_ShortPreimageOrMissingHtlc_When_Creating_Then_Throws()
    {
        // Act / Assert
        Assert.Throws<ArgumentException>(() => SweepInputFactory.HtlcPreimageClaim(
                                             Output(OutputDescriptorKind.RemoteOfferedHtlc, 0,
                                                    HtlcDirection.Incoming), s_txId, s_point, [1, 2, 3]));
        Assert.Throws<ArgumentException>(() => SweepInputFactory.HtlcTimeoutClaim(
                                             Output(OutputDescriptorKind.RemoteReceivedHtlc, 0), s_txId, s_point));
        Assert.Throws<ArgumentException>(() => SweepInputFactory.ToLocal(
                                             Output(OutputDescriptorKind.DelayedToLocal, 144, withScript: false), s_txId,
                                             s_point));
    }

    [Fact]
    public void Given_UnsignedSweep_When_GettingSigningContext_Then_InputKeyMaterialAndTx()
    {
        // Arrange
        var penalty = SweepInputFactory.SecondLevelPenalty(s_txId, 900, [0x51], s_secret);
        var claim = SweepInputFactory.HtlcTimeoutClaim(Output(OutputDescriptorKind.RemoteReceivedHtlc, 0,
                                                              HtlcDirection.Outgoing), s_txId, s_point);
        var unsigned = new UnsignedSweepTransaction(new SignedTransaction(s_txId, [1, 2, 3]), [penalty, claim],
                                                    [0x00, 0x14], 1_000, 100, 800);

        // Act
        var first = unsigned.GetSigningContext(0);
        var second = unsigned.GetSigningContext(1);

        // Assert
        Assert.Equal((SweepKeyKind.Revocation, 0, 900UL, s_secret),
                     (first.KeyKind, first.InputIndex, first.AmountSat, first.PerCommitmentSecret!.Value));
        Assert.Equal((SweepKeyKind.HtlcRemotePoint, 1, s_point),
                     (second.KeyKind, second.InputIndex, second.PerCommitmentPoint!.Value));
        Assert.Equal([1, 2, 3], second.UnsignedTransaction);
        Assert.Throws<ArgumentOutOfRangeException>(() => unsigned.GetSigningContext(2));
    }

    private static CommitmentOutputDescriptor Output(OutputDescriptorKind kind, ushort csv,
                                                     HtlcDirection? direction = null, bool withScript = true) =>
        new(7, 1_000, kind, [0x00, 0x20], withScript ? [0x51] : null,
            direction is { } d ? new SpecHtlc(d, 1, 1_000_000, new byte[32], 600) : null, csv, false);
}