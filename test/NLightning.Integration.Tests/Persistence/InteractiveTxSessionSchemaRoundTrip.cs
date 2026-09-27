using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace NLightning.Integration.Tests.Persistence;

using Domain.Bitcoin.ValueObjects;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Money;
using Domain.Protocol.InteractiveTx;
using Domain.Protocol.InteractiveTx.Enums;
using Domain.Protocol.InteractiveTx.Models;
using Infrastructure.Persistence.Contexts;
using Infrastructure.Repositories.Database.Channel;

/// <summary>
/// Provider-agnostic proof for migration <c>AddInteractiveTxSessions</c> (splicing plan IT3-T2), shared by the SQLite
/// test and the Docker Postgres test: the schema right before it moves forward, then a session with every field set
/// and a minimal one round-trip field by field (times to the tick, every blob byte-exact), an update replaces every
/// mutable field, the list reads are oldest first, the unresolved read leaves out aborted and resolved sessions, a
/// duplicate key fails the save and a delete removes the row.
/// </summary>
internal static class InteractiveTxSessionSchemaRoundTrip
{
    private const string MigrationName = "_AddInteractiveTxSessions";

    public static async Task AssertAsync(Func<NLightningDbContext> contextFactory, CancellationToken cancellationToken)
    {
        // Arrange: the schema right before AddInteractiveTxSessions, then the migration
        await using (var context = contextFactory())
        {
            var migrations = context.Database.GetMigrations().ToList();
            var target = migrations.Single(m => m.EndsWith(MigrationName, StringComparison.Ordinal));
            var migrator = context.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[migrations.IndexOf(target) - 1], cancellationToken);

            // Act
            await migrator.MigrateAsync(cancellationToken: cancellationToken);
            Assert.Empty(await context.Database.GetPendingMigrationsAsync(cancellationToken));
        }

        var channelA = ChannelIdOf(0xA1);
        var channelB = ChannelIdOf(0xB2);
        var createdAt = new DateTimeOffset(2026, 9, 27, 10, 11, 12, TimeSpan.Zero).AddTicks(1_234_567);
        var full = FullSession(channelA, createdAt);
        var minimal = MinimalSession(channelA, createdAt.AddMinutes(-5));
        var aborted = MinimalSession(channelB, createdAt.AddMinutes(1)) with
        {
            State = InteractiveTxSessionState.Aborted
        };
        var resolved = MinimalSession(channelB, createdAt.AddMinutes(2)) with
        {
            State = InteractiveTxSessionState.Signed,
            ResolvedAt = createdAt.AddHours(3).AddTicks(9)
        };

        await using (var context = contextFactory())
        {
            var repository = new InteractiveTxSessionDbRepository(context);
            repository.Add(full);
            repository.Add(minimal);
            repository.Add(aborted);
            repository.Add(resolved);

            // A staged row is visible to this unit of work's key read
            AssertSessionEqual(full, await repository.GetByIdAsync(channelA, full.SessionId));
            await context.SaveChangesAsync(cancellationToken);
        }

        // Assert: every field of both shapes
        await using (var context = contextFactory())
        {
            var repository = new InteractiveTxSessionDbRepository(context);
            AssertSessionEqual(full, await repository.GetByIdAsync(channelA, full.SessionId));
            AssertSessionEqual(minimal, await repository.GetByIdAsync(channelA, minimal.SessionId));
            AssertSessionEqual(resolved, await repository.GetByIdAsync(channelB, resolved.SessionId));
            Assert.Null(await repository.GetByIdAsync(channelB, full.SessionId));
            Assert.Null(await repository.GetByIdAsync(channelA, Guid.NewGuid()));

            // Oldest first
            var ofA = await repository.GetByChannelIdAsync(channelA);
            Assert.Equal([minimal.SessionId, full.SessionId], ofA.Select(s => s.SessionId));
            Assert.Equal(2, (await repository.GetByChannelIdAsync(channelB)).Count);
            Assert.Empty(await repository.GetByChannelIdAsync(ChannelIdOf(0xC3)));

            // Neither the aborted nor the resolved one
            var unresolved = await repository.GetUnresolvedAsync();
            Assert.Equal([minimal.SessionId, full.SessionId], unresolved.Select(s => s.SessionId));
        }

        // Act: the driver's next step replaces every mutable field
        var updated = full with
        {
            Inputs = [full.Inputs[1]],
            Outputs = [],
            LocalContribution = InteractiveTxContribution.Empty,
            ConstructedTx = null,
            OurWitnesses = [],
            TheirWitnesses = null,
            OurSharedInputSignature = null,
            TheirSharedInputSignature = Signature(0x77),
            CommitmentSignedSent = false,
            TxSignaturesReceived = false,
            State = InteractiveTxSessionState.AwaitingTxSignatures,
            FeeratePerKw = 5_000,
            Locktime = 900_001,
            ResolvedAt = createdAt.AddDays(1)
        };
        await using (var context = contextFactory())
        {
            var repository = new InteractiveTxSessionDbRepository(context);
            await repository.UpdateAsync(updated);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => repository.UpdateAsync(minimal with
            {
                SessionId = Guid.NewGuid()
            }));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new InteractiveTxSessionDbRepository(context);
            AssertSessionEqual(updated, await repository.GetByIdAsync(channelA, full.SessionId));
            Assert.Equal([minimal.SessionId], (await repository.GetUnresolvedAsync()).Select(s => s.SessionId));

            // A duplicate key fails the save
            repository.Add(minimal);
            await Assert.ThrowsAnyAsync<Exception>(() => context.SaveChangesAsync(cancellationToken));
        }

        // Act: delete
        await using (var context = contextFactory())
        {
            var repository = new InteractiveTxSessionDbRepository(context);
            Assert.True(await repository.DeleteAsync(channelA, minimal.SessionId));
            Assert.Null(await repository.GetByIdAsync(channelA, minimal.SessionId));
            Assert.False(await repository.DeleteAsync(channelA, minimal.SessionId));
            Assert.False(await repository.DeleteAsync(channelA, Guid.NewGuid()));
            await context.SaveChangesAsync(cancellationToken);
        }

        await using (var context = contextFactory())
        {
            var repository = new InteractiveTxSessionDbRepository(context);
            Assert.Null(await repository.GetByIdAsync(channelA, minimal.SessionId));
            Assert.Equal([full.SessionId], (await repository.GetByChannelIdAsync(channelA)).Select(s => s.SessionId));
        }
    }

    /// <summary>A splice we initiated, past both <c>tx_signatures</c>, with every optional field set.</summary>
    internal static InteractiveTxSessionModel FullSession(ChannelId channelId, DateTimeOffset createdAt)
    {
        var sharedInput = new InteractiveTxInput(0, InteractiveTxParty.Local, TxIdOf(0x01), 1, 0xFFFFFFFD,
                                                 LightningMoney.Satoshis(1_000_000), P2Wsh(0x10), null, true);
        var walletInput = new InteractiveTxInput(2, InteractiveTxParty.Local, TxIdOf(0x02), 0, 0xFFFFFFFD,
                                                 LightningMoney.MilliSatoshis(250_000_123UL), P2Wpkh(0x20),
                                                 Enumerable.Range(0, 300).Select(i => (byte)i).ToArray(), false);
        var theirInput = new InteractiveTxInput(ulong.MaxValue, InteractiveTxParty.Remote, TxIdOf(0x03),
                                                uint.MaxValue, 0, LightningMoney.Satoshis(40_000), P2Tr(0x30),
                                                [], false);
        var sharedOutput = new InteractiveTxOutput(4, InteractiveTxParty.Local, LightningMoney.Satoshis(1_200_000),
                                                   P2Wsh(0x40), true);
        var theirChange = new InteractiveTxOutput(7, InteractiveTxParty.Remote, LightningMoney.Satoshis(546),
                                                  P2Wpkh(0x50), false);
        var contribution = new InteractiveTxContribution(
            [
                new ContributedInput(TxIdOf(0x02), 0, [0xDE, 0xAD], 0xFFFFFFFD, LightningMoney.Satoshis(250_000),
                                     P2Wpkh(0x20), 272)
            ],
            [
                new ContributedOutput(LightningMoney.Satoshis(10_000), P2Tr(0x60), true),
                new ContributedOutput(LightningMoney.MilliSatoshis(1UL), BitcoinScript.Empty, false)
            ],
            Guid.Parse("0f0e0d0c-0b0a-0908-0706-050403020100"));

        return new InteractiveTxSessionModel
        {
            ChannelId = channelId,
            SessionId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Purpose = InteractiveTxPurpose.SpliceRbf,
            IsInitiator = true,
            FeeratePerKw = uint.MaxValue,
            Locktime = 850_000,
            Inputs = [sharedInput, walletInput, theirInput],
            Outputs = [sharedOutput, theirChange],
            LocalContribution = contribution,
            ConstructedTx = new ConstructedInteractiveTx(TxIdOf(0x99),
                                                         Enumerable.Range(0, 200).Select(i => (byte)(i * 3))
                                                                   .ToArray(),
                                                         850_000, [sharedInput, walletInput, theirInput],
                                                         [sharedOutput, theirChange], 1_234, 0),
            OurWitnesses = [new Witness([0x02, 0x47, 0x30]), new Witness([])],
            TheirWitnesses = [new Witness(Enumerable.Repeat((byte)0xAB, 107).ToArray())],
            OurSharedInputSignature = Signature(0x11),
            TheirSharedInputSignature = Signature(0x22),
            CommitmentSignedSent = true,
            CommitmentSignedReceived = true,
            TxSignaturesSent = true,
            TxSignaturesReceived = true,
            State = InteractiveTxSessionState.Signed,
            CreatedAt = createdAt,
            ResolvedAt = null
        };
    }

    /// <summary>A dual-funded open's negotiation we did not initiate, right after our <c>commitment_signed</c>.</summary>
    internal static InteractiveTxSessionModel MinimalSession(ChannelId channelId, DateTimeOffset createdAt)
    {
        return new InteractiveTxSessionModel
        {
            ChannelId = channelId,
            SessionId = Guid.NewGuid(),
            Purpose = InteractiveTxPurpose.DualFund,
            IsInitiator = false,
            FeeratePerKw = 253,
            Locktime = 0,
            Inputs = [],
            Outputs = [],
            LocalContribution = InteractiveTxContribution.Empty,
            State = InteractiveTxSessionState.AwaitingCommitmentSigned,
            CreatedAt = createdAt
        };
    }

    /// <summary>Compares every field; the records hold lists and byte arrays, which record equality compares by
    /// reference.</summary>
    internal static void AssertSessionEqual(InteractiveTxSessionModel expected, InteractiveTxSessionModel? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.ChannelId, actual.ChannelId);
        Assert.Equal(expected.SessionId, actual.SessionId);
        Assert.Equal(expected.Purpose, actual.Purpose);
        Assert.Equal(expected.IsInitiator, actual.IsInitiator);
        Assert.Equal(expected.FeeratePerKw, actual.FeeratePerKw);
        Assert.Equal(expected.Locktime, actual.Locktime);
        AssertInputsEqual(expected.Inputs, actual.Inputs);
        AssertOutputsEqual(expected.Outputs, actual.Outputs);

        Assert.Equal(expected.LocalContribution.ReservationId, actual.LocalContribution.ReservationId);
        Assert.Equal(expected.LocalContribution.Inputs.Count, actual.LocalContribution.Inputs.Count);
        for (var i = 0; i < expected.LocalContribution.Inputs.Count; i++)
        {
            var e = expected.LocalContribution.Inputs[i];
            var a = actual.LocalContribution.Inputs[i];
            Assert.Equal(e.PrevTxId, a.PrevTxId);
            Assert.Equal(e.PrevTxVout, a.PrevTxVout);
            Assert.Equal(e.PrevTx, a.PrevTx);
            Assert.Equal(e.Sequence, a.Sequence);
            Assert.Equal(e.Amount, a.Amount);
            Assert.Equal(e.ScriptPubKey, a.ScriptPubKey);
            Assert.Equal(e.InputWeight, a.InputWeight);
        }

        Assert.Equal(expected.LocalContribution.Outputs, actual.LocalContribution.Outputs);

        if (expected.ConstructedTx is null)
        {
            Assert.Null(actual.ConstructedTx);
        }
        else
        {
            Assert.NotNull(actual.ConstructedTx);
            Assert.Equal(expected.ConstructedTx.TxId, actual.ConstructedTx.TxId);
            Assert.Equal(expected.ConstructedTx.UnsignedTx, actual.ConstructedTx.UnsignedTx);
            Assert.Equal(expected.ConstructedTx.Locktime, actual.ConstructedTx.Locktime);
            AssertInputsEqual(expected.ConstructedTx.Inputs, actual.ConstructedTx.Inputs);
            AssertOutputsEqual(expected.ConstructedTx.Outputs, actual.ConstructedTx.Outputs);
            Assert.Equal(expected.ConstructedTx.EstimatedWeight, actual.ConstructedTx.EstimatedWeight);
            Assert.Equal(expected.ConstructedTx.SharedOutputIndex, actual.ConstructedTx.SharedOutputIndex);
        }

        AssertWitnessesEqual(expected.OurWitnesses, actual.OurWitnesses);
        AssertWitnessesEqual(expected.TheirWitnesses, actual.TheirWitnesses);
        Assert.Equal(expected.OurSharedInputSignature, actual.OurSharedInputSignature);
        Assert.Equal(expected.TheirSharedInputSignature, actual.TheirSharedInputSignature);
        Assert.Equal(expected.CommitmentSignedSent, actual.CommitmentSignedSent);
        Assert.Equal(expected.CommitmentSignedReceived, actual.CommitmentSignedReceived);
        Assert.Equal(expected.TxSignaturesSent, actual.TxSignaturesSent);
        Assert.Equal(expected.TxSignaturesReceived, actual.TxSignaturesReceived);
        Assert.Equal(expected.State, actual.State);
        Assert.Equal(expected.CreatedAt, actual.CreatedAt);
        Assert.Equal(expected.ResolvedAt, actual.ResolvedAt);
    }

    internal static ChannelId ChannelIdOf(byte fill) => new(Enumerable.Repeat(fill, 32).ToArray());

    private static void AssertInputsEqual(IReadOnlyList<InteractiveTxInput> expected,
                                          IReadOnlyList<InteractiveTxInput> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
        {
            var e = expected[i];
            var a = actual[i];
            Assert.Equal(e.SerialId, a.SerialId);
            Assert.Equal(e.AddedBy, a.AddedBy);
            Assert.Equal(e.PrevTxId, a.PrevTxId);
            Assert.Equal(e.PrevTxVout, a.PrevTxVout);
            Assert.Equal(e.Sequence, a.Sequence);
            Assert.Equal(e.Amount, a.Amount);
            Assert.Equal(e.ScriptPubKey, a.ScriptPubKey);
            Assert.Equal(e.PrevTx, a.PrevTx);
            Assert.Equal(e.IsShared, a.IsShared);
        }
    }

    private static void AssertOutputsEqual(IReadOnlyList<InteractiveTxOutput> expected,
                                           IReadOnlyList<InteractiveTxOutput> actual)
    {
        // Records of value types only: equality is structural
        Assert.Equal(expected, actual);
    }

    private static void AssertWitnessesEqual(IReadOnlyList<Witness>? expected, IReadOnlyList<Witness>? actual)
    {
        if (expected is null)
        {
            Assert.Null(actual);
            return;
        }

        Assert.NotNull(actual);
        Assert.Equal(expected.Count, actual.Count);
        for (var i = 0; i < expected.Count; i++)
            Assert.Equal((byte[])expected[i], (byte[])actual[i]);
    }

    private static TxId TxIdOf(byte fill) => new(Enumerable.Range(0, 32).Select(i => (byte)(fill + i)).ToArray());

    private static CompactSignature Signature(byte fill) =>
        new(Enumerable.Range(0, 64).Select(i => (byte)(fill ^ i)).ToArray());

    private static BitcoinScript P2Wpkh(byte fill) => new([0x00, 0x14, .. Enumerable.Repeat(fill, 20)]);

    private static BitcoinScript P2Wsh(byte fill) => new([0x00, 0x20, .. Enumerable.Repeat(fill, 32)]);

    private static BitcoinScript P2Tr(byte fill) => new([0x51, 0x20, .. Enumerable.Repeat(fill, 32)]);
}