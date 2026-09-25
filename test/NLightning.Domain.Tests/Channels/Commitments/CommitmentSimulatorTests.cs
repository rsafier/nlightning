using System.Collections.Concurrent;

namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Channels.Commitments;

/// <summary>
/// Plan N4-T5: seeded random runs of two engines (<see cref="CommitmentPairSimulator"/>), every invariant after every
/// step. A failure message starts with "seed N"; replay it with <see cref="Given_Seed_When_Replayed_Then_InvariantsHold"/>.
/// </summary>
public class CommitmentSimulatorTests(ITestOutputHelper output)
{
    /// <summary>Seeds in the default (CI) run.</summary>
    private const int DefaultSeeds = 500;

    /// <summary>Seeds in the long run (<c>--filter "Category=Long"</c>).</summary>
    private const int LongSeeds = 10_000;

    [Fact]
    public void Given_500Seeds_When_Simulated_Then_InvariantsHoldAndEveryPathIsCovered()
    {
        // Arrange / Act
        var stats = RunSeeds(0, DefaultSeeds);

        // Assert: the schedule really exercised what it claims to.
        Assert.True(stats.Adds > 4_000 && stats.AddsRefused > 2_000, stats.ToString());
        Assert.True(stats.Fulfills > 800 && stats.Fails > 500 && stats.FailMalformeds > 200, stats.ToString());
        Assert.True(stats.FeeUpdates > 1_000 && stats.FeesRefused > 100, stats.ToString());
        Assert.True(stats.CrossedCommitments > 2_000 && stats.FeeOnlyCommitments > 150, stats.ToString());
        Assert.True(stats.Disconnects > 600 && stats.DroppedOnDisconnect > 1_000, stats.ToString());
        Assert.True(stats.RetransmittedCommitments > 200 && stats.RetransmittedRevocations > 150, stats.ToString());
        Assert.True(stats.RetransmittedUpdates > 2_500 && stats.GateRefusals > 200, stats.ToString());
        Assert.True(stats.MaxOpenHtlcs > 30, stats.ToString());
    }

    [Fact(Explicit = true)]
    [Trait("Category", "Long")]
    public void Given_10kSeeds_When_Simulated_Then_InvariantsHold()
    {
        // Arrange / Act / Assert
        RunSeeds(DefaultSeeds, LongSeeds);
    }

    [Fact]
    public void Given_483Limit_When_BothSidesFill_Then_RefusedExactlyAtLimitAndSettled()
    {
        // Arrange: a large channel where both sides accept the BOLT 2 maximum of 483 HTLCs; 500 sat HTLCs are trimmed
        // (they still count towards max_accepted_htlcs), 1000 sat ones are not.
        var party = new CommitmentParty(546, 10_000, 1, 483, ulong.MaxValue);
        var config = new SimulatorConfig(10_000_000_000, 6_000_000_000, 253, false, party, party, null, null, 0, 0,
                                         StepWeights.Balanced);
        var simulator = new CommitmentPairSimulator(483, config);

        // Act
        simulator.RunFill(500_000, 1_000_000);

        // Assert: one node held all 2 x 483 HTLCs at once, and all were settled.
        Assert.Equal(2 * 483, simulator.Stats.MaxOpenHtlcs);
        Assert.Empty(simulator.Alice.State.Htlcs);
        Assert.Empty(simulator.Bob.State.Htlcs);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(152)] // B2-FEE-R03 was checked on the prospective commitment, rejecting an honest update_fee
    [InlineData(1795)] // B2-ADD-R02 counted our crossed (unsigned) adds in the funder's fee, rejecting an honest add
    [InlineData(76293)] // same, for our adds the funder acked but had not signed when it re-sent its add on reconnect
    [InlineData(1116)] // funder update_fee crossed non-funder adds: the commit-time fee check fails the channel
    public void Given_Seed_When_Replayed_Then_InvariantsHold(int seed)
    {
        // Arrange
        var simulator = new CommitmentPairSimulator(seed);

        // Act / Assert
        simulator.Run();
    }

    /// <summary>Runs the seeds in parallel; on failure reports the lowest failing seed (with its trace) and how many
    /// failed.</summary>
    private SimulatorStats RunSeeds(int first, int count)
    {
        var total = new SimulatorStats();
        var failures = new ConcurrentBag<SimulatorFailureException>();
        // A few threads only: the runs allocate a lot and more threads mostly wait for the workstation GC.
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(4, Environment.ProcessorCount) };
        Parallel.For(first, first + count, options, () => new SimulatorStats(), (seed, _, stats) =>
        {
            var simulator = new CommitmentPairSimulator(seed);
            try
            {
                simulator.Run();
                stats.Add(simulator.Stats);
            }
            catch (SimulatorFailureException e)
            {
                failures.Add(e);
            }

            return stats;
        }, stats =>
        {
            lock (total)
                total.Add(stats);
        });

        output.WriteLine($"seeds {first}..{first + count - 1}: {total}");
        if (!failures.IsEmpty)
        {
            var seeds = string.Join(", ", failures.Select(f => f.Seed).Order().Take(20));
            Assert.Fail($"{failures.Count} seeds failed ({seeds}); first: {failures.MinBy(f => f.Seed)!.Message}");
        }

        return total;
    }
}