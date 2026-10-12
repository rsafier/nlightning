namespace NLightning.Domain.Channels.Commitments;

using Bitcoin.Transactions.Enums;
using Enums;
using Exceptions;

/// <summary>
/// The per-HTLC state machine (core-lightning's <c>htlc_state</c>): the transition table and the per-state flags.
/// </summary>
/// <remarks>
/// <para>
/// Every update passes the five BOLT 2 stages (pending on the receiver, in the receiver's latest commitment, receiver's
/// previous commitment revoked, in the sender's latest commitment, sender's previous commitment revoked); an add and a
/// removal each take five states. Fee updates reuse the add half (owner = funder); they are final at
/// <see cref="HtlcState.SentAddAckRevocation"/> / <see cref="HtlcState.RcvdAddAckRevocation"/>.
/// </para>
/// <code>
///  we offered             L R  next                 | they offered            L R  next
///  SentAddHtlc 10         - -  SendCommit  -> 11    | RcvdAddHtlc 30          - -  RecvCommit  -> 31
///  SentAddCommit 11       - x  RecvRevoke  -> 12    | RcvdAddCommit 31        x -  SendRevoke  -> 32
///  RcvdAddRevocation 12   - x  RecvCommit  -> 13    | SentAddRevocation 32    x -  SendCommit  -> 33
///  RcvdAddAckCommit 13    x x  SendRevoke  -> 14    | SentAddAckCommit 33     x x  RecvRevoke  -> 34 (lock-in)
///  SentAddAckRevocation 14 x x RecvRemove  -> 15    | RcvdAddAckRevocation 34 x x  SendRemove  -> 35
///  RcvdRemoveHtlc 15      x x  RecvCommit  -> 16    | SentRemoveHtlc 35       x x  SendCommit  -> 36
///  RcvdRemoveCommit 16    - x  SendRevoke  -> 17    | SentRemoveCommit 36     x -  RecvRevoke  -> 37
///  SentRemoveRevocation 17 - x SendCommit  -> 18    | RcvdRemoveRevocation 37 x -  RecvCommit  -> 38
///  SentRemoveAckCommit 18 - -  RecvRevoke  -> 19    | RcvdRemoveAckCommit 38  - -  SendRevoke  -> 39
///  RcvdRemoveAckRevocation 19 final                 | SentRemoveAckRevocation 39 final
/// </code>
/// Every other (state, event) pair is illegal: <see cref="Next"/> throws and <see cref="TryNext"/> returns false.
/// Legacy states 0-3 are not part of the machine.
/// </remarks>
public static class HtlcStateTable
{
    /// <summary>All states of the machine (legacy values excluded), in numeric order.</summary>
    public static IReadOnlyList<HtlcState> States { get; } =
    [
        HtlcState.SentAddHtlc, HtlcState.SentAddCommit, HtlcState.RcvdAddRevocation, HtlcState.RcvdAddAckCommit,
        HtlcState.SentAddAckRevocation, HtlcState.RcvdRemoveHtlc, HtlcState.RcvdRemoveCommit,
        HtlcState.SentRemoveRevocation, HtlcState.SentRemoveAckCommit, HtlcState.RcvdRemoveAckRevocation,
        HtlcState.RcvdAddHtlc, HtlcState.RcvdAddCommit, HtlcState.SentAddRevocation, HtlcState.SentAddAckCommit,
        HtlcState.RcvdAddAckRevocation, HtlcState.SentRemoveHtlc, HtlcState.SentRemoveCommit,
        HtlcState.RcvdRemoveRevocation, HtlcState.RcvdRemoveAckCommit, HtlcState.SentRemoveAckRevocation
    ];

    /// <summary>True for the states of the machine (10-19, 30-39); false for legacy and undefined values.</summary>
    public static bool IsDefined(HtlcState state) =>
        state is >= HtlcState.SentAddHtlc and <= HtlcState.RcvdRemoveAckRevocation
            or >= HtlcState.RcvdAddHtlc and <= HtlcState.SentRemoveAckRevocation;

    /// <summary>
    /// Tries the transition for <paramref name="htlcEvent"/>. Returns false when the event does not move this state.
    /// </summary>
    public static bool TryNext(HtlcState state, HtlcEvent htlcEvent, out HtlcState next)
    {
        HtlcState? result = (state, htlcEvent) switch
        {
            (HtlcState.SentAddHtlc, HtlcEvent.SendCommit) => HtlcState.SentAddCommit,
            (HtlcState.SentAddCommit, HtlcEvent.RecvRevoke) => HtlcState.RcvdAddRevocation,
            (HtlcState.RcvdAddRevocation, HtlcEvent.RecvCommit) => HtlcState.RcvdAddAckCommit,
            (HtlcState.RcvdAddAckCommit, HtlcEvent.SendRevoke) => HtlcState.SentAddAckRevocation,
            (HtlcState.SentAddAckRevocation, HtlcEvent.RecvRemove) => HtlcState.RcvdRemoveHtlc,
            (HtlcState.RcvdRemoveHtlc, HtlcEvent.RecvCommit) => HtlcState.RcvdRemoveCommit,
            (HtlcState.RcvdRemoveCommit, HtlcEvent.SendRevoke) => HtlcState.SentRemoveRevocation,
            (HtlcState.SentRemoveRevocation, HtlcEvent.SendCommit) => HtlcState.SentRemoveAckCommit,
            (HtlcState.SentRemoveAckCommit, HtlcEvent.RecvRevoke) => HtlcState.RcvdRemoveAckRevocation,

            (HtlcState.RcvdAddHtlc, HtlcEvent.RecvCommit) => HtlcState.RcvdAddCommit,
            (HtlcState.RcvdAddCommit, HtlcEvent.SendRevoke) => HtlcState.SentAddRevocation,
            (HtlcState.SentAddRevocation, HtlcEvent.SendCommit) => HtlcState.SentAddAckCommit,
            (HtlcState.SentAddAckCommit, HtlcEvent.RecvRevoke) => HtlcState.RcvdAddAckRevocation,
            (HtlcState.RcvdAddAckRevocation, HtlcEvent.SendRemove) => HtlcState.SentRemoveHtlc,
            (HtlcState.SentRemoveHtlc, HtlcEvent.SendCommit) => HtlcState.SentRemoveCommit,
            (HtlcState.SentRemoveCommit, HtlcEvent.RecvRevoke) => HtlcState.RcvdRemoveRevocation,
            (HtlcState.RcvdRemoveRevocation, HtlcEvent.RecvCommit) => HtlcState.RcvdRemoveAckCommit,
            (HtlcState.RcvdRemoveAckCommit, HtlcEvent.SendRevoke) => HtlcState.SentRemoveAckRevocation,
            _ => null
        };

        next = result ?? state;
        return result.HasValue;
    }

    /// <summary>
    /// The transition for <paramref name="htlcEvent"/>.
    /// </summary>
    /// <exception cref="HtlcStateTransitionException">The event is illegal in <paramref name="state"/>.</exception>
    public static HtlcState Next(HtlcState state, HtlcEvent htlcEvent)
    {
        return TryNext(state, htlcEvent, out var next)
                   ? next
                   : throw new HtlcStateTransitionException(state, htlcEvent);
    }

    /// <summary>
    /// Who offered the HTLC: <see cref="HtlcDirection.Outgoing"/> for 10-19 (we offered),
    /// <see cref="HtlcDirection.Incoming"/> for 30-39. For fee updates this is the funder (Outgoing = we are funder).
    /// </summary>
    public static HtlcDirection Owner(HtlcState state)
    {
        EnsureDefined(state);
        return state <= HtlcState.RcvdRemoveAckRevocation ? HtlcDirection.Outgoing : HtlcDirection.Incoming;
    }

    /// <summary>The HTLC is an output (or trimmed amount) of our latest commitment.</summary>
    public static bool IsInLocalCommit(HtlcState state)
    {
        EnsureDefined(state);
        return state is >= HtlcState.RcvdAddAckCommit and <= HtlcState.RcvdRemoveHtlc
                   or >= HtlcState.RcvdAddCommit and <= HtlcState.RcvdRemoveRevocation;
    }

    /// <summary>The HTLC is an output (or trimmed amount) of the peer's latest commitment (including one we signed
    /// but the peer has not yet revoked the previous one for).</summary>
    public static bool IsInRemoteCommit(HtlcState state)
    {
        EnsureDefined(state);
        return state is >= HtlcState.SentAddCommit and <= HtlcState.SentRemoveRevocation
                   or >= HtlcState.SentAddAckCommit and <= HtlcState.SentRemoveHtlc;
    }

    /// <summary>The HTLC is in the latest commitment of <paramref name="side"/>.</summary>
    public static bool IsInCommit(HtlcState state, CommitmentSide side) =>
        side == CommitmentSide.Local ? IsInLocalCommit(state) : IsInRemoteCommit(state);

    /// <summary>
    /// The removal of the HTLC is already committed in the latest commitment of <paramref name="side"/> (the HTLC was
    /// in that commitment before and is not any more).
    /// </summary>
    public static bool IsRemovedFrom(HtlcState state, CommitmentSide side)
    {
        EnsureDefined(state);
        return side == CommitmentSide.Local
                   ? state is >= HtlcState.RcvdRemoveCommit and <= HtlcState.RcvdRemoveAckRevocation
                         or >= HtlcState.RcvdRemoveAckCommit
                   : state is >= HtlcState.SentRemoveAckCommit and <= HtlcState.RcvdRemoveAckRevocation
                         or >= HtlcState.SentRemoveCommit;
    }

    /// <summary>A removal (fulfill/fail) has been sent or received for the HTLC (states 15-19, 35-39).</summary>
    public static bool IsRemoval(HtlcState state)
    {
        EnsureDefined(state);
        return state is >= HtlcState.RcvdRemoveHtlc and <= HtlcState.RcvdRemoveAckRevocation
                   or >= HtlcState.SentRemoveHtlc;
    }

    /// <summary>
    /// The HTLC is gone from both commitments and both previous commitments are revoked: its removal is irrevocably
    /// committed and the record can be folded into the balances.
    /// </summary>
    public static bool IsFinal(HtlcState state)
    {
        EnsureDefined(state);
        return state is HtlcState.RcvdRemoveAckRevocation or HtlcState.SentRemoveAckRevocation;
    }

    /// <summary>
    /// The add is irrevocably committed: in both commitments and both previous commitments are revoked
    /// (<see cref="HtlcState.SentAddAckRevocation"/> / <see cref="HtlcState.RcvdAddAckRevocation"/> or later). An incoming
    /// HTLC is "locked in" at <see cref="HtlcState.RcvdAddAckRevocation"/>; only then may it be removed or forwarded.
    /// </summary>
    public static bool IsAddIrrevocablyCommitted(HtlcState state)
    {
        EnsureDefined(state);
        return state is >= HtlcState.SentAddAckRevocation and <= HtlcState.RcvdRemoveAckRevocation
                   or >= HtlcState.RcvdAddAckRevocation;
    }

    /// <summary>
    /// A fee update in this state is final: it is in both commitments and both previous commitments are revoked.
    /// </summary>
    public static bool IsFeeFinal(HtlcState state) =>
        state is HtlcState.SentAddAckRevocation or HtlcState.RcvdAddAckRevocation;

    /// <summary>The state a new HTLC or fee update starts in.</summary>
    public static HtlcState Initial(HtlcDirection owner) =>
        owner == HtlcDirection.Outgoing ? HtlcState.SentAddHtlc : HtlcState.RcvdAddHtlc;

    private static void EnsureDefined(HtlcState state)
    {
        if (!IsDefined(state))
            throw new ArgumentOutOfRangeException(nameof(state), state,
                                                  "Not a state of the HTLC state machine (legacy or undefined value)");
    }
}