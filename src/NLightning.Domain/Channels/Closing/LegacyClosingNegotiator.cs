namespace NLightning.Domain.Channels.Closing;

/// <summary>A <c>fee_range</c> (BOLT 2 <c>closing_signed_tlvs</c> type 1), in satoshis, both ends inclusive.</summary>
public sealed record ClosingFeeRange
{
    public ulong MinFeeSat { get; }
    public ulong MaxFeeSat { get; }

    public ClosingFeeRange(ulong minFeeSat, ulong maxFeeSat)
    {
        if (minFeeSat > maxFeeSat)
            throw new ArgumentException($"min_fee_satoshis {minFeeSat} is above max_fee_satoshis {maxFeeSat}");

        MinFeeSat = minFeeSat;
        MaxFeeSat = maxFeeSat;
    }

    public bool Contains(ulong feeSat) => feeSat >= MinFeeSat && feeSat <= MaxFeeSat;

    /// <summary>The fee in this range closest to <paramref name="feeSat"/>.</summary>
    public ulong Clamp(ulong feeSat) => Math.Clamp(feeSat, MinFeeSat, MaxFeeSat);

    /// <summary>The overlap of both ranges, or null when they do not overlap.</summary>
    public ClosingFeeRange? Intersect(ClosingFeeRange other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var min = Math.Max(MinFeeSat, other.MinFeeSat);
        var max = Math.Min(MaxFeeSat, other.MaxFeeSat);
        return min <= max ? new ClosingFeeRange(min, max) : null;
    }

    public override string ToString() => $"[{MinFeeSat}, {MaxFeeSat}] sat";
}

/// <summary>What a legacy <c>closing_signed</c> step decided.</summary>
public enum ClosingDecisionKind : byte
{
    /// <summary>Send a <c>closing_signed</c> with <see cref="ClosingDecision.FeeSat"/> (and the range, if any).</summary>
    Propose = 0,

    /// <summary>
    /// Both sides signed <see cref="ClosingDecision.FeeSat"/>: sign, broadcast, and send the same fee back when
    /// <see cref="ClosingDecision.Reply"/> is set.
    /// </summary>
    Agree = 1,

    /// <summary>Send a <c>warning</c>; close the connection when <see cref="ClosingDecision.CloseConnection"/>.</summary>
    Warn = 2,

    /// <summary>Fail the channel.</summary>
    Fail = 3
}

/// <summary>The outcome of a negotiation step.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="FeeSat">The fee to send or to close at (0 for a warning or a failure).</param>
/// <param name="FeeRange">The <c>fee_range</c> to send with it, or null for none.</param>
/// <param name="Reply">For <see cref="ClosingDecisionKind.Agree"/>: send a <c>closing_signed</c> with the fee.</param>
/// <param name="RequirementId">The BOLT 2 plan requirement that decided it.</param>
/// <param name="Reason">Why, for a warning or a failure.</param>
/// <param name="CloseConnection">For a warning: close the connection after it.</param>
public sealed record ClosingDecision(ClosingDecisionKind Kind, ulong FeeSat, ClosingFeeRange? FeeRange, bool Reply,
                                     string RequirementId, string? Reason = null, bool CloseConnection = false);

/// <summary>
/// The state of one legacy fee negotiation. It lives in memory only: BOLT 2 restarts the negotiation on every
/// reconnection (B2-RE-29).
/// </summary>
public sealed record ClosingNegotiation
{
    /// <summary>True when we funded the channel: we pay the fee and propose first (B2-CLS-01).</summary>
    public required bool IsFunder { get; init; }

    /// <summary>
    /// The fees we accept and the <c>fee_range</c> we send: as the funder, what we are prepared to pay; as the
    /// non-funder, a fairly low minimum up to the funder's balance (B2-CLS-04).
    /// </summary>
    public required ClosingFeeRange Acceptable { get; init; }

    /// <summary>Send <c>fee_range</c> with our <c>closing_signed</c> (BOLT 2 SHOULD; off only to exercise the legacy
    /// "strictly between" negotiation).</summary>
    public bool SendFeeRange { get; init; } = true;

    public ulong? LastSentFeeSat { get; init; }
    public ClosingFeeRange? LastSentRange { get; init; }
    public ulong? LastReceivedFeeSat { get; init; }
    public ClosingFeeRange? LastReceivedRange { get; init; }

    /// <summary>The number of <c>closing_signed</c> received in this negotiation.</summary>
    public int Rounds { get; init; }
}

/// <summary>
/// The pure legacy <c>closing_signed</c> fee negotiation of BOLT 2 (B2-CLS-01..R09), with and without
/// <c>fee_range</c>.
/// </summary>
/// <remarks>
/// Each call returns the decision and the next state; nothing else changes. Signatures, the transaction and the dust
/// check (B2-CLS-R10) are the caller's.
/// </remarks>
public static class LegacyClosingNegotiator
{
    /// <summary>
    /// The most <c>closing_signed</c> we receive in one negotiation while holding our last fee (B2-CLS-R09) before we
    /// give up with a warning and close the connection (the negotiation restarts on the next connection).
    /// </summary>
    public const int MaxRounds = 100;

    /// <summary>
    /// The funder's opening <c>closing_signed</c> (B2-CLS-01, B2-CLS-02): its estimate clamped into what it accepts,
    /// with that range as <c>fee_range</c>.
    /// </summary>
    public static (ClosingDecision Decision, ClosingNegotiation Next) Open(ClosingNegotiation state, ulong idealFeeSat)
    {
        ArgumentNullException.ThrowIfNull(state);
        var fee = state.Acceptable.Clamp(idealFeeSat);
        var range = state.SendFeeRange ? state.Acceptable : null;
        return Send(state, new ClosingDecision(ClosingDecisionKind.Propose, fee, range, true, "B2-CLS-02"));
    }

    /// <summary>A <c>closing_signed</c> from the peer, in the order of the BOLT 2 receiver requirements.</summary>
    /// <param name="state">The negotiation so far.</param>
    /// <param name="feeSat">The received <c>fee_satoshis</c>.</param>
    /// <param name="theirRange">The received <c>fee_range</c>, if any.</param>
    /// <param name="idealFeeSat">Our own estimate (used when we have not proposed yet).</param>
    public static (ClosingDecision Decision, ClosingNegotiation Next) Receive(ClosingNegotiation state, ulong feeSat,
                                                                               ClosingFeeRange? theirRange,
                                                                               ulong idealFeeSat)
    {
        ArgumentNullException.ThrowIfNull(state);
        var received = state with
        {
            LastReceivedFeeSat = feeSat,
            LastReceivedRange = theirRange,
            Rounds = state.Rounds + 1
        };

        // B2-CLS-R02: the fee we sent last came back
        if (state.LastSentFeeSat == feeSat)
            return (new ClosingDecision(ClosingDecisionKind.Agree, feeSat, state.LastSentRange, false, "B2-CLS-R02"),
                    received);

        // B2-CLS-R03: a fee inside the range we sent: close at it and send it back
        if (state.LastSentRange is { } sentRange && sentRange.Contains(feeSat))
            return Send(received,
                        new ClosingDecision(ClosingDecisionKind.Agree, feeSat, sentRange, true, "B2-CLS-R03"));

        return theirRange is not null
                   ? ReceiveWithRange(state, received, feeSat, theirRange)
                   : ReceiveWithoutRange(state, received, feeSat, idealFeeSat);
    }

    /// <summary>
    /// The range we send (or would send) against <paramref name="theirRange"/>: the funder keeps its own; the
    /// non-funder sets its maximum to at least the received one (B2-CLS-04).
    /// </summary>
    public static ClosingFeeRange OurRangeAgainst(ClosingNegotiation state, ClosingFeeRange theirRange)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(theirRange);
        if (state.LastSentRange is { } sent)
            return sent;

        return state.IsFunder
                   ? state.Acceptable
                   : new ClosingFeeRange(state.Acceptable.MinFeeSat,
                                         Math.Max(state.Acceptable.MaxFeeSat, theirRange.MaxFeeSat));
    }

    private static (ClosingDecision, ClosingNegotiation) ReceiveWithRange(ClosingNegotiation state,
                                                                           ClosingNegotiation received, ulong feeSat,
                                                                           ClosingFeeRange theirRange)
    {
        var ours = OurRangeAgainst(state, theirRange);
        var overlap = ours.Intersect(theirRange);

        // B2-CLS-R04: no overlap; the peer may still send a better range (failing after a timeout is not done here)
        if (overlap is null)
            return (new ClosingDecision(ClosingDecisionKind.Warn, 0, null, false, "B2-CLS-R04",
                                        $"fee_range {theirRange} does not overlap ours {ours}"), received);

        var rangeToSend = state.SendFeeRange ? ours : null;
        if (state.IsFunder)
        {
            // B2-CLS-R05: the funder takes any fee in the overlap and MUST reply with it
            if (!overlap.Contains(feeSat))
                return (new ClosingDecision(ClosingDecisionKind.Fail, 0, null, false, "B2-CLS-R05",
                                            $"fee_satoshis {feeSat} is outside the overlap {overlap}"), received);

            return Send(received,
                        new ClosingDecision(ClosingDecisionKind.Agree, feeSat, rangeToSend, true, "B2-CLS-R05"));
        }

        // B2-CLS-R06: a non-funder that already proposed only accepts its own fee back (handled by R02)
        if (state.LastSentFeeSat.HasValue)
            return (new ClosingDecision(ClosingDecisionKind.Fail, 0, null, false, "B2-CLS-R06",
                                        $"fee_satoshis {feeSat} differs from the {state.LastSentFeeSat} sat we sent"),
                    received);

        // Otherwise propose a fee in the overlap: the received one when it is there (the negotiation then ends)
        var chosen = overlap.Clamp(feeSat);
        var kind = chosen == feeSat ? ClosingDecisionKind.Agree : ClosingDecisionKind.Propose;
        return Send(received, new ClosingDecision(kind, chosen, rangeToSend, true, "B2-CLS-R06"));
    }

    private static (ClosingDecision, ClosingNegotiation) ReceiveWithoutRange(ClosingNegotiation state,
                                                                              ClosingNegotiation received,
                                                                              ulong feeSat, ulong idealFeeSat)
    {
        // A peer that sends no fee_range negotiates by the "strictly between" rule: we stop sending ours too, so both
        // sides apply the same rules (a range met by a fee outside the overlap would fail the channel, B2-CLS-R05)
        received = received with { SendFeeRange = false };
        ClosingFeeRange? rangeToSend = null;

        // B2-CLS-R07: after our proposal the peer must move strictly between our last fee and its previous one
        if (state is { LastSentFeeSat: { } lastSent, LastReceivedFeeSat: { } previous }
         && !IsStrictlyBetween(feeSat, lastSent, previous))
            return (new ClosingDecision(ClosingDecisionKind.Warn, 0, null, false, "B2-CLS-R07",
                                        $"fee_satoshis {feeSat} is not strictly between {lastSent} and {previous}",
                                        CloseConnection: true), received);

        // B2-CLS-R08: we agree, send it back
        if (state.Acceptable.Contains(feeSat))
            return Send(received,
                        new ClosingDecision(ClosingDecisionKind.Agree, feeSat, rangeToSend, true, "B2-CLS-R08"));

        // B2-CLS-R09: propose strictly between the received fee and our last one, as close to it as we accept
        ulong proposal;
        if (state.LastSentFeeSat is not { } last)
        {
            proposal = state.Acceptable.Clamp(idealFeeSat);
        }
        else
        {
            proposal = state.Acceptable.Clamp(Midpoint(last, feeSat));
            if (!IsStrictlyBetween(proposal, last, feeSat))
            {
                // Our last fee is already our bound. BOLT 2 only says SHOULD move strictly between; a peer that moves
                // by small steps (LND lowers its fee by 10 % per round) reaches our fee if we hold it, so we re-send it
                // instead of failing the channel, for at most MaxRounds messages
                if (received.Rounds > MaxRounds)
                    return (new ClosingDecision(ClosingDecisionKind.Warn, 0, null, false, "B2-CLS-R09",
                                                $"no agreement after {received.Rounds} closing_signed; our limit is {last} sat",
                                                CloseConnection: true), received);

                return Send(received,
                            new ClosingDecision(ClosingDecisionKind.Propose, last, rangeToSend, true, "B2-CLS-R09"));
            }
        }

        return Send(received,
                    new ClosingDecision(ClosingDecisionKind.Propose, proposal, rangeToSend, true, "B2-CLS-R09"));
    }

    /// <summary>Records what a decision sends.</summary>
    private static (ClosingDecision, ClosingNegotiation) Send(ClosingNegotiation state, ClosingDecision decision) =>
        (decision, decision.Reply
                       ? state with { LastSentFeeSat = decision.FeeSat, LastSentRange = decision.FeeRange }
                       : state);

    private static bool IsStrictlyBetween(ulong value, ulong a, ulong b) =>
        value > Math.Min(a, b) && value < Math.Max(a, b);

    private static ulong Midpoint(ulong a, ulong b) => Math.Min(a, b) + (Math.Max(a, b) - Math.Min(a, b)) / 2;
}