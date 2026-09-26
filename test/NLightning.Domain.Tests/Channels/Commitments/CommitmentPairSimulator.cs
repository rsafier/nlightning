using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;

namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Bitcoin.Transactions.Enums;
using Domain.Bitcoin.Transactions.Factories;
using Domain.Channels.Commitments;
using Domain.Channels.Commitments.Events;
using Domain.Channels.Commitments.Interfaces;
using Domain.Channels.Enums;
using Domain.Channels.ValueObjects;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;

/// <summary>Relative weights of the random step kinds (a delivery is also the fallback of an impossible step).</summary>
internal readonly record struct StepWeights(int Add, int Remove, int Fee, int Commit, int Deliver)
{
    public static readonly StepWeights Balanced = new(22, 12, 4, 17, 45);
    public static readonly StepWeights AddBurst = new(45, 5, 2, 8, 40);
    public static readonly StepWeights LazyCommit = new(22, 12, 4, 5, 57);
    public static readonly StepWeights FeeStorm = new(15, 10, 20, 15, 40);

    public int Total => Add + Remove + Fee + Commit + Deliver;
}

/// <summary>
/// Knobs of one simulated channel. <see cref="Random"/> draws a mix of anchors/legacy, small and large channels,
/// asymmetric dust limits and reserves, small and large <c>max_accepted_htlcs</c>, one-sided and balanced funding, and
/// a step mix.
/// </summary>
internal sealed record SimulatorConfig(
    ulong AliceMsat,
    ulong BobMsat,
    uint FeeratePerKw,
    bool Anchors,
    CommitmentParty AliceParty,
    CommitmentParty BobParty,
    ulong? AliceMaxDustExposureMsat,
    ulong? BobMaxDustExposureMsat,
    int Steps,
    double DisconnectRate,
    StepWeights Weights)
{
    public static SimulatorConfig Random(Random rng)
    {
        var fundingSat = rng.Next(4) == 0 ? (ulong)rng.NextInt64(20_000, 200_000)
                                          : (ulong)rng.NextInt64(200_000, 5_000_001);
        var bobMsat = rng.Next(4) switch
        {
            0 => 0UL,
            1 => (ulong)rng.NextInt64(1, (long)fundingSat * 100) * 10,
            _ => (ulong)rng.NextInt64(0, (long)fundingSat * 1_000 + 1)
        };
        var anchors = rng.Next(2) == 0;
        var reserveSat = Math.Max(354, fundingSat / 100);
        ushort[] maxAccepted = [2, 5, 12, 30, 483];

        CommitmentParty PartyFor() =>
            new((ulong)rng.Next(354, 1_500), rng.Next(3) == 0 ? 0 : reserveSat,
                rng.Next(2) == 0 ? 1 : (ulong)rng.Next(1_000, 600_000), maxAccepted[rng.Next(maxAccepted.Length)],
                rng.Next(3) == 0 ? fundingSat * 1_000 / (ulong)rng.Next(2, 10) : ulong.MaxValue);

        StepWeights[] mixes = [StepWeights.Balanced, StepWeights.AddBurst, StepWeights.LazyCommit, StepWeights.FeeStorm];
        var feeratePerKw = (uint)rng.Next(253, 15_000);

        // BOLT 2 open_channel: the funder must afford the full fee (and anchors) of the initial commitment.
        var openingCostMsat = checked(CommitmentFeeCalculator.FunderCostSatoshis(feeratePerKw, anchors, 0) * 1_000);
        bobMsat = Math.Min(bobMsat, fundingSat * 1_000 - openingCostMsat);
        return new SimulatorConfig(fundingSat * 1_000 - bobMsat, bobMsat, feeratePerKw, anchors,
                                   PartyFor(), PartyFor(),
                                   rng.Next(4) == 0 ? (ulong)rng.NextInt64(1_000_000, 50_000_000) : null,
                                   rng.Next(4) == 0 ? (ulong)rng.NextInt64(1_000_000, 50_000_000) : null,
                                   rng.Next(80, 260), rng.Next(3) == 0 ? 0.0 : 0.02,
                                   rng.Next(2) == 0 ? StepWeights.Balanced : mixes[rng.Next(mixes.Length)]);
    }
}

/// <summary>What one simulated run exercised (asserted in aggregate so the harness cannot silently stop covering a
/// path).</summary>
internal sealed class SimulatorStats
{
    public int Adds;
    public int AddsRefused;
    public int Fulfills;
    public int Fails;
    public int FailMalformeds;
    public int FeeUpdates;
    public int FeesRefused;
    public int CommitmentsSigned;
    public int CrossedCommitments;
    public int Revocations;
    public int Disconnects;
    public int RetransmittedCommitments;
    public int RetransmittedRevocations;
    public int RetransmittedUpdates;
    public int DroppedOnDisconnect;
    public int GateRefusals;
    public int MaxOpenHtlcs;
    public int FeeOnlyCommitments;
    public int LockedInEvents;
    public int FulfilledEvents;
    public int FailedEvents;
    public int SettledEvents;
    public int IncomingSettledEvents;

    /// <summary>Runs that ended with the non-funder failing the channel because the funder's update crossed its adds
    /// (see <see cref="CommitmentPairSimulator"/>).</summary>
    public int CrossedFeeFailures;

    /// <summary>Local refusals by requirement id.</summary>
    public SortedDictionary<string, int> Refusals { get; } = new(StringComparer.Ordinal);

    public void Add(SimulatorStats other)
    {
        Adds += other.Adds;
        AddsRefused += other.AddsRefused;
        Fulfills += other.Fulfills;
        Fails += other.Fails;
        FailMalformeds += other.FailMalformeds;
        FeeUpdates += other.FeeUpdates;
        FeesRefused += other.FeesRefused;
        CommitmentsSigned += other.CommitmentsSigned;
        CrossedCommitments += other.CrossedCommitments;
        Revocations += other.Revocations;
        Disconnects += other.Disconnects;
        RetransmittedCommitments += other.RetransmittedCommitments;
        RetransmittedRevocations += other.RetransmittedRevocations;
        RetransmittedUpdates += other.RetransmittedUpdates;
        DroppedOnDisconnect += other.DroppedOnDisconnect;
        GateRefusals += other.GateRefusals;
        MaxOpenHtlcs = Math.Max(MaxOpenHtlcs, other.MaxOpenHtlcs);
        FeeOnlyCommitments += other.FeeOnlyCommitments;
        LockedInEvents += other.LockedInEvents;
        FulfilledEvents += other.FulfilledEvents;
        FailedEvents += other.FailedEvents;
        SettledEvents += other.SettledEvents;
        IncomingSettledEvents += other.IncomingSettledEvents;
        CrossedFeeFailures += other.CrossedFeeFailures;
        foreach (var (id, n) in other.Refusals)
            Refusals[id] = Refusals.GetValueOrDefault(id) + n;
    }

    public override string ToString() =>
        $"adds {Adds} (refused {AddsRefused}), fulfills {Fulfills}, fails {Fails}, malformed {FailMalformeds}, "
      + $"fees {FeeUpdates} (refused {FeesRefused}), CS {CommitmentsSigned} (crossed {CrossedCommitments}, "
      + $"fee-only {FeeOnlyCommitments}), RAA {Revocations}, disconnects {Disconnects} (dropped {DroppedOnDisconnect}, "
      + $"re-sent CS {RetransmittedCommitments}, RAA {RetransmittedRevocations}, updates {RetransmittedUpdates}), "
      + $"gate refusals {GateRefusals}, max open HTLCs {MaxOpenHtlcs}, crossed-fee channel failures "
      + $"{CrossedFeeFailures}; events: locked-in {LockedInEvents}, fulfilled {FulfilledEvents}, failed "
      + $"{FailedEvents}, settled {SettledEvents}, incoming settled {IncomingSettledEvents}; refusals: "
      + string.Join(", ", Refusals.Select(r => $"{r.Key} {r.Value}"));
}

/// <summary>The run ended in the expected channel failure of <see cref="SimulatorStats.CrossedFeeFailures"/>.
/// </summary>
internal sealed class CrossedFeeChannelFailure : Exception;

/// <summary>A harness failure: carries the seed and the tail of the step trace so the run can be replayed.</summary>
internal sealed class SimulatorFailureException(int seed, string message, IEnumerable<string> trace, Exception? inner)
    : Exception($"seed {seed}: {message}\nlast steps:\n  {string.Join("\n  ", trace)}", inner)
{
    public int Seed { get; } = seed;
}

/// <summary>
/// Plan N4-T5: two <see cref="ChannelCommitments"/> engines (Alice = funder, Bob) joined by two FIFO links and driven by
/// a seeded random schedule of add / fulfill / fail / fail_malformed / update_fee / commitment_signed /
/// revoke_and_ack, deliveries and disconnects. Messages are never reordered within a direction (BOLT 8 is an ordered
/// stream); the two directions interleave freely. After every step the invariants of plan §3.4 that exist at engine
/// level are checked (see <see cref="CheckInvariants"/>); at the end both sides settle every HTLC and must agree with
/// an independent ledger.
/// </summary>
/// <remarks>
/// A disconnect drops both links, runs <see cref="ChannelCommitments.RevertUncommitted"/> on both sides and then
/// replays what BOLT 2 §Message Retransmission asks for, computed from the two <c>channel_reestablish</c> numbers
/// (<c>next_commitment_number</c> = local commitment + 1, <c>next_revocation_number</c> = the peer's view of our
/// commitment): the last <c>revoke_and_ack</c>, the updates covered by an unreceived <c>commitment_signed</c> plus that
/// <c>commitment_signed</c> (same signatures), in the original relative order, then every unsigned update.
/// </remarks>
internal sealed class CommitmentPairSimulator
{
    private const int TraceLength = 60;
    private const uint MinFeerate = 253;
    private const uint MaxFeerate = 100_000;

    private readonly int _seed;
    private readonly Random _rng;
    private readonly SimulatorConfig _config;
    private readonly Queue<string> _trace = new();
    private readonly Dictionary<Hash, Secret> _preimages = new();
    private int _step;

    /// <summary>The funder sent an add or <c>update_fee</c> while adds of the non-funder were still in flight to it.
    /// </summary>
    private bool _funderUpdateCrossedAdds;
    private (SimNode To, SimMessage Message)? _delivering;

    public SimNode Alice { get; }
    public SimNode Bob { get; }
    public SimulatorStats Stats { get; } = new();

    /// <summary>Independent ledger: what Alice's settled balance must be once every HTLC is resolved.</summary>
    private long _expectedAliceMsat;

    public CommitmentPairSimulator(int seed, SimulatorConfig? config = null)
    {
        _seed = seed;
        _rng = new Random(seed);
        _config = config ?? SimulatorConfig.Random(_rng);
        var aliceParams = new CommitmentParams(true, (_config.AliceMsat + _config.BobMsat) / 1_000, _config.Anchors,
                                               _config.AliceParty, _config.BobParty, _config.AliceMaxDustExposureMsat);
        var bobParams = new CommitmentParams(false, aliceParams.FundingSatoshis, _config.Anchors, _config.BobParty,
                                             _config.AliceParty, _config.BobMaxDustExposureMsat);
        Alice = new SimNode("alice", CommitmentsTestKit.AliceTag, CommitmentsTestKit.BobTag,
                            Create(aliceParams, _config.AliceMsat, _config.BobMsat, CommitmentsTestKit.BobTag));
        Bob = new SimNode("bob", CommitmentsTestKit.BobTag, CommitmentsTestKit.AliceTag,
                          Create(bobParams, _config.BobMsat, _config.AliceMsat, CommitmentsTestKit.AliceTag));
        _expectedAliceMsat = (long)_config.AliceMsat;
    }

    private ChannelCommitments Create(CommitmentParams @params, ulong localMsat, ulong remoteMsat, byte peerTag) =>
        ChannelCommitments.Create(CommitmentsTestKit.ChannelId, @params, localMsat, remoteMsat, _config.FeeratePerKw,
                                  CommitmentsTestKit.Point(peerTag, 0), CommitmentsTestKit.Point(peerTag, 1));

    /// <summary>Runs the random schedule, then settles every HTLC and checks the final agreement.</summary>
    public void Run() =>
        Execute(() =>
        {
            for (_step = 0; _step < _config.Steps; _step++)
            {
                RandomStep();
                CheckInvariants();
            }

            Settle();
        });

    /// <summary>
    /// Both sides offer HTLCs of one of <paramref name="amountsMsat"/> (with deliveries and signatures in between) until
    /// the peer's <c>max_accepted_htlcs</c> refuses the next one (B2-ADD-S08) exactly at the limit, then settle.
    /// </summary>
    public void RunFill(params ulong[] amountsMsat) =>
        Execute(() =>
        {
            var full = new HashSet<SimNode>();
            for (_step = 0; full.Count < 2 && _step < 100_000; _step++)
            {
                var node = _rng.Next(2) == 0 ? Alice : Bob;
                var roll = _rng.Next(10);
                if (roll < 5 && !full.Contains(node))
                {
                    var refusal = TryAdd(node, amountsMsat[_rng.Next(amountsMsat.Length)]);
                    if (refusal is not null)
                    {
                        Check(refusal == "B2-ADD-S08", $"{node.Name} add refused with {refusal} while filling");
                        var offered = node.State.Htlcs.Values.Count(h => h.Direction == HtlcDirection.Outgoing);
                        Check(offered == node.State.Params.Remote.MaxAcceptedHtlcs,
                              $"{node.Name} refused at {offered} offered HTLCs");
                        full.Add(node);
                    }
                }
                else if (roll < 7)
                {
                    TryCommit(node);
                }
                else
                {
                    Deliver(node);
                }

                CheckInvariants();
            }

            Check(full.Count == 2, "The limit was never reached");
            Settle();
        });

    private void Execute(Action run)
    {
        try
        {
            Trace($"config {_config}");
            CheckInvariants();
            run();
        }
        catch (SimulatorFailureException)
        {
            throw;
        }
        catch (CrossedFeeChannelFailure)
        {
            // A protocol race, not an engine bug: the funder's update crossed non-funder adds and the commitment it
            // then signed cannot pay its fee. The non-funder fails the channel (as LND does); nothing is left to settle.
            Stats.CrossedFeeFailures++;
        }
        catch (Exception e)
        {
            throw Fail($"{e.GetType().Name}: {e.Message}", e);
        }
    }

    #region Random schedule

    private void RandomStep()
    {
        var roll = _rng.NextDouble();
        if (roll < _config.DisconnectRate)
        {
            Disconnect();
            return;
        }

        var node = _rng.Next(2) == 0 ? Alice : Bob;
        var w = _config.Weights;
        var pick = _rng.Next(w.Total);
        // Hopeless attempts (refusals) are kept but thinned out: exceptions are the harness's main cost.
        if ((pick -= w.Add) < 0)
        {
            if ((Spendable(node) > 0 && !AtMaxAccepted(node)) || _rng.Next(5) == 0)
                TryAdd(node);
            else
                Deliver(node);
        }
        else if ((pick -= w.Remove) < 0)
            TryRemove(node);
        else if ((pick -= w.Fee) < 0)
            TryFee(node);
        else if (pick - w.Commit < 0)
            TryCommit(node);
        else
            Deliver(node);
    }

    /// <summary>Offers an HTLC; returns the requirement id when the engine refuses it.</summary>
    private string? TryAdd(SimNode node, ulong? amountMsat = null)
    {
        var preimageBytes = new byte[32];
        _rng.NextBytes(preimageBytes);
        var preimage = new Secret(preimageBytes);
        var hash = CommitmentsTestKit.HashOf(preimage);
        var amount = amountMsat ?? RandomAmount(node);
        var cltv = (uint)_rng.Next(500, 800_000);
        var expectedId = node.State.LocalNextHtlcId;
        try
        {
            var result = node.State.SendAdd(amount, hash, cltv, CommitmentsTestKit.Onion);
            var add = Assert.IsType<OutboundAddHtlc>(Assert.Single(result.Outbound)).Htlc;
            Check(add.Id == expectedId, $"{node.Name} add id {add.Id}, expected {expectedId} (B2-ADD-S10)");
            _preimages[hash] = preimage;
            NoteFunderUpdate(node);
            Apply(node, result, $"{node.Name} add #{add.Id} {amount} msat");
            node.Journal.Add(new JournalEntry(JournalKind.Add, add.Id));
            Stats.Adds++;
            return null;
        }
        catch (CommitmentRefusedException e)
        {
            ExpectRefusal(e, "B2-ADD-S01", "B2-ADD-S02", "B2-ADD-S03", "B2-ADD-S04", "B2-ADD-S06", "B2-ADD-S08",
                          "B2-ADD-S09", "B2-ADD-R02", "B2-DUST-03", "B2-DUST-04");
            Trace($"{node.Name} add {amount} msat refused: {e.RequirementId}");
            Stats.AddsRefused++;
            return e.RequirementId;
        }
    }

    /// <summary>A rough upper bound of what <paramref name="node"/> can still offer: settled balance minus its
    /// reserve and its open offers (fees ignored).</summary>
    private static ulong Spendable(SimNode node)
    {
        var state = node.State;
        var offered = state.Htlcs.Values.Where(h => h.Direction == HtlcDirection.Outgoing)
                           .Aggregate(0UL, (sum, h) => sum + h.AmountMsat);
        var used = offered + state.Params.LocalReserveMsat;
        return state.LocalBalanceMsat > used ? state.LocalBalanceMsat - used : 0;
    }

    private static bool AtMaxAccepted(SimNode node) =>
        node.State.Htlcs.Values.Count(h => h.Direction == HtlcDirection.Outgoing)
     >= node.State.Params.Remote.MaxAcceptedHtlcs;

    private ulong RandomAmount(SimNode node)
    {
        var spendable = (long)Math.Min(Spendable(node), 2_000_000_000) + 1_000;
        var minimum = _rng.Next(10) == 0 ? 1 : (long)node.State.Params.Remote.HtlcMinimumMsat;
        return (ulong)Math.Max(minimum, _rng.Next(10) switch
        {
            0 => _rng.Next(1, 2_000),
            < 4 => _rng.Next(1_000, 2_000_000),
            < 8 => _rng.NextInt64(1_000, spendable / 4 + 1_000),
            < 9 => _rng.NextInt64(1_000, spendable),
            _ => _rng.NextInt64(1_000, Math.Max(2_000, (long)node.State.LocalBalanceMsat + 1_000))
        });
    }

    private void TryRemove(SimNode node)
    {
        var incoming = node.State.Htlcs.Values.Where(h => h.Direction == HtlcDirection.Incoming).ToList();
        if (incoming.Count == 0)
        {
            Deliver(node);
            return;
        }

        var lockedInOnly = incoming.Where(h => h.State == HtlcState.RcvdAddAckRevocation).ToList();
        if (_rng.Next(10) != 0)
        {
            if (lockedInOnly.Count == 0)
            {
                Deliver(node);
                return;
            }

            incoming = lockedInOnly;
        }

        var htlc = incoming[_rng.Next(incoming.Count)];
        var lockedIn = htlc.State == HtlcState.RcvdAddAckRevocation;
        var kind = _rng.Next(20) switch
        {
            < 10 => HtlcRemovalKind.Fulfill,
            < 17 => HtlcRemovalKind.Fail,
            _ => HtlcRemovalKind.FailMalformed
        };
        try
        {
            var result = Remove(node.State, htlc, kind);
            Check(lockedIn, $"{node.Name} removed HTLC {htlc.Id} in {htlc.State}, before lock-in (I8)");
            Check(node.LockedInEvents.Contains(htlc.Id),
                  $"{node.Name} removed HTLC {htlc.Id} without an IncomingHtlcLockedIn event (I8)");
            Apply(node, result, $"{node.Name} {kind} #{htlc.Id} ({htlc.AmountMsat} msat)");
            node.Journal.Add(new JournalEntry(JournalKind.Remove, htlc.Id));
            if (kind == HtlcRemovalKind.Fulfill)
            {
                Stats.Fulfills++;
                // The receiver of the HTLC gets its amount once the fulfill is irrevocable (never undone).
                _expectedAliceMsat += node == Alice ? (long)htlc.AmountMsat : -(long)htlc.AmountMsat;
            }
            else if (kind == HtlcRemovalKind.Fail)
            {
                Stats.Fails++;
            }
            else
            {
                Stats.FailMalformeds++;
            }
        }
        catch (CommitmentRefusedException e)
        {
            // I8: an HTLC can be removed only once the add is irrevocably committed, and only once.
            Check(!lockedIn, $"{node.Name} could not remove locked-in HTLC {htlc.Id}: {e.Message}");
            ExpectRefusal(e, "B2-DEL-03", "B2-DEL-R07");
            Trace($"{node.Name} {kind} #{htlc.Id} in {htlc.State} refused: {e.RequirementId}");
            Stats.GateRefusals++;
        }
    }

    private CommitmentsResult Remove(ChannelCommitments state, HtlcRecord htlc, HtlcRemovalKind kind) => kind switch
    {
        HtlcRemovalKind.Fulfill => state.SendFulfill(htlc.Id, _preimages[htlc.PaymentHash], CommitmentsTestKit.Sha256),
        HtlcRemovalKind.Fail => state.SendFail(htlc.Id, FailureReason(htlc.Id)),
        _ => state.SendFailMalformed(htlc.Id, 0x8000 | 5, SHA256.HashData(CommitmentsTestKit.Onion))
    };

    private static byte[] FailureReason(ulong id)
    {
        var reason = new byte[292];
        BinaryPrimitives.WriteUInt64BigEndian(reason, id);
        return reason;
    }

    private void TryFee(SimNode node)
    {
        if (node == Bob && _rng.Next(10) != 0)
        {
            Deliver(node);
            return;
        }

        var current = node.State.LatestFeeratePerKw;
        var feerate = (uint)Math.Clamp(_rng.Next(4) switch
        {
            0 => _rng.Next((int)MinFeerate, 1_000),
            1 => (long)current * 2,
            2 => (long)current / 2,
            _ => _rng.Next((int)MinFeerate, 12_000)
        }, MinFeerate, MaxFeerate);
        try
        {
            var result = node.State.SendFee(feerate);
            Check(node == Alice, "The non-funder was allowed to send update_fee (B2-FEE-S02)");
            NoteFunderUpdate(node);
            Apply(node, result, $"{node.Name} update_fee {feerate}");
            node.Journal.Add(new JournalEntry(JournalKind.Fee, result.Next.FeeUpdates[^1].Sequence));
            Stats.FeeUpdates++;
        }
        catch (CommitmentRefusedException e)
        {
            ExpectRefusal(e, node == Alice ? ["B2-FEE-R03"] : ["B2-FEE-S02"]);
            Trace($"{node.Name} update_fee {feerate} refused: {e.RequirementId}");
            Stats.FeesRefused++;
        }
    }

    private void NoteFunderUpdate(SimNode node)
    {
        if (node == Alice && Bob.State.LocalNextHtlcId > Alice.State.RemoteNextHtlcId)
            _funderUpdateCrossedAdds = true;
    }

    private void TryCommit(SimNode node)
    {
        if (!node.State.CanSendCommit)
        {
            if (_rng.Next(10) == 0)
            {
                var e = Assert.Throws<CommitmentRefusedException>(() => node.State.SendCommit(node.Signer));
                ExpectRefusal(e, "B2-CS-S01", "B2-CS-S06");
            }

            Deliver(node);
            return;
        }

        SendCommit(node);
    }

    private void SendCommit(SimNode node)
    {
        var htlcMoves = node.State.Htlcs.Values.Count(h => HtlcStateTable.TryNext(h.State, HtlcEvent.SendCommit, out _));
        var result = node.State.SendCommit(node.Signer);
        var cs = Assert.IsType<OutboundCommitmentSigned>(Assert.Single(result.Outbound));
        Check(cs.RemoteCommitmentNumber == node.State.RemoteCommit.Number + 1,
              $"{node.Name} signed remote commitment {cs.RemoteCommitmentNumber} after {node.State.RemoteCommit.Number}");
        Apply(node, result, $"{node.Name} commitment_signed #{cs.RemoteCommitmentNumber}");
        node.LastSentCommitAfterRevoke = true;
        Stats.CommitmentsSigned++;
        if (htlcMoves == 0)
            Stats.FeeOnlyCommitments++;
    }

    #endregion

    #region Delivery

    /// <summary>Delivers the oldest message <paramref name="to"/> has waiting (FIFO per direction).</summary>
    private void Deliver(SimNode to)
    {
        var from = Peer(to);
        if (!from.Outbox.TryDequeue(out var message))
            return;

        _delivering = (to, message);

        switch (message)
        {
            case AddMessage add:
                Apply(to, to.State.ReceiveAdd(add.Htlc.Id, add.Htlc.AmountMsat, add.Htlc.PaymentHash,
                                              add.Htlc.CltvExpiry, add.Htlc.OnionRoutingPacket),
                      $"{to.Name} <- add #{add.Htlc.Id} {add.Htlc.AmountMsat} msat");
                break;
            case RemoveMessage { Removal.Kind: HtlcRemovalKind.Fulfill } remove:
                var fulfilled = Apply(to, to.State.ReceiveFulfill(remove.Id, remove.Removal.PaymentPreimage!.Value,
                                                                  CommitmentsTestKit.Sha256),
                                      $"{to.Name} <- fulfill #{remove.Id}");
                // I10: the preimage we will persist is the right one.
                var htlc = fulfilled.GetHtlc(HtlcDirection.Outgoing, remove.Id)!;
                Check(CommitmentsTestKit.HashOf(htlc.Removal!.PaymentPreimage!.Value).Equals(htlc.PaymentHash),
                      $"{to.Name} stored a preimage that does not match HTLC {remove.Id}");
                break;
            case RemoveMessage { Removal.Kind: HtlcRemovalKind.Fail } remove:
                Apply(to, to.State.ReceiveFail(remove.Id, remove.Removal.Reason), $"{to.Name} <- fail #{remove.Id}");
                break;
            case RemoveMessage remove:
                Apply(to, to.State.ReceiveFailMalformed(remove.Id, remove.Removal.FailureCode,
                                                        remove.Removal.Sha256OfOnion),
                      $"{to.Name} <- fail_malformed #{remove.Id}");
                break;
            case FeeMessage fee:
                Apply(to, to.State.ReceiveFee(fee.FeeratePerKw, MinFeerate, MaxFeerate),
                      $"{to.Name} <- update_fee {fee.FeeratePerKw}");
                break;
            case CommitMessage commit:
                ReceiveCommit(to, from, commit);
                break;
            case RevokeMessage revoke:
                ReceiveRevoke(to, from, revoke);
                break;
        }
    }

    private void ReceiveCommit(SimNode to, SimNode from, CommitMessage commit)
    {
        Check(commit.Number == to.State.LocalCommit.Number + 1,
              $"{to.Name} got commitment_signed #{commit.Number} while at local commitment {to.State.LocalCommit.Number}");
        if (to.State.RemoteNextCommit is not null)
            Stats.CrossedCommitments++;

        CommitmentsResult result;
        try
        {
            result = to.State.ReceiveCommit(commit.Signatures, to.Verifier);
        }
        catch (CommitmentViolationException e) when (to == Bob && _funderUpdateCrossedAdds
                                                  && e.RequirementId is "B2-ADD-R02" or "B2-FEE-R03")
        {
            Trace($"{to.Name} fails the channel: {e.RequirementId} {e.Message}");
            throw new CrossedFeeChannelFailure();
        }

        var raa = Assert.IsType<OutboundRevokeAndAck>(Assert.Single(result.Outbound));

        // I7 (spec level, and through the fake signature digest at "tx" level): what the sender signed is what the
        // receiver now holds.
        var signed = from.State.RemoteNextCommit?.Commit
                  ?? throw Fail($"{to.Name} got commitment_signed but {from.Name} has no unacked commitment");
        Check(signed.Number == commit.Number, $"{from.Name} signed #{signed.Number}, message says #{commit.Number}");
        CheckMirrored(signed.Spec, result.Next.LocalCommit.Spec, $"{to.Name} local #{commit.Number}");

        // I3: the secret of commitment n is released only once commitment n + 1 is held with valid signatures.
        Check(raa.RevokedCommitmentNumber + 1 == result.Next.LocalCommit.Number
           && raa.NextCommitmentNumber == result.Next.LocalCommit.Number + 1,
              $"{to.Name} revokes #{raa.RevokedCommitmentNumber} / next #{raa.NextCommitmentNumber} at local commitment {result.Next.LocalCommit.Number}");

        to.State = result.Next;
        to.Outbox.Enqueue(RevokeFor(to));
        to.LastSentCommitAfterRevoke = false;
        Trace($"{to.Name} <- commitment_signed #{commit.Number}, -> revoke_and_ack #{raa.RevokedCommitmentNumber}");
        CheckTransition(to, result);
    }

    private void ReceiveRevoke(SimNode to, SimNode from, RevokeMessage revoke)
    {
        var result = to.State.ReceiveRevoke(revoke.Secret, revoke.NextPoint, to.RevocationVerifier);
        Check(result.Next.RemoteCommit.Number == to.State.RemoteCommit.Number + 1,
              $"{to.Name} remote commitment went {to.State.RemoteCommit.Number} -> {result.Next.RemoteCommit.Number}");
        Check(result.Next.RemoteCommit.Number == from.State.LocalCommit.Number,
              $"{to.Name} now holds remote #{result.Next.RemoteCommit.Number}, {from.Name} is at #{from.State.LocalCommit.Number}");
        Check(ReferenceEquals(result.Transition.RevokedRemoteCommit, to.State.RemoteCommit),
              $"{to.Name} revoke_and_ack does not report remote #{to.State.RemoteCommit.Number} for the revocation log");
        to.RevokedByPeer = Math.Max(to.RevokedByPeer, result.Next.RemoteCommit.Number);
        Apply(to, result, $"{to.Name} <- revoke_and_ack (remote now #{result.Next.RemoteCommit.Number})");
        Stats.Revocations++;
    }

    /// <summary>The RAA for the current local commitment (fake secrets encode (node, number), see the kit).</summary>
    private static RevokeMessage RevokeFor(SimNode node)
    {
        var number = node.State.LocalCommit.Number;
        return new RevokeMessage(number - 1, CommitmentsTestKit.SecretFor(node.Tag, number - 1),
                                 CommitmentsTestKit.Point(node.Tag, number + 1));
    }

    /// <summary>Swaps in the new snapshot (I2: only after the operation succeeded), then queues its messages (I1).</summary>
    private ChannelCommitments Apply(SimNode node, CommitmentsResult result, string what)
    {
        node.State = result.Next;
        foreach (var outbound in result.Outbound)
            node.Outbox.Enqueue(ToMessage(node, outbound));

        Trace(what);
        CheckTransition(node, result);
        return result.Next;
    }

    private static SimMessage ToMessage(SimNode node, CommitmentOutbound outbound) => outbound switch
    {
        OutboundAddHtlc add => new AddMessage(add.Htlc),
        OutboundFulfillHtlc f => new RemoveMessage(f.Id, HtlcRemoval.Fulfill(f.PaymentPreimage)),
        OutboundFailHtlc f => new RemoveMessage(f.Id, HtlcRemoval.Fail(f.Reason)),
        OutboundFailMalformedHtlc f => new RemoveMessage(f.Id, HtlcRemoval.FailMalformed(f.FailureCode, f.Sha256OfOnion)),
        OutboundUpdateFee f => new FeeMessage(f.FeeratePerKw),
        OutboundCommitmentSigned cs => new CommitMessage(cs.RemoteCommitmentNumber, cs.Signatures),
        OutboundRevokeAndAck => RevokeFor(node),
        _ => throw new InvalidOperationException($"Unexpected outbound {outbound}")
    };

    private void CheckTransition(SimNode node, CommitmentsResult result)
    {
        foreach (var settled in result.Transition.SettledHtlcs)
        {
            Check(HtlcStateTable.IsFinal(settled.State), $"{node.Name} settled HTLC {settled.Key} in {settled.State}");
            Check(settled.Removal is not null, $"{node.Name} settled HTLC {settled.Key} without a removal");
            Check(node.Settled.Add(settled.Key), $"{node.Name} settled HTLC {settled.Key} twice (I9)");
        }

        foreach (var dropped in result.Transition.DroppedHtlcs)
            Check(dropped is { Direction: HtlcDirection.Incoming, State: HtlcState.RcvdAddHtlc },
                  $"{node.Name} dropped HTLC {dropped.Key} in {dropped.State}");

        CheckEvents(node, result);
    }

    /// <summary>
    /// N4-T4 / I8: every event is raised at the right time and exactly once, and can be re-derived from what the
    /// transition asks to persist (the startup replay after a crash between the save and the delivery).
    /// </summary>
    private void CheckEvents(SimNode node, CommitmentsResult result)
    {
        var settledOutgoing = result.Transition.SettledHtlcs.Where(h => h.Direction == HtlcDirection.Outgoing)
                                    .ToDictionary(h => h.Id);
        var settledIncoming = result.Transition.SettledHtlcs.Where(h => h.Direction == HtlcDirection.Incoming)
                                    .ToDictionary(h => h.Id);
        foreach (var domainEvent in result.Events)
        {
            Check(domainEvent.ChannelId.Equals(CommitmentsTestKit.ChannelId),
                  $"{node.Name} raised {domainEvent} for another channel");
            switch (domainEvent)
            {
                case IncomingHtlcLockedIn lockedIn:
                    var incoming = result.Next.GetHtlc(HtlcDirection.Incoming, lockedIn.HtlcId);
                    Check(incoming?.State == HtlcState.RcvdAddAckRevocation,
                          $"{node.Name} raised IncomingHtlcLockedIn #{lockedIn.HtlcId} in {incoming?.State} (I8)");
                    Check(node.LockedInEvents.Add(lockedIn.HtlcId),
                          $"{node.Name} raised IncomingHtlcLockedIn #{lockedIn.HtlcId} twice");
                    Stats.LockedInEvents++;
                    break;
                case OutgoingHtlcFulfilled fulfilled:
                    Check(CommitmentsTestKit.HashOf(fulfilled.PaymentPreimage).Equals(fulfilled.PaymentHash),
                          $"{node.Name} raised OutgoingHtlcFulfilled #{fulfilled.HtlcId} with a wrong preimage");
                    Check(node.FulfilledEvents.Add(fulfilled.HtlcId),
                          $"{node.Name} raised OutgoingHtlcFulfilled #{fulfilled.HtlcId} twice");
                    Stats.FulfilledEvents++;
                    break;
                case OutgoingHtlcFailed failed:
                    // B2-FWD-02: only once the removal is irrevocably committed.
                    Check(settledOutgoing.TryGetValue(failed.HtlcId, out var failedHtlc)
                       && failedHtlc is { State: HtlcState.RcvdRemoveAckRevocation, Removal.IsFulfill: false },
                          $"{node.Name} raised OutgoingHtlcFailed #{failed.HtlcId} before the fail was irrevocable (I8)");
                    Check(node.FailedEvents.Add(failed.HtlcId),
                          $"{node.Name} raised OutgoingHtlcFailed #{failed.HtlcId} twice");
                    Stats.FailedEvents++;
                    break;
                case OutgoingHtlcSettled settled:
                    Check(settledOutgoing.TryGetValue(settled.HtlcId, out var settledHtlc)
                       && settledHtlc.Removal!.Kind == settled.Kind,
                          $"{node.Name} raised OutgoingHtlcSettled #{settled.HtlcId} for an HTLC that did not settle");
                    Check(settled.Kind != HtlcRemovalKind.Fulfill || node.FulfilledEvents.Contains(settled.HtlcId),
                          $"{node.Name} settled fulfilled HTLC #{settled.HtlcId} without OutgoingHtlcFulfilled");
                    Check(settled.Kind == HtlcRemovalKind.Fulfill || node.FailedEvents.Contains(settled.HtlcId),
                          $"{node.Name} settled failed HTLC #{settled.HtlcId} without OutgoingHtlcFailed first");
                    Check(node.SettledEvents.Add(settled.HtlcId),
                          $"{node.Name} raised OutgoingHtlcSettled #{settled.HtlcId} twice");
                    Stats.SettledEvents++;
                    break;
                case IncomingHtlcSettled incomingSettled:
                    Check(settledIncoming.TryGetValue(incomingSettled.HtlcId, out var incomingHtlc)
                       && incomingHtlc is { State: HtlcState.SentRemoveAckRevocation }
                       && incomingHtlc.Removal!.Kind == incomingSettled.Kind,
                          $"{node.Name} raised IncomingHtlcSettled #{incomingSettled.HtlcId} for an HTLC that did not settle");
                    Check(node.LockedInEvents.Contains(incomingSettled.HtlcId),
                          $"{node.Name} settled incoming HTLC #{incomingSettled.HtlcId} that was never locked in");
                    Check(node.IncomingSettledEvents.Add(incomingSettled.HtlcId),
                          $"{node.Name} raised IncomingHtlcSettled #{incomingSettled.HtlcId} twice");
                    Stats.IncomingSettledEvents++;
                    break;
                default:
                    throw Fail($"{node.Name} raised unknown event {domainEvent}");
            }
        }

        // Every outgoing HTLC that settled raised its settle event now.
        foreach (var id in settledOutgoing.Keys)
            Check(node.SettledEvents.Contains(id), $"{node.Name} settled HTLC #{id} without OutgoingHtlcSettled");
        foreach (var id in settledIncoming.Keys)
            Check(node.IncomingSettledEvents.Contains(id),
                  $"{node.Name} settled incoming HTLC #{id} without IncomingHtlcSettled");

        // Completeness: every locked-in incoming HTLC and every known preimage has had its event.
        foreach (var htlc in result.Next.Htlcs.Values)
        {
            if (htlc.Direction == HtlcDirection.Incoming)
                Check(!HtlcStateTable.IsAddIrrevocablyCommitted(htlc.State) || node.LockedInEvents.Contains(htlc.Id),
                      $"{node.Name} HTLC {htlc.Key} is locked in ({htlc.State}) but no event was raised");
            else
                Check(htlc.KnownPreimage is null || node.FulfilledEvents.Contains(htlc.Id),
                      $"{node.Name} knows the preimage of HTLC {htlc.Key} but raised no OutgoingHtlcFulfilled");
        }

        // I8 replay: what the transition persists re-derives every event just raised.
        if (result.Events.Count == 0)
            return;
        var pending = ChannelDomainEvents.DerivePending(result.Next, result.Transition.SettledHtlcs)
                                         .Select(e => (e.GetType(), e.HtlcId)).ToHashSet();
        foreach (var domainEvent in result.Events)
            Check(pending.Contains((domainEvent.GetType(), domainEvent.HtlcId)),
                  $"{node.Name} raised {domainEvent.GetType().Name} #{domainEvent.HtlcId} but it cannot be re-derived from the persisted state (I8)");
    }

    #endregion

    #region Disconnect and retransmission

    private void Disconnect()
    {
        Stats.Disconnects++;
        Trace($"-- disconnect (in flight: alice->bob {Alice.Outbox.Count}, bob->alice {Bob.Outbox.Count})");
        Alice.Outbox.Clear();
        Bob.Outbox.Clear();
        foreach (var node in new[] { Alice, Bob })
        {
            var result = node.State.RevertUncommitted();
            Stats.DroppedOnDisconnect += result.Transition.DroppedHtlcs.Count;
            Apply(node, result, $"{node.Name} reverts uncommitted peer updates");
        }

        CheckInvariants();

        // channel_reestablish numbers.
        var aliceNextCommitment = Alice.State.LocalCommit.Number + 1;
        var bobNextCommitment = Bob.State.LocalCommit.Number + 1;
        var aliceNextRevocation = Alice.State.RemoteCommit.Number;
        var bobNextRevocation = Bob.State.RemoteCommit.Number;
        Retransmit(Alice, bobNextCommitment, bobNextRevocation);
        Retransmit(Bob, aliceNextCommitment, aliceNextRevocation);
    }

    /// <summary>BOLT 2 §Message Retransmission, from <paramref name="node"/>'s side.</summary>
    private void Retransmit(SimNode node, ulong peerNextCommitment, ulong peerNextRevocation)
    {
        var state = node.State;

        // The peer is missing our last revoke_and_ack when it still expects the revocation of our previous commitment.
        var resendRevoke = state.LocalCommit.Number > 0 && peerNextRevocation == state.LocalCommit.Number - 1;
        Check(resendRevoke || peerNextRevocation == state.LocalCommit.Number,
              $"{node.Name}: peer next_revocation_number {peerNextRevocation}, our local commitment {state.LocalCommit.Number}");

        // The peer is missing our last commitment_signed when its next commitment number is the one we signed.
        var resendCommit = state.RemoteNextCommit is { } pending && peerNextCommitment == pending.Commit.Number;
        Check(resendCommit || peerNextCommitment == (state.RemoteNextCommit?.Commit.Number ?? state.RemoteCommit.Number) + 1,
              $"{node.Name}: peer next_commitment_number {peerNextCommitment}, remote {state.RemoteCommit.Number}/{state.RemoteNextCommit?.Commit.Number}");

        var signedUpdates = new List<SimMessage>();
        var unsignedUpdates = new List<SimMessage>();
        var keep = new List<JournalEntry>();
        foreach (var entry in node.Journal)
        {
            var (message, signed) = Replay(state, entry);
            if (message is null)
                continue;

            keep.Add(entry);
            if (!signed)
                unsignedUpdates.Add(message);
            else if (resendCommit)
                signedUpdates.Add(message);
        }

        node.Journal.Clear();
        node.Journal.AddRange(keep);

        if (resendRevoke && node.LastSentCommitAfterRevoke)
            ResendRevoke(node);
        if (resendCommit)
        {
            foreach (var message in signedUpdates)
                node.Outbox.Enqueue(message);
            var sent = state.RemoteNextCommit!;
            node.Outbox.Enqueue(new CommitMessage(sent.Commit.Number, sent.SentSignatures));
            Stats.RetransmittedCommitments++;
        }

        if (resendRevoke && !node.LastSentCommitAfterRevoke)
            ResendRevoke(node);
        foreach (var message in unsignedUpdates)
            node.Outbox.Enqueue(message);

        Stats.RetransmittedUpdates += unsignedUpdates.Count + signedUpdates.Count;
        Trace($"{node.Name} re-sends: revoke {resendRevoke}, commit {resendCommit} (+{signedUpdates.Count} signed), "
            + $"{unsignedUpdates.Count} unsigned");
    }

    private void ResendRevoke(SimNode node)
    {
        node.Outbox.Enqueue(RevokeFor(node));
        Stats.RetransmittedRevocations++;
    }

    /// <summary>
    /// The message to replay for a journal entry, and whether it was covered by our last (unacked) commitment_signed;
    /// null when the peer has it for good (it is in a commitment the peer signed or revoked).
    /// </summary>
    private static (SimMessage? Message, bool Signed) Replay(ChannelCommitments state, JournalEntry entry)
    {
        switch (entry.Kind)
        {
            case JournalKind.Add:
                var add = state.GetHtlc(HtlcDirection.Outgoing, entry.Id);
                return add?.State switch
                {
                    HtlcState.SentAddHtlc => (new AddMessage(add), false),
                    HtlcState.SentAddCommit => (new AddMessage(add), true),
                    _ => (null, false)
                };
            case JournalKind.Remove:
                var removed = state.GetHtlc(HtlcDirection.Incoming, entry.Id);
                return removed?.State switch
                {
                    HtlcState.SentRemoveHtlc => (new RemoveMessage(removed.Id, removed.Removal!), false),
                    HtlcState.SentRemoveCommit => (new RemoveMessage(removed.Id, removed.Removal!), true),
                    _ => (null, false)
                };
            default:
                var fee = state.FeeUpdates.FirstOrDefault(f => f.Sequence == entry.Id);
                return fee?.State switch
                {
                    HtlcState.SentAddHtlc => (new FeeMessage(fee.FeeratePerKw), false),
                    HtlcState.SentAddCommit => (new FeeMessage(fee.FeeratePerKw), true),
                    _ => (null, false)
                };
        }
    }

    #endregion

    #region Settle

    /// <summary>
    /// Stops the random schedule: delivers everything, resolves every HTLC (randomly fulfill or fail) and signs until
    /// both sides are quiescent, then checks I6/I7 against the independent ledger and I11 (no error after
    /// reconnections).
    /// </summary>
    private void Settle()
    {
        Trace("-- settle");
        for (var round = 0; round < 10_000; round++)
        {
            var progressed = false;
            while (Alice.Outbox.Count > 0 || Bob.Outbox.Count > 0)
            {
                Deliver(Bob.Outbox.Count == 0 || (Alice.Outbox.Count > 0 && _rng.Next(2) == 0) ? Bob : Alice);
                CheckInvariants();
                progressed = true;
            }

            foreach (var node in new[] { Alice, Bob })
            {
                foreach (var htlc in node.State.Htlcs.Values.Where(h => h.State == HtlcState.RcvdAddAckRevocation))
                {
                    Check(node.LockedInEvents.Contains(htlc.Id),
                          $"{node.Name} settles HTLC {htlc.Id} without an IncomingHtlcLockedIn event (I8)");
                    var kind = _rng.Next(3) == 0 ? HtlcRemovalKind.Fail : HtlcRemovalKind.Fulfill;
                    Apply(node, Remove(node.State, htlc, kind), $"{node.Name} settles #{htlc.Id} with {kind}");
                    node.Journal.Add(new JournalEntry(JournalKind.Remove, htlc.Id));
                    if (kind == HtlcRemovalKind.Fulfill)
                        _expectedAliceMsat += node == Alice ? (long)htlc.AmountMsat : -(long)htlc.AmountMsat;
                    progressed = true;
                }

                if (node.State.CanSendCommit)
                {
                    SendCommit(node);
                    progressed = true;
                }

                CheckInvariants();
            }

            if (!progressed)
                break;
        }

        foreach (var node in new[] { Alice, Bob })
        {
            Check(node.State.Htlcs.IsEmpty, $"{node.Name} still has {node.State.Htlcs.Count} HTLCs after settling");
            Check(node.State.RemoteNextCommit is null, $"{node.Name} still waits for a revoke_and_ack");
            Check(!node.State.HasPendingChangesForRemote && !node.State.HasPendingChangesForLocal,
                  $"{node.Name} still has pending changes");
            Check(node.State.FeeUpdates.Count == 1, $"{node.Name} has {node.State.FeeUpdates.Count} fee updates");

            // N4-T4: every HTLC the node offered was locked in by the peer once and settled here once, fulfilled or
            // failed (never both).
            Check(node.SettledEvents.SetEquals(Peer(node).LockedInEvents),
                  $"{node.Name} settled {node.SettledEvents.Count} HTLCs, the peer locked in {Peer(node).LockedInEvents.Count}");
            Check(!node.FulfilledEvents.Overlaps(node.FailedEvents)
               && node.SettledEvents.SetEquals(node.FulfilledEvents.Union(node.FailedEvents)),
                  $"{node.Name} settled HTLCs do not match its fulfilled/failed events");
            Check(node.IncomingSettledEvents.SetEquals(node.LockedInEvents),
                  $"{node.Name} settled {node.IncomingSettledEvents.Count} incoming HTLCs, locked in {node.LockedInEvents.Count}");
        }

        Check(Alice.State.LocalBalanceMsat == (ulong)_expectedAliceMsat,
              $"alice balance {Alice.State.LocalBalanceMsat}, ledger says {_expectedAliceMsat}");
        Check(Bob.State.RemoteBalanceMsat == (ulong)_expectedAliceMsat,
              $"bob sees alice at {Bob.State.RemoteBalanceMsat}, ledger says {_expectedAliceMsat}");
        Check(Alice.State.LatestFeeratePerKw == Bob.State.LatestFeeratePerKw, "feerates differ after settling");
        CheckMirrored(Alice.State.LocalCommit.Spec, Bob.State.RemoteCommit.Spec, "alice local after settling");
        CheckMirrored(Bob.State.LocalCommit.Spec, Alice.State.RemoteCommit.Spec, "bob local after settling");
        Check(Alice.State.LocalCommit.Spec.Htlcs.Count == 0 && Bob.State.LocalCommit.Spec.Htlcs.Count == 0,
              "HTLCs left in a commitment after settling");
    }

    #endregion

    #region Invariants

    /// <summary>
    /// Engine-level invariants of plan §3.4, checked after every step. I1/I2 hold by construction of the harness
    /// (outbound is queued only after the snapshot is swapped, refused operations leave it untouched); I3, I7 and I10
    /// are checked where the messages are produced/consumed; I12 (data loss) needs reestablish (N7) and is not modeled.
    /// </summary>
    public void CheckInvariants()
    {
        foreach (var node in new[] { Alice, Bob })
        {
            var state = node.State;
            var peer = Peer(node).State;
            var funding = state.Params.FundingMsat;

            // I6: conservation in every commitment, current and prospective.
            Check(checked(state.LocalBalanceMsat + state.RemoteBalanceMsat) == funding,
                  $"{node.Name} settled balances do not add up to funding");
            foreach (var spec in new[]
                     {
                         state.LocalCommit.Spec, state.RemoteCommit.Spec, state.RemoteNextCommit?.Commit.Spec,
                         state.BuildSpec(CommitmentSide.Local), state.BuildSpec(CommitmentSide.Remote)
                     })
            {
                if (spec is not null)
                    Check(spec.TotalMsat == funding, $"{node.Name} {spec} does not conserve funding (I6)");
            }

            // A commitment the funder signed for the non-funder pays its fee: the funder's balance covers the fee and
            // anchors (the commit-time check behind the lenient update-time ones, B2-FEE-R03/B2-ADD-R02). The funder's
            // own commitment is not checked: an update_fee that crosses non-funder adds can leave it short, which
            // BOLT 2 tolerates (the non-funder judged the fee it knew, B2-ADD-S04; the funder's receiver rule R02 does
            // not charge it the fee).
            if (!state.Params.LocalIsFunder)
            {
                var localSpec = state.LocalCommit.Spec;
                var funderCost = CommitmentFeeCalculator.FunderCostMsat(localSpec, state.Params.Local.DustLimitSatoshis,
                                                                        state.Params.OptionAnchors);
                Check(localSpec.RemoteMsat >= funderCost,
                      $"{node.Name} holds local commitment {state.LocalCommit.Number} whose funder has {localSpec.RemoteMsat} msat for a {funderCost} msat fee");
            }

            // Structure: every HTLC is in a defined state of its own half of the machine.
            foreach (var htlc in state.Htlcs.Values)
            {
                Check(HtlcStateTable.IsDefined(htlc.State) && HtlcStateTable.Owner(htlc.State) == htlc.Direction,
                      $"{node.Name} HTLC {htlc.Key} in {htlc.State}");
                Check(HtlcStateTable.IsRemoval(htlc.State) == htlc.Removal is not null,
                      $"{node.Name} HTLC {htlc.Key} removal does not match {htlc.State}");
                Check(!HtlcStateTable.IsFinal(htlc.State), $"{node.Name} kept final HTLC {htlc.Key}");
                var next = htlc.Direction == HtlcDirection.Outgoing ? state.LocalNextHtlcId : state.RemoteNextHtlcId;
                Check(htlc.Id < next, $"{node.Name} HTLC {htlc.Key} is not below the next id {next}");
            }

            // I9: numbers and ids never go back (the peer's id counter rewinds only over dropped unsigned adds).
            Check(state.LocalCommit.Number >= node.LastLocalNumber && state.RemoteCommit.Number >= node.LastRemoteNumber
               && state.LocalNextHtlcId >= node.LastLocalNextId,
                  $"{node.Name} commitment numbers or ids went back (I9)");
            Check(state.RemoteNextHtlcId <= peer.LocalNextHtlcId,
                  $"{node.Name} expects peer id {state.RemoteNextHtlcId}, peer only offered up to {peer.LocalNextHtlcId}");
            node.LastLocalNumber = state.LocalCommit.Number;
            node.LastRemoteNumber = state.RemoteCommit.Number;
            node.LastLocalNextId = state.LocalNextHtlcId;

            // Only the funder's fee updates, pruned to one final entry.
            Check(state.FeeUpdates.Count(f => f.IsFinal) == 1 && state.FeeUpdates[0].IsFinal,
                  $"{node.Name} fee updates are not pruned to one final head");

            // I4/I5: our latest commitment is the only one we hold and carries valid signatures.
            if (state.LocalCommit.RemoteSignatures is { } signatures && !ReferenceEquals(node.Verified, state.LocalCommit))
            {
                Check(node.Verifier.Matches(state.LocalCommit.Number, state.LocalCommit.Spec, signatures),
                      $"{node.Name} holds local commitment {state.LocalCommit.Number} without valid signatures (I5)");
                node.Verified = state.LocalCommit;
            }

            // I7: the peer's latest local commitment is one we signed and both see it the same way.
            var peerLocal = peer.LocalCommit;
            var signedByUs = peerLocal.Number == state.RemoteCommit.Number
                                 ? state.RemoteCommit
                                 : state.RemoteNextCommit?.Commit.Number == peerLocal.Number
                                     ? state.RemoteNextCommit.Commit
                                     : null;
            Check(signedByUs is not null,
                  $"{Peer(node).Name} holds commitment #{peerLocal.Number}, {node.Name} signed #{state.RemoteCommit.Number}/{state.RemoteNextCommit?.Commit.Number}");
            CheckMirrored(signedByUs!.Spec, peerLocal.Spec, $"{Peer(node).Name} local #{peerLocal.Number} (I7)");

            // The peer's commitment points we use are the ones it gave us (fake points encode (node, number)).
            Check(state.RemoteCommit.PerCommitmentPoint.Equals(CommitmentsTestKit.Point(node.PeerTag, state.RemoteCommit.Number)),
                  $"{node.Name} uses a wrong point for remote commitment {state.RemoteCommit.Number}");
        }

        Stats.MaxOpenHtlcs = Math.Max(Stats.MaxOpenHtlcs, Math.Max(Alice.State.Htlcs.Count, Bob.State.Htlcs.Count));
    }

    #endregion

    private SimNode Peer(SimNode node) => node == Alice ? Bob : Alice;

    /// <summary>The same commitment seen from both nodes (holder flips, balances and HTLC directions swap).</summary>
    private void CheckMirrored(CommitmentSpec fromSigner, CommitmentSpec fromHolder, string what)
    {
        var mirrored = fromSigner.Holder != fromHolder.Holder && fromSigner.FeeratePerKw == fromHolder.FeeratePerKw
                    && fromSigner.ToHolderMsat == fromHolder.ToHolderMsat
                    && fromSigner.ToCounterpartyMsat == fromHolder.ToCounterpartyMsat
                    && fromSigner.Htlcs.Count == fromHolder.Htlcs.Count
                    && fromHolder.Htlcs.ToHashSet()
                                  .SetEquals(fromSigner.Htlcs.Select(h => h with { Direction = Flip(h.Direction) }));
        Check(mirrored, $"{what}: {fromSigner} vs {fromHolder}");
    }

    private static HtlcDirection Flip(HtlcDirection direction) =>
        direction == HtlcDirection.Outgoing ? HtlcDirection.Incoming : HtlcDirection.Outgoing;

    private void ExpectRefusal(CommitmentRefusedException e, params string[] allowed)
    {
        Check(allowed.Contains(e.RequirementId), $"Unexpected refusal {e.Message}");
        Stats.Refusals[e.RequirementId] = Stats.Refusals.GetValueOrDefault(e.RequirementId) + 1;
    }

    private void Check(bool condition, string message)
    {
        if (!condition)
            throw Fail(message);
    }

    /// <summary>Like <see cref="Check(bool, string)"/>, but the message is only formatted when the check fails (the
    /// invariants run after every step).</summary>
    private void Check(bool condition, [InterpolatedStringHandlerArgument(nameof(condition))] ref CheckMessage message)
    {
        if (!condition)
            throw Fail(message.ToStringAndClear());
    }

    private SimulatorFailureException Fail(string message, Exception? inner = null) =>
        new(_seed, $"step {_step}: {message} (last delivery: {_delivering?.To.Name} <- {_delivering?.Message})",
            _trace.Append(Describe(Alice)).Append(Describe(Bob)), inner);

    private static string Describe(SimNode node)
    {
        var state = node.State;
        var htlcs = string.Join(" ", state.Htlcs.Values.Select(h => $"{h.Direction}#{h.Id}:{h.AmountMsat}@{(byte)h.State}"));
        var fees = string.Join(" ", state.FeeUpdates.Select(f => $"{f.FeeratePerKw}@{(byte)f.State}"));
        return $"state {node.Name}: balances {state.LocalBalanceMsat}/{state.RemoteBalanceMsat}, local #{state.LocalCommit.Number}, "
             + $"remote #{state.RemoteCommit.Number}/{state.RemoteNextCommit?.Commit.Number}, next ids "
             + $"{state.LocalNextHtlcId}/{state.RemoteNextHtlcId}, fees [{fees}], htlcs [{htlcs}], params {state.Params}";
    }

    private void Trace(string line)
    {
        _trace.Enqueue($"[{_step}] {line}");
        if (_trace.Count > TraceLength)
            _trace.Dequeue();
    }
}

/// <summary>Formats a <c>Check</c> message only when the condition is false.</summary>
[InterpolatedStringHandler]
internal ref struct CheckMessage
{
    private DefaultInterpolatedStringHandler _inner;
    private readonly bool _enabled;

    public CheckMessage(int literalLength, int formattedCount, bool condition, out bool shouldAppend)
    {
        _enabled = !condition;
        shouldAppend = _enabled;
        _inner = _enabled ? new DefaultInterpolatedStringHandler(literalLength, formattedCount) : default;
    }

    public void AppendLiteral(string value) => _inner.AppendLiteral(value);

    public void AppendFormatted<T>(T value) => _inner.AppendFormatted(value);

    public string ToStringAndClear() => _enabled ? _inner.ToStringAndClear() : string.Empty;
}

/// <summary>One simulated node: its engine snapshot, its outgoing link and what it needs for retransmission.</summary>
internal sealed class SimNode(string name, byte tag, byte peerTag, ChannelCommitments state)
{
    public string Name { get; } = name;
    public byte Tag { get; } = tag;
    public byte PeerTag { get; } = peerTag;
    public ChannelCommitments State { get; set; } = state;

    /// <summary>Messages sent to the peer and not yet delivered (lost on disconnect).</summary>
    public Queue<SimMessage> Outbox { get; } = new();

    /// <summary>Updates we sent, in order, until the peer has them for good (the N7 "sent log").</summary>
    public List<JournalEntry> Journal { get; } = [];

    /// <summary>The order of our last commitment_signed and revoke_and_ack (BOLT 2: re-send them in that order).</summary>
    public bool LastSentCommitAfterRevoke { get; set; }

    /// <summary>The highest peer commitment number whose predecessor the peer revoked.</summary>
    public ulong RevokedByPeer { get; set; }

    public HashSet<HtlcKey> Settled { get; } = [];

    /// <summary>Ids of the HTLCs each event was raised for (each at most once).</summary>
    public HashSet<ulong> LockedInEvents { get; } = [];
    public HashSet<ulong> FulfilledEvents { get; } = [];
    public HashSet<ulong> FailedEvents { get; } = [];
    public HashSet<ulong> SettledEvents { get; } = [];
    public HashSet<ulong> IncomingSettledEvents { get; } = [];

    /// <summary>The last local commitment whose signatures were checked (they are checked once per commitment).</summary>
    public LocalCommit? Verified { get; set; }
    public ulong LastLocalNumber { get; set; }
    public ulong LastRemoteNumber { get; set; }
    public ulong LastLocalNextId { get; set; }

    public DigestCommitmentSigner Signer => new(this);
    public DigestCommitmentVerifier Verifier => new(State.Params.Local.DustLimitSatoshis, State.Params.OptionAnchors);
    public FakeRevocationVerifier RevocationVerifier { get; } = new();
}

internal enum JournalKind
{
    Add,
    Remove,
    Fee
}

/// <summary>A sent update: HTLC id (add: ours; remove: the peer's) or fee sequence.</summary>
internal readonly record struct JournalEntry(JournalKind Kind, ulong Id);

internal abstract record SimMessage;

internal sealed record AddMessage(HtlcRecord Htlc) : SimMessage;

internal sealed record RemoveMessage(ulong Id, HtlcRemoval Removal) : SimMessage;

internal sealed record FeeMessage(uint FeeratePerKw) : SimMessage;

internal sealed record CommitMessage(ulong Number, CommitmentSignatures Signatures) : SimMessage;

internal sealed record RevokeMessage(ulong RevokedNumber, Secret Secret, CompactPubKey NextPoint) : SimMessage;

/// <summary>
/// "Signs" a commitment with a digest of its content as seen by its holder, so a verifier that rebuilt a different
/// commitment (other balances, HTLC set, feerate, number or trimming) rejects it: a stand-in for byte-identical
/// transactions (I7). Also checks that we never sign a commitment the peer has already revoked, nor skip or repeat a
/// number, and that we sign against the peer's point for that number.
/// </summary>
internal sealed class DigestCommitmentSigner(SimNode node) : ICommitmentSigner
{
    public CommitmentSignatures SignRemoteCommitment(ChannelId channelId, ulong number, CommitmentSpec spec,
                                                     CompactPubKey remotePerCommitmentPoint)
    {
        var state = node.State;
        if (number != state.RemoteCommit.Number + 1 || number <= node.RevokedByPeer)
            throw new InvalidOperationException(
                $"{node.Name} asked to sign remote commitment {number} (remote at {state.RemoteCommit.Number}, peer revoked up to {node.RevokedByPeer})");
        if (!remotePerCommitmentPoint.Equals(CommitmentsTestKit.Point(node.PeerTag, number)))
            throw new InvalidOperationException($"{node.Name} signs remote commitment {number} with a wrong point");

        return CommitmentDigest.Sign(number, spec, state.Params.Remote.DustLimitSatoshis, state.Params.OptionAnchors);
    }
}

/// <summary>Accepts exactly the signatures <see cref="DigestCommitmentSigner"/> produces for the same content.</summary>
internal sealed class DigestCommitmentVerifier(ulong localDustSat, bool anchors) : ICommitmentVerifier
{
    public bool VerifyLocalCommitment(ChannelId channelId, ulong number, CommitmentSpec spec,
                                      CommitmentSignatures signatures) => Matches(number, spec, signatures);

    public bool Matches(ulong number, CommitmentSpec spec, CommitmentSignatures signatures) =>
        CommitmentDigest.Sign(number, spec, localDustSat, anchors) is var expected
     && expected.Signature.Equals(signatures.Signature)
     && expected.HtlcSignatures.SequenceEqual(signatures.HtlcSignatures);
}

internal static class CommitmentDigest
{
    /// <summary>
    /// Holder-perspective encoding: number, feerate, to_local, to_remote, then each HTLC as (offered by holder, id,
    /// amount, hash, expiry) in a canonical order; one HTLC "signature" per untrimmed HTLC.
    /// </summary>
    public static CommitmentSignatures Sign(ulong number, CommitmentSpec spec, ulong holderDustSat, bool anchors)
    {
        var htlcs = spec.Htlcs.OrderBy(h => h.IsOfferedBy(spec.Holder)).ThenBy(h => h.Id).ToList();
        var buffer = new byte[8 + 4 + 8 + 8 + htlcs.Count * (1 + 8 + 8 + 32 + 4)];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt64BigEndian(span, number);
        BinaryPrimitives.WriteUInt32BigEndian(span[8..], spec.FeeratePerKw);
        BinaryPrimitives.WriteUInt64BigEndian(span[12..], spec.ToHolderMsat);
        BinaryPrimitives.WriteUInt64BigEndian(span[20..], spec.ToCounterpartyMsat);
        var offset = 28;
        foreach (var htlc in htlcs)
        {
            span[offset] = htlc.IsOfferedBy(spec.Holder) ? (byte)1 : (byte)0;
            BinaryPrimitives.WriteUInt64BigEndian(span[(offset + 1)..], htlc.Id);
            BinaryPrimitives.WriteUInt64BigEndian(span[(offset + 9)..], htlc.AmountMsat);
            ((byte[])htlc.PaymentHash).CopyTo(span[(offset + 17)..]);
            BinaryPrimitives.WriteUInt32BigEndian(span[(offset + 49)..], htlc.CltvExpiry);
            offset += 53;
        }

        var digest = SHA256.HashData(buffer);
        var untrimmed = htlcs.Where(h => !CommitmentFeeCalculator.IsHtlcTrimmed(spec, h, holderDustSat, anchors))
                             .Select((h, i) => ToSignature(SHA256.HashData([.. digest, (byte)i, .. BitConverter.GetBytes(h.Id)])))
                             .ToList();
        return new CommitmentSignatures(ToSignature(digest), untrimmed);
    }

    private static CompactSignature ToSignature(byte[] digest)
    {
        var bytes = new byte[64];
        digest.CopyTo(bytes, 0);
        digest.CopyTo(bytes, 32);
        return new CompactSignature(bytes);
    }
}