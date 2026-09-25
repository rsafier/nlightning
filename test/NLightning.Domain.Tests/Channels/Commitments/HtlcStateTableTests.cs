namespace NLightning.Domain.Tests.Channels.Commitments;

using Domain.Channels.Commitments;
using Domain.Channels.Enums;
using Domain.Exceptions;
using E = Domain.Channels.Commitments.HtlcEvent;
using S = Domain.Channels.Enums.HtlcState;

/// <summary>
/// Exhaustive tests of the per-HTLC state machine: every (state, event) pair and every per-state flag, against a table
/// written out independently from the implementation (plan §3.3 / core-lightning <c>htlc_state</c>).
/// </summary>
public class HtlcStateTableTests
{
    /// <summary>The only legal transitions: (from, event, to).</summary>
    private static readonly (S From, E Event, S To)[] s_legal =
    [
        (S.SentAddHtlc, E.SendCommit, S.SentAddCommit),
        (S.SentAddCommit, E.RecvRevoke, S.RcvdAddRevocation),
        (S.RcvdAddRevocation, E.RecvCommit, S.RcvdAddAckCommit),
        (S.RcvdAddAckCommit, E.SendRevoke, S.SentAddAckRevocation),
        (S.SentAddAckRevocation, E.RecvRemove, S.RcvdRemoveHtlc),
        (S.RcvdRemoveHtlc, E.RecvCommit, S.RcvdRemoveCommit),
        (S.RcvdRemoveCommit, E.SendRevoke, S.SentRemoveRevocation),
        (S.SentRemoveRevocation, E.SendCommit, S.SentRemoveAckCommit),
        (S.SentRemoveAckCommit, E.RecvRevoke, S.RcvdRemoveAckRevocation),
        (S.RcvdAddHtlc, E.RecvCommit, S.RcvdAddCommit),
        (S.RcvdAddCommit, E.SendRevoke, S.SentAddRevocation),
        (S.SentAddRevocation, E.SendCommit, S.SentAddAckCommit),
        (S.SentAddAckCommit, E.RecvRevoke, S.RcvdAddAckRevocation),
        (S.RcvdAddAckRevocation, E.SendRemove, S.SentRemoveHtlc),
        (S.SentRemoveHtlc, E.SendCommit, S.SentRemoveCommit),
        (S.SentRemoveCommit, E.RecvRevoke, S.RcvdRemoveRevocation),
        (S.RcvdRemoveRevocation, E.RecvCommit, S.RcvdRemoveAckCommit),
        (S.RcvdRemoveAckCommit, E.SendRevoke, S.SentRemoveAckRevocation)
    ];

    /// <summary>Per-state flags: owner, in local, in remote, removed from local, removed from remote, removal, final,
    /// add irrevocably committed.</summary>
    private static readonly Dictionary<S, (HtlcDirection Owner, bool InL, bool InR, bool GoneL, bool GoneR, bool
        Removal, bool Final, bool AddIrrevocable)> s_flags = new()
        {
            [S.SentAddHtlc] = (HtlcDirection.Outgoing, false, false, false, false, false, false, false),
            [S.SentAddCommit] = (HtlcDirection.Outgoing, false, true, false, false, false, false, false),
            [S.RcvdAddRevocation] = (HtlcDirection.Outgoing, false, true, false, false, false, false, false),
            [S.RcvdAddAckCommit] = (HtlcDirection.Outgoing, true, true, false, false, false, false, false),
            [S.SentAddAckRevocation] = (HtlcDirection.Outgoing, true, true, false, false, false, false, true),
            [S.RcvdRemoveHtlc] = (HtlcDirection.Outgoing, true, true, false, false, true, false, true),
            [S.RcvdRemoveCommit] = (HtlcDirection.Outgoing, false, true, true, false, true, false, true),
            [S.SentRemoveRevocation] = (HtlcDirection.Outgoing, false, true, true, false, true, false, true),
            [S.SentRemoveAckCommit] = (HtlcDirection.Outgoing, false, false, true, true, true, false, true),
            [S.RcvdRemoveAckRevocation] = (HtlcDirection.Outgoing, false, false, true, true, true, true, true),
            [S.RcvdAddHtlc] = (HtlcDirection.Incoming, false, false, false, false, false, false, false),
            [S.RcvdAddCommit] = (HtlcDirection.Incoming, true, false, false, false, false, false, false),
            [S.SentAddRevocation] = (HtlcDirection.Incoming, true, false, false, false, false, false, false),
            [S.SentAddAckCommit] = (HtlcDirection.Incoming, true, true, false, false, false, false, false),
            [S.RcvdAddAckRevocation] = (HtlcDirection.Incoming, true, true, false, false, false, false, true),
            [S.SentRemoveHtlc] = (HtlcDirection.Incoming, true, true, false, false, true, false, true),
            [S.SentRemoveCommit] = (HtlcDirection.Incoming, true, false, false, true, true, false, true),
            [S.RcvdRemoveRevocation] = (HtlcDirection.Incoming, true, false, false, true, true, false, true),
            [S.RcvdRemoveAckCommit] = (HtlcDirection.Incoming, false, false, true, true, true, false, true),
            [S.SentRemoveAckRevocation] = (HtlcDirection.Incoming, false, false, true, true, true, true, true)
        };

    public static TheoryData<S, E> AllPairs()
    {
        var data = new TheoryData<S, E>();
        foreach (var state in HtlcStateTable.States)
            foreach (var htlcEvent in Enum.GetValues<E>())
                data.Add(state, htlcEvent);

        return data;
    }

    public static TheoryData<S> AllStates() => new(HtlcStateTable.States);

    public static TheoryData<S> LegacyStates() => new(S.Offered, S.Fulfilled, S.Failed, S.Expired, (S)4, (S)20, (S)29,
                                                      (S)40, (S)255);

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Given_StateAndEvent_When_Next_Then_MatchesTableOrThrows(S state, E htlcEvent)
    {
        // Arrange
        var expected = s_legal.Where(t => t.From == state && t.Event == htlcEvent).Select(t => (S?)t.To)
                              .SingleOrDefault();

        // Act
        var moved = HtlcStateTable.TryNext(state, htlcEvent, out var next);

        // Assert
        if (expected is { } to)
        {
            Assert.True(moved);
            Assert.Equal(to, next);
            Assert.Equal(to, HtlcStateTable.Next(state, htlcEvent));
        }
        else
        {
            Assert.False(moved);
            Assert.Equal(state, next);
            var exception = Assert.Throws<HtlcStateTransitionException>(() => HtlcStateTable.Next(state, htlcEvent));
            Assert.Equal(state, exception.State);
            Assert.Equal(htlcEvent, exception.Event);
            Assert.IsAssignableFrom<ChannelErrorException>(exception);
        }
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void Given_State_When_QueryingFlags_Then_MatchTable(S state)
    {
        // Arrange
        var (owner, inL, inR, goneL, goneR, removal, final, addIrrevocable) = s_flags[state];

        // Act / Assert
        Assert.True(HtlcStateTable.IsDefined(state));
        Assert.Equal(owner, HtlcStateTable.Owner(state));
        Assert.Equal(inL, HtlcStateTable.IsInLocalCommit(state));
        Assert.Equal(inR, HtlcStateTable.IsInRemoteCommit(state));
        Assert.Equal(inL, HtlcStateTable.IsInCommit(state, CommitmentSide.Local));
        Assert.Equal(inR, HtlcStateTable.IsInCommit(state, CommitmentSide.Remote));
        Assert.Equal(goneL, HtlcStateTable.IsRemovedFrom(state, CommitmentSide.Local));
        Assert.Equal(goneR, HtlcStateTable.IsRemovedFrom(state, CommitmentSide.Remote));
        Assert.Equal(removal, HtlcStateTable.IsRemoval(state));
        Assert.Equal(final, HtlcStateTable.IsFinal(state));
        Assert.Equal(addIrrevocable, HtlcStateTable.IsAddIrrevocablyCommitted(state));
        Assert.Equal(state is S.SentAddAckRevocation or S.RcvdAddAckRevocation, HtlcStateTable.IsFeeFinal(state));
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void Given_State_When_InACommit_Then_NotRemovedFromIt(S state)
    {
        // An HTLC can't be both in a commitment and removed from it.
        foreach (var side in Enum.GetValues<CommitmentSide>())
            Assert.False(HtlcStateTable.IsInCommit(state, side) && HtlcStateTable.IsRemovedFrom(state, side));
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void Given_NonFinalState_When_Enumerating_Then_ExactlyOneLegalEvent(S state)
    {
        // Act
        var legal = Enum.GetValues<E>().Count(e => HtlcStateTable.TryNext(state, e, out _));

        // Assert
        Assert.Equal(HtlcStateTable.IsFinal(state) ? 0 : 1, legal);
    }

    [Theory]
    [MemberData(nameof(AllStates))]
    public void Given_Transition_When_Moving_Then_OwnerNeverChanges(S state)
    {
        foreach (var htlcEvent in Enum.GetValues<E>())
            if (HtlcStateTable.TryNext(state, htlcEvent, out var next))
                Assert.Equal(HtlcStateTable.Owner(state), HtlcStateTable.Owner(next));
    }

    [Fact]
    public void Given_OurAdd_When_Walking_Then_FiveStagesEachWayInNineSteps()
    {
        // Arrange: BOLT 2 update lifecycle for an HTLC we offer and the peer then removes (B2-NO-01).
        E[] events =
        [
            E.SendCommit, E.RecvRevoke, E.RecvCommit, E.SendRevoke, // add: in their commit, then ours
            E.RecvRemove, E.RecvCommit, E.SendRevoke, E.SendCommit, E.RecvRevoke // removal: ours first, then theirs
        ];

        // Act
        var state = HtlcStateTable.Initial(HtlcDirection.Outgoing);
        foreach (var htlcEvent in events)
            state = HtlcStateTable.Next(state, htlcEvent);

        // Assert
        Assert.Equal(S.RcvdRemoveAckRevocation, state);
        Assert.True(HtlcStateTable.IsFinal(state));
    }

    [Fact]
    public void Given_TheirAdd_When_Walking_Then_LockedInAfterFourStepsAndFinalAfterNine()
    {
        // Arrange
        E[] addEvents = [E.RecvCommit, E.SendRevoke, E.SendCommit, E.RecvRevoke];
        E[] removeEvents = [E.SendRemove, E.SendCommit, E.RecvRevoke, E.RecvCommit, E.SendRevoke];

        // Act
        var state = HtlcStateTable.Initial(HtlcDirection.Incoming);
        foreach (var htlcEvent in addEvents)
        {
            Assert.False(HtlcStateTable.IsAddIrrevocablyCommitted(state));
            state = HtlcStateTable.Next(state, htlcEvent);
        }

        var lockedIn = state;
        foreach (var htlcEvent in removeEvents)
            state = HtlcStateTable.Next(state, htlcEvent);

        // Assert
        Assert.Equal(S.RcvdAddAckRevocation, lockedIn);
        Assert.True(HtlcStateTable.IsAddIrrevocablyCommitted(lockedIn));
        Assert.Equal(S.SentRemoveAckRevocation, state);
        Assert.True(HtlcStateTable.IsFinal(state));
    }

    [Fact]
    public void Given_StatesList_When_Enumerated_Then_AllTwentyMachineStatesOnce()
    {
        // Arrange
        var expected = Enum.GetValues<S>().Where(s => (byte)s is >= 10 and <= 19 or >= 30 and <= 39).ToList();

        // Assert
        Assert.Equal(20, HtlcStateTable.States.Count);
        Assert.Equal(expected, HtlcStateTable.States);
        Assert.Equal(20, s_flags.Count);
    }

    [Theory]
    [MemberData(nameof(LegacyStates))]
    public void Given_LegacyOrUnknownState_When_Used_Then_Rejected(S state)
    {
        // Assert: legacy values 0-3 stay decodable but are not part of the machine.
        Assert.False(HtlcStateTable.IsDefined(state));
        Assert.Throws<ArgumentOutOfRangeException>(() => HtlcStateTable.Owner(state));
        Assert.Throws<ArgumentOutOfRangeException>(() => HtlcStateTable.IsInLocalCommit(state));
        Assert.Throws<ArgumentOutOfRangeException>(() => HtlcStateTable.IsInRemoteCommit(state));
        Assert.Throws<ArgumentOutOfRangeException>(() => HtlcStateTable.IsRemovedFrom(state, CommitmentSide.Local));
        Assert.Throws<ArgumentOutOfRangeException>(() => HtlcStateTable.IsRemoval(state));
        Assert.Throws<ArgumentOutOfRangeException>(() => HtlcStateTable.IsFinal(state));
        Assert.Throws<ArgumentOutOfRangeException>(() => HtlcStateTable.IsAddIrrevocablyCommitted(state));
        foreach (var htlcEvent in Enum.GetValues<E>())
            Assert.Throws<HtlcStateTransitionException>(() => HtlcStateTable.Next(state, htlcEvent));
    }

    [Fact]
    public void Given_LegacyValues_When_Cast_Then_Unchanged()
    {
        // Persisted legacy rows must keep decoding to the same values (N5 maps them).
        Assert.Equal(0, (byte)S.Offered);
        Assert.Equal(1, (byte)S.Fulfilled);
        Assert.Equal(2, (byte)S.Failed);
        Assert.Equal(3, (byte)S.Expired);
    }
}