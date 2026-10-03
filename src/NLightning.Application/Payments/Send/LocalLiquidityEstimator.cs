namespace NLightning.Application.Payments.Send;

using Domain.Channels.Commitments;
using Domain.Crypto.ValueObjects;
using Domain.Exceptions;
using Domain.Protocol.Onion.Constants;

/// <summary>
/// The largest HTLC we may offer on a channel right now, by asking the commitment engine itself (a dry run of
/// <see cref="ChannelCommitments.SendAdd"/> on the immutable snapshot, never persisted): every BOLT 2 sender rule the
/// real offer checks (balance, the reserve the peer requires, the commitment fee we pay as funder with one more HTLC
/// output, <c>max_htlc_value_in_flight_msat</c>, <c>max_accepted_htlcs</c>, <c>htlc_minimum_msat</c>, dust exposure)
/// bounds it.
/// </summary>
/// <remarks>
/// The rules are monotonic above the trim threshold, so a binary search finds the largest accepted amount; below it a
/// trimmed HTLC can pass where a slightly larger untrimmed one fails, and the search may then settle below the true
/// maximum, which is safe. The real offer can still be refused when the channel changed meanwhile; the payment then
/// bounds that channel and plans again.
/// </remarks>
public static class LocalLiquidityEstimator
{
    private static readonly ReadOnlyMemory<byte> s_dryRunOnion = new byte[OnionConstants.PacketLength];
    private static readonly Hash s_dryRunHash = new(new byte[32]);

    /// <summary>
    /// The largest amount, in msat, of one more HTLC on <paramref name="commitments"/> after the HTLCs of
    /// <paramref name="plannedMsat"/> were offered on it; 0 when none fits.
    /// </summary>
    /// <param name="commitments">The channel's current snapshot.</param>
    /// <param name="plannedMsat">HTLCs already planned on the channel (dry-run in order before the new one).</param>
    /// <param name="cltvExpiry">A plausible <c>cltv_expiry</c> (the rules do not depend on it).</param>
    public static ulong MaxSendableMsat(ChannelCommitments commitments, IReadOnlyList<ulong> plannedMsat,
                                        uint cltvExpiry)
    {
        ArgumentNullException.ThrowIfNull(commitments);
        ArgumentNullException.ThrowIfNull(plannedMsat);

        var state = commitments;
        foreach (var amount in plannedMsat)
        {
            if (!TrySend(state, amount, cltvExpiry, out var next))
                return 0;
            state = next;
        }

        // Below the peer's htlc_minimum_msat every amount fails, so the search starts there
        ulong low = Math.Max(1, state.Params.Remote.HtlcMinimumMsat), high = state.LocalBalanceMsat, best = 0;
        while (low <= high)
        {
            var mid = low + (high - low) / 2;
            if (TrySend(state, mid, cltvExpiry, out _))
            {
                best = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return best;
    }

    private static bool TrySend(ChannelCommitments state, ulong amountMsat, uint cltvExpiry,
                                out ChannelCommitments next)
    {
        try
        {
            next = state.SendAdd(amountMsat, s_dryRunHash, cltvExpiry, s_dryRunOnion).Next;
            return true;
        }
        catch (CommitmentRefusedException)
        {
            next = state;
            return false;
        }
    }
}